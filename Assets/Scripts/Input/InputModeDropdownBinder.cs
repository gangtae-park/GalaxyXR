using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class InputModeDropdownBinder : MonoBehaviour
{
    [Header("Refs")]
    public TMP_Dropdown inputModeDropdown;
    public InputModeManager modeManager;
    public VoiceInputManager voiceInputManager;

    [Header("Dropdown labels")]
    public bool syncLabelsOnStart = true;
    public string gestureOnlyLabel = "Gesture input";
    public string uiInteractionLabel = "UI Interaction";
    public string voiceOnlyLabel = "Voice input";

    [Header("Voice test shortcut")]
    [Tooltip("For the current test panel: selecting Voice immediately opens one STT listening window.")]
    public bool startListeningWhenVoiceModeSelected = true;

    void Awake()
    {
        ResolveReferences();
    }

    void OnEnable()
    {
        ResolveReferences();
        if (inputModeDropdown != null)
        {
            inputModeDropdown.onValueChanged.RemoveListener(HandleDropdownChanged);
            inputModeDropdown.onValueChanged.AddListener(HandleDropdownChanged);
        }
    }

    void Start()
    {
        if (syncLabelsOnStart && inputModeDropdown != null)
        {
            inputModeDropdown.ClearOptions();
            inputModeDropdown.AddOptions(new List<string>
            {
                gestureOnlyLabel,
                uiInteractionLabel,
                voiceOnlyLabel
            });
            int currentValue = ModeToDropdownIndex(modeManager != null ? modeManager.CurrentMode : InputMode.GestureOnly);
            inputModeDropdown.SetValueWithoutNotify(currentValue);
            inputModeDropdown.RefreshShownValue();
        }
    }

    void OnDisable()
    {
        if (inputModeDropdown != null)
            inputModeDropdown.onValueChanged.RemoveListener(HandleDropdownChanged);
    }

    public void HandleDropdownChanged(int index)
    {
        ResolveReferences();
        int clamped = Mathf.Clamp(index, 0, 2);

        if (modeManager == null)
        {
            Debug.LogWarning("[InputModeDropdownBinder] InputModeManager is not assigned.");
            return;
        }

        modeManager.SetModeByIndex(clamped);

        bool voiceMode = clamped == 2;
        if (voiceMode && startListeningWhenVoiceModeSelected)
        {
            if (voiceInputManager != null)
                voiceInputManager.StartListening();
            else
                Debug.LogWarning("[InputModeDropdownBinder] VoiceInputManager is not assigned; cannot StartListening.");
        }
    }

    void ResolveReferences()
    {
        if (modeManager == null) modeManager = FindObjectOfType<InputModeManager>();
        if (voiceInputManager == null) voiceInputManager = FindObjectOfType<VoiceInputManager>();
    }

    static int ModeToDropdownIndex(InputMode mode)
    {
        if (mode == InputMode.UIOnly) return 1;
        if (mode == InputMode.VoiceOnly || mode == InputMode.GazeVoice) return 2;
        return 0;
    }
}
