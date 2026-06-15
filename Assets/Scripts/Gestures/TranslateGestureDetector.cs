using System;
using System.Collections.Generic;
using UnityEngine;

/*
TranslateGestureDetector

Standalone detector for the Translate gesture, independent of the tight-pinch
Search / Ask pipeline (PinchStrokeCapture + GestureRouter).

Translate sequence the user actually performs:
  pinch -> open thumb+index slightly (5..12 cm "V" shape) ->
  hold the V while sweeping the hand right or down-right (drawing the bounding box) ->
  close back to a pinch / open fully ->
  palm swipe to confirm.

Detection strategy (pinch is ignored; what we recognize is the V hold + sweep + swipe):
  1) Thumb-index distance must stay in [vDistMin, vDistMax] for at least
     vSustainSeconds. The sustain window rejects brief V-band pass-throughs that
     happen when a tight-pinch Search/Ask gesture is released.
  2) Once "armed", we record the hand anchor as corner A and sample the path.
  3) While the V is held, the hand displacement must point within a cone of
     rightwardConeDegrees off camera-right, the path must stay roughly straight
     (max perpendicular deviation bounded by maxStraightnessDeviation * forward
     travel), and the rightward travel must exceed minMotionDistance.
  4) When the V band is left (back to a pinch OR fully open), if the motion
     criterion is met, transition to AreaDefined; otherwise return to Idle.
  5) From AreaDefined, a rapid sideways palm swipe within palmSwipeWindowSeconds
     confirms the gesture; timeout cancels.

Output: GESTURE_EVENT packets via MsgSender with eventType
  START | AREA_DEFINED | END | FAIL
plus a RECOGNIZED packet on confirm, matching the format used by other gestures.
*/

public class TranslateGestureDetector : MonoBehaviour
{
    public enum State
    {
        Idle,
        VHeld,
        AreaDefined,
    }

    [Header("Hand joints")]
    public Transform thumbTip;
    public Transform indexTip;
    public Transform handAnchor;
    public Transform palm;

    [Header("Camera")]
    public Camera referenceCamera;

    [Header("V band (meters)")]
    public float vDistMin = 0.05f;
    public float vDistMax = 0.12f;
    public float vSustainSeconds = 0.50f;

    [Header("Drawing motion")]
    public float minMotionDistance = 0.10f;
    [Range(0f, 90f)] public float rightwardConeDegrees = 60f;
    public float maxStraightnessDeviation = 0.35f;
    public float pathSampleDistance = 0.01f;

    [Header("VHeld arming")]
    public float vArmMinRightwardTravel = 0.03f;

    [Header("VHeld exit")]
    public float stopMotionDistance = 0.015f;
    public float stopHoldSeconds = 0.30f;

    [Header("Palm swipe")]
    public float palmSwipeWindowSeconds = 3.0f;
    public float palmSwipeMinSpeed = 0.8f;

    [Header("After confirm")]
    public float confirmCooldownSeconds = 1.0f;

    [Header("Output")]
    public string translateGestureName = "Translate";
    public MsgSender msgSender;

    [Header("Status")]
    [SerializeField] private State currentState = State.Idle;
    [SerializeField] private float currentThumbIndexDistance;
    [SerializeField] private float currentRightwardTravel;
    [SerializeField] private float currentPerpDeviation;

    public State CurrentState => currentState;
    public event Action<State, State> OnStateChanged;
    public event Action OnTranslateConfirmed;
    public event Action OnTranslateCancelled;

    private bool _inVBand;
    private float _vBandEnterTime = -1f;
    private Vector3 _vBandEnterAnchor;
    private Vector3 _areaCornerA;
    private Vector3 _areaCornerB;
    private float _areaDefinedTime;
    private Vector3 _lastSwipeSample;
    private float _lastSwipeSampleTime;
    private Vector3 _lastMotionAnchor;
    private float _lastMotionTime;
    private float _confirmCooldownUntil = -1f;
    private readonly List<Vector3> _pathSamples = new List<Vector3>(256);

    void Update()
    {
        if (thumbTip == null || indexTip == null) return;

        Vector3 anchor = GetHandAnchorPosition();
        currentThumbIndexDistance = Vector3.Distance(thumbTip.position, indexTip.position);
        bool inBand = currentThumbIndexDistance >= vDistMin && currentThumbIndexDistance <= vDistMax;
        float now = Time.time;

        switch (currentState)
        {
            case State.Idle:
                HandleIdle(anchor, inBand, now);
                break;
            case State.VHeld:
                HandleVHeld(anchor, inBand, now);
                break;
            case State.AreaDefined:
                HandleAreaDefined(now);
                break;
        }

        _inVBand = inBand;
    }

    void HandleIdle(Vector3 anchor, bool inBand, float now)
    {
        // Block all re-arming for a brief window after a successful Confirm() so a
        // post-swipe motion can't immediately trigger another Translate START.
        if (now < _confirmCooldownUntil) return;

        if (!inBand)
        {
            _vBandEnterTime = -1f;
            return;
        }

        if (!_inVBand)
        {
            _vBandEnterTime = now;
            _vBandEnterAnchor = anchor;
            return;
        }

        if (now - _vBandEnterTime < vSustainSeconds) return;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return;
        Vector3 right = cam.transform.right;
        Vector3 delta = anchor - _vBandEnterAnchor;
        float forward = Vector3.Dot(delta, right);
        if (forward < vArmMinRightwardTravel) return;
        if (Vector3.Angle(delta, right) > rightwardConeDegrees) return;

        _areaCornerA = _vBandEnterAnchor;
        _pathSamples.Clear();
        _pathSamples.Add(_areaCornerA);
        if (Vector3.Distance(_areaCornerA, anchor) > pathSampleDistance)
            _pathSamples.Add(anchor);
        currentRightwardTravel = forward;
        currentPerpDeviation = 0f;
        _lastMotionAnchor = anchor;
        _lastMotionTime = now;
        TransitionTo(State.VHeld);
        SendEvent(translateGestureName, "START");
    }

    void HandleVHeld(Vector3 anchor, bool inBand, float now)
    {
        AppendPathSample(anchor);
        EvaluateMotion(anchor);

        if (Vector3.Distance(anchor, _lastMotionAnchor) > stopMotionDistance)
        {
            _lastMotionAnchor = anchor;
            _lastMotionTime = now;
        }
        bool stopped = (now - _lastMotionTime) > stopHoldSeconds;

        if (inBand && !stopped) return;

        _areaCornerB = anchor;
        string exitReason = inBand ? "motion stopped" : "V band released";
        Debug.Log($"[TranslateGestureDetector] VHeld exit ({exitReason})");

        if (MotionQualifies())
        {
            _areaDefinedTime = now;
            _lastSwipeSample = GetPalmPosition();
            _lastSwipeSampleTime = now;
            SendEvent(translateGestureName, "END");
            TransitionTo(State.AreaDefined);
        }
        else
        {
            SendEvent(translateGestureName, "FAIL");
            TransitionTo(State.Idle);
        }
    }

    void HandleAreaDefined(float now)
    {
        if (now - _areaDefinedTime > palmSwipeWindowSeconds)
        {
            Cancel("palm-swipe window expired");
            return;
        }
        if (PalmSwipeDetected(now)) Confirm();
    }

    void AppendPathSample(Vector3 anchor)
    {
        if (_pathSamples.Count == 0 ||
            Vector3.Distance(_pathSamples[_pathSamples.Count - 1], anchor) > pathSampleDistance)
        {
            _pathSamples.Add(anchor);
        }
    }

    void EvaluateMotion(Vector3 anchor)
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return;
        Vector3 right = cam.transform.right;
        Vector3 delta = anchor - _areaCornerA;
        currentRightwardTravel = Vector3.Dot(delta, right);

        if (delta.sqrMagnitude < 0.0001f)
        {
            currentPerpDeviation = 0f;
            return;
        }

        Vector3 dirN = delta.normalized;
        float maxPerp = 0f;
        for (int i = 0; i < _pathSamples.Count; i++)
        {
            Vector3 toP = _pathSamples[i] - _areaCornerA;
            Vector3 perpComp = toP - dirN * Vector3.Dot(toP, dirN);
            float m = perpComp.magnitude;
            if (m > maxPerp) maxPerp = m;
        }
        currentPerpDeviation = maxPerp;
    }

    bool MotionQualifies()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return false;
        Vector3 delta = _areaCornerB - _areaCornerA;
        Vector3 right = cam.transform.right;
        float forward = Vector3.Dot(delta, right);

        if (forward < minMotionDistance)
        {
            Debug.Log($"[TranslateGestureDetector] reject: forward {forward*100f:F1}cm < {minMotionDistance*100f:F1}cm");
            return false;
        }
        float angle = Vector3.Angle(delta, right);
        if (angle > rightwardConeDegrees)
        {
            Debug.Log($"[TranslateGestureDetector] reject: angle {angle:F1}° > {rightwardConeDegrees:F1}°");
            return false;
        }
        if (maxStraightnessDeviation > 0f && currentPerpDeviation > forward * maxStraightnessDeviation)
        {
            Debug.Log($"[TranslateGestureDetector] reject: perp {currentPerpDeviation*100f:F1}cm > {forward*maxStraightnessDeviation*100f:F1}cm");
            return false;
        }
        return true;
    }

    bool PalmSwipeDetected(float now)
    {
        Vector3 palmPos = GetPalmPosition();
        float dt = now - _lastSwipeSampleTime;
        if (dt <= 0f)
        {
            _lastSwipeSample = palmPos;
            _lastSwipeSampleTime = now;
            return false;
        }
        Vector3 vel = (palmPos - _lastSwipeSample) / dt;
        _lastSwipeSample = palmPos;
        _lastSwipeSampleTime = now;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector3 right = cam != null ? cam.transform.right : Vector3.right;
        float horizontalSpeed = Mathf.Abs(Vector3.Dot(vel, right));
        return horizontalSpeed > palmSwipeMinSpeed;
    }

    Vector3 GetHandAnchorPosition()
    {
        if (handAnchor != null) return handAnchor.position;
        return (thumbTip.position + indexTip.position) * 0.5f;
    }

    Vector3 GetPalmPosition()
    {
        if (palm != null) return palm.position;
        if (handAnchor != null) return handAnchor.position;
        return (thumbTip.position + indexTip.position) * 0.5f;
    }

    void Confirm()
    {
        Debug.Log($"[TranslateGestureDetector] CONFIRMED. A={_areaCornerA}  B={_areaCornerB}");
        // SendEvent(translateGestureName, "END");
        SendRecognized(translateGestureName);
        _confirmCooldownUntil = Time.time + confirmCooldownSeconds;
        try { OnTranslateConfirmed?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
        TransitionTo(State.Idle);
    }

    void Cancel(string reason)
    {
        Debug.Log($"[TranslateGestureDetector] cancelled: {reason}");
        SendEvent(translateGestureName, "FAIL");
        try { OnTranslateCancelled?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
        TransitionTo(State.Idle);
    }

    void TransitionTo(State next)
    {
        if (next == currentState) return;
        State prev = currentState;
        currentState = next;
        Debug.Log(
            $"[TranslateGestureDetector] {prev} -> {next}  " +
            $"dist={currentThumbIndexDistance*100f:F1}cm  " +
            $"rightTravel={currentRightwardTravel*100f:F1}cm  " +
            $"perp={currentPerpDeviation*100f:F1}cm"
        );
        try { OnStateChanged?.Invoke(prev, next); } catch (Exception e) { Debug.LogError(e); }
    }

    void SendEvent(string gestureName, string eventType)
    {
        if (msgSender == null) return;
        var payload = new GestureEventPayload
        {
            gestureName = gestureName,
            eventType = eventType,
        };
        msgSender.SendGestureEvent(payload);
    }

    void SendRecognized(string gestureName)
    {
        if (msgSender == null) return;
        var payload = new GestureEventPayload
        {
            gestureName = gestureName,
            eventType = "RECOGNIZED",
        };
        msgSender.SendGestureRecognized(payload);
    }
}
