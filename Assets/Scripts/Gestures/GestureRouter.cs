using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/*
Single component that drives the unified gesture pipeline.

- SINGLE-HAND Jackknife gestures (Search / Ask / Anchor):
1) Right-hand pinch rising edge => start capture; sample HandFeatureSource at minFrameInterval into a buffer.
2) Falling edge => gesture end. Run JackknifeUnifiedRecognizer ONCE on the pinch-start~release buffer.
    match                       -> SendGestureEvent(name, END) + RECOGNIZED
    no match / buffer too short -> SendGestureEvent(Pending, END) + FAIL
   No time-window, no periodic classification: the user's release delimits the gesture, so trailing idle motion never enters the buffer.

- TWO-HAND gestures (Compare): left pinch rising edge while right is held => READY; the two INDEX-TIP Transforms (rightIndexTip / leftIndexTip assigned in Inspector) coming within handsTogetherDistance => RECOGNIZED.

- HandPose gestures (Save / Capture / Translate) driven by HandPoseRecognizer:
    Save     : left palm open + right index scribble on palm.
    Capture  : both hands framing L-shape, held for cameraHoldSeconds.
    Translate: right thumb+index "C" pose (TranslateStart) => START (rightIndexTip world position snapshotted). Recognizer distinguishes Start vs End using the SAME base checks (extension / curl / palm-left) and inverts only the thumb-index-gap: gap >= translateThumbIndexGapMin => Start, gap < min => End. So a smooth close transitions Start -> End without a None gap. The closing pinch InputAction is EXCLUDED from Jackknife because the rising-edge branch is gated by !translatePending. Base-pose failure (extension out of range, curl lost, palm rotated off) => FAIL.
*/

public class GestureRouter : MonoBehaviour
{
    [Header("Pinch trigger (right-hand)")]
    public InputActionReference pinchAction;
    [Range(0f, 1f)] public float pinchValueThreshold = 0.9f;
    [Tooltip("Skip gesture start when the right-hand pinch is aimed at ANY interactive element: UI (card buttons, drag handles) OR 3D XR interactables (anchor pins, sticky notes). Prevents these pinches from being mis-detected as new gestures.")]
    public bool suppressGestureWhenPinchOverUI = true;
    [Tooltip("Log every suppression so you can verify the guard is firing correctly during dogfooding. Includes which subsystem (UI vs XR interactable) blocked the pinch.")]
    public bool logUiSuppression = true;

    [Header("References")]
    public HandFeatureSource featureSource;
    public JackknifeUnifiedRecognizer recognizer;
    public MsgSender msgSender;
    [Tooltip("Optional. When assigned, the router will skip gesture recognition while CurrentMode is not GestureOnly. Auto-resolved at runtime if left empty.")]
    public InputModeManager inputModeManager;

    [Header("Capture")]
    public float minFrameInterval = 0.033f; //30Hz

    [Header("Recognition")]
    [Tooltip("If the pinch is released with fewer than this many buffered frames, the gesture is rejected as too short before Jackknife is even invoked. Guards against accidental micro-pinches.")]
    public int minFramesForRecognition = 20;

    [Header("Routing")]
    public string pendingReferentName = "Pending";

    [Header("Compare (two-hand)")]
    public InputActionReference leftPinchAction;
    [Tooltip("Assign the RIGHT index-fingertip Transform (from the XR Hand rig -- e.g. the index-tip bone under the right-hand skeleton). The Compare confirm distance is Vector3.Distance(rightIndexTip.position, leftIndexTip.position). Also reused as the position source for the Translate sweep start/end snapshot.")]
    public Transform rightIndexTip;
    [Tooltip("Assign the LEFT index-fingertip Transform. Compare needs both index tips to be non-null.")]
    public Transform leftIndexTip;
    public string compareGestureName = "Compare";
    public float compareReadyTimeoutSeconds = 2f;
    public float handsTogetherDistance = 0.05f;
    public bool requireBothPinchHeldToComplete = true;
    [Tooltip("Log rp/lp/distance every Compare-ready tick. Turn on when the confirm distance seems wrong so you can see the raw index-tip positions the router is comparing.")]
    public bool verboseCompareLogging = false;

    [Header("Audio feedback")]
    [Tooltip("Optional. If left empty, an AudioSource is auto-added to this GameObject the first time a feedback clip needs to play.")]
    public AudioSource feedbackAudioSource;
    [Tooltip("Activation cue -- played whenever a gesture BEGINS: right pinch accepted (Jackknife START), SaveEntry pose detected, CapturePose detected, Translate C-shape detected. UI/XR-suppressed pinches do NOT play it.")]
    public AudioClip activationClip;
    [Tooltip("Deactivation cue -- played whenever a gesture ENDS regardless of match/reject outcome: pinch release (Jackknife END), Save match/cancel, Camera match/cancel, Translate match/cancel.")]
    public AudioClip deactivationClip;
    [Range(0f, 1f)] public float feedbackVolume = 1f;

    [Header("Pose recognition (Save / Capture)")]
    public HandPoseRecognizer poseRecognizer;
    public float poseEvalIntervalSeconds = 0.1f;
    public string saveGestureName = "Save";
    public float saveEntryHoldTimeoutSeconds = 2f;
    public string captureGestureName = "Capture";
    public float cameraHoldSeconds = 1.2f;
    public string translateGestureName = "Translate";
    // Translate now fires END + RECOGNIZED as soon as Jackknife matches, just
    // like every other gesture. The old READY / palm-swipe confirmation step
    // is gone -- usability trumps the extra confirm.
    public bool verboseCameraHoldLogging = false;
    public float cameraHoldLogIntervalSeconds = 0.5f;
    [Tooltip("After Translate RECOGNIZED, block the Jackknife pinch rising edge for this many seconds. The finger-close motion that ended Translate also fires the pinch InputAction (thumb touches index), and without this cooldown the same physical action can leak into a phantom Jackknife capture the next frame. 0.3-0.5s is usually enough.")]
    public float postGestureCooldownSeconds = 0.4f;

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
    [SerializeField] private bool translatePending;
    [SerializeField] private bool translateRearmRequired;
    [SerializeField] private float translateElapsed;
    [SerializeField] private Vector3 translateStartHandPos;
    [SerializeField] private Vector3 translateEndHandPos;
    [SerializeField] private bool pinchSuppressed;
    [SerializeField] private string pinchSuppressReason = "";
    [SerializeField] private float pinchCooldownUntil;
    [SerializeField] private string lastPoseResult = "";

    public bool IsCapturing => capturing;
    public bool IsCompareReady => compareReady;
    public int BufferFrameCount => bufferFrameCount;
    public bool IsPinchSuppressed => pinchSuppressed;

    // External components (e.g. CaptureManager while the shutter card is open,
    // or any other subsystem that needs the same pinch action for its own
    // meaning) call this to gate off the Jackknife pinch rising edge. The
    // reason string is stored on the inspector for debugging only. Only ONE
    // suppression source is supported right now -- if multiple sources ever
    // need to overlap we'd need a refcount or a token-set.
    public void SetPinchSuppressed(bool suppressed, string reason = "")
    {
        if (pinchSuppressed == suppressed && pinchSuppressReason == reason) return;
        pinchSuppressed = suppressed;
        pinchSuppressReason = suppressed ? (string.IsNullOrEmpty(reason) ? "unspecified" : reason) : "";
        Debug.Log($"[Study Log][GestureRouter] pinchSuppressed={suppressed} reason='{pinchSuppressReason}'");
    }

    public event Action OnCaptureStarted;
    public event Action<string> OnCaptureRecognized;
    public event Action OnCaptureRejected;
    public event Action OnCompareReady;

    private bool _wasPressed;
    private bool _leftWasPressed;
    private float _captureStartTime;
    private float _lastSampleTime = -1f;
    private float _compareReadyStartTime;
    private float _nextPoseEvalTime;
    private float _saveEntryDeadline;
    private float _cameraEnterTime;
    private float _nextCameraHoldLogTime;
    private float _translateEnterTime;
    private readonly List<float[]> _frames = new List<float[]>(128);

    void OnEnable()
    {
        if (inputModeManager == null) inputModeManager = FindObjectOfType<InputModeManager>();
        pinchAction?.action.Enable();
        leftPinchAction?.action.Enable();
    }

    bool IsGestureModeActive()
    {
        if (inputModeManager == null) return true;
        return inputModeManager.CurrentMode == InputMode.GestureOnly;
    }

    void OnDisable()
    {
        // Mode change can fire while the user is mid-pinch (e.g. they pinched
        // the dropdown to switch to UI Interaction). Without this cleanup,
        // Python receives a START packet but no terminator -- the gesture
        // hangs until the cached state ages out. Force a FAIL on every
        // pending branch so the backend cleanly drops the in-flight session.
        if (capturing) FinishWithReject("router_disabled");
        if (compareReady) FinishCompareCancel("router_disabled");
        if (savePending) FinishSaveCancel("router_disabled");
        if (cameraPending) FinishCameraCancel("router_disabled");
        if (translatePending) FinishTranslateCancel("router_disabled");
        // Clear pinchSuppressed on disable so a stale suppression can't outlive
        // a scene reload. External components (CaptureManager, ...) are
        // expected to re-issue SetPinchSuppressed(true) on re-enable if their
        // suppressive state is still active.
        if (pinchSuppressed) SetPinchSuppressed(false, "router_disabled");

        // Reset edge-trigger latches so a still-held pinch doesn't get
        // mis-interpreted as a fresh press the moment OnEnable runs again.
        _wasPressed = false;
        _leftWasPressed = false;
        saveRearmRequired = false;
        cameraRearmRequired = false;

        pinchAction?.action.Disable();
        leftPinchAction?.action.Disable();
    }

    void Update()
    {
        if (!IsGestureModeActive()) return;

        bool rightPressed = ReadPressed(pinchAction);
        bool leftPressed = ReadPressed(leftPinchAction);

        // Translate end is now POSE-based: HandPoseRecognizer distinguishes
        // TranslateStart (C open) from TranslateEnd (C closed, gap < min) using
        // the same base checks, so a smooth closing motion transitions cleanly
        // Start -> End. The Jackknife rising-edge branch below is gated by
        // !translatePending, which means the closing-pinch InputAction that
        // fires when the fingers meet is naturally excluded from Jackknife --
        // no state-side end check needed here.
        EvaluatePoseRecognition();

        if (compareReady)
        {
            UpdateCompareReady(rightPressed, leftPressed);
        }
        else
        {
            if (rightPressed && !_wasPressed && !capturing && !translatePending && !pinchSuppressed && Time.time >= pinchCooldownUntil)
            {
                // Gate the rising edge so a pinch aimed at a card button, an
                // anchor pin, or a sticky note doesn't kick off a phantom
                // gesture. Two pipelines to consult:
                //   * UI: EventSystem's tracked-device pointer state (cards).
                //   * XR: XRInteractionManager hover state (anchor pin +
                //     StickyNote both use XRSimpleInteractable, which routes
                //     hover through the interaction manager -- NOT
                //     EventSystem -- so the UI check alone missed them).
                if (suppressGestureWhenPinchOverUI)
                {
                    string blockedBy = null;
                    if (IsPinchOverUi()) blockedBy = "UI";
                    else if (IsPinchOverXrInteractable()) blockedBy = "XR interactable";
                    if (blockedBy != null)
                    {
                        if (logUiSuppression)
                            Debug.Log($"[GestureRouter] pinch rising edge suppressed -- aimed at {blockedBy}");
                        // Freeze the edge state so the release doesn't fire either.
                        _wasPressed = rightPressed;
                        _leftWasPressed = leftPressed;
                        return;
                    }
                }
                if (savePending) InterruptSave();
                if (cameraPending) InterruptCapture();
                StartCapture();
            }

            if (capturing)
            {
                if (leftPressed && !_leftWasPressed)
                {
                    // Left rising edge takes priority over the right release
                    // edge -- if both happen on the same tick we still enter
                    // Compare mode, and UpdateCompareReady will cancel if the
                    // right hand is already unpressed.
                    EnterCompareReady();
                }
                else if (!rightPressed && _wasPressed)
                {
                    // Falling edge = gesture end. Grab one final frame so the
                    // buffer covers motion right up to the release, then hand
                    // the full pinch-hold trajectory to Jackknife exactly once.
                    // Release sound fires here (not inside FinishCaptureOnRelease)
                    // so the user hears the pinch-end cue regardless of whether
                    // classification matches or rejects.
                    ContinueCapture();
                    PlayFeedback(deactivationClip);
                    FinishCaptureOnRelease();
                }
                else
                {
                    ContinueCapture();
                }
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
        capturing = true;
        captureElapsed = 0f;
        bufferFrameCount = 0;
        lastRecognized = "";

        SendEvent(pendingReferentName, "START");
        PlayFeedback(activationClip);
        try { OnCaptureStarted?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // Append one feature frame if the sampling interval has elapsed. The pinch
    // release edge (handled in Update) is what ends the capture -- there is no
    // fixed window and no periodic classification anymore, so trailing idle
    // motion never enters the buffer.
    void ContinueCapture()
    {
        float now = Time.time;
        captureElapsed = now - _captureStartTime;

        if (_lastSampleTime < 0f || (now - _lastSampleTime) >= minFrameInterval)
        {
            float[] f = featureSource.BuildFeatureFrame();
            if (f != null) _frames.Add(f);
            _lastSampleTime = now;
            bufferFrameCount = _frames.Count;
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

    // Called on the right-pinch falling edge. Rejects short buffers up front
    // (avoids feeding accidental micro-pinches into Jackknife) and otherwise
    // classifies the pinch-hold trajectory exactly once.
    void FinishCaptureOnRelease()
    {
        if (_frames.Count < minFramesForRecognition)
        {
            FinishWithReject($"too_short ({_frames.Count} < {minFramesForRecognition})");
            return;
        }
        if (!JackknifeClassify())
            FinishWithReject("no_match");
    }

    void FinishWithMatch(string name)
    {
        capturing = false;
        lastRecognized = name;

        Debug.Log($"[Study Log][GestureRouter] RECOGNIZED: '{name}' at {captureElapsed:F2}s ({_frames.Count} frames)");
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
        Debug.Log($"[Study Log][GestureRouter] READY: '{compareGestureName}'");
        // READY is a distinct marker for Compare: the Python side freezes the
        // gaze trail here so the "bring hands together" motion that follows
        // adds no gaze points. END is deliberately withheld until FinishCompareMatch
        // so the compare.handle handler doesn't run before the hands actually meet.
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

        if (rightIndexTip == null || leftIndexTip == null)
        {
            handsDistance = -1f;
            if (verboseCompareLogging)
                Debug.LogWarning($"[Study Log][GestureRouter] Compare index-tip Transforms unassigned (right={rightIndexTip} left={leftIndexTip}) -- assign them on the GestureRouter component in the Inspector");
            return;
        }
        Vector3 rp = rightIndexTip.position;
        Vector3 lp = leftIndexTip.position;
        handsDistance = Vector3.Distance(rp, lp);
        if (verboseCompareLogging)
            Debug.Log($"[Study Log][GestureRouter] Compare rp={rp} lp={lp} dist={handsDistance:F3} thresh={handsTogetherDistance:F3}");
        if (handsDistance <= handsTogetherDistance)
            FinishCompareMatch();
    }

    void FinishCompareMatch()
    {
        compareReady = false;
        lastRecognized = compareGestureName;
        Debug.Log($"[Study Log][GestureRouter] RECOGNIZED: '{compareGestureName}'");
        // END is what schedules Python's compare.handle -- it's fired NOW,
        // not at READY, because the hands-together moment is the actual
        // gesture completion. RECOGNIZED follows the END+RECOGNIZED convention
        // the other pose gestures use.
        SendEvent(compareGestureName, "END");
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

        // While translatePending is true, retry with the LOOSE hold check
        // whenever the strict enter check said None. This lets the palm
        // orientation and the index max-extension drift mid-sweep (which they
        // naturally do as the arm moves) without breaking the gesture. Only a
        // real base failure (thumb/index below MIN ext, or middle/ring/little
        // uncurling) will still surface as None -> cancel.
        if (translatePending && kind == HandPoseKind.None)
        {
            HandPoseKind hold = HandPoseKind.None;
            try { hold = poseRecognizer.EvaluateTranslateHold(); }
            catch (Exception e) { Debug.LogError($"[GestureRouter] EvaluateTranslateHold threw: {e}"); }
            if (hold == HandPoseKind.TranslateStart || hold == HandPoseKind.TranslateEnd)
            {
                kind = hold;
                lastPoseResult = kind + "(hold)";
            }
        }

        switch (kind)
        {
            case HandPoseKind.SaveEntry:
                if (cameraPending) FinishCameraCancel("pose changed to {Save}");
                if (translatePending) FinishTranslateCancel($"pose changed to '{saveGestureName}'");
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
                if (translatePending) FinishTranslateCancel($"pose changed to '{saveGestureName}'");
                if (!savePending)
                {
                    if (saveRearmRequired) break;
                    EnterSavePending();
                }
                FinishSaveMatch();
                break;
            case HandPoseKind.CapturePose:
                if (savePending) FinishSaveCancel($"pose changed to '{captureGestureName}'");
                if (translatePending) FinishTranslateCancel($"pose changed to '{captureGestureName}'");
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
            case HandPoseKind.TranslateStart:
                if (savePending) FinishSaveCancel($"pose changed to '{translateGestureName}'");
                if (cameraPending) FinishCameraCancel($"pose changed to '{translateGestureName}'");
                if (!translatePending)
                {
                    if (!translateRearmRequired) EnterTranslatePending();
                }
                else
                {
                    // Keep sampling the right index-tip position so we know
                    // where the sweep ended when the user finally closes their
                    // fingers. Null Transform is non-fatal -- we just keep the
                    // last valid sample.
                    if (rightIndexTip != null) translateEndHandPos = rightIndexTip.position;
                    translateElapsed = Time.time - _translateEnterTime;
                }
                break;
            case HandPoseKind.TranslateEnd:
                // Same base pose as Start -- only the thumb-index gap crossed
                // below translateThumbIndexGapMin. Treat as the RECOGNIZED
                // trigger iff we were already pending. If not pending, this is
                // just a coincidental closed-C shape and we ignore it.
                if (translatePending)
                {
                    if (rightIndexTip != null) translateEndHandPos = rightIndexTip.position;
                    FinishTranslateMatch();
                }
                break;
            case HandPoseKind.None:
            default:
                if (savePending) FinishSaveCancel("palm pose lost");
                if (cameraPending) FinishCameraCancel("camera pose broken");
                // Base pose broke -- extension out of range, curl lost, or
                // palm rotated off "left". Per user preference the C-shape
                // breaking cancels Translate; the only other way out is the
                // pose-based TranslateEnd handled in the case above.
                if (translatePending) FinishTranslateCancel("C-shape base broken");
                saveRearmRequired = false;
                cameraRearmRequired = false;
                translateRearmRequired = false;
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
        PlayFeedback(activationClip);
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
        PlayFeedback(deactivationClip);
        ConsumeRightPinchIfActive();
        try { OnCaptureRecognized?.Invoke(saveGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishSaveCancel(string reason)
    {
        savePending = false;
        saveRearmRequired = true;
        Debug.Log($"[Study Log][GestureRouter] REJECT: '{saveGestureName}', {reason}");
        SendEvent(saveGestureName, "FAIL");
        PlayFeedback(deactivationClip);
        ConsumeRightPinchIfActive();
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
        PlayFeedback(activationClip);
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
        PlayFeedback(deactivationClip);
        ConsumeRightPinchIfActive();
        try { OnCaptureRecognized?.Invoke(captureGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishCameraCancel(string reason)
    {
        cameraPending = false;
        cameraRearmRequired = true;
        Debug.Log($"[Study Log][GestureRouter] REJECT: '{captureGestureName}', {reason}");
        SendEvent(captureGestureName, "FAIL");
        PlayFeedback(deactivationClip);
        ConsumeRightPinchIfActive();
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void InterruptCapture()
    {
        cameraPending = false;
        Debug.Log($"[Study Log][GestureRouter] {captureGestureName} interrupted by right pinch (handing over to Jackknife)");
        SendEvent(captureGestureName, "FAIL");
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // Translate Manager
    void EnterTranslatePending()
    {
        translatePending = true;
        _translateEnterTime = Time.time;
        translateElapsed = 0f;
        // Snapshot the right index-tip position at entry so the sweep vector
        // is start->end when the user closes their fingers. If the Transform
        // isn't assigned, zero out and let the log flag it.
        translateStartHandPos = rightIndexTip != null ? rightIndexTip.position : Vector3.zero;
        translateEndHandPos = translateStartHandPos;
        lastRecognized = "";
        Debug.Log($"[Study Log][GestureRouter] {translateGestureName} pending: right C-shape at {translateStartHandPos}");
        SendEvent(translateGestureName, "START");
        PlayFeedback(activationClip);
        try { OnCaptureStarted?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishTranslateMatch()
    {
        translatePending = false;
        translateRearmRequired = true;
        translateElapsed = Time.time - _translateEnterTime;
        Vector3 sweep = translateEndHandPos - translateStartHandPos;
        lastRecognized = translateGestureName;
        Debug.Log($"[Study Log][GestureRouter] RECOGNIZED: '{translateGestureName}' held {translateElapsed:F2}s, sweep={sweep} (|{sweep.magnitude:F3}m|)");
        SendEvent(translateGestureName, "END");
        SendEvent(translateGestureName, "RECOGNIZED");
        PlayFeedback(deactivationClip);
        ConsumeRightPinchIfActive();
        // Extra time-window guard: the finger-close motion that just finished
        // Translate keeps the pinch active for a bit, and any brief release +
        // re-press within the next few frames would otherwise sneak into a
        // fresh Jackknife capture. Cooldown blocks the rising-edge check
        // outright until the user has clearly moved on.
        SuppressPinchForCooldown(postGestureCooldownSeconds, "post-translate");
        try { OnCaptureRecognized?.Invoke(translateGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void FinishTranslateCancel(string reason)
    {
        translatePending = false;
        translateRearmRequired = true;
        Debug.Log($"[Study Log][GestureRouter] REJECT: '{translateGestureName}', {reason}");
        SendEvent(translateGestureName, "FAIL");
        PlayFeedback(deactivationClip);
        ConsumeRightPinchIfActive();
        try { OnCaptureRejected?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // Whenever a pose-based gesture (Save / Capture / Translate) resolves --
    // whether as RECOGNIZED or as FAIL -- the user's hand is often mid-motion
    // and a pinch InputAction can fire incidentally (closing fingers, hand
    // relax, etc.). Without this consumption the same frame's rising edge
    // would leak into StartCapture and trigger a phantom Jackknife session.
    // Setting _wasPressed=true invalidates the rising-edge check until the
    // user physically releases and re-pinches.
    void ConsumeRightPinchIfActive()
    {
        if (ReadPressed(pinchAction)) _wasPressed = true;
    }

    // Time-window sibling to ConsumeRightPinchIfActive: even after the current
    // frame's edge is consumed, a follow-up pinch a few frames later can still
    // slip through if the user's fingers keep touching then briefly release +
    // re-press. This pushes a "cooldown deadline" forward so the rising-edge
    // check refuses any new StartCapture until the deadline passes. Deadline
    // only moves FORWARD -- overlapping calls extend but never shorten.
    void SuppressPinchForCooldown(float seconds, string reason)
    {
        if (seconds <= 0f) return;
        float newDeadline = Time.time + seconds;
        if (newDeadline <= pinchCooldownUntil) return;
        pinchCooldownUntil = newDeadline;
        Debug.Log($"[Study Log][GestureRouter] pinch cooldown +{seconds:F2}s ('{reason}') until t={pinchCooldownUntil:F2}");
    }

    // Cached XRInteractionManager reference + scratch list for the XR hover
    // check. We look up the manager lazily so scene load ordering doesn't
    // matter, and re-fetch if the cached one goes null (scene reload).
    private XRInteractionManager _xrManager;
    private readonly List<IXRInteractor> _xrInteractorScratch = new List<IXRInteractor>();

    // Returns true when the pinch rising edge is aimed at an XR interactable
    // (anchor pin, sticky note, XR grab handle). Uses XRInteractionManager's
    // hover state -- both AnchorPin and StickyNote use XRSimpleInteractable
    // with allowGazeInteraction=false, so only HAND ray/direct interactors
    // ever satisfy this check (gaze doesn't register hover on them).
    bool IsPinchOverXrInteractable()
    {
        if (_xrManager == null)
            _xrManager = FindObjectOfType<XRInteractionManager>();
        if (_xrManager == null) return false;

        _xrInteractorScratch.Clear();
        _xrManager.GetRegisteredInteractors(_xrInteractorScratch);
        for (int i = 0; i < _xrInteractorScratch.Count; i++)
        {
            if (_xrInteractorScratch[i] is IXRHoverInteractor hover && hover.hasHover)
                return true;
        }
        return false;
    }

    // Returns true when SOME pointer registered with EventSystem is currently
    // over a UI raycast target. The Input System UI Input Module keeps a
    // separate pointer state per tracked device (right hand pinch, left hand
    // pinch, mouse, etc.), so we probe several candidate ids:
    //   -1              => default pointer (mouse / older fallback)
    //   0..3            => touch / tracked device slots the input module
    //                      typically assigns
    // If any of them has a hovered UI target we treat the incoming pinch as
    // a UI interaction and let ExecuteEvents deliver the click through the
    // normal EventSystem pipeline instead of turning it into a gesture.
    bool IsPinchOverUi()
    {
        EventSystem es = EventSystem.current;
        if (es == null) return false;

        // Cheap path: the parameterless overload checks the most recently
        // updated pointer. When the user is aiming at a button, that's the
        // right-hand tracked device -- so this handles the common case in
        // one call without allocating.
        if (es.IsPointerOverGameObject())
            return true;

        // Belt-and-braces: scan the low pointer ids that the Input System UI
        // Input Module assigns to tracked / touch devices. Cheap: each call
        // is a dict lookup. Range picked wide enough to cover both hands +
        // any secondary device without over-scanning.
        for (int id = -1; id <= 8; id++)
        {
            if (id == 0) continue; // 0 is unassigned in the input module
            if (es.IsPointerOverGameObject(id)) return true;
        }
        return false;
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

    // Lazily attach a 2D AudioSource on first use so the user only has to drop
    // clips into the Inspector -- no manual component setup required.
    void EnsureFeedbackAudioSource()
    {
        if (feedbackAudioSource != null) return;
        feedbackAudioSource = GetComponent<AudioSource>();
        if (feedbackAudioSource == null)
        {
            feedbackAudioSource = gameObject.AddComponent<AudioSource>();
            feedbackAudioSource.playOnAwake = false;
            feedbackAudioSource.spatialBlend = 0f;
        }
    }

    void PlayFeedback(AudioClip clip)
    {
        if (clip == null) return;
        EnsureFeedbackAudioSource();
        feedbackAudioSource.PlayOneShot(clip, feedbackVolume);
    }
}
