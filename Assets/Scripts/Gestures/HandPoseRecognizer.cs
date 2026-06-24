using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/*
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
  CapturePose  : BOTH hands form an "L" (thumb + index extended, perpendicular;
                middle/ring/little curled) AND the two L's interlock to a frame
                -- the thumbs are anti-parallel and the indexes are anti-parallel.
                This is a snapshot only; the 2-second hold required for Capture
                RECOGNIZED is enforced by GestureRouter, not here.

GestureRouter is the polling driver; this component is stateless about the
gesture lifecycle (START/END/FAIL is the router's job). The only state we keep
internally is the small ring of recent right-index-tip lateral samples needed
to count scribble reversals.
*/

public enum HandPoseKind { None, SaveEntry, Save, CapturePose }

public class HandPoseRecognizer : MonoBehaviour
{
    [Header("Finger Extension")]
    [Range(0.6f, 1f)] public float fingerExtendedRatio = 0.90f;

    [Header("Palm orientation (Save)")]
    [Range(0f, 1f)] public float palmUpDotThreshold = 0.68f;

    [Header("Scribble (Save)")]
    public float scribbleWindowSeconds = 1.5f;
    public float scribbleMaxPalmDistance = 0.06f;
    public int scribbleMinReversals = 3;
    public float scribbleMinLateralTravel = 0.03f;
    public float scribbleJitterDeadband = 0.002f;

    [Header("Camera pose (Capture)")]
    [Range(0f, 1f)] public float fingerCurledRatio = 0.7f;
    [Range(0f, 1f)] public float thumbIndexPerpDot = 0.7f;
    [Range(-1f, 0f)] public float handsAntiParallelDot = -0.5f;

    [Header("Status (read-only)")]
    [SerializeField] private HandPoseKind lastResult;
    [SerializeField] private bool leftHandTracked;
    [SerializeField] private bool rightHandTracked;
    [SerializeField] private bool leftPalmOpen;
    [SerializeField] private bool leftPalmFacingUp;
    [SerializeField] private float indexPalmDistance = -1f;
    [SerializeField] private int scribbleReversals;
    [SerializeField] private float scribbleLateralTravel;
    [SerializeField] private bool cameraLeftLShape;
    [SerializeField] private bool cameraRightLShape;
    [SerializeField] private float cameraThumbsDot = 1f;
    [SerializeField] private float cameraIndexesDot = 1f;
    [SerializeField] private float lThumb, lIndex, lMiddle, lRing, lLittle;
    [SerializeField] private float rThumb, rIndex, rMiddle, rRing, rLittle;
    [SerializeField] private float lThumbIndexDot = 1f;
    [SerializeField] private float rThumbIndexDot = 1f;
    [SerializeField] private string lLShapeReject = "";
    [SerializeField] private string rLShapeReject = "";

    [Header("Debug")]
    public bool verboseLogging = false;

    public HandPoseKind LastResult => lastResult;
    public int CurrentScribbleSampleCount => _scribble.Count;

    private XRHandSubsystem _handSubsystem;

    private struct ScribbleSample { public float t; public float u; }
    private readonly List<ScribbleSample> _scribble = new List<ScribbleSample>(64);

    void OnEnable()
    {
        TryGetHandSubsystem(out _handSubsystem);
    }

    
    //Snapshot the current hand poses and decide which referent pose is being held.
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

        // Snapshot per-finger extension ratios for both hands
        SnapshotFingerRatios(left);
        SnapshotFingerRatios(right);

        // {Capture} pose check
        if (IsCapturePose(left, right))
            return Reset(HandPoseKind.CapturePose);
        
        // {Save} pose check
        // 1) left palm fully open + facing up
        leftPalmOpen = IsHandFullyOpen(left);
        if (!leftPalmOpen) return Reset(HandPoseKind.None);

        if (!left.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose leftPalmPose))
            return Reset(HandPoseKind.None);
        Vector3 palmCenter = leftPalmPose.position;
        Vector3 palmNormal = leftPalmPose.rotation * new Vector3(0f, -1f, 0f);
        leftPalmFacingUp = Vector3.Dot(palmNormal, Vector3.up) >= palmUpDotThreshold;
        if (!leftPalmFacingUp) return Reset(HandPoseKind.None);

        // 2) right index tip close to the left palm + lateral scribble
        if (!TryGetPos(right, XRHandJointID.IndexTip, out Vector3 indexTip))
        {
            ClearScribble();
            indexPalmDistance = -1f;
            return Stamp(HandPoseKind.SaveEntry);
        }

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

    HandPoseKind Stamp(HandPoseKind k)
    {
        lastResult = k;
        if (verboseLogging)
        {
            Debug.Log(
                $"[HandPoseRecognizer] kind={lastResult}" +
                $" | tracked L={leftHandTracked} R={rightHandTracked}" +
                $" | L T={lThumb:F2} I={lIndex:F2} M={lMiddle:F2} R={lRing:F2} L={lLittle:F2}" +
                $" | R T={rThumb:F2} I={rIndex:F2} M={rMiddle:F2} R={rRing:F2} L={rLittle:F2}" +
                // $" (extended >= {fingerExtendedRatio:F2}, curled <= {fingerCurledRatio:F2}; 0.00 = joints not tracked)" +
                // $" | leftPalmOpen={leftPalmOpen} facingUp={leftPalmFacingUp}" +
                $" | leftL={cameraLeftLShape}({lLShapeReject}) rightL={cameraRightLShape}({rLShapeReject})" +
                $" L={lThumbIndexDot:F2} R={rThumbIndexDot:F2}" +
                // $" (perp needs |dot| <= {thumbIndexPerpDot:F2})" +
                $" | thumbsDot={cameraThumbsDot:F2} indexesDot={cameraIndexesDot:F2}"
                // $" (anti-parallel needs <= {handsAntiParallelDot:F2})" +
                // $" | scribble samples={CurrentScribbleSampleCount}" +
                // $" reversals={scribbleReversals} travel={scribbleLateralTravel:F3}m"
            );
        }
        return k;
    }

    HandPoseKind Reset(HandPoseKind k)
    {
        ClearScribble();
        leftPalmOpen = false;
        leftPalmFacingUp = false;
        indexPalmDistance = -1f;
        if (k != HandPoseKind.CapturePose)
        {
            cameraLeftLShape = false;
            cameraRightLShape = false;
            cameraThumbsDot = 1f;
            cameraIndexesDot = 1f;
        }
        return Stamp(k);
    }

    void ClearScribble()
    {
        _scribble.Clear();
        scribbleReversals = 0;
        scribbleLateralTravel = 0f;
    }

    // ====== pose checks ======

    void SnapshotFingerRatios(XRHand hand)
    {
        float t = ThumbExtensionRatio(hand);
        float i = FingerExtensionRatio(hand, XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
            XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip);
        float m = FingerExtensionRatio(hand, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
            XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip);
        float r = FingerExtensionRatio(hand, XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
            XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip);
        float l = FingerExtensionRatio(hand, XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
            XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip);
        if (hand.handedness == Handedness.Left)
        { lThumb = t; lIndex = i; lMiddle = m; lRing = r; lLittle = l; }
        else
        { rThumb = t; rIndex = i; rMiddle = m; rRing = r; rLittle = l; }
    }

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

    bool IsCapturePose(XRHand left, XRHand right)
    {
        cameraLeftLShape  = IsCameraFrameLShape(left,  out Vector3 leftThumb,  out Vector3 leftIndex);
        cameraRightLShape = IsCameraFrameLShape(right, out Vector3 rightThumb, out Vector3 rightIndex);
        if (!cameraLeftLShape || !cameraRightLShape) return false;

        cameraThumbsDot  = Vector3.Dot(leftThumb,  rightThumb);
        cameraIndexesDot = Vector3.Dot(leftIndex,  rightIndex);
        if (cameraThumbsDot  > handsAntiParallelDot) return false;
        if (cameraIndexesDot > handsAntiParallelDot) return false;
        return true;
    }

    bool IsCameraFrameLShape(XRHand hand, out Vector3 thumbDir, out Vector3 indexDir)
    {
        thumbDir = Vector3.zero;
        indexDir = Vector3.zero;
        bool isLeft = hand.handedness == Handedness.Left;
        string rej = "";

        if (ThumbExtensionRatio(hand) < fingerExtendedRatio) rej = "thumb not extended";
        else if (FingerExtensionRatio(hand, XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
            XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip) < fingerExtendedRatio) rej = "index not extended";
        else if (FingerExtensionRatio(hand, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
            XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip) > fingerCurledRatio) rej = "middle not curled";
        else if (FingerExtensionRatio(hand, XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
            XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip) > fingerCurledRatio) rej = "ring not curled";
        else if (FingerExtensionRatio(hand, XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
            XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip) > fingerCurledRatio) rej = "little not curled";

        if (rej != "")
        {
            if (isLeft) lLShapeReject = rej; else rLShapeReject = rej;
            return false;
        }

        thumbDir = FingerDirection(hand, XRHandJointID.ThumbProximal, XRHandJointID.ThumbTip);
        indexDir = FingerDirection(hand, XRHandJointID.IndexProximal, XRHandJointID.IndexTip);
        if (thumbDir == Vector3.zero || indexDir == Vector3.zero)
        {
            if (isLeft) lLShapeReject = "thumb/index joints not tracked"; else rLShapeReject = "thumb/index joints not tracked";
            return false;
        }

        float dot = Vector3.Dot(thumbDir, indexDir);
        if (isLeft) lThumbIndexDot = dot; else rThumbIndexDot = dot;

        if (Mathf.Abs(dot) > thumbIndexPerpDot)
        {
            if (isLeft) lLShapeReject = $"thumb-index not perpendicular (|{dot:F2}| > {thumbIndexPerpDot:F2})";
            else        rLShapeReject = $"thumb-index not perpendicular (|{dot:F2}| > {thumbIndexPerpDot:F2})";
            return false;
        }

        if (isLeft) lLShapeReject = "OK"; else rLShapeReject = "OK";
        return true;
    }

    static Vector3 FingerDirection(XRHand hand, XRHandJointID baseJoint, XRHandJointID tipJoint)
    {
        if (!TryGetPos(hand, baseJoint, out Vector3 a) ||
            !TryGetPos(hand, tipJoint, out Vector3 b)) return Vector3.zero;
        Vector3 d = b - a;
        return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.zero;
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
