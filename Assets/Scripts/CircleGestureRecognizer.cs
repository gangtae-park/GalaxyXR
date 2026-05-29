using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class CircleGestureRecognizer : MonoBehaviour
{
    [Serializable]
    public class CircleGesturePayload
    {
        public string gestureName;
        public string eventType;
    }

    [Header("Hand Anchor")]
    public Transform handAnchor;
    public Transform indexTip;

    [Header("Message Sender")]
    public MsgSender msgSender;

    [Header("XR Pinch Action")]
    public InputActionReference pinchAction;
    public float pinchValueThreshold = 0.7f;

    [Header("Sampling")]
    public float minSampleDistance = 0.01f;
    public int minPointCount = 12;

    [Header("Circle Validation")]
    public float maxRadiusStdRatio = 0.35f;
    public float maxStartEndDistance = 0.1f;
    public float minWidthHeightRatio = 0.4f;

    private bool wasPinching = false;
    private List<Vector3> points = new List<Vector3>();

    /// <summary>
    /// Fired in EndRecording() when the pinch trajectory is validated as a circle.
    /// Subscribers (e.g. GestureAnchorTracker) can use this to act ONLY on successful gestures
    /// while ignoring FAIL/empty pinches.
    /// </summary>
    public event Action OnGestureRecognized;

    /// <summary>
    /// Fired when a pinch ended but didn't qualify as a valid circle (or hand tracking lost).
    /// </summary>
    public event Action OnGestureFailed;

    void OnEnable()
    {
        pinchAction?.action.Enable();
    }

    void OnDisable()
    {
        pinchAction?.action.Disable();
    }

    void Update()
    {
        if (pinchAction == null || pinchAction.action == null)
        {
            if (wasPinching)
            {
                Debug.Log("[Search/FindInfo] Pinch action missing");
                SendGestureEvent("FAIL");
                points.Clear();
                wasPinching = false;
            }
            return;
        }

        if (indexTip == null)
        {
            Debug.Log("[Search/FindInfo] Index finger tip missing");
            SendGestureEvent("FAIL");
            points.Clear();
            wasPinching = false;
            return;
        }

        float pinchValue = pinchAction.action.ReadValue<float>();
        bool isPinching = pinchValue >= pinchValueThreshold;
        Vector3 trajectoryPoint = indexTip.position;

        if (isPinching && !wasPinching)
        {
            StartRecording(trajectoryPoint);
        }
        else if (isPinching && wasPinching)
        {
            RecordPoint(trajectoryPoint);
        }
        else if (!isPinching && wasPinching)
        {
            EndRecording();
        }

        wasPinching = isPinching;
    }


    void StartRecording(Vector3 startPoint)
    {
        points.Clear();
        points.Add(startPoint);
        Debug.Log("[Search/FindInfo] Pinch Detected");
        SendGestureEvent("START");
    }

    void RecordPoint(Vector3 point)
    {
        if (points.Count == 0 || Vector3.Distance(points[points.Count - 1], point) > minSampleDistance)
        {
            points.Add(point);
        }
    }

    void EndRecording()
    {
        Debug.Log($"[Search/FindInfo] Pinch Ended. Collected {points.Count} points");

        Vector3 endPoint = points.Count > 0 ? points[points.Count - 1] : Vector3.zero;
        SendGestureEvent("END");

        Vector3 circleCenter;
        if (IsCircle(points, out circleCenter))
        {
            Debug.Log($"[Search/FindInfo] Gesture Recognized: Search / Find Info.");
            SendGestureSignal();
            try { OnGestureRecognized?.Invoke(); }
            catch (Exception e) { Debug.LogError($"[CircleCheck] OnGestureRecognized subscriber threw: {e}"); }
        }
        else
        {
            SendGestureEvent("FAIL");
            try { OnGestureFailed?.Invoke(); }
            catch (Exception e) { Debug.LogError($"[CircleCheck] OnGestureFailed subscriber threw: {e}"); }
        }

        points.Clear();
    }

    bool IsCircle(List<Vector3> pts, out Vector3 detectedCenter)
    {
        detectedCenter = Vector3.zero;

        if (pts == null || pts.Count < minPointCount)
        {
            Debug.Log($"[Search/FindInfo] Failed: Not enough points ({pts?.Count ?? 0})");
            return false;
        }

        // 1) center check
        Vector3 center = Vector3.zero;
        foreach (var p in pts)
            center += p;
        center /= pts.Count;
        detectedCenter = center;

        // 2) radius check
        List<float> radii = new List<float>();
        float radiusSum = 0f;

        foreach (var p in pts)
        {
            float r = Vector3.Distance(p, center);
            radii.Add(r);
            radiusSum += r;
        }

        float meanRadius = radiusSum / radii.Count;
        if (meanRadius < 0.025f)
        {
            Debug.Log($"[Search/FindInfo] Failed: Radius too small ({meanRadius})");
            return false;
        }

        float variance = 0f;
        foreach (var r in radii)
        {
            float diff = r - meanRadius;
            variance += diff * diff;
        }
        variance /= radii.Count;
        float std = Mathf.Sqrt(variance);

        float radiusStdRatio = std / meanRadius;
        if (radiusStdRatio > maxRadiusStdRatio)
        {
            Debug.Log($"[Search/FindInfo] Failed: Radius variance too high ({radiusStdRatio})");
            return false;
        }

        // 3) start-end distance
        float startEndDistance = Vector3.Distance(pts[0], pts[pts.Count - 1]);
        if (startEndDistance > maxStartEndDistance)
        {
            Debug.Log($"[Search/FindInfo] Failed: Start and end too far ({startEndDistance})");
            return false;
        }

        // 4) bounding box ratio
        Vector3 min = pts[0];
        Vector3 max = pts[0];

        foreach (var p in pts)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        Vector3 size = max - min;

        float[] dims = new float[] { size.x, size.y, size.z };
        System.Array.Sort(dims);

        float width = dims[2];
        float height = dims[1];

        if (height < 0.0001f)
        {
            Debug.Log($"[Search/FindInfo] Failed: Height too small ({height})");
            return false;
        }

        float WHRatio = Mathf.Min(width, height) / Mathf.Max(width, height);
        if (WHRatio < minWidthHeightRatio)
        {
            Debug.Log($"[Search/FindInfo] Failed: Shape too elongated ({WHRatio})");
            return false;
        }

        Debug.Log($"[Search/FindInfo] Success: Circle detected | meanRadius: {meanRadius} | radiusStdRatio: {radiusStdRatio} | startEndDistance: {startEndDistance} | WHRatio: {WHRatio}");
        return true;
    }

    void SendGestureEvent(string eventType)
    {
        if (msgSender == null)
        {
            Debug.LogWarning("[Search/FindInfo] msgSender is not assigned.");
            return;
        }

        CircleGesturePayload payload = new CircleGesturePayload
        {
            gestureName = "Search/Find Info",
            eventType = eventType
        };

        Debug.Log($"[Search/FindInfo] Sending gesture event={eventType}");
        msgSender.SendGestureEvent(payload);
    }

    void SendGestureSignal()
    {
        if (msgSender == null)
        {
            Debug.LogWarning("[Search/FindInfo] msgSender is not assigned. Gesture signal was not forwarded.");
            return;
        }
    
        CircleGesturePayload payload = new CircleGesturePayload
        {
            gestureName = "Search/Find Info",
            eventType = "RECOGNIZED"
        };

        Debug.Log("[Search/FindInfo] Sending gesture signal");
        msgSender.SendCircleGesture(payload);
    }

}