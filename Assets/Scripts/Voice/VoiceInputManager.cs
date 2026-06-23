using System;
using System.Collections;
using System.Text;
using UnityEngine;

public class VoiceInputManager : MonoBehaviour
{
    [Header("Mode")]
    [SerializeField] private InputMode currentMode = InputMode.VoiceOnly;

    [Header("Refs")]
    public AndroidSpeechRecognizerBridge speechBridge;
    public MsgSender msgSender;
    public RuntimeAudioPermissionRequester permissionRequester;
    public InteractionLogger interactionLogger;

    [Header("Listening")]
    public float listenTimeoutSeconds = 10f;
    public bool autoResolveReferences = true;
    public bool verboseLogging = true;

    [Header("Status")]
    [SerializeField] private bool waitingForFinalTranscript;
    [SerializeField] private string lastTranscript;

    private Coroutine _timeoutRoutine;
    public bool IsListening => waitingForFinalTranscript || (speechBridge != null && speechBridge.IsListening);
    public string LastTranscript => lastTranscript;

    public event Action<InputMode> OnListeningStarted;
    public event Action<string> OnListeningStopped;
    public event Action<string> OnPartialTranscript;
    public event Action<string> OnFinalTranscript;
    public event Action<string> OnVoiceError;

    void Awake()
    {
        ResolveReferences();
    }

    void OnEnable()
    {
        ResolveReferences();
        SubscribeBridge();
    }

    void OnDisable()
    {
        if (speechBridge != null && speechBridge.IsListening) speechBridge.CancelListening();
        FinishListening("disabled");
        UnsubscribeBridge();
    }

    public void SetInputMode(InputMode mode)
    {
        if (mode == InputMode.GazeVoice) mode = InputMode.VoiceOnly;
        currentMode = mode;
    }

    public void StartListening()
    {
        ResolveReferences();

        if (!IsVoiceMode(currentMode))
        {
            Debug.LogWarning($"[VoiceInputManager] StartListening ignored in mode={currentMode}");
            return;
        }

        if (waitingForFinalTranscript || (speechBridge != null && speechBridge.IsListening))
        {
            if (verboseLogging) Debug.Log("[VoiceInputManager] already listening.");
            return;
        }

        if (permissionRequester != null && !permissionRequester.EnsurePermission())
        {
            LogFailure("START_LISTENING", "RECORD_AUDIO permission is not granted yet.");
            return;
        }

        if (speechBridge == null)
        {
            LogFailure("START_LISTENING", "AndroidSpeechRecognizerBridge is not assigned.");
            return;
        }

        waitingForFinalTranscript = true;
        OnListeningStarted?.Invoke(currentMode);
        speechBridge.StartListening();
        StartTimeout();

        if (verboseLogging) Debug.Log($"[VoiceInputManager] StartListening mode={currentMode}");
    }

    public void StopListening()
    {
        if (speechBridge != null && speechBridge.IsListening) speechBridge.StopListening();
        FinishListening("stopped");
    }

    public void CancelListening()
    {
        if (speechBridge != null && speechBridge.IsListening) speechBridge.CancelListening();
        FinishListening("cancelled");
    }

    public void SubmitMockTranscript(string transcript)
    {
        HandleFinalTranscript(transcript);
    }

    void HandleFinalTranscript(string transcript)
    {
        lastTranscript = transcript;
        LogUnicodeCodePoints("[VoiceInputManager] final", transcript);
        OnFinalTranscript?.Invoke(transcript ?? "");
        FinishListening("final");

        ResolveReferences();

        string rawTranscript = transcript == null ? "" : transcript.Trim();
        if (string.IsNullOrWhiteSpace(rawTranscript))
        {
            LogFailure("VOICE_TRANSCRIPT", "Final transcript is empty.");
            return;
        }

        if (msgSender == null)
        {
            LogFailure("ASK_QUESTION", "MsgSender is not assigned.");
            return;
        }

        msgSender.SendVoiceTranscriptToLlm(rawTranscript);
        LogSuccess("ASK_QUESTION", rawTranscript);
    }

    static void LogUnicodeCodePoints(string prefix, string transcript)
    {
        string text = transcript ?? "";
        StringBuilder builder = new StringBuilder();
        builder.Append(prefix)
            .Append(" transcript length=")
            .Append(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            builder.Append('\n')
                .Append("char[")
                .Append(i)
                .Append("]='")
                .Append(PrintableChar(c))
                .Append("' code=U+")
                .Append(((int)c).ToString("X4"));
        }

        Debug.Log(builder.ToString());
    }

    static string PrintableChar(char c)
    {
        switch (c)
        {
            case '\r': return "\\r";
            case '\n': return "\\n";
            case '\t': return "\\t";
            case '\'': return "\\'";
            case '\\': return "\\\\";
            default: return c.ToString();
        }
    }

    void HandlePartialTranscript(string transcript)
    {
        OnPartialTranscript?.Invoke(transcript ?? "");
        if (verboseLogging) Debug.Log($"[VoiceInputManager] partial transcript='{transcript}'");
    }

    void HandleSpeechError(int errorCode, string message)
    {
        FinishListening("error");
        LogFailure("STT_ERROR", $"code={errorCode} {message}");
    }

    void StartTimeout()
    {
        StopTimeout();
        if (listenTimeoutSeconds > 0f) _timeoutRoutine = StartCoroutine(TimeoutRoutine());
    }

    void StopTimeout()
    {
        if (_timeoutRoutine != null)
        {
            StopCoroutine(_timeoutRoutine);
            _timeoutRoutine = null;
        }
    }

    IEnumerator TimeoutRoutine()
    {
        yield return new WaitForSecondsRealtime(listenTimeoutSeconds);
        if (!waitingForFinalTranscript) yield break;
        if (speechBridge != null && speechBridge.IsListening) speechBridge.CancelListening();
        FinishListening("timeout");
        LogFailure("TIMEOUT", $"No final transcript within {listenTimeoutSeconds:F1}s.");
    }

    void FinishListening(string reason)
    {
        bool wasListening = waitingForFinalTranscript;
        StopTimeout();
        waitingForFinalTranscript = false;
        if (wasListening) OnListeningStopped?.Invoke(reason ?? "");
    }

    void SubscribeBridge()
    {
        if (speechBridge == null) return;
        speechBridge.OnFinalTranscript -= HandleFinalTranscript;
        speechBridge.OnPartialTranscript -= HandlePartialTranscript;
        speechBridge.OnError -= HandleSpeechError;
        speechBridge.OnFinalTranscript += HandleFinalTranscript;
        speechBridge.OnPartialTranscript += HandlePartialTranscript;
        speechBridge.OnError += HandleSpeechError;
    }

    void UnsubscribeBridge()
    {
        if (speechBridge == null) return;
        speechBridge.OnFinalTranscript -= HandleFinalTranscript;
        speechBridge.OnPartialTranscript -= HandlePartialTranscript;
        speechBridge.OnError -= HandleSpeechError;
    }

    void ResolveReferences()
    {
        if (!autoResolveReferences) return;
        if (speechBridge == null) speechBridge = FindObjectOfType<AndroidSpeechRecognizerBridge>();
        if (msgSender == null) msgSender = FindObjectOfType<MsgSender>();
        if (permissionRequester == null) permissionRequester = FindObjectOfType<RuntimeAudioPermissionRequester>();
        if (interactionLogger == null) interactionLogger = FindObjectOfType<InteractionLogger>();
    }

    void LogSuccess(string packetType, string transcript)
    {
        if (interactionLogger == null) return;
        interactionLogger.LogInteraction(new InteractionLogEntry
        {
            input_mode = currentMode.ToString(),
            source = currentMode == InputMode.GazeVoice ? "gaze_voice" : "voice",
            raw_transcript = transcript,
            parsed_command = "RawTranscript",
            target_strategy = "llm_direct",
            sent_packet_type = packetType,
            success = true
        });
    }

    void LogFailure(string packetType, string error)
    {
        Debug.LogWarning($"[VoiceInputManager] {packetType} failed: {error}");
        OnVoiceError?.Invoke(error ?? "");
        if (interactionLogger == null) return;
        interactionLogger.LogInteraction(new InteractionLogEntry
        {
            input_mode = currentMode.ToString(),
            source = currentMode == InputMode.GazeVoice ? "gaze_voice" : "voice",
            raw_transcript = lastTranscript,
            parsed_command = "RawTranscript",
            target_strategy = "llm_direct",
            sent_packet_type = packetType,
            success = false,
            error_message = error
        });
    }

    static bool IsVoiceMode(InputMode mode)
    {
        return mode == InputMode.VoiceOnly || mode == InputMode.GazeVoice;
    }
}
