using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

public class MsgSender : MonoBehaviour
{
    [Serializable]
    public class GestureSignalPayload
    {
        public string gestureName;
        public string eventType;
    }

    public static MsgSender Instance { get; private set; }

    [Header("Network")]
    public string serverIP = "192.168.0.0";
    public int port = 5005;

    [Header("Assign from Input Actions")]
    public EyeGazeReader eyeGazeReader;
    public InputActionReference gazeRotationAction;
    public InputActionReference gazeTrackingStateAction;

    [Header("Send Rate")]
    public float sendHz = 30f;

    private UdpClient client;
    private float nextSendTime = 0f;
    private int seq = 0;

    void Start()
    {
        Instance = this;
        client = new UdpClient();
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
            Debug.LogWarning("[SENDER] EyeGazeReader not assigned.");
        }

        Camera referenceCamera = Camera.main;
        Vector3 localGazeDir = Vector3.zero;

        if (referenceCamera == null)
        {
            Debug.LogWarning("[SENDER] Camera.main is null.");
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

    public void SendCircleGesture(CircleGestureRecognizer.CircleGesturePayload payload)
    {
        if (payload == null)
        {
            Debug.LogWarning("[SENDER] SendCircleGesture called with null payload.");
            return;
        }

        SendGesturePacket(
            payload.gestureName
        );
    }

    public void SendGestureSignal(CircleGestureRecognizer.CircleGesturePayload payload)
    {
        SendCircleGesture(payload);
    }

    public void SendGestureEvent(CircleGestureRecognizer.CircleGesturePayload payload)
    {
        if (payload == null)
        {
            Debug.LogWarning("[SENDER] SendGestureEvent called with null payload.");
            return;
        }

        string safeGestureName = string.IsNullOrEmpty(payload.gestureName) ? "UnknownGesture" : payload.gestureName.Replace(",", "_");
        string safeEventType = string.IsNullOrEmpty(payload.eventType) ? "UNKNOWN" : payload.eventType.Replace(",", "_");

        string msg = string.Join(",",
            "GESTURE_EVENT",
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            safeGestureName,
            safeEventType
        );

        Debug.Log($"[SENDER] Sending gesture event packet: {msg}");

        seq++;
        SendPacket(msg);
    }

    public void SendGesturePacket(string gestureName)
    {
        string safeGestureName = string.IsNullOrEmpty(gestureName) ? "UnknownGesture" : gestureName.Replace(",", "_");

        string msg = string.Join(",",
            "GESTURE",
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            safeGestureName
        );

        Debug.Log($"[SENDER] Sending gesture packet: {msg}");

        seq++;
        SendPacket(msg);
    }

    /// <summary>
    /// Send a free-text user question for the most recent Ask gesture target.
    /// Wire format:  ASK_QUESTION,<seq>,<time>,<question (may contain commas)>
    /// Newlines are stripped so the packet stays a single line.
    /// </summary>
    public void SendAskQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            Debug.LogWarning("[SENDER] SendAskQuestion called with empty question.");
            return;
        }

        string safe = question.Replace("\r", " ").Replace("\n", " ");

        string msg = string.Join(",",
            "ASK_QUESTION",
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            safe
        );

        Debug.Log($"[SENDER] Sending ASK_QUESTION: {msg}");

        seq++;
        SendPacket(msg);
    }

    private void SendPacket(string msg)
    {
        if (client == null)
        {
            Debug.LogWarning("[SENDER] UDP client is null. Packet not sent.");
            return;
        }

        byte[] data = Encoding.UTF8.GetBytes(msg);
        client.Send(data, data.Length, serverIP, port);
        Debug.Log($"[SENDER] Packet sent to {serverIP}:{port} | {msg}");
    }
}