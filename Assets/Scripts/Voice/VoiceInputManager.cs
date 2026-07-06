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
    public Camera referenceCamera;
    public EyeGazeReader eyeGazeReader;

    [Header("Listening")]
    public float listenTimeoutSeconds = 10f;
    public bool autoResolveReferences = true;
    public bool verboseLogging = true;

    [Header("Backend packet")]
    [Tooltip("VOICE_COMMAND tells the backend to pair this transcript with the current frame/object context.")]
    public bool sendVoiceCommandPacket = true;
    [Tooltip("When an Ask gesture card is waiting, use the transcript as that card's question instead of a standalone voice command.")]
    public bool routePendingAskCard = true;
    [Tooltip("POST one JSON request containing the voice-start pose and final transcript. Python's /voice_command handler pairs the transcript with the ADB-stream frame captured at listen-start. Falls back to the legacy UDP VOICE_COMMAND only when disabled.")]
    public bool sendSnapshotVoiceRequest = true;
    [Tooltip("Optional. When assigned (or Resources/NetworkSettings.asset exists), the endpoint is derived from that shared asset; voiceSnapshotServerUrl below is used only as a legacy fallback.")]
    public NetworkSettings networkSettings;
    public string voiceSnapshotServerUrl = "http://192.168.0.3:5007/voice_command";
    public float voiceContextMaxAgeSeconds = 120f;

    [Header("Status")]
    [SerializeField] private bool waitingForFinalTranscript;
    [SerializeField] private string lastTranscript;

    private Coroutine _timeoutRoutine;
    private VoiceRequestContext _activeVoiceRequest;
    public bool IsListening => waitingForFinalTranscript || (speechBridge != null && speechBridge.IsListening);
    public string LastTranscript => lastTranscript;

    public event Action<InputMode> OnListeningStarted;
    public event Action<string> OnListeningStopped;
    public event Action<string> OnPartialTranscript;
    public event Action<string> OnFinalTranscript;
    public event Action<string> OnVoiceError;

    // Python's /voice_command handler no longer needs Unity to ship a JPG:
    // Unity's ScreenCapture on Android XR only sees its own rendered output
    // (passthrough video is OS-composited and never reaches the framebuffer),
    // so the LLM was reasoning over a mostly empty scene. Instead we send the
    // camera pose captured at listen-start; the Python side pairs the
    // transcript with the ADB screenrecord frame it's already streaming from
    // the device -- the same source the gesture handlers use.
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
        // image_* intentionally empty: Python uses its ADB-stream frame.
        public string image_mime;
        public string image_base64;
        public int image_width;
        public int image_height;
        public float camera_pos_x;
        public float camera_pos_y;
        public float camera_pos_z;
        public float camera_rot_x;
        public float camera_rot_y;
        public float camera_rot_z;
        public float camera_rot_w;
        // Eye-gaze snapshot at listen-start. Normalized viewport coordinates
        // (0..1, Unity convention: origin bottom-left). Python translates
        // these to ADB frame pixels and injects them into the VLM prompt so
        // GPT-5 can prioritise the gaze target when resolving pronouns --
        // this is the core mechanism from GazePointAR (Lee et al., CHI '24).
        public bool gaze_tracked;
        public float gaze_viewport_x;
        public float gaze_viewport_y;
    }

    struct CapturePoseSnapshot
    {
        public int screenWidth;
        public int screenHeight;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public Matrix4x4 projectionMatrix;
        public float verticalFov;
        public float aspect;
        public bool orthographic;
        public float orthographicSize;

        public static CapturePoseSnapshot From(Camera cam)
        {
            return new CapturePoseSnapshot
            {
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                cameraPosition = cam != null ? cam.transform.position : Vector3.zero,
                cameraRotation = cam != null ? cam.transform.rotation : Quaternion.identity,
                projectionMatrix = cam != null ? cam.projectionMatrix : Matrix4x4.identity,
                verticalFov = cam != null ? cam.fieldOfView : 60f,
                aspect = cam != null ? cam.aspect : (Screen.height > 0 ? Screen.width / (float)Screen.height : 1.777f),
                orthographic = cam != null && cam.orthographic,
                orthographicSize = cam != null ? cam.orthographicSize : 5f
            };
        }
    }

    class VoiceRequestContext
    {
        public string requestId;
        public float listenStartedTime;
        public CapturePoseSnapshot capturePose;
        public bool gazeTracked;
        public Vector2 gazeViewport;
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
        _activeVoiceRequest = null;
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

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        CapturePoseSnapshot pose = CapturePoseSnapshot.From(cam);
        CaptureGazeViewport(cam, out Vector2 gazeViewport, out bool gazeTracked);

        string requestId = Guid.NewGuid().ToString("N");
        if (msgSender != null)
        {
            // Register the listen-start camera pose so downstream anchor
            // resolvers (VLM result handlers) can project response bboxes
            // back into the world using the pose the user was looking with
            // when they started speaking. image_width/height are 0 because
            // the actual frame ships from Python's ADB stream and its dims
            // come back in the response.
            string registered = msgSender.RegisterCaptureSnapshotForRequest(
                requestId,
                "voice listening START",
                0,
                0,
                pose.screenWidth,
                pose.screenHeight,
                pose.cameraPosition,
                pose.cameraRotation,
                pose.projectionMatrix,
                pose.verticalFov,
                pose.aspect,
                pose.orthographic,
                pose.orthographicSize,
                voiceContextMaxAgeSeconds);
            if (!string.IsNullOrEmpty(registered)) requestId = registered;
        }

        _activeVoiceRequest = new VoiceRequestContext
        {
            requestId = requestId,
            listenStartedTime = Time.unscaledTime,
            capturePose = pose,
            gazeTracked = gazeTracked,
            gazeViewport = gazeViewport,
        };

        if (verboseLogging) Debug.Log($"[VoiceInputManager] voice request started request_id={requestId} camera_pos=({pose.cameraPosition.x:F3},{pose.cameraPosition.y:F3},{pose.cameraPosition.z:F3}) gaze_tracked={gazeTracked} gaze_viewport=({gazeViewport.x:F3},{gazeViewport.y:F3})");
    }

    // Ports ObjectUiRequestManager.CaptureGazeViewport so voice_command carries
    // the same normalised gaze coordinate. GazePointAR's key insight is that
    // *explicit* gaze information disambiguates pronouns better than any
    // "look at the center" heuristic; without this Python could only guess.
    void CaptureGazeViewport(Camera cam, out Vector2 viewport, out bool hasViewport)
    {
        viewport = new Vector2(0.5f, 0.5f);
        hasViewport = false;
        if (cam == null || eyeGazeReader == null || !eyeGazeReader.LatestIsTracked)
            return;

        Vector3 gazeDir = eyeGazeReader.LatestGazeDirection;
        if (gazeDir.sqrMagnitude < 0.0001f) return;

        Vector3 world = cam.transform.position + gazeDir.normalized * 1.2f;
        Vector3 vp = cam.WorldToViewportPoint(world);
        if (vp.z <= 0f) return;

        viewport = new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));
        hasViewport = true;
    }

    void SendVoiceSnapshotOrFallback(string transcript, string fallbackPacketType)
    {
        if (sendSnapshotVoiceRequest && _activeVoiceRequest != null)
        {
            StartCoroutine(PostVoiceRequest(_activeVoiceRequest, transcript, fallbackPacketType));
            return;
        }

        SendLegacyTextPacket(transcript, fallbackPacketType);
    }

    IEnumerator PostVoiceRequest(VoiceRequestContext context, string transcript, string fallbackPacketType)
    {
        string url = ResolveVoiceCommandUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            Debug.LogWarning("[VoiceInputManager] voice command URL unresolved; falling back to legacy voice packet.");
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
            screen_width = context.capturePose.screenWidth,
            screen_height = context.capturePose.screenHeight,
            transcript = transcript ?? "",
            // Intentionally empty: Python's /voice_command handler uses its
            // ADB-stream frame (state.latest_frame) and reports actual dims
            // in the VLM response.
            image_mime = "",
            image_base64 = "",
            image_width = 0,
            image_height = 0,
            camera_pos_x = context.capturePose.cameraPosition.x,
            camera_pos_y = context.capturePose.cameraPosition.y,
            camera_pos_z = context.capturePose.cameraPosition.z,
            camera_rot_x = context.capturePose.cameraRotation.x,
            camera_rot_y = context.capturePose.cameraRotation.y,
            camera_rot_z = context.capturePose.cameraRotation.z,
            camera_rot_w = context.capturePose.cameraRotation.w,
            gaze_tracked = context.gazeTracked,
            gaze_viewport_x = context.gazeViewport.x,
            gaze_viewport_y = context.gazeViewport.y,
        };

        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");

            if (verboseLogging)
                Debug.Log($"[VoiceInputManager] POST voice request request_id={context.requestId} bytes={body.Length} url={url}");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                if (verboseLogging)
                    Debug.Log($"[VoiceInputManager] voice request POST accepted request_id={context.requestId} code={request.responseCode} body={request.downloadHandler.text}");
            }
            else
            {
                Debug.LogWarning($"[VoiceInputManager] voice request POST failed request_id={context.requestId}: {request.error} ({request.result}); falling back to {fallbackPacketType}.");
                SendLegacyTextPacket(transcript, fallbackPacketType);
            }
        }
    }

    string ResolveVoiceCommandUrl()
    {
        NetworkSettings s = networkSettings != null ? networkSettings : NetworkSettings.Instance;
        if (s != null) return s.VoiceCommandUrl;
        return voiceSnapshotServerUrl;
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
        if (referenceCamera == null) referenceCamera = Camera.main;
        if (eyeGazeReader == null) eyeGazeReader = FindObjectOfType<EyeGazeReader>();
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
