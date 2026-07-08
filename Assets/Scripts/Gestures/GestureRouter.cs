using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

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
    // Translate now fires END + RECOGNIZED as soon as Jackknife matches, just
    // like every other gesture. The old READY / palm-swipe confirmation step
    // is gone -- usability trumps the extra confirm.
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
        // Mode change can fire while the user is mid-pinch (e.g. they pinched
        // the dropdown to switch to UI Interaction). Without this cleanup,
        // Python receives a START packet but no terminator -- the gesture
        // hangs until the cached state ages out. Force a FAIL on every
        // pending branch so the backend cleanly drops the in-flight session.
        if (capturing) FinishWithReject("router_disabled");
        if (compareReady) FinishCompareCancel("router_disabled");
        if (savePending) FinishSaveCancel("router_disabled");
        if (cameraPending) FinishCameraCancel("router_disabled");

        // Reset edge-trigger latches so a still-held pinch doesn't get
        // mis-interpreted as a fresh press the moment OnEnable runs again.
        _wasPressed = false;
        _leftWasPressed = false;
        saveRearmRequired = false;
        cameraRearmRequired = false;

        pinchAction?.action.Disable();
        leftPinchAction?.action.Disable();
        rightPinchPositionAction?.action.Disable();
        leftPinchPositionAction?.action.Disable();
    }

    void Update()
    {
        if (!IsGestureModeActive()) return;

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
