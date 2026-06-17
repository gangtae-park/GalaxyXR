using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Logs the current pinch_value from an XRI InputActionReference at a fixed rate,
/// so you can see exactly what values your hand produces in closed / half-pinch /
/// rest states. Useful for picking pinch threshold constants.
///
/// Usage:
///   1. Add this component to any GameObject in the scene.
///   2. Drag the same Pinch action reference you use in PinchStrokeCapture into
///      `pinchAction`.
///   3. Run the build on the headset. Watch the Unity log (or adb logcat).
///
/// The component also tracks running min / max so you can see the full range you
/// produced during a session.
/// </summary>
public class PinchValueLogger : MonoBehaviour
{
    [Header("Input")]
    public InputActionReference pinchAction;

    [Header("Hand joints (for measured thumb-index distance)")]
    [Tooltip("Drag the thumb tip transform here to also log actual world-space distance.")]
    public Transform thumbTip;
    [Tooltip("Drag the index tip transform here to also log actual world-space distance.")]
    public Transform indexTip;

    [Header("Logging")]
    [Tooltip("How many log lines per second. 5 is comfortable; raise to 20 for finer detail.")]
    [Range(1, 60)] public int logHz = 5;
    [Tooltip("Also print a state label (CLOSED / HALF / OPEN) based on the thresholds below.")]
    public bool logStateLabel = true;

    [Header("Pinch-value state thresholds (for the label only)")]
    [Range(0f, 1f)] public float closedThreshold = 0.7f;
    [Range(0f, 1f)] public float halfThreshold = 0.15f;

    [Header("Distance state thresholds (meters)")]
    [Tooltip("Below this distance = fully closed pinch (start/end of Translate).")]
    public float distClosedMax = 0.025f;
    [Tooltip("Above distClosedMax and below this = intentional half-pinch (frame drawing).")]
    public float distHalfMax = 0.10f;

    [Header("Live stats (read-only)")]
    [SerializeField] private float currentValue;
    [SerializeField] private float sessionMin = 1f;
    [SerializeField] private float sessionMax = 0f;
    [SerializeField] private string currentValueState = "?";

    [SerializeField] private float currentDistance = -1f;
    [SerializeField] private float distMin = 999f;
    [SerializeField] private float distMax = 0f;
    [SerializeField] private string currentDistState = "?";

    private float _lastLogTime;

    void OnEnable()
    {
        pinchAction?.action.Enable();
        ResetMinMax();
        Debug.Log("[PinchValueLogger] started. " +
                  $"pinch_value action={(pinchAction != null ? pinchAction.action.name : "null")}, " +
                  $"thumbTip={(thumbTip != null)}, indexTip={(indexTip != null)}");
    }

    void OnDisable()
    {
        pinchAction?.action.Disable();
    }

    void Update()
    {
        // 1) pinch_value
        if (pinchAction != null && pinchAction.action != null)
        {
            float v = pinchAction.action.ReadValue<float>();
            currentValue = v;
            if (v < sessionMin) sessionMin = v;
            if (v > sessionMax) sessionMax = v;
            currentValueState = ClassifyByValue(v);
        }

        // 2) thumb-index world distance (much more useful for half-pinch detection)
        if (thumbTip != null && indexTip != null)
        {
            float d = Vector3.Distance(thumbTip.position, indexTip.position);
            currentDistance = d;
            if (d < distMin) distMin = d;
            if (d > distMax) distMax = d;
            currentDistState = ClassifyByDistance(d);
        }
        else
        {
            currentDistance = -1f;
            currentDistState = "no-joints";
        }

        // Throttled log
        float interval = 1f / Mathf.Max(1, logHz);
        if (Time.unscaledTime - _lastLogTime >= interval)
        {
            _lastLogTime = Time.unscaledTime;
            if (logStateLabel)
            {
                Debug.Log(
                    $"[Pinch] value={currentValue:F3}({currentValueState})  " +
                    $"dist={currentDistance*100f:F1}cm({currentDistState})  " +
                    $"session[value:{sessionMin:F2}-{sessionMax:F2} " +
                    $"dist:{distMin*100f:F1}-{distMax*100f:F1}cm]"
                );
            }
            else
            {
                Debug.Log($"[Pinch] value={currentValue:F3} dist={currentDistance*100f:F1}cm");
            }
        }
    }

    string ClassifyByValue(float v)
    {
        if (v >= closedThreshold) return "CLOSED";
        if (v >= halfThreshold)   return "HALF";
        return "OPEN";
    }

    string ClassifyByDistance(float d)
    {
        if (d < distClosedMax) return "CLOSED";
        if (d < distHalfMax)   return "HALF";
        return "OPEN";
    }

    [ContextMenu("Reset session min/max")]
    public void ResetMinMax()
    {
        sessionMin = 1f;
        sessionMax = 0f;
        distMin = 999f;
        distMax = 0f;
        Debug.Log("[PinchValueLogger] min/max reset.");
    }
}
