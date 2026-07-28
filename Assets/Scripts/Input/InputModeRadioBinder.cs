using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Radio-button replacement for InputModeDropdownBinder: three Toggles in a
/// ToggleGroup select the input method with a single click, instead of the
/// dropdown's open-then-pick two-step. Toggle order maps to the same indices
/// InputModeManager.SetModeByIndex already uses:
///   0 = Gesture input, 1 = UI Interaction, 2 = Voice input.
/// </summary>
public class InputModeRadioBinder : MonoBehaviour
{
    [Header("Refs (order = mode index)")]
    public Toggle gestureToggle;      // index 0
    public Toggle uiInteractionToggle; // index 1
    public Toggle voiceToggle;        // index 2
    public InputModeManager modeManager;
    public VoiceInputManager voiceInputManager;

    [Header("Voice test shortcut")]
    [Tooltip("For manual testing only. Production voice mode waits for VoiceHandTrigger instead.")]
    public bool startListeningWhenVoiceModeSelected = false;

    Toggle[] _toggles;

    void Awake()
    {
        ResolveReferences();
        _toggles = new[] { gestureToggle, uiInteractionToggle, voiceToggle };
    }

    void OnEnable()
    {
        ResolveReferences();
        for (int i = 0; i < _toggles.Length; i++)
        {
            if (_toggles[i] == null) continue;
            int index = i; // capture per-iteration for the closure
            _toggles[i].onValueChanged.RemoveAllListeners();
            _toggles[i].onValueChanged.AddListener(isOn =>
            {
                if (isOn) HandleModeSelected(index);
            });
        }
    }

    void Start()
    {
        // Reflect the manager's current mode without firing the callbacks.
        int current = ModeToIndex(modeManager != null ? modeManager.CurrentMode : InputMode.GestureOnly);
        for (int i = 0; i < _toggles.Length; i++)
        {
            if (_toggles[i] != null)
                _toggles[i].SetIsOnWithoutNotify(i == current);
        }
    }

    void OnDisable()
    {
        foreach (var t in _toggles)
        {
            if (t != null) t.onValueChanged.RemoveAllListeners();
        }
    }

    void HandleModeSelected(int index)
    {
        ResolveReferences();
        if (modeManager == null)
        {
            Debug.LogWarning("[InputModeRadioBinder] InputModeManager is not assigned.");
            return;
        }

        modeManager.SetModeByIndex(index);

        if (index == 2 && startListeningWhenVoiceModeSelected)
        {
            if (voiceInputManager != null)
                voiceInputManager.StartListening();
            else
                Debug.LogWarning("[InputModeRadioBinder] VoiceInputManager is not assigned; cannot StartListening.");
        }
    }

    void ResolveReferences()
    {
        if (modeManager == null) modeManager = FindObjectOfType<InputModeManager>();
        if (voiceInputManager == null) voiceInputManager = FindObjectOfType<VoiceInputManager>();
    }

    static int ModeToIndex(InputMode mode)
    {
        if (mode == InputMode.UIOnly) return 1;
        if (mode == InputMode.VoiceOnly || mode == InputMode.GazeVoice) return 2;
        return 0;
    }
}
