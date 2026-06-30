using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

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
    [Tooltip("Optional. When assigned, the router will skip gesture recognition while CurrentMode is not GestureOnly. Auto-resolved at runtime if left empty.")]
    public InputModeManager inputModeManager;

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
    public float poseEvalIntervalSeconds = 0.1f;
    public string saveGestureName = "Save";
    public float saveEntryHoldTimeoutSeconds = 2f;
    public string captureGestureName = "Capture";
    public float cameraHoldSeconds = 1.2f;
    public string translateGestureName = "Translate";
    [Tooltip("After Jackknife recognises Translate, the router waits this many seconds for a confirming leftward swipe of the right palm. No swipe within the window -> Translate FAIL.")]
    public float translateReadyTimeoutSeconds = 10f;
    [Tooltip("Camera used as the reference for the 'leftward' direction. Null -> Camera.main.")]
    public Camera referenceCamera;
    [Tooltip("Sliding window for tracking the right palm's leftward motion (seconds).")]
    public float swipeWindowSeconds = 0.7f;
    [Tooltip("Minimum leftward (relative to camera) displacement of the right palm within the window to count as a swipe (metres).")]
    public float swipeMinLeftwardDistance = 0.15f;
    [Tooltip("Minimum leftward velocity of the latest sample so the swipe must still be in progress (metres/second).")]
    public float swipeMinLeftwardVelocity = 0.3f;
    [Tooltip("Periodic log of the translateReady countdown + swipe diagnostics so build-and-run users can see wait progress + tune thresholds.")]
    public bool verboseTranslateReadyLogging = true;
    public float translateReadyLogIntervalSeconds = 1f;
    public bool verboseCameraHoldLogging = false;
    public float cameraHoldLogIntervalSeconds = 0.5f;

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
    [SerializeField] private bool translateReady;
    [SerializeField] private float translateReadyElapsed;
    [SerializeField] private float swipeLeftwardDistance;
    [SerializeField] private float swipeLeftwardVelocity;
    [SerializeField] private string swipeReason = "";
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
    private float _translateReadyStartTime;
    private float _nextTranslateReadyLogTime;
    private XRHandSubsystem _handSubsystem;
    private struct SwipeSample { public float t; public Vector3 pos; }
    private readonly List<SwipeSample> _swipeBuf = new List<SwipeSample>(64);
    private readonly List<float[]> _frames = new List<float[]>(128);

    void OnEnable()
    {
        if (inputModeManager == null) inputModeManager = FindObjectOfType<InputModeManager>();
        pinchAction?.action.Enable();
        leftPinchAction?.action.Enable();
        rightPinchPositionAction?.action.Enable();
        leftPinchPositionAction?.action.Enable();
    }

    bool IsGestureModeActive()
    {
        if (inputModeManager == null) return true;
        return inputModeManager.CurrentMode == InputMode.GestureOnly;
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
        if (!IsGestureModeActive()) return;

        EvaluatePoseRecognition();
        UpdateRightHandSwipe();

        bool rightPressed = ReadPressed(pinchAction);
        bool leftPressed = ReadPressed(leftPinchAction);

        if (compareReady)
        {
            UpdateCompareReady(rightPressed, leftPressed);
        }
        else
        {
            if (rightPressed && !_wasPressed && !capturing && !translateReady)
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

        // Translate has a two-stage confirm: Jackknife match enters a "ready"
        // window; an actual palm-forward swipe is required to fire END.
        if (!string.IsNullOrEmpty(translateGestureName) && name == translateGestureName)
        {
            EnterTranslateReady();
            return;
        }

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

        // TranslateReady tick: while waiting for the confirming swipe, track
        // elapsed and time out. The swipe itself is handled in the switch below.
        // Swipe detection runs in UpdateRightHandSwipe() at frame rate. Here we
        // only handle the timeout while in translateReady; ignore HandPoseKind
        // results (Save / Capture / None) so they don't interfere.
        if (translateReady)
        {
            translateReadyElapsed = Time.time - _translateReadyStartTime;
            if (translateReadyElapsed >= translateReadyTimeoutSeconds)
                FinishTranslateCancel("timeout, no swipe");
            return;
        }

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

    // ====== Translate (two-stage) ======
    // Stage 1 (here): Jackknife recognised "Translate" -> send READY, wait for swipe.
    // Stage 2: palm-forward swipe -> send END + RECOGNIZED (handled in EvaluatePoseRecognition).
    void EnterTranslateReady()
    {
        translateReady = true;
        _translateReadyStartTime = Time.time;
        translateReadyElapsed = 0f;
        _nextTranslateReadyLogTime = Time.time + Mathf.Max(0.05f, translateReadyLogIntervalSeconds);
        _swipeBuf.Clear();
        swipeLeftwardDistance = 0f;
        swipeLeftwardVelocity = 0f;
        swipeReason = "";
        Debug.Log($"[Study Log][GestureRouter] {translateGestureName} READY (waiting for right-palm leftward swipe, timeout {translateReadyTimeoutSeconds:F1}s)");
        SendEvent(translateGestureName, "READY");
    }

    // Track the right palm's position in a sliding window and fire FinishTranslateMatch
    // when leftward (relative to the camera) displacement and velocity both exceed
    // their thresholds. No pose / palm-orientation checks -- intentionally simple.
    void UpdateRightHandSwipe()
    {
        if (!translateReady) { if (_swipeBuf.Count > 0) _swipeBuf.Clear(); return; }
        if (_handSubsystem == null && !TryGetHandSubsystem(out _handSubsystem))
        { swipeReason = "no hand subsystem"; return; }

        XRHand right = _handSubsystem.rightHand;
        if (!right.isTracked) { swipeReason = "right hand not tracked"; _swipeBuf.Clear(); return; }
        if (!right.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose))
        { swipeReason = "no right palm joint"; _swipeBuf.Clear(); return; }

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) { swipeReason = "no camera"; return; }

        float now = Time.time;
        _swipeBuf.Add(new SwipeSample { t = now, pos = palmPose.position });
        // Drop stale samples outside the sliding window.
        float cutoff = now - swipeWindowSeconds;
        int firstKeep = 0;
        while (firstKeep < _swipeBuf.Count && _swipeBuf[firstKeep].t < cutoff) firstKeep++;
        if (firstKeep > 0) _swipeBuf.RemoveRange(0, firstKeep);
        if (_swipeBuf.Count < 2) { swipeReason = $"buf={_swipeBuf.Count}"; return; }

        // Leftward = -camera.right.
        Vector3 camLeft = -cam.transform.right;
        Vector3 disp = _swipeBuf[_swipeBuf.Count - 1].pos - _swipeBuf[0].pos;
        swipeLeftwardDistance = Vector3.Dot(disp, camLeft);

        var prev = _swipeBuf[_swipeBuf.Count - 2];
        var cur  = _swipeBuf[_swipeBuf.Count - 1];
        float dt = Mathf.Max(1e-4f, cur.t - prev.t);
        swipeLeftwardVelocity = Vector3.Dot(cur.pos - prev.pos, camLeft) / dt;

        if (swipeLeftwardDistance < swipeMinLeftwardDistance)
        { swipeReason = $"dist {swipeLeftwardDistance:F2}m < {swipeMinLeftwardDistance:F2}m"; goto MaybeLog; }
        if (swipeLeftwardVelocity < swipeMinLeftwardVelocity)
        { swipeReason = $"vel {swipeLeftwardVelocity:F2} < {swipeMinLeftwardVelocity:F2}"; goto MaybeLog; }

        swipeReason = $"SWIPE dist={swipeLeftwardDistance:F2}m vel={swipeLeftwardVelocity:F2}";
        Debug.Log($"[Study Log][GestureRouter] swipe DETECTED ({swipeReason})");
        _swipeBuf.Clear();
        FinishTranslateMatch();
        return;

    MaybeLog:
        if (verboseTranslateReadyLogging && Time.time >= _nextTranslateReadyLogTime)
        {
            _nextTranslateReadyLogTime = Time.time + Mathf.Max(0.05f, translateReadyLogIntervalSeconds);
            float remaining = Mathf.Max(0f, translateReadyTimeoutSeconds - (Time.time - _translateReadyStartTime));
            Debug.Log($"[Study Log][GestureRouter] {translateGestureName} READY {translateReadyElapsed:F1}s/{translateReadyTimeoutSeconds:F1}s " +
                      $"(rem {remaining:F1}s) | swipe dist={swipeLeftwardDistance:F2}m (need>={swipeMinLeftwardDistance:F2}m) " +
                      $"vel={swipeLeftwardVelocity:F2} (need>={swipeMinLeftwardVelocity:F2}) buf={_swipeBuf.Count} reason=[{swipeReason}]");
        }
    }

    // Walk all hand subsystems, prefer one currently running. Same pattern as
    // HandPoseRecognizer / CaptureControlCard.
    private static List<XRHandSubsystem> s_subs;
    static bool TryGetHandSubsystem(out XRHandSubsystem sub)
    {
        s_subs ??= new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(s_subs);
        for (int i = 0; i < s_subs.Count; i++)
            if (s_subs[i].running) { sub = s_subs[i]; return true; }
        sub = s_subs.Count > 0 ? s_subs[0] : null;
        return sub != null;
    }

    void FinishTranslateMatch()
    {
        translateReady = false;
        lastRecognized = translateGestureName;
        Debug.Log($"[Study Log][GestureRouter] {translateGestureName} RECOGNIZED via swipe (waited {translateReadyElapsed:F2}s)");
        SendEvent(translateGestureName, "END");
        SendEvent(translateGestureName, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(translateGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishTranslateCancel(string reason)
    {
        translateReady = false;
        _swipeBuf.Clear();
        lastRecognized = "translate-cancelled";
        Debug.Log($"[Study Log][GestureRouter] {translateGestureName} cancelled ({reason})");
        SendEvent(translateGestureName, "FAIL");
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
