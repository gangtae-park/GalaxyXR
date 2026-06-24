using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/*
HandPoseRecognizer

Per-frame geometric pose recognition for the non-pinch referent gestures
("Save" and, later, "Capture"). This is intentionally NOT Jackknife-based --
those referents are static postures (optionally followed by a small repeated
motion), and template-based trajectory matching is the wrong tool.

The recognizer fetches an XRHandSubsystem from the running subsystems and on
every Evaluate() returns one of:

  None        : neither posture matches (or hands not tracked)
  SaveEntry   : LEFT palm fully open and facing world-up. Right hand free.
  Save        : SaveEntry  +  the right index TIP is close to the left palm
                AND its lateral motion across the palm width axis has reversed
                direction at least N times within a short sliding window
                (a "scribble" pattern). The right hand's *pose* (open, pinched,
                fisted, ...) is intentionally unconstrained -- only the index
                tip's motion on the palm matters.
  CameraPose  : reserved for the future Capture gesture; currently always
                returns None until the Capture pose checks are added here.

GestureRouter is the polling driver; this component is stateless about the
gesture lifecycle (START/END/FAIL is the router's job). The only state we keep
internally is the small ring of recent right-index-tip lateral samples needed
to count scribble reversals.
*/

public enum HandPoseKind { None, SaveEntry, Save, CameraPose }

public class HandPoseRecognizer : MonoBehaviour
{
    [Header("Finger extension (ratio of straight to segmented length, 0..1)")]
    [Tooltip("Finger is 'extended' when straight/segmented >= this. 1.0 = perfectly straight.")]
    [Range(0.6f, 1f)] public float fingerExtendedRatio = 0.90f;

    [Header("Palm orientation (Save entry)")]
    [Tooltip("Dot(palmNormal, world up) must be >= this for palm to count as facing up.")]
    [Range(0f, 1f)] public float palmUpDotThreshold = 0.68f;

    [Header("Scribble (Save)")]
    [Tooltip("Sliding window in seconds over which lateral direction reversals are counted.")]
    public float scribbleWindowSeconds = 1.5f;
    [Tooltip("Right index tip must stay within this perpendicular distance from the left palm plane (meters).")]
    public float scribbleMaxPalmDistance = 0.06f;
    [Tooltip("Minimum number of lateral direction reversals required within the window.")]
    public int scribbleMinReversals = 3;
    [Tooltip("Minimum total lateral travel within the window (meters), to reject tiny jitter.")]
    public float scribbleMinLateralTravel = 0.03f;
    [Tooltip("Per-sample dead band: lateral deltas below this are ignored when counting reversals.")]
    public float scribbleJitterDeadband = 0.002f;

    [Header("Status (read-only)")]
    [SerializeField] private HandPoseKind lastResult;
    [SerializeField] private bool leftHandTracked;
    [SerializeField] private bool rightHandTracked;
    [SerializeField] private bool leftPalmOpen;
    [SerializeField] private bool leftPalmFacingUp;
    [SerializeField] private float indexPalmDistance = -1f;
    [SerializeField] private int scribbleReversals;
    [SerializeField] private float scribbleLateralTravel;

    public HandPoseKind LastResult => lastResult;
    /// <summary>Number of right-index scribble samples currently held in the
    /// sliding window. Used by GestureRouter as a "scribble in progress?"
    /// signal -- when this is &gt; 0 the router refreshes its SaveEntry
    /// timeout so a slow scribble isn't cancelled mid-motion.</summary>
    public int CurrentScribbleSampleCount => _scribble.Count;

    private XRHandSubsystem _handSubsystem;

    private struct ScribbleSample { public float t; public float u; }
    private readonly List<ScribbleSample> _scribble = new List<ScribbleSample>(64);

    void OnEnable()
    {
        TryGetHandSubsystem(out _handSubsystem);
    }

    /// <summary>
    /// Snapshot the current hand poses and decide which referent pose (if any)
    /// is being held. Cheap enough to call at ~10 Hz from GestureRouter.
    /// </summary>
    public HandPoseKind Evaluate()
    {
        if (_handSubsystem == null && !TryGetHandSubsystem(out _handSubsystem))
            return Reset(HandPoseKind.None);

        XRHand left = _handSubsystem.leftHand;
        XRHand right = _handSubsystem.rightHand;
        leftHandTracked = left.isTracked;
        rightHandTracked = right.isTracked;

        if (!left.isTracked || !right.isTracked)
            return Reset(HandPoseKind.None);

        // ---- Stage 1: left palm fully open + facing up ----
        leftPalmOpen = IsHandFullyOpen(left);
        if (!leftPalmOpen) return Reset(HandPoseKind.None);

        // Palm normal direction follows the XR Hands authoritative convention from
        // XRHandOrientationUtility.GetHandAxisDirection:
        //   PalmDirection = palmJointRotation * (0, -1, 0)   (local -Y of the palm joint),
        // pointing OUT of the palm side regardless of handedness.
        if (!left.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose leftPalmPose))
            return Reset(HandPoseKind.None);
        Vector3 palmCenter = leftPalmPose.position;
        Vector3 palmNormal = leftPalmPose.rotation * new Vector3(0f, -1f, 0f);
        leftPalmFacingUp = Vector3.Dot(palmNormal, Vector3.up) >= palmUpDotThreshold;
        if (!leftPalmFacingUp) return Reset(HandPoseKind.None);

        // ---- Stage 2: right index tip close to the left palm + lateral scribble ----
        // We deliberately do NOT gate on a specific right-hand pose -- the user can
        // scribble with an open hand, a pinch, or any shape; only the index tip's
        // motion across the palm matters.
        if (!TryGetPos(right, XRHandJointID.IndexTip, out Vector3 indexTip))
        {
            ClearScribble();
            indexPalmDistance = -1f;
            return Stamp(HandPoseKind.SaveEntry);
        }

        // Lateral "across-palm toward the thumb side" axis. XR Hands convention:
        //   ThumbExtendedDirection = palmJointRotation * (handednessMul, 0, 0)
        // Only sign-consistency matters for counting scribble velocity reversals.
        float handednessMul = left.handedness == Handedness.Left ? -1f : 1f;
        Vector3 widthAxis = leftPalmPose.rotation * new Vector3(handednessMul, 0f, 0f);

        Vector3 rel = indexTip - palmCenter;
        indexPalmDistance = Mathf.Abs(Vector3.Dot(rel, palmNormal));
        if (indexPalmDistance > scribbleMaxPalmDistance)
        {
            ClearScribble();
            return Stamp(HandPoseKind.SaveEntry);
        }
        float u = Vector3.Dot(rel, widthAxis);

        float now = Time.time;
        _scribble.Add(new ScribbleSample { t = now, u = u });
        // Drop stale samples outside the sliding window.
        float cutoff = now - scribbleWindowSeconds;
        int firstKeep = 0;
        while (firstKeep < _scribble.Count && _scribble[firstKeep].t < cutoff) firstKeep++;
        if (firstKeep > 0) _scribble.RemoveRange(0, firstKeep);

        // Count direction reversals (with dead band to ignore micro-jitter) and total lateral travel.
        int reversals = 0;
        float travel = 0f;
        float lastSign = 0f;
        for (int i = 1; i < _scribble.Count; i++)
        {
            float du = _scribble[i].u - _scribble[i - 1].u;
            travel += Mathf.Abs(du);
            if (Mathf.Abs(du) < scribbleJitterDeadband) continue;
            float sign = Mathf.Sign(du);
            if (lastSign != 0f && sign != lastSign) reversals++;
            lastSign = sign;
        }
        scribbleReversals = reversals;
        scribbleLateralTravel = travel;

        if (reversals >= scribbleMinReversals && travel >= scribbleMinLateralTravel)
        {
            ClearScribble();
            return Stamp(HandPoseKind.Save);
        }
        return Stamp(HandPoseKind.SaveEntry);
    }

    HandPoseKind Stamp(HandPoseKind k) { lastResult = k; return k; }

    HandPoseKind Reset(HandPoseKind k)
    {
        ClearScribble();
        leftPalmOpen = false;
        leftPalmFacingUp = false;
        indexPalmDistance = -1f;
        return Stamp(k);
    }

    void ClearScribble()
    {
        _scribble.Clear();
        scribbleReversals = 0;
        scribbleLateralTravel = 0f;
    }

    // ---------- pose checks ----------

    // "Hand fully open" = every finger straight enough that straight/segmented >= fingerExtendedRatio.
    bool IsHandFullyOpen(XRHand hand)
    {
        if (ThumbExtensionRatio(hand) < fingerExtendedRatio) return false;
        if (FingerExtensionRatio(hand, XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
            XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip) < fingerExtendedRatio) return false;
        if (FingerExtensionRatio(hand, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
            XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip) < fingerExtendedRatio) return false;
        if (FingerExtensionRatio(hand, XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
            XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip) < fingerExtendedRatio) return false;
        if (FingerExtensionRatio(hand, XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
            XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip) < fingerExtendedRatio) return false;
        return true;
    }

    static float FingerExtensionRatio(XRHand hand, XRHandJointID mcp, XRHandJointID p,
                                       XRHandJointID inter, XRHandJointID dist, XRHandJointID tip)
    {
        if (!TryGetPos(hand, mcp, out Vector3 a) ||
            !TryGetPos(hand, p, out Vector3 b) ||
            !TryGetPos(hand, inter, out Vector3 c) ||
            !TryGetPos(hand, dist, out Vector3 d) ||
            !TryGetPos(hand, tip, out Vector3 e)) return 0f;
        float straight = Vector3.Distance(a, e);
        float seg = Vector3.Distance(a, b) + Vector3.Distance(b, c) +
                    Vector3.Distance(c, d) + Vector3.Distance(d, e);
        return seg > 1e-5f ? straight / seg : 0f;
    }

    static float ThumbExtensionRatio(XRHand hand)
    {
        if (!TryGetPos(hand, XRHandJointID.ThumbMetacarpal, out Vector3 a) ||
            !TryGetPos(hand, XRHandJointID.ThumbProximal, out Vector3 b) ||
            !TryGetPos(hand, XRHandJointID.ThumbDistal, out Vector3 c) ||
            !TryGetPos(hand, XRHandJointID.ThumbTip, out Vector3 d)) return 0f;
        float straight = Vector3.Distance(a, d);
        float seg = Vector3.Distance(a, b) + Vector3.Distance(b, c) + Vector3.Distance(c, d);
        return seg > 1e-5f ? straight / seg : 0f;
    }

    static bool TryGetPos(XRHand hand, XRHandJointID id, out Vector3 p)
    {
        p = default;
        var j = hand.GetJoint(id);
        if (!j.TryGetPose(out Pose pose)) return false;
        p = pose.position;
        return true;
    }

    // Mirrors VRTemplate/Scripts/HandSubsystemManager's helper: walk all subsystems
    // and prefer one that is currently running.
    static List<XRHandSubsystem> s_subs;
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
