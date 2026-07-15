using UnityEngine;
using UnityEngine.InputSystem;

/*
Watches the left-hand pinch action and, after a sustained hold while the user
is in UI Interaction mode, kicks off an ObjectUI capture / YOLO / depth /
bubble-spawn round trip via ObjectUiRequestManager.BeginObjectUiRequest().

The pattern mirrors VoiceHandTrigger.cs (voice mode) so the two trigger
components stay symmetric. The hand pinch (rather than a delayed timer) is
what minimises head-pose drift between capture and bubble placement:
capture happens at the moment the user commits with a hold, not 3 s after
selecting a dropdown.
*/

public class ObjectUiHandTrigger : MonoBehaviour
{
    [Header("Refs")]
    public ObjectUiRequestManager requestManager;
    public InputModeManager modeManager;
    [Tooltip("Optional. When set (or auto-found), the router's activation clip plays the moment the long pinch is recognised -- same cue as gesture starts.")]
    public GestureRouter feedbackRouter;

    [Header("Optional Input Action")]
    [Tooltip("Optional override. If empty, this component listens for a left-hand pinch/select value.")]
    public InputActionReference triggerAction;

    [Header("Left-hand pinch hold")]
    [Range(0f, 1f)] public float triggerThreshold = 0.85f;
    public float holdSeconds = 1f;
    public float cooldownSeconds = 1.5f;
    public bool requireUIMode = true;
    public bool useFallbackLeftHandPinch = true;
    public bool verboseLogging = true;

    [Header("Status (read-only)")]
    [SerializeField] private float holdTime;

    private InputAction _fallbackAction;
    private bool _triggeredThisHold;
    private float _lastTriggerTime = -999f;

    void Awake() { ResolveReferences(); }

    void OnEnable()
    {
        ResolveReferences();
        triggerAction?.action?.Enable();
        EnsureFallbackAction();
        _fallbackAction?.Enable();
        ResetHold();
    }

    void OnDisable()
    {
        triggerAction?.action?.Disable();
        _fallbackAction?.Disable();
        ResetHold();
    }

    void Update()
    {
        if (requireUIMode && modeManager != null && modeManager.CurrentMode != InputMode.UIOnly)
        {
            ResetHold();
            return;
        }

        float value = ReadTriggerValue();
        if (value < triggerThreshold)
        {
            ResetHold();
            return;
        }

        holdTime += Time.unscaledDeltaTime;
        if (_triggeredThisHold || holdTime < holdSeconds) return;
        if (Time.unscaledTime - _lastTriggerTime < cooldownSeconds) return;

        _triggeredThisHold = true;
        _lastTriggerTime = Time.unscaledTime;
        TriggerObjectUiRequest();
    }

    void TriggerObjectUiRequest()
    {
        ResolveReferences();
        if (requestManager == null)
        {
            Debug.LogWarning("[ObjectUiHandTrigger] ObjectUiRequestManager is not assigned.");
            return;
        }
        if (verboseLogging) Debug.Log("[ObjectUiHandTrigger] left-hand pinch hold detected; firing ObjectUI request.");
        if (feedbackRouter != null) feedbackRouter.PlayActivationCue();
        requestManager.BeginObjectUiRequest();
    }

    float ReadTriggerValue()
    {
        InputAction action = triggerAction != null ? triggerAction.action : _fallbackAction;
        if (action == null) return 0f;

        try
        {
            if (action.activeControl != null && action.activeControl.valueType == typeof(float))
                return action.ReadValue<float>();
        }
        catch { }

        try { return action.IsPressed() ? 1f : 0f; }
        catch { return 0f; }
    }

    void EnsureFallbackAction()
    {
        if (!useFallbackLeftHandPinch || triggerAction != null || _fallbackAction != null) return;

        _fallbackAction = new InputAction("LeftHandObjectUiTrigger", InputActionType.Value, expectedControlType: "Axis");
        _fallbackAction.AddBinding("<MetaAimHand>{LeftHand}/pinchStrengthIndex");
        _fallbackAction.AddBinding("<HandInteraction>{LeftHand}/pinchValue");
        _fallbackAction.AddBinding("<HandInteraction>{LeftHand}/pointerActivateValue");
        _fallbackAction.AddBinding("<XRController>{LeftHand}/{Grip}");
    }

    void ResolveReferences()
    {
        if (requestManager == null) requestManager = FindObjectOfType<ObjectUiRequestManager>();
        if (modeManager == null) modeManager = FindObjectOfType<InputModeManager>();
        if (feedbackRouter == null) feedbackRouter = FindObjectOfType<GestureRouter>();
    }

    void ResetHold()
    {
        holdTime = 0f;
        _triggeredThisHold = false;
    }
}
