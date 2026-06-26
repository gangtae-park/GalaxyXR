using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

/*
CaptureControlCard

The semi-transparent rectangle that pops up over a recognised object while
the user is still in the camera-frame pose. Behaviour:

  - Centre = spawn anchor + (current wrist-midpoint - wrist-midpoint at spawn).
    So the card starts ON the object and then translates by the DELTA the
    user's hand-midpoint moves from its spawn pose -- like a remote-control
    grip. Hands held still at spawn pose -> card stays on object; hands move
    together by 10 cm -> card translates by 10 cm. If tracking on either
    hand is lost, the card just stays at its last good position.
  - Width / height each frame = distance between the right-hand and left-hand
    wrists, projected onto the card's local right / up axes. Wrist is chosen
    over pinch position because the wrist barely moves during the pinch
    micro-motion -- using pinch position made the rectangle jitter every time
    the user prepared to pinch.
  - Card billboards to the camera so it always faces the user.
  - Shutter trigger: both pinch actions pressed at the same time. Fires once
    via OnShutterFired (CaptureManager destroys the card).
  - Timeout: shutterTimeoutSeconds after spawn with no shutter -> OnTimedOut.

The shutter does NOT actually take a screenshot yet -- per the current spec,
firing the event is enough; the spawner just dismisses the card. Hook a real
screenshot routine to OnShutterFired later.
*/
public class CaptureControlCard : MonoBehaviour
{
    [Header("Refs (set in prefab Inspector)")]
    [Tooltip("Optional child Transform whose localScale carries the visual rectangle. If null, this GameObject's scale is used. Useful when the prefab root needs its own scale and only a child quad/panel should resize.")]
    public Transform sizeTarget;

    [Header("Timing")]
    [Tooltip("Auto-close if no two-hand pinch within this many seconds.")]
    public float shutterTimeoutSeconds = 10f;

    [Header("Sizing")]
    [Tooltip("Minimum world-space rectangle size (metres). Prevents the card collapsing to invisibility when hands meet.")]
    public Vector2 minWorldSize = new Vector2(0.08f, 0.08f);
    [Tooltip("Maximum world-space rectangle size (metres). Stops runaway when a hand is briefly tracked at a weird position.")]
    public Vector2 maxWorldSize = new Vector2(3f, 3f);

    [Header("Pinch threshold")]
    [Range(0f, 1f)] public float pinchValueThreshold = 0.9f;

    [Header("Axis tuning")]
    [Tooltip("Mirror the rectangle horizontally (multiplies localScale.x by -1). Toggle if the visual flips left/right after resizing.")]
    public bool invertX = false;
    [Tooltip("Mirror the rectangle vertically (multiplies localScale.y by -1). Toggle if the visual flips top/bottom -- typically a prefab pivot or quad orientation issue.")]
    public bool invertY = false;

    [Header("Debug")]
    public bool verboseLogging = true;

    public event Action OnShutterFired;
    public event Action OnTimedOut;

    public bool HasFired => _fired;

    private Camera _camera;
    private Vector3 _centerWorldPos;
    private InputAction _rightPinch, _leftPinch;
    private XRHandSubsystem _handSubsystem;
    private float _spawnTime;
    private bool _fired;
    private Vector3 _wristMidpointAtSpawn;
    private bool _haveWristBaseline;

    /// <summary>Called by CaptureManager right after Instantiate.</summary>
    public void Initialize(
        Vector3 centerWorldPos,
        Vector2 initialWorldSize,
        Camera camera,
        InputActionReference rightPinch,
        InputActionReference leftPinch)
    {
        _camera = camera != null ? camera : Camera.main;
        _centerWorldPos = centerWorldPos;
        _spawnTime = Time.time;
        _fired = false;

        _rightPinch = rightPinch?.action; _rightPinch?.Enable();
        _leftPinch  = leftPinch?.action;  _leftPinch?.Enable();
        TryGetHandSubsystem(out _handSubsystem);
        // Baseline captured on the first frame BOTH wrists are tracked, not here --
        // wrists may not be available yet on the very frame of Initialize().
        _haveWristBaseline = false;

        transform.position = _centerWorldPos;
        BillboardToCamera();
        ApplySize(initialWorldSize);

        if (verboseLogging)
            Debug.Log($"[CaptureControlCard] opened at {_centerWorldPos} initialSize={initialWorldSize}");
    }

    void Update()
    {
        if (_fired) return;

        // Position: midpoint of the two wrists when both are tracked so the
        // card translates as the user moves both hands together. Falls back
        // to the initial spawn anchor when tracking is lost, so the card
        // doesn't jump or vanish mid-motion.
        // Split the two reads so both `out` parameters are unconditionally assigned
        // (with `&&` the second call would short-circuit when the first fails).
        bool hasRh = TryGetWristPos(true, out Vector3 rh);
        bool hasLh = TryGetWristPos(false, out Vector3 lh);
        bool hasBoth = hasRh && hasLh;

        if (hasBoth)
        {
            Vector3 midpoint = (rh + lh) * 0.5f;
            if (!_haveWristBaseline)
            {
                // First frame both wrists are tracked: this midpoint becomes the
                // "neutral" hand pose. From here, hand-midpoint displacement maps
                // 1:1 to card displacement from the spawn anchor.
                _wristMidpointAtSpawn = midpoint;
                _haveWristBaseline = true;
            }
            transform.position = _centerWorldPos + (midpoint - _wristMidpointAtSpawn);
        }
        // else: leave transform.position at its last good value (set previously, or
        // _centerWorldPos from Initialize) -- avoids jumps when tracking blinks out.
        BillboardToCamera();

        if (hasBoth)
            ApplySizeFromHands(rh, lh);

        // Shutter: both pinches pressed simultaneously.
        if (ReadPressed(_rightPinch) && ReadPressed(_leftPinch))
        {
            FireShutter();
            return;
        }

        if (Time.time - _spawnTime >= shutterTimeoutSeconds)
            FireTimeout();
    }

    // ---------- sizing ----------

    void ApplySize(Vector2 worldSize)
    {
        Transform t = sizeTarget != null ? sizeTarget : transform;
        Vector3 s = t.localScale;
        // Sign on each axis is a separate user-toggle (Inspector). Magnitude is always
        // |worldSize|; the sign flips the mesh on that axis (mirror), which is the
        // workaround for prefabs whose visible face / pivot is oriented backwards.
        s.x = (invertX ? -1f : 1f) * Mathf.Abs(worldSize.x);
        s.y = (invertY ? -1f : 1f) * Mathf.Abs(worldSize.y);
        t.localScale = s;
    }

    void ApplySizeFromHands(Vector3 rh, Vector3 lh)
    {
        // Size is just the wrist separation projected onto the card's local axes
        // -- independent of the centre point, so no reference offset is needed.
        Vector3 diff = rh - lh;
        float dx = Vector3.Dot(diff, transform.right);
        float dy = Vector3.Dot(diff, transform.up);
        float w = Mathf.Clamp(Mathf.Abs(dx), minWorldSize.x, maxWorldSize.x);
        float h = Mathf.Clamp(Mathf.Abs(dy), minWorldSize.y, maxWorldSize.y);
        ApplySize(new Vector2(w, h));
    }

    void BillboardToCamera()
    {
        if (_camera == null) return;
        Vector3 toCam = _camera.transform.position - transform.position;
        if (toCam.sqrMagnitude < 1e-6f) return;
        // -toCam so the card's +Z faces the camera (default quad/canvas normal).
        transform.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
    }

    // ---------- triggers ----------

    void FireShutter()
    {
        if (_fired) return;
        _fired = true;
        if (verboseLogging) Debug.Log("[CaptureControlCard] shutter fired (both pinches).");
        try { OnShutterFired?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void FireTimeout()
    {
        if (_fired) return;
        _fired = true;
        if (verboseLogging) Debug.Log($"[CaptureControlCard] timed out after {shutterTimeoutSeconds:F1}s.");
        try { OnTimedOut?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // ---------- input helpers (mirror GestureRouter pattern) ----------

    bool ReadPressed(InputAction act)
    {
        if (act == null) return false;
        try
        {
            if (act.activeControl != null && act.activeControl.valueType == typeof(float))
                return act.ReadValue<float>() >= pinchValueThreshold;
        }
        catch { }
        try { return act.IsPressed(); } catch { return false; }
    }

    bool TryGetWristPos(bool right, out Vector3 pos)
    {
        pos = Vector3.zero;
        if (_handSubsystem == null && !TryGetHandSubsystem(out _handSubsystem)) return false;
        XRHand hand = right ? _handSubsystem.rightHand : _handSubsystem.leftHand;
        if (!hand.isTracked) return false;
        if (!hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose pose)) return false;
        pos = pose.position;
        return true;
    }

    // Mirrors VRTemplate/Scripts/HandSubsystemManager and our HandPoseRecognizer:
    // walk all subsystems, prefer one that is currently running.
    static List<XRHandSubsystem> s_subs;
    static bool TryGetHandSubsystem(out XRHandSubsystem sub)
    {
        s_subs ??= new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(s_subs);
        for (int i = 0; i < s_subs.Count; i++)
            if (s_subs[i].running) { sub = s_subs[i]; return true; }
        sub = s_subs.Count > 0 ? s_subs[0] : null;
        return sub != null;
    }
}
