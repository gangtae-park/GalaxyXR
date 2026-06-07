using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.XR.Hands;

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
    public bool useOpenXRFallbackPinch = true;
    [Tooltip("Ignore palm-facing system navigation and Circle to Search pinches.")]
    public bool ignoreSystemGestures = true;
    public float pinchDebugLogIntervalSeconds = 1f;

    [Header("Camera")]
    public Camera referenceCamera;

    public event Action<Stroke> OnStrokeStarted;
    public event Action<Stroke> OnStrokeUpdated;
    public event Action<Stroke> OnStrokeCompleted;
    public event Action OnStrokeCancelled;

    private bool _wasPinching = false;
    private Stroke _currentStroke;
    private float _nextPinchDebugLogTime;
    private bool _loggedInputDetails;
    private bool _loggedSystemGesture;

    void OnEnable()
    {
        pinchAction?.action.Enable();
        LogInputDetails();
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
        if (indexTip == null)
        {
            CancelStroke("Index tip missing");
            return;
        }

        if (ignoreSystemGestures && IsSystemGestureActive())
        {
            CancelStroke("Android XR system gesture active");
            if (!_loggedSystemGesture)
            {
                Debug.LogWarning(
                    "[PinchStrokeCapture] Ignoring system pinch. Keep the palm facing " +
                    "away from the headset when drawing the app's Search circle.");
                _loggedSystemGesture = true;
            }
            return;
        }

        _loggedSystemGesture = false;
        float pinchValue = ReadPinchValue();
        bool isPinching = pinchValue >= pinchValueThreshold;

        if (pinchDebugLogIntervalSeconds > 0f && Time.realtimeSinceStartup >= _nextPinchDebugLogTime)
        {
            _nextPinchDebugLogTime = Time.realtimeSinceStartup + pinchDebugLogIntervalSeconds;
            Debug.Log($"[PinchStrokeCapture] pinchValue={pinchValue:F3}, threshold={pinchValueThreshold:F2}, indexTip={(indexTip != null ? indexTip.name : "null")}");
        }

        if (isPinching && !_wasPinching)
        {
            Debug.Log($"[PinchStrokeCapture] Pinch threshold crossed ({pinchValue:F3} >= {pinchValueThreshold:F3})");
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

    float ReadPinchValue()
    {
        float value = 0f;

        if (pinchAction != null && pinchAction.action != null)
            value = Mathf.Max(value, pinchAction.action.ReadValue<float>());

        if (!useOpenXRFallbackPinch)
            return value;

        value = Mathf.Max(value, ReadAxis("<MetaAimHand>{RightHand}/pinchStrengthIndex"));
        value = Mathf.Max(value, ReadAxis("<MetaAimHand>{LeftHand}/pinchStrengthIndex"));
        value = Mathf.Max(value, ReadAxis("<HandInteraction>{RightHand}/pinchValue"));
        value = Mathf.Max(value, ReadAxis("<HandInteraction>{LeftHand}/pinchValue"));
        value = Mathf.Max(value, ReadAxis("<XRHandDevice>{RightHand}/pinchValue"));
        value = Mathf.Max(value, ReadAxis("<XRHandDevice>{LeftHand}/pinchValue"));
        value = Mathf.Max(value, ReadButton("<HandInteraction>{RightHand}/pinchTouched"));
        value = Mathf.Max(value, ReadButton("<HandInteraction>{LeftHand}/pinchTouched"));
        value = Mathf.Max(value, ReadButton("<KHRSimpleController>{RightHand}/select"));
        value = Mathf.Max(value, ReadButton("<KHRSimpleController>{LeftHand}/select"));

        return value;
    }

    static float ReadAxis(string controlPath)
    {
        AxisControl control = InputSystem.FindControl(controlPath) as AxisControl;
        return control != null ? control.ReadValue() : 0f;
    }

    static float ReadButton(string controlPath)
    {
        ButtonControl control = InputSystem.FindControl(controlPath) as ButtonControl;
        return control != null && control.isPressed ? 1f : 0f;
    }

    static bool IsSystemGestureActive()
    {
        return HasSystemGestureFlag("<MetaAimHand>{RightHand}/aimFlags") ||
               HasSystemGestureFlag("<MetaAimHand>{LeftHand}/aimFlags");
    }

    static bool HasSystemGestureFlag(string controlPath)
    {
        IntegerControl control = InputSystem.FindControl(controlPath) as IntegerControl;
        if (control == null)
            return false;

        MetaAimFlags flags = (MetaAimFlags)(ulong)control.ReadValue();
        return (flags & (MetaAimFlags.SystemGesture | MetaAimFlags.MenuPressed)) != 0;
    }

    void LogInputDetails()
    {
        if (_loggedInputDetails || pinchAction == null || pinchAction.action == null)
            return;

        InputAction action = pinchAction.action;
        Debug.Log(
            $"[PinchStrokeCapture] Input ready: {action.actionMap?.name}/{action.name}, " +
            $"type={action.type}, expected={action.expectedControlType}, bindings={action.bindings.Count}"
        );
        _loggedInputDetails = true;
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
