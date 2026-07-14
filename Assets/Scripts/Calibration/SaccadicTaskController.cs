using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/*
SaccadicTaskController

Post-calibration accuracy evaluation for CalibrationScene. Flow:

  1. CalibrationManager finishes the 9-dot calibration and calls
     BeginEvaluationPhase() (instead of loading the next scene directly).
  2. A "Test Start" target appears at the grid center -- same pinch-hold
     interaction as the calibration dots (the scene has no UI canvas, so a
     dot-style XRSimpleInteractable IS the button).
  3. On confirm: 3-2-1 countdown, then SACCADE_BEGIN is sent and the random
     saccadic task runs: `fixationCount` fixation dots, `fixationSeconds`
     each, at random positions inside the SAME rectangle the calibration grid
     spans. Consecutive fixations are at least `minSaccadeAngleDeg` degrees
     of visual angle apart (measured from the head camera).
  4. Each fixation is announced with SACCADE_FIX,seq,t,index,u,v where u/v
     is the position normalized to the grid extent (u: 0=left col..1=right
     col, v: 0=top row..1=bottom row). calibration.py maps u/v to expected
     screen coords via the known 9-dot norm targets and records mapped gaze
     + the ADB screen for pixel-error analysis.
  5. After the last fixation SACCADE_END is sent and the manager's
     FinishCalibration() loads the next scene.

CalibSender keeps its idle GAZE stream running the whole time, which is what
calibration.py maps through the freshly fitted ridge model.
*/

public class SaccadicTaskController : MonoBehaviour
{
    [Header("Refs")]
    public CalibrationManager manager;
    [Tooltip("Defaults to CalibSender.Instance at runtime.")]
    public CalibSender calibSender;

    [Header("Task")]
    public int fixationCount = 30;
    public float fixationSeconds = 1.0f;
    [Range(0, 10)] public int countdownSeconds = 3;
    [Tooltip("Minimum visual angle (degrees, from the head camera) between consecutive fixations.")]
    public float minSaccadeAngleDeg = 3f;

    [Header("Start target")]
    public float startHoldSeconds = 1.0f;
    public float startScaleMultiplier = 1.6f;

    [Header("Label / countdown text")]
    [Tooltip("3D TMP font size; ~0.1 m character height per unit.")]
    public float labelFontSize = 1.0f;
    public Color labelColor = Color.white;
    [Tooltip("Meters above the grid center for the 'Test Start' label.")]
    public float labelYOffset = 0.12f;

    private GameObject _startTarget;
    private GameObject _fixationDot;
    private TextMeshPro _label;
    private Coroutine _holdCoroutine;
    private Vector3 _startTargetInitialScale;
    private bool _running;

    // grid rect in dotParent local space, mirrors CalibrationManager.CreateDots()
    private float _xMin, _xMax, _yTop, _yBottom, _zLocal;

    public void BeginEvaluationPhase()
    {
        if (_running) return;
        if (manager == null || manager.dotParent == null || manager.dotPrefab == null)
        {
            Debug.LogError("[SaccadicTask] manager/dotParent/dotPrefab missing -- skipping evaluation, loading next scene.");
            manager?.FinishCalibration();
            return;
        }
        if (calibSender == null) calibSender = CalibSender.Instance;

        ComputeGridRect();
        SpawnStartTarget();
        ShowLabel("Test Start", GridCenterLocal() + new Vector3(0f, labelYOffset, 0f));
        Debug.Log("[SaccadicTask] Evaluation phase ready -- waiting for Test Start pinch-hold.");
    }

    void ComputeGridRect()
    {
        float xExt = (manager.columns - 1) * 0.5f * manager.horizontalSpacing;
        float yExt = (manager.rows - 1) * 0.5f * manager.verticalSpacing;
        Vector3 off = manager.gridOffset;
        _xMin = -xExt + off.x;
        _xMax = xExt + off.x;
        _yTop = yExt + off.y;      // v = 0 (top row, like dot 0)
        _yBottom = -yExt + off.y;  // v = 1 (bottom row, like dot 8)
        _zLocal = off.z;
    }

    Vector3 GridCenterLocal() => new Vector3((_xMin + _xMax) * 0.5f, (_yTop + _yBottom) * 0.5f, _zLocal);

    Vector3 UvToLocal(float u, float v) =>
        new Vector3(Mathf.Lerp(_xMin, _xMax, u), Mathf.Lerp(_yTop, _yBottom, v), _zLocal);

    // ---------- Test Start target (dot-prefab based pinch-hold button) ----------

    void SpawnStartTarget()
    {
        _startTarget = Instantiate(manager.dotPrefab, manager.dotParent);
        _startTarget.name = "SaccadeTestStart";
        _startTarget.transform.localPosition = GridCenterLocal();
        _startTarget.transform.localRotation = Quaternion.identity;

        // Reuse the prefab's visual + interactable, but not its calibration logic.
        var calibDot = _startTarget.GetComponent<CalibrationDot>();
        if (calibDot != null) Destroy(calibDot);

        _startTargetInitialScale = _startTarget.transform.localScale;

        var interactable = _startTarget.GetComponent<XRSimpleInteractable>();
        if (interactable == null)
        {
            Debug.LogError("[SaccadicTask] dotPrefab has no XRSimpleInteractable; cannot build Test Start target.");
            return;
        }
        interactable.selectEntered.AddListener(OnStartSelectEntered);
        interactable.selectExited.AddListener(OnStartSelectExited);
        _startTarget.SetActive(true);
    }

    void OnStartSelectEntered(SelectEnterEventArgs args)
    {
        if (_running) return;
        CancelStartHold();
        _holdCoroutine = StartCoroutine(StartHoldRoutine());
    }

    void OnStartSelectExited(SelectExitEventArgs args)
    {
        if (_running) return;
        CancelStartHold();
        if (_startTarget != null) _startTarget.transform.localScale = _startTargetInitialScale;
    }

    IEnumerator StartHoldRoutine()
    {
        float elapsed = 0f;
        Vector3 targetScale = _startTargetInitialScale * startScaleMultiplier;
        while (elapsed < startHoldSeconds)
        {
            elapsed += Time.deltaTime;
            _startTarget.transform.localScale = Vector3.Lerp(
                _startTargetInitialScale, targetScale, Mathf.Clamp01(elapsed / startHoldSeconds));
            yield return null;
        }
        _holdCoroutine = null;
        Debug.Log("[SaccadicTask] Test Start confirmed.");
        StartCoroutine(RunTask());
    }

    void CancelStartHold()
    {
        if (_holdCoroutine != null)
        {
            StopCoroutine(_holdCoroutine);
            _holdCoroutine = null;
        }
    }

    // ---------- The task itself ----------

    IEnumerator RunTask()
    {
        _running = true;
        if (_startTarget != null) Destroy(_startTarget);

        for (int i = countdownSeconds; i > 0; i--)
        {
            ShowLabel(i.ToString(), GridCenterLocal());
            yield return new WaitForSeconds(1f);
        }
        HideLabel();

        calibSender?.SendSaccadeBegin(fixationCount);

        _fixationDot = Instantiate(manager.dotPrefab, manager.dotParent);
        _fixationDot.name = "SaccadeFixationDot";
        StripInteraction(_fixationDot);
        _fixationDot.SetActive(true);

        bool hasPrev = false;
        Vector3 prevWorld = Vector3.zero;
        int shown = 0;

        for (int i = 0; i < fixationCount; i++)
        {
            Vector2 uv = SampleNextUv(prevWorld, hasPrev);
            Vector3 local = UvToLocal(uv.x, uv.y);
            _fixationDot.transform.localPosition = local;
            prevWorld = _fixationDot.transform.position;
            hasPrev = true;

            calibSender?.SendSaccadeFixation(i, uv.x, uv.y);
            shown++;
            yield return new WaitForSeconds(fixationSeconds);
        }

        Destroy(_fixationDot);
        calibSender?.SendSaccadeEnd(shown);
        Debug.Log($"[SaccadicTask] Done ({shown} fixations) -- loading next scene.");

        yield return new WaitForSeconds(manager.nextSceneDelaySeconds);
        manager.FinishCalibration();
    }

    Vector2 SampleNextUv(Vector3 prevWorld, bool hasPrev)
    {
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;

        Vector2 best = new Vector2(Random.value, Random.value);
        float bestAngle = -1f;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            Vector2 uv = new Vector2(Random.value, Random.value);
            if (!hasPrev || cam == null) return uv;

            Vector3 world = manager.dotParent.TransformPoint(UvToLocal(uv.x, uv.y));
            float angle = Vector3.Angle(prevWorld - camPos, world - camPos);
            if (angle >= minSaccadeAngleDeg) return uv;
            if (angle > bestAngle)
            {
                bestAngle = angle;
                best = uv;
            }
        }
        Debug.LogWarning($"[SaccadicTask] Could not reach {minSaccadeAngleDeg} deg separation after 100 tries; using best candidate ({bestAngle:F2} deg).");
        return best;
    }

    static void StripInteraction(GameObject go)
    {
        var calibDot = go.GetComponent<CalibrationDot>();
        if (calibDot != null) Destroy(calibDot);
        var interactable = go.GetComponent<XRSimpleInteractable>();
        if (interactable != null) Destroy(interactable);
        foreach (var col in go.GetComponentsInChildren<Collider>())
            Destroy(col);
    }

    // ---------- Floating label (runtime 3D TMP, faces the same way as the grid) ----------

    void ShowLabel(string text, Vector3 localPos)
    {
        if (_label == null)
        {
            var go = new GameObject("SaccadeLabel");
            go.transform.SetParent(manager.dotParent, false);
            _label = go.AddComponent<TextMeshPro>();
            _label.alignment = TextAlignmentOptions.Center;
            _label.fontSize = labelFontSize;
            _label.color = labelColor;
            _label.rectTransform.sizeDelta = new Vector2(2f, 0.5f);
        }
        _label.transform.localPosition = localPos;
        _label.transform.localRotation = Quaternion.identity;
        _label.text = text;
        _label.gameObject.SetActive(true);
    }

    void HideLabel()
    {
        if (_label != null) _label.gameObject.SetActive(false);
    }
}
