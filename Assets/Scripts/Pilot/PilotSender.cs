using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/*
PilotSender

Streams gaze + both-hand joint data to MacProgram/pilot_receiver.py during the
pilot data-collection study (DataCollectionScene). Modeled on CalibSender /
MsgSender but trimmed to only what the pilot needs.

Packets (CSV over UDP, one datagram each):

  PILOT_SAMPLE,seq,t,in_trial,gaze_tracked,gx,gy,gz,
               head_px,head_py,head_pz,head_qx,head_qy,head_qz,head_qw,
               left_tracked,right_tracked,
               <25 left joints  x,y,z>, <25 right joints x,y,z>
      - sent continuously at sendHz (also outside a trial, so the Mac side can
        show a live gaze preview and confirm connectivity before recording).
      - gaze dir and hand joint positions are CAMERA-LOCAL (same convention as
        MsgSender's GAZE packet, i.e. what the ridge mapping was trained on).
        Hand joints: p_local = camRot^-1 * (p_world - cam_pos).
      - head pose is WORLD-space, so world positions are reconstructible.
      - untracked gaze/hand fields are zero.

  PILOT_BEGIN,seq,t,participantId,referent,trialIndex   -- trial window opens
  PILOT_END,seq,t,participantId,referent,trialIndex     -- trial window closes

Joint order per hand (25 points):
  wrist,
  thumb:  Metacarpal, Proximal, Distal, Tip            (cmc, mcp, ip, tip)
  index / middle / ring / little:
          Metacarpal, Proximal, Intermediate, Distal, Tip
          (meta, mcp, pip, dip, tip)
The finger Metacarpals are included so HandPoseRecognizer's extension/curl
ratios (which anchor at the metacarpal) can be reproduced exactly from the
CSV for parameter tuning. Jackknife template export drops them again to stay
compatible with the 21-joint (63-value) template format.

NOTE: DataCollectionScene should NOT also contain a MsgSender, otherwise both
will stream gaze packets to the same port in parallel.
*/

public class PilotSender : MonoBehaviour
{
    public static PilotSender Instance { get; private set; }

    [Header("Network")]
    [Tooltip("Optional explicit asset; when null, Resources/NetworkSettings.asset is used. The Mac address and port come ONLY from NetworkSettings.")]
    public NetworkSettings networkSettings;
    private string serverIP = "192.168.0.8";
    private int port = 5005;

    [Header("Refs")]
    public EyeGazeReader eyeGazeReader;

    [Header("Send Rate")]
    public float sendHz = 60f;

    [Header("Logging")]
    public bool verboseLogging = false;

    public bool TrialActive { get; private set; }

    // 25 joints per hand (see file header for the rationale).
    static readonly XRHandJointID[] JointsPerHand =
    {
        XRHandJointID.Wrist,
        XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
        XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
        XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
        XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
        XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip,
    };

    private UdpClient client;
    private float nextSendTime = 0f;
    private int seq = 0;

    private int activeParticipant = -1;
    private string activeReferent = "";
    private int activeTrialIndex = -1;

    private XRHandSubsystem _handSubsystem;
    private static List<XRHandSubsystem> s_subs;
    private readonly StringBuilder _sb = new StringBuilder(2048);

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        ApplyNetworkSettings();
        client = new UdpClient();
        TryGetHandSubsystem(out _handSubsystem);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        client?.Close();
        client = null;
    }

    void ApplyNetworkSettings()
    {
        NetworkSettings settings = networkSettings != null ? networkSettings : NetworkSettings.Instance;
        if (settings == null)
        {
            Debug.LogWarning($"[PilotSender] NetworkSettings asset missing -- falling back to built-in default {serverIP}:{port}. Create Resources/NetworkSettings.asset.");
            return;
        }
        if (!string.IsNullOrEmpty(settings.serverIP)) serverIP = settings.serverIP;
        if (settings.commandUdpPort > 0) port = settings.commandUdpPort;
        Debug.Log($"[PilotSender] Network config from NetworkSettings: {serverIP}:{port}");
    }

    // ---------- Trial lifecycle (called from PilotStudyController) ----------

    public void BeginTrial(int participantId, string referent, int trialIndex)
    {
        activeParticipant = participantId;
        activeReferent = SanitizeField(referent, "Unknown");
        activeTrialIndex = trialIndex;
        TrialActive = true;
        nextSendTime = 0f;   // sample immediately at window start
        Debug.Log($"[PilotSender] BEGIN P{participantId} referent={activeReferent} trial={trialIndex}");
        SendControlPacket("PILOT_BEGIN");
    }

    public void EndTrial()
    {
        if (!TrialActive) return;
        Debug.Log($"[PilotSender] END P{activeParticipant} referent={activeReferent} trial={activeTrialIndex}");
        SendControlPacket("PILOT_END");
        TrialActive = false;
        activeParticipant = -1;
        activeReferent = "";
        activeTrialIndex = -1;
    }

    // ---------- Per-frame stream ----------

    void Update()
    {
        if (Time.unscaledTime < nextSendTime) return;

        float interval = 1f / Mathf.Max(1f, sendHz);
        nextSendTime = Time.unscaledTime + interval;

        Camera cam = Camera.main;
        if (cam == null)
        {
            if (verboseLogging) Debug.LogWarning("[PilotSender] Camera.main is null.");
            return;
        }

        Transform camT = cam.transform;
        Quaternion invCamRot = Quaternion.Inverse(camT.rotation);

        // --- Gaze (camera-local direction, same as MsgSender / ridge input) ---
        bool gazeTracked = false;
        Vector3 localGazeDir = Vector3.zero;
        if (eyeGazeReader != null)
        {
            gazeTracked = eyeGazeReader.LatestIsTracked;
            Vector3 gazeDir = eyeGazeReader.LatestGazeDirection;
            if (gazeTracked && gazeDir.sqrMagnitude > 0f)
                localGazeDir = (invCamRot * gazeDir).normalized;
        }
        else if (verboseLogging)
        {
            Debug.LogWarning("[PilotSender] EyeGazeReader not assigned.");
        }

        // --- Hands ---
        if (_handSubsystem == null) TryGetHandSubsystem(out _handSubsystem);
        bool leftTracked = false, rightTracked = false;

        _sb.Length = 0;
        _sb.Append("PILOT_SAMPLE,");
        _sb.Append(seq.ToString(CultureInfo.InvariantCulture)).Append(',');
        _sb.Append(Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
        _sb.Append(TrialActive ? '1' : '0').Append(',');
        _sb.Append(gazeTracked ? '1' : '0').Append(',');
        AppendVec3(localGazeDir, "F6");
        AppendVec3(camT.position, "F4");
        Quaternion q = camT.rotation;
        AppendF(q.x, "F5"); AppendF(q.y, "F5"); AppendF(q.z, "F5"); AppendF(q.w, "F5");

        if (_handSubsystem != null)
        {
            leftTracked = _handSubsystem.leftHand.isTracked;
            rightTracked = _handSubsystem.rightHand.isTracked;
        }
        _sb.Append(leftTracked ? '1' : '0').Append(',');
        _sb.Append(rightTracked ? '1' : '0').Append(',');

        AppendHandJoints(_handSubsystem != null ? _handSubsystem.leftHand : default, leftTracked, camT.position, invCamRot);
        AppendHandJoints(_handSubsystem != null ? _handSubsystem.rightHand : default, rightTracked, camT.position, invCamRot);

        // strip trailing comma
        if (_sb[_sb.Length - 1] == ',') _sb.Length -= 1;

        seq++;
        SendPacket(_sb.ToString());
    }

    void AppendHandJoints(XRHand hand, bool tracked, Vector3 camPos, Quaternion invCamRot)
    {
        for (int i = 0; i < JointsPerHand.Length; i++)
        {
            Vector3 local = Vector3.zero;
            if (tracked && hand.GetJoint(JointsPerHand[i]).TryGetPose(out Pose pose))
                local = invCamRot * (pose.position - camPos);
            AppendVec3(local, "F4");
        }
    }

    void AppendVec3(Vector3 v, string fmt)
    {
        AppendF(v.x, fmt); AppendF(v.y, fmt); AppendF(v.z, fmt);
    }

    void AppendF(float f, string fmt)
    {
        _sb.Append(f.ToString(fmt, CultureInfo.InvariantCulture)).Append(',');
    }

    // ---------- Wire ----------

    void SendControlPacket(string eventType)
    {
        string msg = string.Join(",",
            eventType,
            seq.ToString(CultureInfo.InvariantCulture),
            Time.unscaledTime.ToString("F4", CultureInfo.InvariantCulture),
            activeParticipant.ToString(CultureInfo.InvariantCulture),
            activeReferent,
            activeTrialIndex.ToString(CultureInfo.InvariantCulture)
        );
        seq++;
        SendPacket(msg);
    }

    void SendPacket(string msg)
    {
        if (client == null) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(msg);
            client.Send(data, data.Length, serverIP, port);
            if (verboseLogging && !msg.StartsWith("PILOT_SAMPLE,"))
                Debug.Log($"[PilotSender] sent {msg}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[PilotSender] send failed: {e.Message}");
        }
    }

    static string SanitizeField(string value, string fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return value.Replace(",", "_").Replace("\r", " ").Replace("\n", " ");
    }

    static bool TryGetHandSubsystem(out XRHandSubsystem sub)
    {
        s_subs ??= new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(s_subs);
        for (int i = 0; i < s_subs.Count; i++)
        {
            if (s_subs[i].running) { sub = s_subs[i]; return true; }
        }
        sub = s_subs.Count > 0 ? s_subs[0] : null;
        return sub != null;
    }
}
