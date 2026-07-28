using UnityEngine;
using UnityEngine.InputSystem;

public class VoiceHandTrigger : MonoBehaviour
{
    [Header("Refs")]
    public VoiceInputManager voiceInputManager;
    public InputModeManager modeManager;

    [Header("Optional Input Action")]
    [Tooltip("Optional override. If empty, this component listens for a left-hand pinch/select value.")]
    public InputActionReference triggerAction;

    [Header("Left-hand pinch hold")]
    [Range(0f, 1f)] public float triggerThreshold = 0.85f;
    public float holdSeconds = 1f;
    public float cooldownSeconds = 1.2f;
    public bool requireVoiceMode = true;
    public bool useFallbackLeftHandPinch = true;
    public bool verboseLogging = true;

    [Header("Palm-up gate")]
    [Tooltip("Only count the pinch hold while the left palm faces the sky. Kills accidental triggers from pinches made in normal hand poses. Disable when driving with controllers (no hand joints -> gate always blocks).")]
    public bool requirePalmUp = true;
    [Tooltip("Max angle (degrees) between the palm normal and world up for the hold to count. 45 = comfortably 'palm up'; smaller = stricter.")]
    [Range(10f, 90f)] public float palmUpMaxAngle = 45f;
    [Tooltip("Flip if the gate feels inverted on this device (triggers palm-DOWN instead of palm-up).")]
    public bool invertPalmNormal = false;

    [Header("Status (read-only)")]
    [SerializeField] private float holdTime;

    private InputAction _fallbackAction;
    private bool _triggeredThisHold;
    private float _lastTriggerTime = -999f;

    void Awake()
    {
        ResolveReferences();
    }

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
        if (requireVoiceMode && modeManager != null && !IsVoiceMode(modeManager.CurrentMode))
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

        // Palm must face the sky for the WHOLE hold -- pinching in a normal
        // hand pose no longer accumulates hold time.
        if (requirePalmUp && !LeftHandPalmUp.IsPalmUp(palmUpMaxAngle, invertPalmNormal))
        {
            ResetHold();
            return;
        }

        holdTime += Time.unscaledDeltaTime;
        if (_triggeredThisHold || holdTime < holdSeconds) return;
        if (Time.unscaledTime - _lastTriggerTime < cooldownSeconds) return;

        _triggeredThisHold = true;
        _lastTriggerTime = Time.unscaledTime;
        StartVoiceListening();
    }

    void StartVoiceListening()
    {
        ResolveReferences();
        if (voiceInputManager == null)
        {
            Debug.LogWarning("[VoiceHandTrigger] VoiceInputManager is not assigned.");
            return;
        }

        if (voiceInputManager.IsListening)
        {
            if (verboseLogging) Debug.Log("[VoiceHandTrigger] ignored; voice input is already listening.");
            return;
        }

        if (verboseLogging) Debug.Log("[VoiceHandTrigger] left-hand pinch hold detected; starting voice input.");
        MsgSender.Instance?.SendStudyEvent("input_start", "voice_listen");
        voiceInputManager.StartListening();
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

        _fallbackAction = new InputAction("LeftHandVoiceTrigger", InputActionType.Value, expectedControlType: "Axis");
        _fallbackAction.AddBinding("<MetaAimHand>{LeftHand}/pinchStrengthIndex");
        _fallbackAction.AddBinding("<HandInteraction>{LeftHand}/pinchValue");
        _fallbackAction.AddBinding("<HandInteraction>{LeftHand}/pointerActivateValue");
        _fallbackAction.AddBinding("<XRController>{LeftHand}/{Grip}");
    }

    void ResolveReferences()
    {
        if (voiceInputManager == null) voiceInputManager = FindObjectOfType<VoiceInputManager>();
        if (modeManager == null) modeManager = FindObjectOfType<InputModeManager>();
    }

    void ResetHold()
    {
        holdTime = 0f;
        _triggeredThisHold = false;
    }

    static bool IsVoiceMode(InputMode mode)
    {
        return mode == InputMode.VoiceOnly || mode == InputMode.GazeVoice;
    }
}
