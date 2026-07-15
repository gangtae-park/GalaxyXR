using UnityEngine;
using UnityEngine.InputSystem;

public class EyeGazeReader : MonoBehaviour
{
    public bool LatestIsTracked { get; private set; } = false;
    public Vector3 LatestGazeDirection { get; private set; } = Vector3.zero;
    // World-space head (camera) rotation sampled in the same Update as the
    // gaze, so a receiver can rebuild the world gaze ray from the camera-local
    // direction and compensate head motion. Updated even when gaze is lost.
    public Quaternion LatestHeadRotation { get; private set; } = Quaternion.identity;

    [Header("Assign from Input Actions")]
    public InputActionReference gazeRotationAction;
    public InputActionReference gazeTrackingStateAction;

    [Header("Marker (debug)")]
    public Transform marker;
    public float markerScale = 0.03f;
    public float markerDistance = 2.0f;

    [Header("Ray (debug)")]
    public float debugRayLength = 10f;

    [Header("Log")]
    public int logEveryNFrames = 36;

    void Start()
    {
        if (marker != null)
        {
            marker.localScale = Vector3.one * markerScale;
            marker.gameObject.SetActive(false);
        }
    }

    void OnEnable()
    {
        gazeRotationAction?.action.Enable();
        gazeTrackingStateAction?.action.Enable();
    }

    void OnDisable()
    {
        gazeRotationAction?.action.Disable();
        gazeTrackingStateAction?.action.Disable();
        LatestIsTracked = false;
        LatestGazeDirection = Vector3.zero;
        if (marker != null) marker.gameObject.SetActive(false);
    }

    void Update()
    {
        if (gazeRotationAction == null) return;

        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null) return;
        LatestHeadRotation = cam.rotation;

        Quaternion gazeRot = gazeRotationAction.action.ReadValue<Quaternion>();
        Vector3 gazeDir = (gazeRot * Vector3.forward).normalized;

        int trackingState = gazeTrackingStateAction != null
            ? gazeTrackingStateAction.action.ReadValue<int>()
            : 0;
        bool isTracked = (trackingState & 1) != 0;

        LatestIsTracked = isTracked;
        LatestGazeDirection = isTracked ? gazeDir : Vector3.zero;

        if (!isTracked)
        {
            if (marker != null) marker.gameObject.SetActive(false);
            return;
        }

        if (marker != null)
        {
            Debug.DrawRay(cam.position, gazeDir * debugRayLength, Color.red);
            marker.gameObject.SetActive(true);
            marker.position = cam.position + gazeDir * markerDistance;
        }

        if (logEveryNFrames > 0 && Time.frameCount % logEveryNFrames == 0)
        {
            Debug.Log($"[GAZE] tracked={isTracked}, gazeDir=({gazeDir.x:F3},{gazeDir.y:F3},{gazeDir.z:F3})");
        }
    }
}
