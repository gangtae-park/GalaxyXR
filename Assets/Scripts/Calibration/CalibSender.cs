using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

/*
CalibSender

Streams gaze samples to MacProgram during calibration. Two packet shapes:

  - GAZE  : continuous (sendHz) idle stream when no dot hold is active.
  - SAMPLE: same shape but with the active dot index, only sent while a
            CalibrationDot is being held (BeginCalibrationHold called and not
            yet cancelled or completed).

Plus 3 control packets bracketing a hold:
  BEGIN,seq,t,dotIdx        -- sent when the user starts a long-pinch
  CANCEL,seq,t,dotIdx       -- sent if they release before completion
  COMPLETE,seq,t,dotIdx     -- sent at the 1-second mark

And 3 packets for the post-calibration random saccadic evaluation task
(SaccadicTaskController). u/v are the fixation position normalized to the
calibration grid extent (u: 0=left col .. 1=right col, v: 0=top row .. 1=
bottom row) so the Python side can interpolate the expected on-screen point
from the known 9-dot norm targets:
  SACCADE_BEGIN,seq,t,numFixations
  SACCADE_FIX,seq,t,fixIndex,u,v
  SACCADE_END,seq,t,numShown

Note: this stream is independent of MsgSender. CalibrationScene should NOT
have a MsgSender in the scene, otherwise both will spam GAZE packets in
parallel.
*/

public class CalibSender : MonoBehaviour
{
    public static CalibSender Instance { get; private set; }

    [Header("Network")]
    [Tooltip("Optional explicit asset; when null, Resources/NetworkSettings.asset is used. The Mac address and port come ONLY from NetworkSettings.")]
    public NetworkSettings networkSettings;
    private string serverIP = "192.168.0.8";
    private int port = 5005;

    [Header("Refs")]
    public EyeGazeReader eyeGazeReader;

    [Header("Send Rate")]
    public float sendHz = 30f;

    [Header("Logging")]
    public bool verboseLogging = false;

    private UdpClient client;
    private float nextSendTime = 0f;
    private int seq = 0;

    private bool isCalibrationHoldActive = false;
    private int activeCalibrationDotIndex = -1;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        ApplyNetworkSettings();
        if (client == null) client = new UdpClient();
    }

    void ApplyNetworkSettings()
    {
        NetworkSettings settings = networkSettings != null ? networkSettings : NetworkSettings.Instance;
        if (settings == null)
        {
            Debug.LogWarning($"[CalibSender] NetworkSettings asset missing -- falling back to built-in default {serverIP}:{port}. Create Resources/NetworkSettings.asset.");
            return;
        }
        if (!string.IsNullOrEmpty(settings.serverIP)) serverIP = settings.serverIP;
        if (settings.commandUdpPort > 0) port = settings.commandUdpPort;
        Debug.Log($"[CalibSender] Network config from NetworkSettings: {serverIP}:{port}");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        client?.Close();
        client = null;
    }

    // ---------- Hold lifecycle (called from CalibrationDot) ----------

    public void BeginCalibrationHold(int dotIndex)
    {
        isCalibrationHoldActive = true;
        activeCalibrationDotIndex = dotIndex;
        nextSendTime = 0f;   // start SAMPLE stream immediately
        Debug.Log($"[CalibSender] BEGIN hold dot={dotIndex}");
        SendControlPacket("BEGIN", dotIndex);
    }

    public void CancelCalibrationHold(int dotIndex)
    {
        if (!isCalibrationHoldActive || activeCalibrationDotIndex != dotIndex) return;
        Debug.Log($"[CalibSender] CANCEL hold dot={dotIndex}");
        SendControlPacket("CANCEL", dotIndex);
        isCalibrationHoldActive = false;
        activeCalibrationDotIndex = -1;
    }

    public void CompleteCalibrationHold(int dotIndex)
    {
        if (!isCalibrationHoldActive || activeCalibrationDotIndex != dotIndex) return;
        Debug.Log($"[CalibSender] COMPLETE hold dot={dotIndex}");
        SendControlPacket("COMPLETE", dotIndex);
        isCalibrationHoldActive = false;
        activeCalibrationDotIndex = -1;
    }

    // ---------- Saccadic evaluation task (called from SaccadicTaskController) ----------

    public void SendSaccadeBegin(int numFixations)
    {
        Debug.Log($"[CalibSender] SACCADE_BEGIN fixations={numFixations}");
        SendSimplePacket("SACCADE_BEGIN", numFixations.ToString(CultureInfo.InvariantCulture));
    }

    public void SendSaccadeFixation(int fixIndex, float u, float v)
    {
        SendSimplePacket("SACCADE_FIX",
            fixIndex.ToString(CultureInfo.InvariantCulture),
            u.ToString("F4", CultureInfo.InvariantCulture),
            v.ToString("F4", CultureInfo.InvariantCulture));
    }

    public void SendSaccadeEnd(int numShown)
    {
        Debug.Log($"[CalibSender] SACCADE_END shown={numShown}");
        SendSimplePacket("SACCADE_END", numShown.ToString(CultureInfo.InvariantCulture));
    }

    void SendSimplePacket(string eventType, params string[] fields)
    {
        string msg = string.Join(",",
            eventType,
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture));
        if (fields.Length > 0) msg = msg + "," + string.Join(",", fields);
        seq++;
        SendPacket(msg);
    }

    // ---------- Per-frame stream ----------

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
        else if (verboseLogging)
        {
            Debug.LogWarning("[CalibSender] EyeGazeReader not assigned.");
        }

        Camera referenceCamera = Camera.main;
        Vector3 localGazeDir = Vector3.zero;

        if (referenceCamera == null)
        {
            if (verboseLogging) Debug.LogWarning("[CalibSender] Camera.main is null.");
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

    // ---------- Wire ----------

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
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(msg);
            client.Send(data, data.Length, serverIP, port);
            // SAMPLE/GAZE 패킷은 30Hz라 logcat 폭주를 피하기 위해 control 패킷만 로그.
            if (verboseLogging && !msg.StartsWith("GAZE,") && !msg.StartsWith("SAMPLE,"))
                Debug.Log($"[CalibSender] sent {msg}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[CalibSender] send failed: {e.Message}");
        }
    }
}
