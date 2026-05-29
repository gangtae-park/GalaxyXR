using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

public class CalibSender : MonoBehaviour
{
    public static CalibSender Instance { get; private set; }

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

    private bool isCalibrationHoldActive = false;
    private int activeCalibrationDotIndex = -1;

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

    public void BeginCalibrationHold(int dotIndex)
    {
        isCalibrationHoldActive = true;
        activeCalibrationDotIndex = dotIndex;
        nextSendTime = 0f;

        Debug.Log($"[CalibSender] Begin calibration hold for {dotIndex}-th dot");
        SendControlPacket("BEGIN", dotIndex);
    }

    public void CancelCalibrationHold(int dotIndex)
    {
        if (!isCalibrationHoldActive || activeCalibrationDotIndex != dotIndex) return;

        Debug.Log($"[CalibSender] Cancel calibration hold for {dotIndex}-th dot");
        SendControlPacket("CANCEL", dotIndex);

        isCalibrationHoldActive = false;
        activeCalibrationDotIndex = -1;
    }

    public void CompleteCalibrationHold(int dotIndex)
    {
        if (!isCalibrationHoldActive || activeCalibrationDotIndex != dotIndex) return;

        Debug.Log($"[CalibSender] Complete calibration hold for {dotIndex}-th dot");
        SendControlPacket("COMPLETE", dotIndex);

        isCalibrationHoldActive = false;
        activeCalibrationDotIndex = -1;
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
            Debug.LogWarning("[CalibSender] EyeGazeReader not assigned.");
        }

        Camera referenceCamera = Camera.main;
        Vector3 localGazeDir = Vector3.zero;

        if (referenceCamera == null)
        {
            Debug.LogWarning("[CalibSender] Camera.main is null.");
        }
        else if (gazeDir.sqrMagnitude > 0f)
        {
            localGazeDir = referenceCamera.transform.InverseTransformDirection(gazeDir).normalized;
        }

        string packetType = isCalibrationHoldActive ? "SAMPLE" : "GAZE";
        int dotIndex = isCalibrationHoldActive ? activeCalibrationDotIndex : -1;

        string msg = string.Join(",",
            packetType,
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            (isTracked ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            dotIndex.ToString(CultureInfo.InvariantCulture),
            localGazeDir.x.ToString("F6", CultureInfo.InvariantCulture),
            localGazeDir.y.ToString("F6", CultureInfo.InvariantCulture),
            localGazeDir.z.ToString("F6", CultureInfo.InvariantCulture)
        );

        seq++;
        SendPacket(msg);
    }

    private void SendControlPacket(string eventType, int dotIndex)
    {
        string msg = string.Join(",",
            eventType,
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            dotIndex.ToString(CultureInfo.InvariantCulture)
        );

        seq++;
        SendPacket(msg);
    }

    private void SendPacket(string msg)
    {
        if (client == null) return;

        byte[] data = Encoding.UTF8.GetBytes(msg);
        client.Send(data, data.Length, serverIP, port);
    }
}