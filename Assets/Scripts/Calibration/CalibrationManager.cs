using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

/*
CalibrationManager

Spawns a grid of CalibrationDot prefabs at fixed local positions under
`dotParent`. Shows them one at a time; each dot calls back OnDotSelected
when its 1-second long-pinch completes, and the manager advances to the
next dot. After the last dot, loads `nextSceneName`.
*/

public class CalibrationManager : MonoBehaviour
{
    [Header("Refs")]
    public Transform dotParent;
    public GameObject dotPrefab;

    [Header("Grid Layout")]
    public int columns = 3;
    public int rows = 3;
    public float horizontalSpacing = 0.25f;
    public float verticalSpacing = 0.25f;
    public Vector3 gridOffset = new Vector3(0f, -0.05f, 0f);

    [Header("After completion")]
    public string nextSceneName = "GazeGestureScene";
    public float nextSceneDelaySeconds = 1.0f;
    [Tooltip("Optional. When assigned, the 9-dot calibration hands off to the random saccadic evaluation task instead of loading the next scene directly; the task loads the next scene when it finishes.")]
    public SaccadicTaskController saccadicTask;

    private readonly List<GameObject> dots = new List<GameObject>();
    private int currentIndex = 0;

    void Start()
    {
        if (dotParent == null)
        {
            Debug.LogError("[CalibrationManager] dotParent not assigned.");
            return;
        }
        if (dotPrefab == null)
        {
            Debug.LogError("[CalibrationManager] dotPrefab not assigned.");
            return;
        }
        CreateDots();
        ShowNextDot();
    }

    void CreateDots()
    {
        dots.Clear();
        int index = 0;

        float xCenterOffset = (columns - 1) * 0.5f;
        float yCenterOffset = (rows - 1) * 0.5f;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                float localX = (col - xCenterOffset) * horizontalSpacing;
                float localY = (yCenterOffset - row) * verticalSpacing;
                Vector3 localPos = new Vector3(localX, localY, 0f) + gridOffset;

                GameObject dot = Instantiate(dotPrefab, dotParent);
                dot.transform.localPosition = localPos;
                dot.transform.localRotation = Quaternion.identity;

                CalibrationDot calibrationDot = dot.GetComponent<CalibrationDot>();
                if (calibrationDot != null)
                {
                    calibrationDot.manager = this;
                    calibrationDot.dotIndex = index;
                    calibrationDot.calibSender = CalibSender.Instance;
                }

                dot.SetActive(false);
                dots.Add(dot);
                index++;
            }
        }
    }

    void ShowNextDot()
    {
        if (currentIndex < dots.Count)
        {
            Debug.Log($"[CalibrationManager] Show dot {currentIndex}");
            dots[currentIndex].SetActive(true);
        }
    }

    public void OnDotSelected(int index)
    {
        if (index != currentIndex)
        {
            Debug.Log($"[CalibrationManager] Ignored dot {index}, currentIndex={currentIndex}");
            return;
        }

        Debug.Log($"[CalibrationManager] Dot {index} confirmed");

        dots[currentIndex].SetActive(false);
        currentIndex++;

        if (currentIndex < dots.Count)
        {
            ShowNextDot();
        }
        else if (saccadicTask != null)
        {
            Debug.Log("[CalibrationManager] Calibration complete -- starting saccadic evaluation.");
            Invoke(nameof(StartSaccadicEvaluation), nextSceneDelaySeconds);
        }
        else
        {
            Debug.Log("[CalibrationManager] Calibration complete -- loading next scene.");
            Invoke(nameof(FinishCalibration), nextSceneDelaySeconds);
        }
    }

    void StartSaccadicEvaluation()
    {
        saccadicTask.BeginEvaluationPhase();
    }

    public void FinishCalibration()
    {
        if (string.IsNullOrEmpty(nextSceneName))
        {
            Debug.LogWarning("[CalibrationManager] nextSceneName empty; staying on current scene.");
            return;
        }
        Debug.Log($"[CalibrationManager] Loading scene '{nextSceneName}'");
        SceneManager.LoadScene(nextSceneName);
    }
}
