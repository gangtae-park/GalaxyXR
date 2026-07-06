using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
SaveNoteCard

Per-instance behaviour for the SaveNoteCard prefab. Owns the input field and
three buttons; raises C# events when the user commits / cancels / closes so
the NoteManager can drive the rest of the flow without the prefab needing any
inspector wiring beyond the field/button refs below.

The card is reused for two purposes:
  - new note    : SetContent(objectName, "") and the user dictates from scratch
  - edit note   : SetContent(objectName, existingText) prefills the field

Input source is now the same Android STT pipeline the Ask flow uses
(AndroidSpeechRecognizerBridge). When the card becomes active we auto-start a
STT session; partial transcripts stream into the input field in real time and
the final transcript is committed as the field's text so Save just needs one
tap. The virtual keyboard is not popped up -- the user can still tap the field
to edit manually via the system input if needed, but the default is voice-only.
The optional micButton restarts a listening session (e.g. to redo the note).
*/
public class SaveNoteCard : MonoBehaviour
{
    [Header("Refs (assign in prefab)")]
    [Tooltip("Optional. Shows which object the note is being attached to.")]
    public TMP_Text objectNameLabel;
    [Tooltip("Required. Text field the transcript streams into; the user can also edit here manually if they tap in.")]
    public TMP_InputField noteField;
    public Button saveButton;
    public Button cancelButton;
    public Button closeButton;

    [Header("Voice input")]
    [Tooltip("Optional. When null, auto-resolves via FindObjectOfType on Awake.")]
    public AndroidSpeechRecognizerBridge speechBridge;
    [Tooltip("Start a STT session as soon as the card is populated (typical flow: user just triggered Save gesture).")]
    public bool autoStartVoiceOnActivate = true;
    [Tooltip("Optional button that restarts listening (e.g. 'redo' mic icon).")]
    public Button micButton;
    [Tooltip("Optional status text ('Listening...', 'Ready', errors).")]
    public TMP_Text micStatusText;
    [Tooltip("What to show while STT is active.")]
    public string listeningStatusMessage = "듣는 중...";
    [Tooltip("What to show when STT is idle or finished.")]
    public string idleStatusMessage = "";

    [Header("Behavior")]
    public bool verboseLogging = true;

    public string ObjectId { get; private set; }
    public string ObjectName { get; private set; }
    public string CurrentText => noteField != null ? noteField.text : "";

    /// <summary>Fired with the current note text when Save is pressed.</summary>
    public event Action<string> OnSaveClicked;
    /// <summary>Fired when Cancel is pressed.</summary>
    public event Action OnCancelClicked;
    /// <summary>Fired when Close (X) is pressed.</summary>
    public event Action OnCloseClicked;

    bool _voiceSubscribed;
    bool _voiceActive;       // true while WE started the current bridge session
    string _committedBase;   // text present when a new listening session began (transcripts appended after this)

    void Awake()
    {
        if (saveButton   != null) saveButton.onClick.AddListener(HandleSave);
        if (cancelButton != null) cancelButton.onClick.AddListener(HandleCancel);
        if (closeButton  != null) closeButton.onClick.AddListener(HandleClose);
        if (micButton    != null) micButton.onClick.AddListener(RestartVoiceListening);

        // Voice-only input: readOnly blocks the system keyboard from ever
        // appearing (tap / focus can't type into the field), while still
        // letting us write to .text programmatically via STT callbacks.
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

    /// <summary>Populate + optionally start dictation.
    /// <paramref name="overwriteExisting"/> is used by the Edit flow: the field
    /// still displays the prior note text (so the user can see what they wrote
    /// before), but the STT base is empty -- the very first partial that
    /// arrives replaces the field entirely instead of appending. Keyboard-free
    /// UX means the user has no way to make small edits, so re-dictating from
    /// scratch is the intended path.</summary>
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
        // "RETAKE": wipe whatever's in the field and start a fresh dictation.
        // The next transcript replaces existing text entirely, not appends.
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
        // Normally we snapshot the current field text as the base so partials
        // append after it. In overwrite mode (RETAKE, Edit flow) the caller has
        // already set _committedBase = "" and we must leave it that way so the
        // incoming transcript replaces the visible existing text on its first
        // partial event.
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
        // Keep the caret at the end so the user can see the growing tail.
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
        // Insert a space between existing text and new dictation when neither
        // side already provides one.
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
