using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Static palm-orientation check shared by the left-hand long-pinch triggers
/// (ObjectUiHandTrigger, VoiceHandTrigger). Gating the pinch hold on
/// "palm faces the sky" kills the accidental triggers that fire whenever the
/// user pinches in a normal hand pose.
///
/// Convention note: OpenXR palm joint orientation has +Y pointing out of the
/// BACK of the hand, so the palm-surface normal is -Y. If a device/runtime
/// reports the opposite, flip with invertNormal. Poses are compared in
/// tracking (session) space; rigs are typically yaw-rotated at most, which
/// leaves the up axis intact.
/// </summary>
public static class LeftHandPalmUp
{
    static XRHandSubsystem s_subsystem;

    /// <summary>True iff the left hand is tracked AND its palm normal is
    /// within maxAngleDeg of straight up. Untracked hand returns false, so
    /// controller-grip fallback users should disable the gate instead.</summary>
    public static bool IsPalmUp(float maxAngleDeg, bool invertNormal)
    {
        return TryGetPalmAngle(invertNormal, out float angle) && angle <= maxAngleDeg;
    }

    /// <summary>Angle (degrees) between the left palm-surface normal and
    /// world up. 0 = palm perfectly facing the sky.</summary>
    public static bool TryGetPalmAngle(bool invertNormal, out float angleDeg)
    {
        angleDeg = 180f;
        XRHandSubsystem sub = GetSubsystem();
        if (sub == null) return false;

        XRHand hand = sub.leftHand;
        if (!hand.isTracked) return false;

        XRHandJoint palm = hand.GetJoint(XRHandJointID.Palm);
        if (!palm.TryGetPose(out Pose pose)) return false;

        Vector3 palmNormal = pose.rotation * (invertNormal ? Vector3.up : Vector3.down);
        angleDeg = Vector3.Angle(palmNormal, Vector3.up);
        return true;
    }

    static XRHandSubsystem GetSubsystem()
    {
        if (s_subsystem != null && s_subsystem.running) return s_subsystem;
        s_subsystem = null;
        var subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (var s in subsystems)
        {
            if (s.running)
            {
                s_subsystem = s;
                break;
            }
        }
        return s_subsystem;
    }
}
