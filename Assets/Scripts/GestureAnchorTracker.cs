using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Captures the world-space gaze anchor when a gesture is recognized, or on
/// direct pinch release when no router is assigned.
/// </summary>
public class GestureAnchorTracker : MonoBehaviour
{
    [Serializable]
    public class AnchorPose
    {
        public string gestureName;       // "Search/Find Info" | "Ask" | etc. So spawners can filter.
        public Vector3 worldPosition;
        public Vector3 cameraPosition;
        public Vector3 cameraRight;
        public Vector3 cameraUp;
        public Vector3 cameraForward;
        public Vector3 gazeDirection;
        public bool gazeWasTracked;
        public float captureTime;
    }

    [Header("Gesture Source (preferred)")]
    [Tooltip("If assigned, anchor fires when this router recognizes a gesture.")]
    public GestureRouter gestureRouter;

    [Header("Eye Gaze (optional)")]
    [Tooltip("If assigned, anchor uses LatestGazeDirection. Otherwise falls back to camera.forward.")]
    public EyeGazeReader eyeGazeReader;
    [Tooltip("If null, Camera.main is used at runtime.")]
    public Camera referenceCamera;

    [Header("Fallback: direct pinch detection")]
    [Tooltip("Used only when gestureRouter is null.")]
    public InputActionReference pinchAction;
    [Range(0f, 1f)] public float pinchValueThreshold = 0.7f;

    [Header("Anchor Placement")]
    [Tooltip("Distance to place the anchor when raycast misses (no scene geometry hit).")]
    public float defaultDepth = 1.5f;
    public LayerMask raycastMask = ~0;
    public float maxRaycastDistance = 20f;

    public AnchorPose LatestAnchor { get; private set; }
    public event Action<AnchorPose> OnGestureEndAnchor;

    private bool _wasPinching = false;

    void OnEnable()
    {
        if (gestureRouter != null)
        {
            gestureRouter.OnGestureRecognized += HandleRouterRecognized;
        }
        else
        {
            pinchAction?.action.Enable();
        }
    }

    void OnDisable()
    {
        if (gestureRouter != null)
        {
            gestureRouter.OnGestureRecognized -= HandleRouterRecognized;
        }
        else
        {
            pinchAction?.action.Disable();
        }
    }

    void HandleRouterRecognized(string gestureName)
    {
        CaptureAnchor(gestureName);
    }

    void Update()
    {
        // Anchors come via subscribed events when gestureRouter is assigned.
        if (gestureRouter != null) return;
        if (pinchAction == null || pinchAction.action == null) return;

        bool isPinching = pinchAction.action.ReadValue<float>() >= pinchValueThreshold;

        if (_wasPinching && !isPinching)
        {
            CaptureAnchor("Unknown");
        }

        _wasPinching = isPinching;
    }

    void CaptureAnchor(string gestureName)
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[GestureAnchorTracker] No camera available; cannot capture anchor.");
            return;
        }

        Vector3 origin = cam.transform.position;

        bool gazeTracked = eyeGazeReader != null && eyeGazeReader.LatestIsTracked &&
                           eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f;

        Vector3 dir = gazeTracked
            ? eyeGazeReader.LatestGazeDirection.normalized
            : cam.transform.forward;

        Vector3 hitPoint;
        if (Physics.Raycast(origin, dir, out RaycastHit hit, maxRaycastDistance, raycastMask, QueryTriggerInteraction.Ignore))
        {
            hitPoint = hit.point;
        }
        else
        {
            hitPoint = origin + dir * defaultDepth;
        }

        AnchorPose pose = new AnchorPose
        {
            gestureName = gestureName,
            worldPosition = hitPoint,
            cameraPosition = origin,
            cameraRight = cam.transform.right,
            cameraUp = cam.transform.up,
            cameraForward = cam.transform.forward,
            gazeDirection = dir,
            gazeWasTracked = gazeTracked,
            captureTime = Time.time,
        };

        LatestAnchor = pose;

        Debug.Log(
            $"[GestureAnchorTracker] Anchor captured for '{gestureName}' at {hitPoint} " +
            $"(gaze={(gazeTracked ? "tracked" : "fallback-camera-forward")})"
        );

        try { OnGestureEndAnchor?.Invoke(pose); }
        catch (Exception e) { Debug.LogError($"[GestureAnchorTracker] subscriber threw: {e}"); }
    }
}
