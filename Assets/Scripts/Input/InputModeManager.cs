using System;
using UnityEngine;

public class InputModeManager : MonoBehaviour
{
    [Header("Mode")]
    public InputMode initialMode = InputMode.GestureOnly;
    public bool applyInitialModeOnStart = true;

    [Header("Mode-controlled controllers")]
    [Tooltip("Scripts that emit gesture commands. Keep providers/permissions out of this list when possible.")]
    public Behaviour[] gestureControllers;
    [Tooltip("Scripts that perform Android STT and route transcript commands.")]
    public Behaviour[] voiceControllers;
    [Tooltip("Scripts that run object/UI interaction requests.")]
    public Behaviour[] uiControllers;
    [Tooltip("Scripts that read gaze state. MsgSender should stay enabled as common pipeline.")]
    public Behaviour[] gazeControllers;

    [Header("Gaze policy")]
    public bool keepGazeEnabledInGestureOnly = true;
    public bool keepGazeEnabledInVoiceOnly = false;
    public bool keepGazeEnabledInUIOnly = true;

    [Header("Legacy voice recorder")]
    public AskVoiceInputController legacyAskVoiceInputController;
    public bool disableLegacyAskVoiceController = true;

    [Header("Object UI")]
    public ObjectUiRequestManager objectUiRequestManager;
    public bool startObjectUiRequestOnUIOnly = false;

    [Header("Logging")]
    public InteractionLogger interactionLogger;
    public bool verboseLogging = true;

    [Header("Status")]
    [SerializeField] private InputMode currentMode;

    public InputMode CurrentMode => currentMode;
    public event Action<InputMode> OnModeChanged;

    void Awake()
    {
        currentMode = initialMode;
    }

    void Start()
    {
        if (applyInitialModeOnStart) SetMode(initialMode);
    }

    public void SetGestureOnly()
    {
        SetMode(InputMode.GestureOnly);
    }

    public void SetVoiceOnly()
    {
        SetMode(InputMode.VoiceOnly);
    }

    public void SetUIOnly()
    {
        SetMode(InputMode.UIOnly);
    }

    public void SetModeByIndex(int index)
    {
        if (index < 0 || index > 2)
        {
            Debug.LogWarning($"[InputModeManager] invalid mode index={index}");
            return;
        }

        if (index == 0) SetMode(InputMode.GestureOnly);
        else if (index == 1) SetMode(InputMode.UIOnly);
        else SetMode(InputMode.VoiceOnly);
    }

    public void SetMode(InputMode mode)
    {
        if (mode == InputMode.GazeVoice) mode = InputMode.VoiceOnly;
        currentMode = mode;
        ApplyMode();
        OnModeChanged?.Invoke(currentMode);
        interactionLogger?.LogModeChanged(currentMode);

        if (currentMode == InputMode.UIOnly && startObjectUiRequestOnUIOnly)
        {
            if (objectUiRequestManager == null)
                objectUiRequestManager = FindObjectOfType<ObjectUiRequestManager>();

            if (objectUiRequestManager != null)
                objectUiRequestManager.BeginObjectUiRequest();
            else
                Debug.LogWarning("[InputModeManager] UIOnly selected but ObjectUiRequestManager is not assigned.");
        }
    }

    void ApplyMode()
    {
        bool gestureEnabled = currentMode == InputMode.GestureOnly;
        bool voiceEnabled = currentMode == InputMode.VoiceOnly || currentMode == InputMode.GazeVoice;
        bool uiEnabled = currentMode == InputMode.UIOnly;
        bool gazeEnabled = currentMode == InputMode.GazeVoice
            || (currentMode == InputMode.GestureOnly && keepGazeEnabledInGestureOnly)
            || (currentMode == InputMode.VoiceOnly && keepGazeEnabledInVoiceOnly)
            || (currentMode == InputMode.UIOnly && keepGazeEnabledInUIOnly);

        if (!voiceEnabled)
            CleanupVoiceListeningUi("mode_changed");

        SetEnabled(gestureControllers, gestureEnabled);
        SetEnabled(voiceControllers, voiceEnabled);
        SetEnabled(uiControllers, uiEnabled);
        SetEnabled(gazeControllers, gazeEnabled);
        NotifyVoiceManagers(currentMode);

        if (disableLegacyAskVoiceController && legacyAskVoiceInputController != null)
        {
            legacyAskVoiceInputController.HideListeningPanel("android_stt_active");
            legacyAskVoiceInputController.allowLegacyRecording = false;
            legacyAskVoiceInputController.enabled = false;
            if (verboseLogging)
                Debug.Log("[InputModeManager] Legacy AskVoiceInputController disabled because Android STT voice input is active.");
        }

        if (verboseLogging)
        {
            Debug.Log($"[InputModeManager] mode={currentMode} gesture={gestureEnabled} voice={voiceEnabled} ui={uiEnabled} gaze={gazeEnabled}");
        }
    }

    void SetEnabled(Behaviour[] controllers, bool enabled)
    {
        if (controllers == null) return;
        for (int i = 0; i < controllers.Length; i++)
        {
            Behaviour controller = controllers[i];
            if (controller == null || controller == this) continue;
            if (controller.enabled != enabled) controller.enabled = enabled;
        }
    }

    void CleanupVoiceListeningUi(string reason)
    {
        Debug.Log("[InputModeManager] voice disabled -> cleanup voice listening UI");
        CleanupVoiceListeningUi(voiceControllers, reason);
        if (legacyAskVoiceInputController != null)
            legacyAskVoiceInputController.HideListeningPanel(reason);
    }

    void CleanupVoiceListeningUi(Behaviour[] controllers, string reason)
    {
        if (controllers == null) return;
        for (int i = 0; i < controllers.Length; i++)
        {
            VoiceInputManager manager = controllers[i] as VoiceInputManager;
            if (manager != null) manager.CancelCurrentVoiceSession(reason);
        }
    }

    void NotifyVoiceManagers(InputMode mode)
    {
        NotifyVoiceManagers(voiceControllers, mode);
    }

    void NotifyVoiceManagers(Behaviour[] controllers, InputMode mode)
    {
        if (controllers == null) return;
        for (int i = 0; i < controllers.Length; i++)
        {
            VoiceInputManager manager = controllers[i] as VoiceInputManager;
            if (manager != null) manager.SetInputMode(mode);
        }
    }
}
