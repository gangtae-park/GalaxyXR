using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

public class CaptureControlCard : MonoBehaviour
{
    [Header("Refs")]
    public Transform sizeTarget;

    [Header("Timing")]
    public float shutterTimeoutSeconds = 10f;

    [Header("Sizing")]
    public Vector2 minWorldSize = new Vector2(0.08f, 0.08f);
    public Vector2 maxWorldSize = new Vector2(3f, 3f);
    [Range(0.05f, 2f)] public float resizeSensitivity = 0.5f;

    [Header("Pinch threshold")]
    [Range(0f, 1f)] public float pinchValueThreshold = 0.9f;

    [Header("Axis tuning")]
    public bool invertX = false;
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
    private Vector3 _baselineHandDiff;
    private Vector2 _initialWorldSize;
    private bool _haveWristBaseline;

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
        _haveWristBaseline = false;
        _initialWorldSize = initialWorldSize;

        transform.position = _centerWorldPos;
        BillboardToCamera();
        ApplySize(initialWorldSize);

        if (verboseLogging)
            Debug.Log($"[CaptureControlCard] opened at {_centerWorldPos} initialSize={initialWorldSize}");
    }

    void Update()
    {
        if (_fired) return;

        bool hasRh = TryGetWristPos(true, out Vector3 rh);
        bool hasLh = TryGetWristPos(false, out Vector3 lh);
        bool hasBoth = hasRh && hasLh;

        if (hasBoth)
        {
            Vector3 midpoint = (rh + lh) * 0.5f;
            if (!_haveWristBaseline)
            {
                _wristMidpointAtSpawn = midpoint;
                _baselineHandDiff = rh - lh;
                _haveWristBaseline = true;
            }
            transform.position = _centerWorldPos + (midpoint - _wristMidpointAtSpawn);
        }
        BillboardToCamera();

        if (hasBoth)
            ApplySizeFromHands(rh, lh);

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
        s.x = (invertX ? -1f : 1f) * Mathf.Abs(worldSize.x);
        s.y = (invertY ? -1f : 1f) * Mathf.Abs(worldSize.y);
        t.localScale = s;
    }

    void ApplySizeFromHands(Vector3 rh, Vector3 lh)
    {
        Vector3 currentDiff  = rh - lh;
        Vector3 baselineDiff = _baselineHandDiff;
        float currentX  = Mathf.Abs(Vector3.Dot(currentDiff,  transform.right));
        float currentY  = Mathf.Abs(Vector3.Dot(currentDiff,  transform.up));
        float baselineX = Mathf.Abs(Vector3.Dot(baselineDiff, transform.right));
        float baselineY = Mathf.Abs(Vector3.Dot(baselineDiff, transform.up));

        float w = Mathf.Clamp(_initialWorldSize.x + (currentX - baselineX) * resizeSensitivity,
                              minWorldSize.x, maxWorldSize.x);
        float h = Mathf.Clamp(_initialWorldSize.y + (currentY - baselineY) * resizeSensitivity,
                              minWorldSize.y, maxWorldSize.y);
        ApplySize(new Vector2(w, h));
    }

    void BillboardToCamera()
    {
        if (_camera == null) return;
        Vector3 toCam = _camera.transform.position - transform.position;
        if (toCam.sqrMagnitude < 1e-6f) return;
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
