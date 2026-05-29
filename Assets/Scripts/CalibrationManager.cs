using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public class CalibrationManager : MonoBehaviour
{
    public Transform dotParent;
    public GameObject dotPrefab;

    [Header("Grid Layout")]
    public int columns = 3;
    public int rows = 3;
    public float horizontalSpacing = 0.12f;
    public float verticalSpacing = 0.12f;
    public Vector3 gridOffset = new Vector3(0f, 0.03f, 0f);

    private List<GameObject> dots = new List<GameObject>();
    private int currentIndex = 0;

    void Start()
    {
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
                    calibrationDot.calibSender = CalibSender    .Instance;
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
        else
        {
            Debug.Log("Calibration Complete");
            Invoke(nameof(FinishCalibration), 1.0f);
        }
    }

    public void FinishCalibration()
    {
        Debug.Log("Calibration complete. Loading gesture scene...");
        SceneManager.LoadScene("GestureScene");
    }
}