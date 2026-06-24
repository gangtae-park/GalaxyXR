using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/*
Single component that drives the unified gesture pipeline.

SINGLE-HAND gestures (Search / Ask / Translate / Anchor):
1) Watch the right-hand pinch action. The rising edge of "pinch pressed" starts a capture window.
2) For 'captureSeconds' after that edge, sample HandFeatureSource at minFrameInterval into a buffer.
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

    [Header("Compare (two-hand)")]
    public InputActionReference leftPinchAction;
    public InputActionReference rightPinchPositionAction;
    public InputActionReference leftPinchPositionAction;
    public string compareGestureName = "Compare";
    public float compareReadyTimeoutSeconds = 3f;
    public float handsTogetherDistance = 0.03f;
    public bool requireBothPinchHeldToComplete = true;

    [Header("Pose recognition (Save / Camera)")]
    [Tooltip("Geometric pose recognizer for non-pinch referents. Optional.")]
    public HandPoseRecognizer poseRecognizer;
    [Tooltip("Seconds between hand-pose evaluations. ~0.1 = 10 Hz; the geometry check is cheap.")]
    public float poseEvalIntervalSeconds = 0.1f;
    public string saveGestureName = "Save";
    [Tooltip("After the left palm-up pose is detected, cancel back to idle if no scribble input arrives within this many seconds. The timer refreshes whenever scribble samples are actively being accumulated, so a slow scribble is not interrupted.")]
    public float saveEntryHoldTimeoutSeconds = 2f;

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
            // Rising edge of the right pinch starts a capture if we're idle.
            // If Save is pending (left palm held up, waiting for scribble), the user
            // is choosing to do a Search/Ask/Translate/Anchor instead -- pre-empt Save
            // so Python's gesture state is cleanly handed over, then open the Jackknife
            // window. Save can re-arm automatically once the pinch flow finishes.
            if (rightPressed && !_wasPressed && !capturing)
            {
                if (savePending) PreemptSaveForPinch();
                StartCapture();
            }

            if (capturing)
            {
                // A left pinch during the capture window arms Compare
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

    // ====== Compare ======
    void EnterCompareReady()
    {
        capturing = false;
        compareReady = true;
        _compareReadyStartTime = Time.time;
        compareReadyElapsed = 0f;
        handsDistance = -1f;
        lastRecognized = "";
        Debug.Log("[Study Log][GestureRouter] COMPARE READY");

        SendEvent(compareGestureName, "READY");
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
        Debug.Log($"[Study Log][GestureRouter] COMPARE RECOGNIZED (hands met at {handsDistance:F3}m).");
        SendEvent(compareGestureName, "END");
        SendEvent(compareGestureName, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(compareGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishCompareCancel(string reason)
    {
        compareReady = false;
        lastRecognized = "compare-cancelled";
        Debug.Log($"[Study Log][GestureRouter] COMPARE cancelled ({reason}).");
        SendEvent(pendingReferentName, "END");
        SendEvent(pendingReferentName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // ====== Pose recognition (Save / Camera) ======
    // Drives the HandPoseRecognizer at a fixed cadence and translates its result
    // into GESTURE_EVENT packets:
    //   idle    + SaveEntry -> Save START   (Python begins gaze logging)
    //   pending + Save      -> Save END + RECOGNIZED
    //   pending + None      -> Save FAIL    (palm pose broken)
    // We skip evaluation entirely while pinch-driven flows are in flight so
    // the two pipelines never compete for the same hand state.
    void EvaluatePoseRecognition()
    {
        if (poseRecognizer == null) return;
        if (capturing || compareReady) return;

        float now = Time.time;
        if (now < _nextPoseEvalTime) return;
        _nextPoseEvalTime = now + Mathf.Max(0.02f, poseEvalIntervalSeconds);

        HandPoseKind kind = HandPoseKind.None;
        try { kind = poseRecognizer.Evaluate(); }
        catch (Exception e) { Debug.LogError($"[GestureRouter] poseRecognizer.Evaluate threw: {e}"); }
        lastPoseResult = kind.ToString();

        switch (kind)
        {
            case HandPoseKind.SaveEntry:
                if (!savePending)
                {
                    // Edge-triggered: after a previous Save (success or cancel) the user
                    // must let the palm pose lapse (None / CameraPose) before another
                    // Save can arm. Otherwise holding the palm up indefinitely would loop
                    // START -> timeout FAIL -> START forever.
                    if (!saveRearmRequired) EnterSavePending();
                }
                else
                {
                    // Refresh the deadline as long as scribble samples are actively
                    // being accumulated (so a slow scribble isn't cancelled mid-motion).
                    // Otherwise: palm is just being held open -- cancel after the timeout.
                    if (poseRecognizer.CurrentScribbleSampleCount > 0)
                        _saveEntryDeadline = Time.time + saveEntryHoldTimeoutSeconds;
                    else if (Time.time >= _saveEntryDeadline)
                        FinishSaveCancel($"no scribble within {saveEntryHoldTimeoutSeconds:F1}s");
                }
                break;
            case HandPoseKind.Save:
                if (!savePending)
                {
                    if (saveRearmRequired) break;
                    EnterSavePending();
                }
                FinishSaveMatch();
                break;
            case HandPoseKind.CameraPose:
                // Reserved -- the Capture gesture will be wired in here later.
                if (savePending) FinishSaveCancel("pose changed to Camera");
                saveRearmRequired = false;
                break;
            case HandPoseKind.None:
            default:
                if (savePending) FinishSaveCancel("palm pose lost");
                saveRearmRequired = false;
                break;
        }
    }

    void EnterSavePending()
    {
        savePending = true;
        lastRecognized = "";
        _saveEntryDeadline = Time.time + saveEntryHoldTimeoutSeconds;
        Debug.Log($"[GestureRouter] {saveGestureName} pending: left palm up. Scribble with the right index within {saveEntryHoldTimeoutSeconds:F1}s to confirm.");
        SendEvent(saveGestureName, "START");
        try { OnCaptureStarted?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishSaveMatch()
    {
        savePending = false;
        saveRearmRequired = true;
        lastRecognized = saveGestureName;
        Debug.Log($"[GestureRouter] {saveGestureName} RECOGNIZED (scribble pattern detected).");
        SendEvent(saveGestureName, "END");
        SendEvent(saveGestureName, "RECOGNIZED");
        try { OnCaptureRecognized?.Invoke(saveGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishSaveCancel(string reason)
    {
        savePending = false;
        saveRearmRequired = true;
        Debug.Log($"[GestureRouter] {saveGestureName} cancelled ({reason}). Re-arm requires the palm pose to lapse.");
        SendEvent(saveGestureName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // Pre-empt a pending Save because the user just pinched (Search/Ask/etc.).
    // Unlike FinishSaveCancel we deliberately leave saveRearmRequired = false so the
    // Save flow re-arms automatically once the pinch capture finishes, without the
    // user having to lower and re-raise the palm.
    void PreemptSaveForPinch()
    {
        savePending = false;
        Debug.Log($"[GestureRouter] {saveGestureName} pre-empted by right pinch (handing over to Jackknife). Will re-arm after capture.");
        SendEvent(saveGestureName, "FAIL");
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
