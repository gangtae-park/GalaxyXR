using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/*
Single component that drives the unified gesture pipeline.

- SINGLE-HAND gestures (Search / Ask / Translate / Anchor):
1) Watch the right-hand pinch action. The rising edge of "pinch pressed" starts a capture window.
2) For 'captureSeconds' after that edge, sample HandFeatureSource at minFrameInterval into a buffer.
3) At the end of the window or every 'recognitionIntervalSeconds', hand the buffer to JackknifeUnifiedRecognizer.
    match   -> SendGestureEvent(name, END) + RECOGNIZED
    reject  -> SendGestureEvent(Pending, END) + FAIL

- TWO-HAND gestures (Compare):


- HandPose gestures (Save / Capture):

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
    public float minFrameInterval = 0.033f; //30Hz

    [Header("Recognition")]
    public float recognitionIntervalSeconds = 0.2f;
    public int minFramesForRecognition = 20;

    [Header("Routing")]
    public string pendingReferentName = "Pending";

    [Header("Compare (two-hand)")]
    public InputActionReference leftPinchAction;
    public InputActionReference rightPinchPositionAction;
    public InputActionReference leftPinchPositionAction;
    public string compareGestureName = "Compare";
    public float compareReadyTimeoutSeconds = 2f;
    public float handsTogetherDistance = 0.05f;
    public bool requireBothPinchHeldToComplete = true;

    [Header("Pose recognition (Save / Capture)")]
    public HandPoseRecognizer poseRecognizer;
    public float poseEvalIntervalSeconds = 0.2f;
    public string saveGestureName = "Save";
    public float saveEntryHoldTimeoutSeconds = 2f;
    public string captureGestureName = "Capture";
    public float cameraHoldSeconds = 1.5f;
    public bool verboseCameraHoldLogging = false;
    public float cameraHoldLogIntervalSeconds = 0.1f;

    [Header("Status (read-only)")]
    [SerializeField] private bool capturing;
    [SerializeField] private float captureElapsed;
    [SerializeField] private int bufferFrameCount;
    [SerializeField] private string lastRecognized = "";
    [SerializeField] private bool compareReady;
    [SerializeField] private float compareReadyElapsed;
    [SerializeField] private float handsDistance = -1f;
    [SerializeField] private bool savePending;
    [SerializeField] private bool saveRearmRequired;
    [SerializeField] private bool cameraPending;
    [SerializeField] private bool cameraRearmRequired;
    [SerializeField] private float cameraHeldElapsed;
    [SerializeField] private string lastPoseResult = "";

    public bool IsCapturing => capturing;
    public bool IsCompareReady => compareReady;
    public int BufferFrameCount => bufferFrameCount;

    public event Action OnCaptureStarted;
    public event Action<string> OnCaptureRecognized;
    public event Action OnCaptureRejected;
    public event Action OnCompareReady;

    private bool _wasPressed;
    private bool _leftWasPressed;
    private float _captureStartTime;
    private float _lastSampleTime = -1f;
    private float _nextRecognitionTime;
    private float _compareReadyStartTime;
    private float _nextPoseEvalTime;
    private float _saveEntryDeadline;
    private float _cameraEnterTime;
    private float _nextCameraHoldLogTime;
    private readonly List<float[]> _frames = new List<float[]>(128);

    void OnEnable()
    {
        pinchAction?.action.Enable();
        leftPinchAction?.action.Enable();
        rightPinchPositionAction?.action.Enable();
        leftPinchPositionAction?.action.Enable();
    }

    void OnDisable()
    {
        pinchAction?.action.Disable();
        leftPinchAction?.action.Disable();
        rightPinchPositionAction?.action.Disable();
        leftPinchPositionAction?.action.Disable();
    }

    void Update()
    {
        EvaluatePoseRecognition();

        bool rightPressed = ReadPressed(pinchAction);
        bool leftPressed = ReadPressed(leftPinchAction);

        if (compareReady)
        {
            UpdateCompareReady(rightPressed, leftPressed);
        }
        else
        {
            if (rightPressed && !_wasPressed && !capturing)
            {
                if (savePending) InterruptSave();
                if (cameraPending) InterruptCapture();
                StartCapture();
            }

            if (capturing)
            {
                if (leftPressed && !_leftWasPressed)
                    EnterCompareReady();
                else
                    ContinueCapture();
            }
        }

        _wasPressed = rightPressed;
        _leftWasPressed = leftPressed;
    }

    void StartCapture()
    {
        Debug.Log("[Study Log][GestureRouter] right-pinch STARTED");

        if (featureSource == null)
        {
            Debug.LogError("[Study Log][GestureRouter] featureSource not assigned; abort.");
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
        catch (Exception e) { Debug.LogError($"[Study Log][GestureRouter] Recognize threw: {e}"); }

        if (string.IsNullOrEmpty(name)) return false;

        FinishWithMatch(name);
        return true;
    }

    void FinishWithMatch(string name)
    {
        capturing = false;
        lastRecognized = name;
        Debug.Log($"[Study Log][GestureRouter] RECOGNIZED: '{name}' at {captureElapsed:F2}s ({_frames.Count} frames) -- early end");
        SendEvent(name, "END");
        SendEvent(name, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(name); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishWithReject(string reason)
    {
        capturing = false;
        lastRecognized = "rejected";
        Debug.Log($"[Study Log][GestureRouter] REJECT: ({reason}) at {captureElapsed:F2}s ({_frames.Count} frames)");
        SendEvent(pendingReferentName, "END");
        SendEvent(pendingReferentName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // ====== Compare ======
    void EnterCompareReady()
    {
        capturing = false;
        compareReady = true;
        _compareReadyStartTime = Time.time;
        compareReadyElapsed = 0f;
        handsDistance = -1f;
        lastRecognized = "";
        Debug.Log($"[Study Log][GestureRouter] READY: '{compareGestureName}");
        SendEvent(compareGestureName, "END");
        try { OnCompareReady?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void UpdateCompareReady(bool rightPressed, bool leftPressed)
    {
        compareReadyElapsed = Time.time - _compareReadyStartTime;

        if (requireBothPinchHeldToComplete && (!rightPressed || !leftPressed))
        {
            FinishCompareCancel("a hand released before the hands met");
            return;
        }

        if (compareReadyElapsed >= compareReadyTimeoutSeconds)
        {
            FinishCompareCancel("timeout");
            return;
        }

        if (TryReadPinchPosition(rightPinchPositionAction, out Vector3 rp) &&
            TryReadPinchPosition(leftPinchPositionAction, out Vector3 lp))
        {
            handsDistance = Vector3.Distance(rp, lp);
            if (handsDistance <= handsTogetherDistance)
                FinishCompareMatch();
        }
    }

    void FinishCompareMatch()
    {
        compareReady = false;
        lastRecognized = compareGestureName;
        Debug.Log($"[Study Log][GestureRouter] RECOGNZIED: '{compareGestureName}'");
        SendEvent(compareGestureName, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(compareGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishCompareCancel(string reason)
    {
        compareReady = false;
        lastRecognized = "compare-cancelled";
        Debug.Log($"[Study Log][GestureRouter] REJECT: '{compareGestureName}', {reason}.");
        SendEvent(pendingReferentName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // ====== Pose Recognition (Save / Capture) ======

    void EvaluatePoseRecognition()
    {
        if (poseRecognizer == null) return;
        if (capturing || compareReady) return;

        float now = Time.time;
        if (now < _nextPoseEvalTime) return;
        _nextPoseEvalTime = now + poseEvalIntervalSeconds;

        HandPoseKind kind = HandPoseKind.None;
        try { kind = poseRecognizer.Evaluate(); }
        catch (Exception e) { Debug.LogError($"[GestureRouter] poseRecognizer.Evaluate threw: {e}"); }
        lastPoseResult = kind.ToString();

        switch (kind)
        {
            case HandPoseKind.SaveEntry:
                if (cameraPending) FinishCameraCancel("pose changed to {Save}");
                if (!savePending)
                {
                    if (!saveRearmRequired) EnterSavePending();
                }
                else
                {
                    if (poseRecognizer.CurrentScribbleSampleCount > 0)
                        _saveEntryDeadline = Time.time + saveEntryHoldTimeoutSeconds;
                    else if (Time.time >= _saveEntryDeadline)
                        FinishSaveCancel($"no scribble within {saveEntryHoldTimeoutSeconds:F1}s");
                }
                break;
            case HandPoseKind.Save:
                if (cameraPending) FinishCameraCancel($"pose changed to '{saveGestureName}'");
                if (!savePending)
                {
                    if (saveRearmRequired) break;
                    EnterSavePending();
                }
                FinishSaveMatch();
                break;
            case HandPoseKind.CapturePose:
                if (savePending) FinishSaveCancel($"pose changed to '{captureGestureName}'");
                if (!cameraPending)
                {
                    if (!cameraRearmRequired) EnterCameraPending();
                }
                else
                {
                    cameraHeldElapsed = Time.time - _cameraEnterTime;
                    if (verboseCameraHoldLogging && Time.time >= _nextCameraHoldLogTime)
                    {
                        _nextCameraHoldLogTime = Time.time + cameraHoldLogIntervalSeconds;
                        Debug.Log($"[Study Log][GestureRouter] {captureGestureName} held {cameraHeldElapsed:F2}s / {cameraHoldSeconds:F2}s");
                    }
                    if (cameraHeldElapsed >= cameraHoldSeconds)
                        FinishCameraMatch();
                }
                break;
            case HandPoseKind.None:
            default:
                if (savePending) FinishSaveCancel("palm pose lost");
                if (cameraPending) FinishCameraCancel("camera pose broken");
                saveRearmRequired = false;
                cameraRearmRequired = false;
                break;
        }
    }

    // Save Manager
    void EnterSavePending()
    {
        savePending = true;
        lastRecognized = "";
        _saveEntryDeadline = Time.time + saveEntryHoldTimeoutSeconds;
        Debug.Log($"[Study Log][GestureRouter] {saveGestureName} pending: left palm up");
        SendEvent(saveGestureName, "START");
        try { OnCaptureStarted?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishSaveMatch()
    {
        savePending = false;
        saveRearmRequired = true;
        lastRecognized = saveGestureName;
        Debug.Log($"[Study Log][GestureRouter] RECOGNIZED: '{saveGestureName}'");
        SendEvent(saveGestureName, "END");
        SendEvent(saveGestureName, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(saveGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishSaveCancel(string reason)
    {
        savePending = false;
        saveRearmRequired = true;
        Debug.Log($"[Study Log][GestureRouter] REJECT: '{saveGestureName}', {reason}");
        SendEvent(saveGestureName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void InterruptSave()
    {
        savePending = false;
        Debug.Log($"[Study Log][GestureRouter] {saveGestureName} interrupted by right pinch (handing over to Jackknife)");
        SendEvent(saveGestureName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // Capture Manager
    void EnterCameraPending()
    {
        cameraPending = true;
        _cameraEnterTime = Time.time;
        cameraHeldElapsed = 0f;
        _nextCameraHoldLogTime = Time.time + cameraHoldLogIntervalSeconds;
        lastRecognized = "";
        Debug.Log($"[Study Log][GestureRouter] {captureGestureName} pose detected");
        SendEvent(captureGestureName, "START");
        try { OnCaptureStarted?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishCameraMatch()
    {
        cameraPending = false;
        cameraRearmRequired = true;
        lastRecognized = captureGestureName;
        Debug.Log($"[Study Log][GestureRouter] RECOGNIZED: '{captureGestureName}'");
        SendEvent(captureGestureName, "END");
        SendEvent(captureGestureName, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(captureGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishCameraCancel(string reason)
    {
        cameraPending = false;
        cameraRearmRequired = true;
        Debug.Log($"[Study Log][GestureRouter] REJECT: '{captureGestureName}', {reason}");
        SendEvent(captureGestureName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void InterruptCapture()
    {
        cameraPending = false;
        Debug.Log($"[Study Log][GestureRouter] {captureGestureName} interrupted by right pinch (handing over to Jackknife)");
        SendEvent(captureGestureName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    bool ReadPressed(InputActionReference actionRef)
    {
        if (actionRef == null || actionRef.action == null) return false;
        var act = actionRef.action;
        try
        {
            if (act.activeControl != null && act.activeControl.valueType == typeof(float))
                return act.ReadValue<float>() >= pinchValueThreshold;
        }
        catch { }
        try { return act.IsPressed(); } catch { return false; }
    }

    bool TryReadPinchPosition(InputActionReference actionRef, out Vector3 pos)
    {
        pos = Vector3.zero;
        if (actionRef == null || actionRef.action == null) return false;
        try { pos = actionRef.action.ReadValue<Vector3>(); return true; }
        catch { return false; }
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
