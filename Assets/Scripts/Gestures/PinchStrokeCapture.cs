using System;
using UnityEngine;
using UnityEngine.InputSystem;

/*
Captures a pinch trajectory as a single Stroke and emits lifecycle events.
This component knows nothing about gesture classification -- GestureRouter
subscribes and decides what the stroke means.
*/

public class PinchStrokeCapture : MonoBehaviour
{
    [Header("Pinch")]
    public Transform indexTip;
    public InputActionReference pinchAction;
    [Range(0f, 1f)] public float pinchValueThreshold = 0.7f;
    public float minSampleDistance = 0.01f;

    [Header("Camera")]
    public Camera referenceCamera;

    public event Action<Stroke> OnStrokeStarted;
    public event Action<Stroke> OnStrokeUpdated;
    public event Action<Stroke> OnStrokeCompleted;
    public event Action OnStrokeCancelled;

    private bool _wasPinching = false;
    private Stroke _currentStroke;

    void OnEnable()
    {
        pinchAction?.action.Enable();
    }

    void OnDisable()
    {
        pinchAction?.action.Disable();
        if (_currentStroke != null)
        {
            _currentStroke = null;
            try { OnStrokeCancelled?.Invoke(); } catch { }
        }
    }

    void Update()
    {
        if (pinchAction == null || pinchAction.action == null)
        {
            CancelStroke("Pinch action missing");
            return;
        }
        if (indexTip == null)
        {
            CancelStroke("Index tip missing");
            return;
        }

        bool isPinching = pinchAction.action.ReadValue<float>() >= pinchValueThreshold;

        if (isPinching && !_wasPinching)
        {
            BeginStroke();
        }
        else if (isPinching && _wasPinching)
        {
            UpdateStroke();
        }
        else if (!isPinching && _wasPinching)
        {
            EndStroke();
        }

        _wasPinching = isPinching;
    }

    void BeginStroke()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;

        _currentStroke = new Stroke
        {
            StartTime = Time.time,
            CameraPositionAtStart = cam != null ? cam.transform.position : Vector3.zero,
            CameraRotationAtStart = cam != null ? cam.transform.rotation : Quaternion.identity,
            CameraRightAtStart   = cam != null ? cam.transform.right    : Vector3.right,
            CameraUpAtStart      = cam != null ? cam.transform.up       : Vector3.up,
            CameraForwardAtStart = cam != null ? cam.transform.forward  : Vector3.forward,
        };
        _currentStroke.WorldPoints.Add(indexTip.position);

        Debug.Log("[PinchStrokeCapture] Stroke STARTED");
        try { OnStrokeStarted?.Invoke(_currentStroke); } catch (Exception e) { Debug.LogError(e); }
    }

    void UpdateStroke()
    {
        if (_currentStroke == null) return;

        Vector3 p = indexTip.position;
        var pts = _currentStroke.WorldPoints;
        if (pts.Count == 0 || Vector3.Distance(pts[pts.Count - 1], p) > minSampleDistance)
        {
            pts.Add(p);
            try { OnStrokeUpdated?.Invoke(_currentStroke); } catch (Exception e) { Debug.LogError(e); }
        }
    }

    void EndStroke()
    {
        if (_currentStroke == null) return;
        _currentStroke.EndTime = Time.time;

        Debug.Log($"[PinchStrokeCapture] Stroke COMPLETED ({_currentStroke.PointCount} points)");
        Stroke completeStroke = _currentStroke;
        _currentStroke = null;
        try { OnStrokeCompleted?.Invoke(completeStroke); } catch (Exception e) { Debug.LogError(e); }
    }

    void CancelStroke(string reason)
    {
        if (_wasPinching || _currentStroke != null)
        {
            Debug.Log($"[PinchStrokeCapture] Stroke CANCELLED: {reason}");
            _currentStroke = null;
            _wasPinching = false;
            try { OnStrokeCancelled?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
        }
    }
}
