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
  CapturePose  : BOTH hands independently form an "L" (thumb + index extended,
                perpendicular; middle/ring/little curled). No interlock check --
                users don't need to hold the exact anti-parallel picture-frame
                pose. This is a snapshot only; the 2-second hold required for
                Capture RECOGNIZED is enforced by GestureRouter, not here.
  TranslateStart : RIGHT hand C-shape. Thumb + index are checked with SEPARATE
                extension bounds because a natural C has an extended thumb but a
                CURVED index (that curve is what makes the C-cup). Concretely:
                thumb ext >= translateThumbMinExtension (straight thumb OK),
                index ext in [translateIndexMinExtension, translateIndexMaxExtension]
                (must be curled -- a fully straight index is Pointing, not C).
                Middle/ring/little curled, tips >= translateThumbIndexGapMin
                apart, palm oriented toward the user's LEFT (mirrored C). Left
                hand unconstrained.
  TranslateEnd  : IDENTICAL base checks as TranslateStart -- extension, curl,
                orientation -- EXCEPT the thumb-index tip distance has fallen
                BELOW translateThumbIndexGapMin (the C is now closed). Only the
                gap distinguishes the two states, so a smooth closing motion
                transitions cleanly Start -> End without ever passing through
                None. GestureRouter treats End as the RECOGNIZED trigger.

GestureRouter is the polling driver; this component is stateless about the
gesture lifecycle (START/END/FAIL is the router's job). The only state we keep
internally is the small ring of recent right-index-tip lateral samples needed
to count scribble reversals.
*/

public enum HandPoseKind { None, SaveEntry, Save, CapturePose, TranslateStart, TranslateEnd }

public class HandPoseRecognizer : MonoBehaviour
{
    [Header("Finger Extension")]
    [Range(0.6f, 1f)] public float fingerExtendedRatio = 0.90f;

    [Header("Palm orientation (Save)")]
    public Transform headCamera;
    [Range(0f, 1f)] public float palmFacingCameraDotThreshold = 0.8f;

    [Header("Scribble (Save)")]
    public float scribbleWindowSeconds = 1.5f;
    public float scribbleMaxPalmDistance = 0.06f;
    public int scribbleMinReversals = 3;
    public float scribbleMinLateralTravel = 0.03f;
    public float scribbleJitterDeadband = 0.002f;

    [Header("Camera pose (Capture)")]
    [Range(0f, 1f)] public float fingerCurledRatio = 0.7f;
    [Range(0f, 1f)] public float thumbIndexPerpDot = 0.7f;
    [Tooltip("UNUSED. The interlock check (thumbs anti-parallel + indexes anti-parallel) was removed so users don't need to hold the exact picture-frame pose. Kept in the Inspector only for scene-file compatibility; safe to ignore.")]
    [Range(-1f, 0f)] public float handsAntiParallelDot = -0.5f;

    [Header("Translate pose (right hand C-shape)")]
    [Tooltip("Minimum tip-to-tip distance between right thumb and right index for the OPEN C-shape (TranslateStart) to count. Sits above the natural pinch resting distance so a closed pinch pose doesn't false-start Translate.")]
    public float translateThumbIndexGapMin = 0.04f;
    [Tooltip("Middle/ring/little fingers must be at or below this extension ratio to distinguish the C from an open hand.")]
    [Range(0f, 1f)] public float translateOtherCurledRatio = 0.7f;
    [Tooltip("Minimum extension ratio required for the RIGHT thumb to count as a C-shape side. No upper cap -- a fully-straight thumb is expected in a natural C. Raise if a curled thumb keeps triggering C by mistake.")]
    [Range(0f, 1f)] public float translateThumbMinExtension = 0.70f;
    [Tooltip("Minimum extension ratio for the RIGHT index. Guards against a fully curled index (fist-like). Should sit clearly BELOW translateIndexMaxExtension so the index has a valid 'curved' window.")]
    [Range(0f, 1f)] public float translateIndexMinExtension = 0.55f;
    [Tooltip("MAXIMUM extension ratio for the RIGHT index. This is the KEY differentiator from Pointing: a pointing index is nearly straight (~0.95) and gets rejected once it goes above this cap; a C-shape index is visibly curved (~0.65-0.85). Lower this to be stricter against straight fingers, raise if a moderately-curved C keeps being rejected as too straight.")]
    [Range(0.5f, 1f)] public float translateIndexMaxExtension = 0.88f;
    [Tooltip("Enforce that the RIGHT palm is oriented so the C opens toward the user's LEFT (mirrored C). Uncheck to accept any wrist rotation. Keep on to reject C-shapes that face the wrong way and to add one more filter against non-Translate poses.")]
    public bool translateStartRequireOrientation = true;
    [Tooltip("How strongly the right palm must face the user's LEFT. Higher = stricter (0.7 accepts only near-perfect left-facing); lower = looser (0.2 accepts almost any leftward tilt). Uses the same palm-normal convention as the Save pose (palm.rotation * (0,-1,0) points OUT of the palm surface). The palm normal and camera-right are BOTH projected onto the horizontal plane before dotting, so head yaw rotates the reference cleanly with the head and head pitch/roll don't leak into the score. Recommended range 0.3-0.6.")]
    [Range(-1f, 1f)] public float translateStartPalmFacingLeftDot = 0.4f;

    [Header("Status (read-only)")]
    [SerializeField] private HandPoseKind lastResult;
    [SerializeField] private bool leftHandTracked;
    [SerializeField] private bool rightHandTracked;
    [SerializeField] private bool leftPalmOpen;
    [SerializeField] private bool leftPalmFacingCamera;
    [SerializeField] private float palmFacingCameraDot;
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
    [SerializeField] private bool translateCShape;
    [SerializeField] private float translateThumbIndexGap = -1f;
    [SerializeField] private float translateThumbExt = -1f;
    [SerializeField] private float translateIndexExt = -1f;
    [SerializeField] private float translatePalmDot = -2f;
    [SerializeField] private string translateReject = "";

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

        // {Translate} pose check -- right-hand "C" (open or closed). Left hand
        // is not constrained, so this must be checked BEFORE Save (which
        // requires a specific left-hand posture but leaves the right free --
        // ambiguous if both would match). EvaluateTranslatePose returns
        // TranslateStart (gap >= min), TranslateEnd (gap < min, same base
        // checks otherwise), or None (base checks fail).
        HandPoseKind translateKind = EvaluateTranslatePose(right);
        if (translateKind != HandPoseKind.None)
            return Reset(translateKind);

        // {Save} pose check
        // 1) left palm fully open + facing the user camera
        leftPalmOpen = IsHandFullyOpen(left);
        if (!leftPalmOpen) return Reset(HandPoseKind.None);

        if (!left.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose leftPalmPose))
            return Reset(HandPoseKind.None);
        Vector3 palmCenter = leftPalmPose.position;
        Vector3 palmNormal = leftPalmPose.rotation * new Vector3(0f, -1f, 0f);
        Vector3 palmToCamera = GetCameraPosition() - palmCenter;
        if (palmToCamera.sqrMagnitude < 1e-8f) return Reset(HandPoseKind.None);
        palmToCamera.Normalize();
        palmFacingCameraDot = Vector3.Dot(palmNormal, palmToCamera);
        leftPalmFacingCamera = palmFacingCameraDot >= palmFacingCameraDotThreshold;
        if (!leftPalmFacingCamera) return Reset(HandPoseKind.None);

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
                // $" | tracked L={leftHandTracked} R={rightHandTracked}" +
                // $" | L T={lThumb:F2} I={lIndex:F2} M={lMiddle:F2} R={lRing:F2} L={lLittle:F2}" +
                // $" | R T={rThumb:F2} I={rIndex:F2} M={rMiddle:F2} R={rRing:F2} L={rLittle:F2}" +
                // $" (extended >= {fingerExtendedRatio:F2}, curled <= {fingerCurledRatio:F2}; 0.00 = joints not tracked)" +
                // $" | leftPalmOpen={leftPalmOpen} facingCamera={leftPalmFacingCamera} ({palmFacingCameraDot:F2})" +
                // $" | leftL={cameraLeftLShape}({lLShapeReject}) rightL={cameraRightLShape}({rLShapeReject})" +
                // $" L={lThumbIndexDot:F2} R={rThumbIndexDot:F2}" +
                // $" | thumbsDot={cameraThumbsDot:F2} indexesDot={cameraIndexesDot:F2}" +
                $" | translateC={translateCShape}({translateReject}) gap={translateThumbIndexGap:F3} thumbExt={translateThumbExt:F2} indexExt={translateIndexExt:F2} palmDot={translatePalmDot:F2}"
            );
        }
        return k;
    }

    HandPoseKind Reset(HandPoseKind k)
    {
        ClearScribble();
        leftPalmOpen = false;
        leftPalmFacingCamera = false;
        palmFacingCameraDot = 0f;
        indexPalmDistance = -1f;
        if (k != HandPoseKind.CapturePose)
        {
            cameraLeftLShape = false;
            cameraRightLShape = false;
            cameraThumbsDot = 1f;
            cameraIndexesDot = 1f;
        }
        if (k != HandPoseKind.TranslateStart && k != HandPoseKind.TranslateEnd)
        {
            translateCShape = false;
            translateThumbIndexGap = -1f;
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

    private Camera _resolvedCamera;
    Vector3 GetCameraPosition()
    {
        if (headCamera != null) return headCamera.position;
        if (_resolvedCamera == null) _resolvedCamera = Camera.main;
        return _resolvedCamera != null ? _resolvedCamera.transform.position : Vector3.zero;
    }

    // Head's right axis for the mirrored-C orientation check. Tracks head
    // rotation each frame -- the reference "left" rotates with the user, so
    // a C-shape held on the current head-relative left always passes. The
    // palm-vs-reference comparison is performed on the horizontal plane in
    // EvaluateTranslatePose so pitch/roll wobble doesn't leak into palmDot.
    Vector3 ResolveReferenceRight()
    {
        Transform src = headCamera;
        if (src == null)
        {
            if (_resolvedCamera == null) _resolvedCamera = Camera.main;
            if (_resolvedCamera != null) src = _resolvedCamera.transform;
        }
        return src != null ? src.right : Vector3.right;
    }

    // Zero the Y component and re-normalize so tilt/roll noise disappears from
    // orientation dot products. If the input is nearly vertical (no horizontal
    // component) we return Vector3.zero -- callers must treat that as ambiguous.
    static Vector3 FlattenHorizontal(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.zero : v.normalized;
    }

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

    // Capture is now the simpler two-hand L check: both hands independently
    // form an L-shape (thumb + index extended and perpendicular, other three
    // curled). The previous interlock constraint -- thumbs anti-parallel AND
    // indexes anti-parallel -- was removed so users don't have to hold the
    // exact "picture frame" pose. cameraThumbsDot / cameraIndexesDot are still
    // computed for the debug status readout but no longer gate the pose.
    bool IsCapturePose(XRHand left, XRHand right)
    {
        cameraLeftLShape  = IsCameraFrameLShape(left,  out Vector3 leftThumb,  out Vector3 leftIndex);
        cameraRightLShape = IsCameraFrameLShape(right, out Vector3 rightThumb, out Vector3 rightIndex);
        if (!cameraLeftLShape || !cameraRightLShape) return false;

        cameraThumbsDot  = Vector3.Dot(leftThumb,  rightThumb);
        cameraIndexesDot = Vector3.Dot(leftIndex,  rightIndex);
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

    // Right-hand "mirrored C": thumb extended (straight is fine), index curved
    // (must NOT be straight -- that's the key discriminator from Pointing),
    // remaining fingers curled, palm oriented so the C opens toward the user's
    // LEFT. Base checks (extension, curl, orientation) are IDENTICAL for Start
    // and End; only the thumb-index tip gap decides which one:
    //   gap >= translateThumbIndexGapMin -> TranslateStart (C is open)
    //   gap <  translateThumbIndexGapMin -> TranslateEnd   (C has closed)
    // Any base failure -> None (the router treats this as cancel).
    HandPoseKind EvaluateTranslatePose(XRHand right)
    {
        translateCShape = false;
        translateThumbIndexGap = -1f;
        translateThumbExt = -1f;
        translateIndexExt = -1f;
        translatePalmDot = -2f;
        translateReject = "";

        // Thumb: extended, no upper cap (a fully-straight thumb is expected).
        float thumbExt = ThumbExtensionRatio(right);
        translateThumbExt = thumbExt;
        if (thumbExt < translateThumbMinExtension)
        { translateReject = $"thumb not extended ({thumbExt:F2} < {translateThumbMinExtension:F2})"; return HandPoseKind.None; }

        // Index: MUST be curved. Both min (rejects a fully-curled fist) and
        // max (rejects a straight Pointing index) are enforced.
        float indexExt = FingerExtensionRatio(right, XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
            XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip);
        translateIndexExt = indexExt;
        if (indexExt < translateIndexMinExtension)
        { translateReject = $"index too curled ({indexExt:F2} < {translateIndexMinExtension:F2})"; return HandPoseKind.None; }
        if (indexExt > translateIndexMaxExtension)
        { translateReject = $"index too straight ({indexExt:F2} > {translateIndexMaxExtension:F2}) -- looks like pointing"; return HandPoseKind.None; }

        if (FingerExtensionRatio(right, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
            XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip) > translateOtherCurledRatio)
        { translateReject = "middle not curled"; return HandPoseKind.None; }
        if (FingerExtensionRatio(right, XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
            XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip) > translateOtherCurledRatio)
        { translateReject = "ring not curled"; return HandPoseKind.None; }
        if (FingerExtensionRatio(right, XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
            XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip) > translateOtherCurledRatio)
        { translateReject = "little not curled"; return HandPoseKind.None; }

        if (!TryGetPos(right, XRHandJointID.ThumbTip, out Vector3 thumbTip) ||
            !TryGetPos(right, XRHandJointID.IndexTip, out Vector3 indexTip))
        { translateReject = "thumb/index tip not tracked"; return HandPoseKind.None; }

        translateThumbIndexGap = Vector3.Distance(thumbTip, indexTip);

        // Orientation: for a mirrored C on the RIGHT hand, the palm faces the
        // user's LEFT. We reuse the Save convention: palm.rotation * (0,-1,0)
        // is the "out of palm surface" direction. To make the dot stable across
        // head yaw, BOTH the palm normal and the camera-right axis are
        // projected onto the horizontal plane before dotting. Applies equally
        // to Start and End -- if the user rotates the hand during closing,
        // the pose invalidates and translatePending will cancel.
        if (translateStartRequireOrientation)
        {
            if (!right.GetJoint(XRHandJointID.Palm).TryGetPose(out Pose palmPose))
            { translateReject = "palm joint not tracked"; return HandPoseKind.None; }
            Vector3 palmNormal = palmPose.rotation * new Vector3(0f, -1f, 0f);

            Vector3 palmH = FlattenHorizontal(palmNormal);
            Vector3 refRightH = FlattenHorizontal(ResolveReferenceRight());
            if (palmH == Vector3.zero || refRightH == Vector3.zero)
            { translateReject = "orientation nearly vertical -- can't project to horizontal"; return HandPoseKind.None; }

            translatePalmDot = Vector3.Dot(palmH, -refRightH);
            if (translatePalmDot < translateStartPalmFacingLeftDot)
            { translateReject = $"palm not facing left ({translatePalmDot:F2} < {translateStartPalmFacingLeftDot:F2})"; return HandPoseKind.None; }
        }

        // Base OK -- decide Start vs End on the gap alone.
        translateCShape = true;
        if (translateThumbIndexGap < translateThumbIndexGapMin)
        {
            translateReject = "OK(End)";
            return HandPoseKind.TranslateEnd;
        }
        translateReject = "OK(Start)";
        return HandPoseKind.TranslateStart;
    }

    // Relaxed variant used by the router ONLY while translatePending is true.
    // Enter path (EvaluateTranslatePose) still enforces palm-left orientation
    // and the index max-extension cap so Translate doesn't false-start from a
    // pointing pose. Once we're committed, the sweep motion often drifts on
    // those axes -- the palm rotates as the arm moves, the index straightens
    // slightly as the wrist extends -- and neither should break the gesture.
    //
    // What DOES break the hold: middle/ring/little uncurling (hand transitions
    // to open palm / different pose), or thumb/index dropping below their MIN
    // extension (fingers curling into a fist / different pose). Everything
    // else is allowed to wobble.
    public HandPoseKind EvaluateTranslateHold()
    {
        if (_handSubsystem == null && !TryGetHandSubsystem(out _handSubsystem))
            return HandPoseKind.None;
        XRHand right = _handSubsystem.rightHand;
        if (!right.isTracked) return HandPoseKind.None;

        float thumbExt = ThumbExtensionRatio(right);
        translateThumbExt = thumbExt;
        if (thumbExt < translateThumbMinExtension) return HandPoseKind.None;

        float indexExt = FingerExtensionRatio(right, XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
            XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip);
        translateIndexExt = indexExt;
        if (indexExt < translateIndexMinExtension) return HandPoseKind.None;

        if (FingerExtensionRatio(right, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
            XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip) > translateOtherCurledRatio)
            return HandPoseKind.None;
        if (FingerExtensionRatio(right, XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
            XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip) > translateOtherCurledRatio)
            return HandPoseKind.None;
        if (FingerExtensionRatio(right, XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
            XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip) > translateOtherCurledRatio)
            return HandPoseKind.None;

        if (!TryGetPos(right, XRHandJointID.ThumbTip, out Vector3 thumbTip) ||
            !TryGetPos(right, XRHandJointID.IndexTip, out Vector3 indexTip))
            return HandPoseKind.None;

        translateThumbIndexGap = Vector3.Distance(thumbTip, indexTip);
        translateCShape = true;
        if (translateThumbIndexGap < translateThumbIndexGapMin)
        {
            translateReject = "OK(End,hold)";
            return HandPoseKind.TranslateEnd;
        }
        translateReject = "OK(Start,hold)";
        return HandPoseKind.TranslateStart;
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
