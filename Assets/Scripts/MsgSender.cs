using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

public class MsgSender : MonoBehaviour
{
    public static MsgSender Instance { get; private set; }

    [Header("Network")]
    [Tooltip("Optional explicit asset; when null, Resources/NetworkSettings.asset is used. The Mac address and port are configured ONLY through NetworkSettings -- there is no per-component override.")]
    public NetworkSettings networkSettings;
    // Resolved from NetworkSettings at Start. Deliberately NOT serialized so
    // the shared asset stays the single source of truth across scenes.
    private string serverIP = "192.168.0.3";
    private int port = 5005;

    /// <summary>Resolved MacProgram host (read-only; see NetworkSettings).</summary>
    public string ServerIP => serverIP;

    [Header("Assign from Input Actions")]
    public EyeGazeReader eyeGazeReader;
    public InputActionReference gazeRotationAction;
    public InputActionReference gazeTrackingStateAction;

    [Header("Send Rate")]
    public float sendHz = 30f;

    [Header("Capture Context")]
    public CaptureContextRegistry captureContextRegistry;
    public bool autoResolveCaptureContextRegistry = true;
    public bool registerCaptureContextOnGestureEvents = true;
    public bool registerCaptureContextOnTextCommands = true;
    [Tooltip("Keep disabled until the backend is ready to parse an extra request_id field in existing CSV packets.")]
    public bool appendRequestIdToOutgoingPackets = false;
    public int captureImageWidth = 0;
    public int captureImageHeight = 0;
    public float captureFallbackDistance = 1.2f;

    private UdpClient client;
    private float nextSendTime = 0f;
    private int seq = 0;
    private string lastRequestId = "";

    public string LastRequestId => lastRequestId;

    void Start()
    {
        Instance = this;
        ApplyNetworkSettings();
        client = new UdpClient();
        ResolveCaptureContextRegistry();
    }

    // Pull address + port from the shared NetworkSettings asset. Explicit
    // inspector assignment wins; otherwise Resources/NetworkSettings is
    // probed. When neither exists we warn and keep the built-in defaults.
    void ApplyNetworkSettings()
    {
        NetworkSettings settings = networkSettings != null ? networkSettings : NetworkSettings.Instance;
        if (settings == null)
        {
            Debug.LogWarning($"[Study Log][SENDER] NetworkSettings asset missing -- falling back to built-in default {serverIP}:{port}. Create Resources/NetworkSettings.asset.");
            return;
        }
        if (!string.IsNullOrEmpty(settings.serverIP)) serverIP = settings.serverIP;
        if (settings.commandUdpPort > 0) port = settings.commandUdpPort;
        Debug.Log($"[Study Log][SENDER] Network config from NetworkSettings: {serverIP}:{port}");
    }

    void OnEnable()
    {
        gazeRotationAction?.action.Enable();
        gazeTrackingStateAction?.action.Enable();
    }

    void OnDisable()
    {
        gazeRotationAction?.action.Disable();
        gazeTrackingStateAction?.action.Disable();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        client?.Close();
    }

    void Update()
    {
        if (Time.unscaledTime < nextSendTime) return;

        float interval = 1f / Mathf.Max(1f, sendHz);
        nextSendTime = Time.unscaledTime + interval;

        bool isTracked = false;
        Vector3 gazeDir = Vector3.zero;

        if (eyeGazeReader != null)
        {
            isTracked = eyeGazeReader.LatestIsTracked;
            gazeDir = eyeGazeReader.LatestGazeDirection;
        }
        else
        {
            Debug.LogWarning("[Study Log][SENDER] EyeGazeReader not assigned.");
        }

        Camera referenceCamera = Camera.main;
        Vector3 localGazeDir = Vector3.zero;

        if (referenceCamera == null)
        {
            Debug.LogWarning("[Study Log][SENDER] Camera.main is null.");
        }
        else if (gazeDir.sqrMagnitude > 0f)
        {
            localGazeDir = referenceCamera.transform.InverseTransformDirection(gazeDir).normalized;
        }

        string packetType = "GAZE";

        // Head rotation rides along so the Mac side can turn the camera-local
        // gaze back into a world ray and compensate head motion when mapping
        // the trail onto a gesture-start frame. Wire format:
        //   GAZE,seq,t,tracked,gx,gy,gz,qx,qy,qz,qw   (11 fields)
        // Old 7-field packets remain parseable on the receiver.
        Quaternion headRot = eyeGazeReader != null
            ? eyeGazeReader.LatestHeadRotation
            : (referenceCamera != null ? referenceCamera.transform.rotation : Quaternion.identity);

        string msg = string.Join(",",
            packetType,
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            (isTracked ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            localGazeDir.x.ToString("F6", CultureInfo.InvariantCulture),
            localGazeDir.y.ToString("F6", CultureInfo.InvariantCulture),
            localGazeDir.z.ToString("F6", CultureInfo.InvariantCulture),
            headRot.x.ToString("F5", CultureInfo.InvariantCulture),
            headRot.y.ToString("F5", CultureInfo.InvariantCulture),
            headRot.z.ToString("F5", CultureInfo.InvariantCulture),
            headRot.w.ToString("F5", CultureInfo.InvariantCulture)
        );

        seq++;
        SendPacket(msg);
    }

    public void SendGestureEvent(GestureEventPayload payload)
    {
        if (payload == null)
        {
            Debug.LogWarning("[Study Log][SENDER] SendGestureEvent called with null payload.");
            return;
        }

        string safeGestureName = string.IsNullOrEmpty(payload.gestureName) ? "UnknownGesture" : payload.gestureName.Replace(",", "_");
        string safeEventType = string.IsNullOrEmpty(payload.eventType) ? "UNKNOWN" : payload.eventType.Replace(",", "_");
        string requestId = registerCaptureContextOnGestureEvents
            ? RegisterCaptureContext($"gesture {safeGestureName}/{safeEventType}")
            : "";

        string msg = string.Join(",",
            "GESTURE_EVENT",
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            safeGestureName,
            safeEventType
        );
        if (appendRequestIdToOutgoingPackets && !string.IsNullOrEmpty(requestId))
            msg = string.Join(",", msg, requestId);

        // Debug.Log($"[Study Log][SENDER] Sending gesture event packet: {msg}");

        seq++;
        SendPacket(msg);
    }

    public void SendAskQuestion(string question)
    {
        SendTextPacket("ASK_QUESTION", question, "ASK_QUESTION");
    }

    public void SendVoiceTranscriptToLlm(string transcript)
    {
        SendVoiceCommand(transcript);
    }

    public void SendVoiceCommand(string transcript)
    {
        SendTextPacket("VOICE_COMMAND", transcript, "VOICE_COMMAND");
    }

    /// <summary>Bubble-menu action triggered against a specific object_id (or
    /// two ids for Compare). Python's object_action handler short-circuits
    /// detection and uses the DB record directly. requestId is the original
    /// /object_ui request id so Python can correlate cached anchors.</summary>
    public void SendObjectAction(string actionName, string objectId, string secondObjectId, string requestId)
    {
        string safeAction = SanitizeField(actionName, "UnknownAction");
        string safeObject = SanitizeField(objectId, "");
        string safeSecond = SanitizeField(secondObjectId, "");
        string safeRequest = SanitizeField(requestId, "");

        string msg = string.Join(",",
            "OBJECT_ACTION",
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            safeAction,
            safeObject,
            safeSecond,
            safeRequest
        );

        Debug.Log($"[Study Log][SENDER] Sending OBJECT_ACTION: {msg}");
        seq++;
        SendPacket(msg);
    }

    static string SanitizeField(string value, string fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return value.Replace(",", "_").Replace("\r", " ").Replace("\n", " ");
    }

    public string RegisterCaptureContextForRequest(string requestId, string reason)
    {
        return RegisterCaptureContextForRequest(requestId, reason, captureImageWidth, captureImageHeight, Camera.main);
    }

    public string RegisterCaptureContextForRequest(string requestId, string reason, int imageWidth, int imageHeight, Camera referenceCamera)
    {
        ResolveCaptureContextRegistry();
        if (captureContextRegistry == null)
        {
            Debug.LogWarning("[CaptureContext] registry unavailable; outgoing command will not have a local capture context.");
            return "";
        }

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        lastRequestId = captureContextRegistry.Register(requestId, imageWidth, imageHeight, cam, captureFallbackDistance);
        Debug.Log($"[CaptureContext] command context registered reason={reason} request_id={lastRequestId} append_to_packet={appendRequestIdToOutgoingPackets}");
        return lastRequestId;
    }

    public string RegisterCaptureSnapshotForRequest(
        string requestId,
        string reason,
        int imageWidth,
        int imageHeight,
        int screenWidth,
        int screenHeight,
        Vector3 cameraPosition,
        Quaternion cameraRotation,
        Matrix4x4 projectionMatrix,
        float verticalFov,
        float aspect,
        bool orthographic,
        float orthographicSize,
        float contextMaxAgeSeconds = 0f)
    {
        ResolveCaptureContextRegistry();
        if (captureContextRegistry == null)
        {
            Debug.LogWarning("[CaptureContext] registry unavailable; outgoing command will not have a local capture context.");
            return "";
        }

        if (contextMaxAgeSeconds > 0f)
            captureContextRegistry.maxContextAgeSeconds = Mathf.Max(captureContextRegistry.maxContextAgeSeconds, contextMaxAgeSeconds);

        lastRequestId = captureContextRegistry.RegisterSnapshot(
            requestId,
            imageWidth,
            imageHeight,
            screenWidth,
            screenHeight,
            cameraPosition,
            cameraRotation,
            projectionMatrix,
            verticalFov,
            aspect,
            orthographic,
            orthographicSize,
            captureFallbackDistance);
        Debug.Log($"[CaptureContext] snapshot context registered reason={reason} request_id={lastRequestId} camera={cameraPosition}");
        return lastRequestId;
    }

    /// <summary>User-study milestone (session_start at the beep, result card
    /// spawn, input_start, ...). Wire format: STUDY,EVENT,&lt;name&gt;,&lt;detail&gt; --
    /// MacProgram's study logger timestamps it into the session CSV on arrival.</summary>
    public void SendStudyEvent(string eventName, string detail = "")
    {
        if (string.IsNullOrEmpty(eventName)) return;
        string safeName = eventName.Replace(",", ";").Replace("\n", " ");
        string safeDetail = (detail ?? "").Replace(",", ";").Replace("\n", " ");
        SendPacket(string.Join(",", "STUDY", "EVENT", safeName, safeDetail));
    }

    void SendTextPacket(string packetType, string text, string logLabel)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Debug.LogWarning($"[Study Log][SENDER] {logLabel} called with empty text.");
            return;
        }

        string safe = text.Replace("\r", " ").Replace("\n", " ").Replace(",", " ");
        string requestId = registerCaptureContextOnTextCommands
            ? RegisterCaptureContext(packetType)
            : "";

        string msg = string.Join(",",
            packetType,
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            safe
        );
        if (appendRequestIdToOutgoingPackets && !string.IsNullOrEmpty(requestId))
            msg = string.Join(",", msg, requestId);

        Debug.Log($"[Study Log][SENDER] Sending {logLabel}: {msg}");

        seq++;
        SendPacket(msg);
    }

    private void SendPacket(string msg)
    {
        if (client == null)
        {
            Debug.LogWarning("[Study Log][SENDER] UDP client is null. Packet not sent.");
            return;
        }

        byte[] data = Encoding.UTF8.GetBytes(msg);
        client.Send(data, data.Length, serverIP, port);

        // if (!msg.StartsWith("GAZE,"))
        //     Debug.Log($"[Study Log][SENDER] Packet sent to {serverIP}:{port} | {msg}");
    }

    string RegisterCaptureContext(string reason)
    {
        return RegisterCaptureContextForRequest("", reason);
    }

    void ResolveCaptureContextRegistry()
    {
        if (!autoResolveCaptureContextRegistry || captureContextRegistry != null) return;
        captureContextRegistry = CaptureContextRegistry.EnsureInstance();
    }
}
