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
    public string serverIP = "192.168.0.3";
    public int port = 5005;

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
        client = new UdpClient();
        ResolveCaptureContextRegistry();
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

        string msg = string.Join(",",
            packetType,
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            (isTracked ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            localGazeDir.x.ToString("F6", CultureInfo.InvariantCulture),
            localGazeDir.y.ToString("F6", CultureInfo.InvariantCulture),
            localGazeDir.z.ToString("F6", CultureInfo.InvariantCulture)
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

        Debug.Log($"[Study Log][SENDER] Sending gesture event packet: {msg}");

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

        if (!msg.StartsWith("GAZE,"))
            Debug.Log($"[Study Log][SENDER] Packet sent to {serverIP}:{port} | {msg}");
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
