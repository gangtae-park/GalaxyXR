using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SaveNoteCard : MonoBehaviour
{
    [Header("Refs (assign in prefab)")]
    public TMP_Text objectNameLabel;
    public TMP_InputField noteField;
    public Button saveButton;
    public Button cancelButton;
    public Button closeButton;

    [Header("Voice input")]
    public AndroidSpeechRecognizerBridge speechBridge;
    public bool autoStartVoiceOnActivate = true;
    public Button micButton;
    public TMP_Text micStatusText;
    public string listeningStatusMessage = "Listening...";
    public string idleStatusMessage = "";

    [Header("Behavior")]
    public bool verboseLogging = true;

    public string ObjectId { get; private set; }
    public string ObjectName { get; private set; }
    public string CurrentText => noteField != null ? noteField.text : "";

    public event Action<string> OnSaveClicked;
    public event Action OnCancelClicked;
    public event Action OnCloseClicked;

    bool _voiceSubscribed;
    bool _voiceActive;
    string _committedBase;
    void Awake()
    {
        if (saveButton   != null) saveButton.onClick.AddListener(HandleSave);
        if (cancelButton != null) cancelButton.onClick.AddListener(HandleCancel);
        if (closeButton  != null) closeButton.onClick.AddListener(HandleClose);
        if (micButton    != null) micButton.onClick.AddListener(RestartVoiceListening);

        if (noteField != null) noteField.readOnly = true;

        if (speechBridge == null) speechBridge = FindObjectOfType<AndroidSpeechRecognizerBridge>();
        SubscribeBridge();
    }

    void OnDestroy()
    {
        if (saveButton   != null) saveButton.onClick.RemoveListener(HandleSave);
        if (cancelButton != null) cancelButton.onClick.RemoveListener(HandleCancel);
        if (closeButton  != null) closeButton.onClick.RemoveListener(HandleClose);
        if (micButton    != null) micButton.onClick.RemoveListener(RestartVoiceListening);

        CancelVoice("card destroyed");
        UnsubscribeBridge();
    }

    public void SetContent(string objectId, string objectName, string existingText = "", bool overwriteExisting = false)
    {
        ObjectId = objectId ?? "";
        ObjectName = objectName ?? "";
        if (objectNameLabel != null) objectNameLabel.text = ObjectName;
        if (noteField != null) noteField.text = existingText ?? "";
        _committedBase = overwriteExisting ? "" : (noteField != null ? noteField.text : "");

        SetMicStatus(idleStatusMessage);
        if (autoStartVoiceOnActivate) StartVoiceListening(overwriteExisting);
    }

    // ---------- Voice input ----------

    public void RestartVoiceListening()
    {
        if (noteField != null) noteField.text = "";
        _committedBase = "";
        StartVoiceListening(overwrite: true);
    }

    void StartVoiceListening(bool overwrite = false)
    {
        if (speechBridge == null)
        {
            if (verboseLogging) Debug.Log("[SaveNoteCard] speechBridge unassigned; voice input disabled.");
            SetMicStatus("(voice unavailable)");
            return;
        }
        if (!overwrite)
            _committedBase = noteField != null ? noteField.text : "";
        if (speechBridge.IsListening)
        {
            if (verboseLogging) Debug.Log("[SaveNoteCard] bridge already listening; piggy-backing.");
            _voiceActive = true;
            SetMicStatus(listeningStatusMessage);
            return;
        }
        speechBridge.StartListening();
        _voiceActive = true;
        SetMicStatus(listeningStatusMessage);
        if (verboseLogging) Debug.Log($"[SaveNoteCard] STT START for note dictation (overwrite={overwrite})");
    }

    void CancelVoice(string reason)
    {
        if (!_voiceActive) return;
        _voiceActive = false;
        if (speechBridge != null && speechBridge.IsListening) speechBridge.CancelListening();
        SetMicStatus(idleStatusMessage);
        if (verboseLogging) Debug.Log($"[SaveNoteCard] STT CANCEL ({reason})");
    }

    void SubscribeBridge()
    {
        if (speechBridge == null || _voiceSubscribed) return;
        speechBridge.OnPartialTranscript += HandlePartial;
        speechBridge.OnFinalTranscript += HandleFinal;
        speechBridge.OnError += HandleError;
        _voiceSubscribed = true;
    }

    void UnsubscribeBridge()
    {
        if (speechBridge == null || !_voiceSubscribed) return;
        speechBridge.OnPartialTranscript -= HandlePartial;
        speechBridge.OnFinalTranscript -= HandleFinal;
        speechBridge.OnError -= HandleError;
        _voiceSubscribed = false;
    }

    void HandlePartial(string transcript)
    {
        if (!_voiceActive) return;
        if (noteField == null) return;
        noteField.text = ComposeText(transcript);
        noteField.caretPosition = noteField.text.Length;
    }

    void HandleFinal(string transcript)
    {
        if (!_voiceActive) return;
        _voiceActive = false;
        if (noteField != null)
        {
            noteField.text = ComposeText(transcript);
            noteField.caretPosition = noteField.text.Length;
        }
        SetMicStatus(idleStatusMessage);
        // User-study milestone: note dictation finished = Save's input_end
        // (gesture/UI recognition earlier was only save_point).
        MsgSender.Instance?.SendStudyEvent("input_end", "save_note_dictated");
        if (verboseLogging) Debug.Log($"[SaveNoteCard] STT FINAL '{transcript}'");
    }

    void HandleError(int code, string message)
    {
        if (!_voiceActive) return;
        _voiceActive = false;
        SetMicStatus($"STT error {code}");
        if (verboseLogging) Debug.LogWarning($"[SaveNoteCard] STT ERROR {code} {message}");
    }

    string ComposeText(string transcript)
    {
        string safe = transcript == null ? "" : transcript;
        if (string.IsNullOrEmpty(_committedBase)) return safe;
        
        if (_committedBase.EndsWith(" ") || safe.StartsWith(" "))
            return _committedBase + safe;
        return _committedBase + " " + safe;
    }

    void SetMicStatus(string message)
    {
        if (micStatusText != null) micStatusText.text = message ?? "";
    }

    void HandleSave()
    {
        CancelVoice("save");
        try { OnSaveClicked?.Invoke(CurrentText); } catch (Exception e) { Debug.LogError(e); }
    }

    void HandleCancel()
    {
        CancelVoice("cancel");
        try { OnCancelClicked?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void HandleClose()
    {
        CancelVoice("close");
        try { OnCloseClicked?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }
}
