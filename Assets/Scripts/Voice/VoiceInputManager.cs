using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class VoiceInputManager : MonoBehaviour
{
    [Header("Mode")]
    [SerializeField] private InputMode currentMode = InputMode.VoiceOnly;

    [Header("Refs")]
    public AndroidSpeechRecognizerBridge speechBridge;
    public MsgSender msgSender;
    public RuntimeAudioPermissionRequester permissionRequester;
    public InteractionLogger interactionLogger;
    public ResultCardSpawner resultCardSpawner;

    [Header("Listening")]
    public float listenTimeoutSeconds = 10f;
    public bool autoResolveReferences = true;
    public bool verboseLogging = true;

    [Header("Backend packet")]
    [Tooltip("VOICE_COMMAND tells the backend to pair this transcript with the current frame/object context.")]
    public bool sendVoiceCommandPacket = true;
    [Tooltip("When an Ask gesture card is waiting, use the transcript as that card's question instead of a standalone voice command.")]
    public bool routePendingAskCard = true;
    [Tooltip("POST one JSON request containing the voice-start screenshot and final transcript. Falls back to the legacy UDP VOICE_COMMAND only when disabled.")]
    public bool sendSnapshotVoiceRequest = true;
    public string voiceSnapshotServerUrl = "http://192.168.0.3:5007/voice_command";
    [Range(1, 100)] public int snapshotJpegQuality = 55;
    public float snapshotReadyTimeoutSeconds = 1.5f;

    [Header("Status")]
    [SerializeField] private bool waitingForFinalTranscript;
    [SerializeField] private string lastTranscript;

    private Coroutine _timeoutRoutine;
    private Coroutine _snapshotRoutine;
    private VoiceRequestContext _activeVoiceRequest;
    public bool IsListening => waitingForFinalTranscript || (speechBridge != null && speechBridge.IsListening);
    public string LastTranscript => lastTranscript;

    public event Action<InputMode> OnListeningStarted;
    public event Action<string> OnListeningStopped;
    public event Action<string> OnPartialTranscript;
    public event Action<string> OnFinalTranscript;
    public event Action<string> OnVoiceError;

    [Serializable]
    class VoiceSnapshotPayload
    {
        public string request_id;
        public string requestId;
        public string source;
        public string gesture;
        public float listen_started_unscaled_time;
        public float transcript_final_unscaled_time;
        public int screen_width;
        public int screen_height;
        public string transcript;
        public string image_mime;
        public string image_base64;
        public int image_width;
        public int image_height;
    }

    class VoiceRequestContext
    {
        public string requestId;
        public float listenStartedTime;
        public bool captureComplete;
        public string captureError;
        public string imageMime = "image/jpeg";
        public string imageBase64 = "";
        public int imageWidth;
        public int imageHeight;
        public int screenWidth;
        public int screenHeight;
    }

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
        CancelCurrentVoiceSession("disabled");
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

        StartVoiceRequestContext();

        waitingForFinalTranscript = true;
        OnListeningStarted?.Invoke(currentMode);
        Debug.Log($"[VOICE_UI] listening panel shown request_id={CurrentVoiceRequestId()}");
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
        CancelCurrentVoiceSession("cancelled");
    }

    public void CancelCurrentVoiceSession(string reason = "cancelled")
    {
        if (speechBridge != null && speechBridge.IsListening) speechBridge.CancelListening();
        if (_snapshotRoutine != null)
        {
            StopCoroutine(_snapshotRoutine);
            _snapshotRoutine = null;
        }
        FinishListening(reason);
    }

    public void HideListeningUi(string reason = "cleanup")
    {
        ResolveReferences();
        resultCardSpawner?.ClearVoiceListeningCards(reason);
    }

    public void SubmitMockTranscript(string transcript)
    {
        HandleFinalTranscript(transcript);
    }

    void HandleFinalTranscript(string transcript)
    {
        if (!waitingForFinalTranscript)
        {
            Debug.LogWarning($"[VOICE_UI][WARN] ignored stale final transcript request_id={CurrentVoiceRequestId()}");
            HideListeningUi("stale_transcript");
            return;
        }

        lastTranscript = transcript;
        string rawTranscript = transcript == null ? "" : transcript.Trim();
        Debug.Log($"[VOICE_UI] transcript received request_id={CurrentVoiceRequestId()} text={rawTranscript}");
        LogUnicodeCodePoints("[VoiceInputManager] final", transcript);
        OnFinalTranscript?.Invoke(transcript ?? "");

        ResolveReferences();

        if (string.IsNullOrWhiteSpace(rawTranscript))
        {
            FinishListening("final");
            LogFailure("VOICE_TRANSCRIPT", "Final transcript is empty.");
            return;
        }

        if (resultCardSpawner != null && resultCardSpawner.HasPendingVoiceListeningCard)
        {
            resultCardSpawner.ClearVoiceListeningCards("stale_placeholder_before_transcript");
            Debug.LogWarning("[VOICE_UI][WARN] ignored placeholder voice request card");
        }

        if (routePendingAskCard && resultCardSpawner != null && resultCardSpawner.PendingAskQuestion != null)
        {
            resultCardSpawner.PendingAskQuestion.SubmitLocal(rawTranscript);
            SendVoiceSnapshotOrFallback(rawTranscript, "ASK_QUESTION");
            LogSuccess(sendSnapshotVoiceRequest ? "VOICE_SNAPSHOT" : "ASK_QUESTION", rawTranscript);
            FinishListening("final");
            return;
        }

        if (!sendVoiceCommandPacket)
        {
            FinishListening("final");
            return;
        }

        if (msgSender == null)
        {
            FinishListening("final");
            LogFailure("VOICE_COMMAND", "MsgSender is not assigned.");
            return;
        }

        SendVoiceSnapshotOrFallback(rawTranscript, "VOICE_COMMAND");
        LogSuccess(sendSnapshotVoiceRequest ? "VOICE_SNAPSHOT" : "VOICE_COMMAND", rawTranscript);
        FinishListening("final");
    }

    void StartVoiceRequestContext()
    {
        if (!sendSnapshotVoiceRequest)
        {
            _activeVoiceRequest = null;
            return;
        }

        if (_snapshotRoutine != null)
        {
            StopCoroutine(_snapshotRoutine);
            _snapshotRoutine = null;
        }

        string requestId = Guid.NewGuid().ToString("N");
        if (msgSender != null)
        {
            string registered = msgSender.RegisterCaptureContextForRequest(requestId, "voice listening START");
            if (!string.IsNullOrEmpty(registered)) requestId = registered;
        }

        _activeVoiceRequest = new VoiceRequestContext
        {
            requestId = requestId,
            listenStartedTime = Time.unscaledTime,
            screenWidth = Screen.width,
            screenHeight = Screen.height,
        };

        _snapshotRoutine = StartCoroutine(CaptureVoiceStartSnapshot(_activeVoiceRequest));
        if (verboseLogging) Debug.Log($"[VoiceInputManager] voice request started request_id={requestId}");
    }

    IEnumerator CaptureVoiceStartSnapshot(VoiceRequestContext context)
    {
        yield return new WaitForEndOfFrame();

        Texture2D texture = null;
        try
        {
            texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null)
                throw new InvalidOperationException("CaptureScreenshotAsTexture returned null.");

            byte[] jpg = ImageConversion.EncodeToJPG(texture, Mathf.Clamp(snapshotJpegQuality, 1, 100));
            context.imageBase64 = Convert.ToBase64String(jpg);
            context.imageWidth = texture.width;
            context.imageHeight = texture.height;
            context.captureComplete = true;
            context.captureError = "";

            if (verboseLogging)
                Debug.Log($"[VoiceInputManager] captured voice-start screenshot request_id={context.requestId} image={context.imageWidth}x{context.imageHeight} jpg_bytes={jpg.Length}");
        }
        catch (Exception e)
        {
            context.captureComplete = true;
            context.captureError = e.Message;
            Debug.LogWarning($"[VoiceInputManager] voice-start screenshot failed request_id={context.requestId}: {e.Message}");
        }
        finally
        {
            if (texture != null) Destroy(texture);
            if (_snapshotRoutine != null) _snapshotRoutine = null;
        }
    }

    void SendVoiceSnapshotOrFallback(string transcript, string fallbackPacketType)
    {
        if (sendSnapshotVoiceRequest && _activeVoiceRequest != null)
        {
            StartCoroutine(PostVoiceSnapshotWhenReady(_activeVoiceRequest, transcript, fallbackPacketType));
            return;
        }

        SendLegacyTextPacket(transcript, fallbackPacketType);
    }

    IEnumerator PostVoiceSnapshotWhenReady(VoiceRequestContext context, string transcript, string fallbackPacketType)
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(0.05f, snapshotReadyTimeoutSeconds);
        while (!context.captureComplete && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (string.IsNullOrEmpty(context.imageBase64))
        {
            Debug.LogWarning($"[VoiceInputManager] voice snapshot unavailable request_id={context.requestId}; falling back to {fallbackPacketType}. captureError={context.captureError}");
            SendLegacyTextPacket(transcript, fallbackPacketType);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(voiceSnapshotServerUrl))
        {
            Debug.LogWarning("[VoiceInputManager] voiceSnapshotServerUrl is empty; falling back to legacy voice packet.");
            SendLegacyTextPacket(transcript, fallbackPacketType);
            yield break;
        }

        VoiceSnapshotPayload payload = new VoiceSnapshotPayload
        {
            request_id = context.requestId,
            requestId = context.requestId,
            source = "android_stt",
            gesture = "VoiceAsk",
            listen_started_unscaled_time = context.listenStartedTime,
            transcript_final_unscaled_time = Time.unscaledTime,
            screen_width = context.screenWidth,
            screen_height = context.screenHeight,
            transcript = transcript ?? "",
            image_mime = context.imageMime,
            image_base64 = context.imageBase64,
            image_width = context.imageWidth,
            image_height = context.imageHeight,
        };

        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest(voiceSnapshotServerUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");

            if (verboseLogging)
                Debug.Log($"[VoiceInputManager] POST voice snapshot request_id={context.requestId} bytes={body.Length} url={voiceSnapshotServerUrl}");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                if (verboseLogging)
                    Debug.Log($"[VoiceInputManager] voice snapshot POST accepted request_id={context.requestId} code={request.responseCode} body={request.downloadHandler.text}");
            }
            else
            {
                Debug.LogWarning($"[VoiceInputManager] voice snapshot POST failed request_id={context.requestId}: {request.error} ({request.result}); falling back to {fallbackPacketType}.");
                SendLegacyTextPacket(transcript, fallbackPacketType);
            }
        }
    }

    void SendLegacyTextPacket(string transcript, string packetType)
    {
        if (msgSender == null)
        {
            LogFailure(packetType, "MsgSender is not assigned.");
            return;
        }

        if (packetType == "ASK_QUESTION")
            msgSender.SendAskQuestion(transcript);
        else
            msgSender.SendVoiceCommand(transcript);
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
        if (!waitingForFinalTranscript) return;
        OnPartialTranscript?.Invoke(transcript ?? "");
        if (verboseLogging) Debug.Log($"[VoiceInputManager] partial transcript='{transcript}'");
    }

    void HandleSpeechError(int errorCode, string message)
    {
        if (!waitingForFinalTranscript)
        {
            Debug.LogWarning($"[VOICE_UI][WARN] ignored stale STT_ERROR code={errorCode} {message}");
            HideListeningUi("stale_error");
            return;
        }

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
        string uiReason = VoiceUiHideReason(reason);
        HideListeningUi(uiReason);
        if (wasListening)
            Debug.Log($"[VOICE_UI] listening panel hidden reason={uiReason}");
        if (wasListening) OnListeningStopped?.Invoke(reason ?? "");
    }

    string CurrentVoiceRequestId()
    {
        return _activeVoiceRequest != null && !string.IsNullOrEmpty(_activeVoiceRequest.requestId)
            ? _activeVoiceRequest.requestId
            : "";
    }

    static string VoiceUiHideReason(string reason)
    {
        if (reason == "final") return "transcript";
        return string.IsNullOrEmpty(reason) ? "cleanup" : reason;
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
        if (resultCardSpawner == null) resultCardSpawner = FindObjectOfType<ResultCardSpawner>();
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
