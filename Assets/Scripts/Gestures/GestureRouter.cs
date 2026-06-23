using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/*
Single component that drives the unified gesture pipeline:
1) Watch the right-hand pinch action. The rising edge of "pinch pressed" starts a capture window.
2) For exactly 'captureSeconds' after that edge, sample HandFeatureSource at minFrameInterval into a buffer.
3) At the end of the window or every 'recognitionIntervalSeconds', hand the buffer to JackknifeUnifiedRecognizer.
    match   -> SendGestureEvent(name, END) + RECOGNIZED
    reject  -> SendGestureEvent(Pending, END) + FAIL
*/

public class GestureRouter : MonoBehaviour
{
    [Header("Pinch trigger (right-hand)")]
    public InputActionReference pinchAction;
    [Range(0f, 1f)] public float pinchValueThreshold = 0.9f;

    [Header("References")]
    public HandFeatureSource featureSource;
    public JackknifeUnifiedRecognizer recognizer;
    public MsgSender msgSender;

    [Header("Capture")]
    public float captureSeconds = 2.5f;
    public float minFrameInterval = 0.03f;

    [Header("Recognition")]
    public float recognitionIntervalSeconds = 0.2f;
    public int minFramesForRecognition = 20;

    [Header("Routing")]
    public string pendingReferentName = "Pending";

    [Header("Status (read-only)")]
    [SerializeField] private bool capturing;
    [SerializeField] private float captureElapsed;
    [SerializeField] private int bufferFrameCount;
    [SerializeField] private string lastRecognized = "";

    public bool IsCapturing => capturing;
    public int BufferFrameCount => bufferFrameCount;

    public event Action OnCaptureStarted;
    public event Action<string> OnCaptureRecognized;
    public event Action OnCaptureRejected;

    private bool _wasPressed;
    private float _captureStartTime;
    private float _lastSampleTime = -1f;
    private float _nextRecognitionTime;
    private readonly List<float[]> _frames = new List<float[]>(128);

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
        bool isPressed = ReadPressed();

        // Rising edge starts a capture if we're idle.
        if (isPressed && !_wasPressed && !capturing)
        {
            StartCapture();
        }
        _wasPressed = isPressed;

        if (capturing) ContinueCapture();
    }

    void StartCapture()
    {
        Debug.Log("[PinchPoseCapture] capture STARTED");

        if (featureSource == null)
        {
            Debug.LogError("[PinchPoseCapture] featureSource not assigned; abort.");
            return;
        }

        _frames.Clear();
        _captureStartTime = Time.time;
        _lastSampleTime = -1f;
        _nextRecognitionTime = _captureStartTime + recognitionIntervalSeconds;
        capturing = true;
        captureElapsed = 0f;
        bufferFrameCount = 0;
        lastRecognized = "";

        SendEvent(pendingReferentName, "START");
        try { OnCaptureStarted?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void ContinueCapture()
    {
        float now = Time.time;
        captureElapsed = now - _captureStartTime;

        // 1) Append a feature frame if interval elapsed.
        if (_lastSampleTime < 0f || (now - _lastSampleTime) >= minFrameInterval)
        {
            float[] f = featureSource.BuildFeatureFrame();
            if (f != null) _frames.Add(f);
            _lastSampleTime = now;
            bufferFrameCount = _frames.Count;
        }

        // 2) Periodically classify the partial buffer. First match wins -- ends capture early.
        if (_frames.Count >= minFramesForRecognition && now >= _nextRecognitionTime)
        {
            _nextRecognitionTime = now + recognitionIntervalSeconds;
            if (JackknifeClassify()) return;
        }

        // 3) Timeout -> FAIL.
        if (captureElapsed >= captureSeconds)
        {   
            FinishWithReject("timeout");
        }
    }

    bool JackknifeClassify()
    {
        if (recognizer == null) return false;

        string name = null;
        try { name = recognizer.Recognize(_frames); }
        catch (Exception e) { Debug.LogError($"[PinchPoseCapture] Recognize threw: {e}"); }

        if (string.IsNullOrEmpty(name)) return false;

        FinishWithMatch(name);
        return true;
    }

    void FinishWithMatch(string name)
    {
        capturing = false;
        lastRecognized = name;
        Debug.Log($"[PinchPoseCapture] RECOGNIZED '{name}' at {captureElapsed:F2}s ({_frames.Count} frames) -- early end");
        SendEvent(name, "END");
        SendEvent(name, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(name); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishWithReject(string reason)
    {
        capturing = false;
        lastRecognized = "rejected";
        Debug.Log($"[PinchPoseCapture] rejected ({reason}) at {captureElapsed:F2}s ({_frames.Count} frames)");
        SendEvent(pendingReferentName, "END");
        SendEvent(pendingReferentName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    bool ReadPressed()
    {
        if (pinchAction == null || pinchAction.action == null) return false;
        var act = pinchAction.action;
        try
        {
            if (act.activeControl != null && act.activeControl.valueType == typeof(float))
                return act.ReadValue<float>() >= pinchValueThreshold;
        }
        catch { }
        try { return act.IsPressed(); } catch { return false; }
    }

    void SendEvent(string gestureName, string eventType)
    {
        if (msgSender == null) return;
        var payload = new GestureEventPayload
        {
            gestureName = gestureName,
            eventType = eventType,
        };
        msgSender.SendGestureEvent(payload);
    }
}
