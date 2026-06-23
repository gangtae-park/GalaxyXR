using UnityEngine;
using UnityEngine.InputSystem;

public class InputModeDebugPanel : MonoBehaviour
{
    [Header("Refs")]
    public InputModeManager modeManager;
    public VoiceInputManager voiceInputManager;

    [Header("Keyboard debug")]
    public bool enableKeyboardShortcuts = true;

    void Update()
    {
        if (!enableKeyboardShortcuts) return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.digit1Key.wasPressedThisFrame) SetGestureOnly();
        if (keyboard.digit2Key.wasPressedThisFrame) SetUIOnly();
        if (keyboard.digit3Key.wasPressedThisFrame) SetVoiceOnly();
        if (keyboard.vKey.wasPressedThisFrame) StartVoiceListening();
    }

    public void SetGestureOnly()
    {
        modeManager?.SetGestureOnly();
    }

    public void SetVoiceOnly()
    {
        modeManager?.SetVoiceOnly();
    }

    public void SetUIOnly()
    {
        modeManager?.SetUIOnly();
    }

    public void StartVoiceListening()
    {
        voiceInputManager?.StartListening();
    }
}
