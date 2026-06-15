using System;
using System.Collections.Generic;
using UnityEngine;

/*
TranslateGestureDetector (continuous Jackknife)

No explicit state machine. Two modes only, controlled by a single bool:

  awaitingSwipe = false  ->  continuous sampling + periodic Jackknife classify
  awaitingSwipe = true   ->  ignore trajectory, watch for palm swipe

Sampling:
  Every minFrameInterval seconds, snapshot the configured handJoints into a
  feature vector and push to a rolling buffer (oldest frames past
  maxBufferSeconds are dropped).

Recognition:
  Every recognitionIntervalSeconds, the current buffer is handed to
  JackknifeTranslateRecognizer. If the result matches translateGestureName:
    - lock AreaCornerA / AreaCornerB from buffer extents
    - clear the buffer
    - fire OnTranslateStarted + OnTranslateAreaDefined back-to-back
    - flip awaitingSwipe = true

Swipe:
  In awaitingSwipe, watch palm velocity. Above palmSwipeMinSpeed -> Confirm.
  After palmSwipeWindowSeconds -> Cancel.

External pause:
  TranslateTemplateRecorder sets externalPause = true while recording so this
  detector doesn't sample/classify on the same hand motion the recorder is
  capturing. Detector clears its buffer while paused.

Feature builder (BuildFeatureFrame) is public so TranslateTemplateRecorder can
reuse it -- the joint configuration lives here and both recording and inference
share the same vector layout.
*/

public class TranslateGestureDetector : MonoBehaviour
{
    [Header("Hand pose features")]
    public Transform[] handJoints;
    public Transform jointOrigin;
    public bool normalizeToCameraOrientation = true;
    public Transform thumbTip;
    public Transform indexTip;
    public Transform palm;
    public Transform handAnchor;
    public Camera referenceCamera;

    [Header("Sampling")]
    public float minFrameInterval = 0.03f;
    public float maxBufferSeconds = 2.5f;

    [Header("Recognition")]
    public float recognitionIntervalSeconds = 0.2f;
    public int minFramesForRecognition = 12;

    [Header("Palm swipe")]
    public float palmSwipeWindowSeconds = 2.0f;
    public float palmSwipeMinSpeed = 0.8f;

    [Header("After confirm")]
    public float confirmCooldownSeconds = 1.0f;

    [Header("Identity")]
    public string translateGestureName = "Translate";

    [Header("References")]
    public JackknifeTranslateRecognizer translateRecognizer;

    [Header("External control")]
    public bool externalPause = false;

    [Header("Status (read-only)")]
    [SerializeField] private bool awaitingSwipe;
    [SerializeField] private int bufferFrameCount;

    public string GestureName => translateGestureName;
    public bool IsAwaitingSwipe => awaitingSwipe;
    public Vector3 AreaCornerA => _cornerA;
    public Vector3 AreaCornerB => _cornerB;

    public event Action<string> OnTranslateStarted;
    public event Action<string> OnTranslateAreaDefined;
    public event Action<string> OnTranslateConfirmed;
    public event Action<string> OnTranslateCancelled;

    private struct PoseFrame
    {
        public float time;
        public float[] features;
        public Vector3 indexTipPos;
        public Vector3 thumbTipPos;
    }

    private readonly List<PoseFrame> _buffer = new List<PoseFrame>(128);
    private float _lastSampleTime = -1f;
    private float _nextRecognitionTime;
    private float _confirmCooldownUntil = -1f;
    private float _awaitingSwipeStartTime;
    private Vector3 _lastSwipeSample;
    private float _lastSwipeSampleTime;
    private Vector3 _cornerA;
    private Vector3 _cornerB;

    public float[] BuildFeatureFrame()
    {
        if (handJoints == null || handJoints.Length == 0) return null;
        int n = handJoints.Length;
        Vector3 origin = jointOrigin != null
            ? jointOrigin.position
            : (handJoints[0] != null ? handJoints[0].position : Vector3.zero);

        Quaternion invCam = Quaternion.identity;
        if (normalizeToCameraOrientation)
        {
            Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
            if (cam != null) invCam = Quaternion.Inverse(cam.transform.rotation);
        }

        float[] f = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            if (handJoints[i] == null) continue;
            Vector3 p = handJoints[i].position - origin;
            if (normalizeToCameraOrientation) p = invCam * p;
            f[3 * i + 0] = p.x;
            f[3 * i + 1] = p.y;
            f[3 * i + 2] = p.z;
        }
        return f;
    }

    void Update()
    {
        if (externalPause)
        {
            ClearBuffer();
            return;
        }
        if (thumbTip == null || indexTip == null) return;
        if (handJoints == null || handJoints.Length == 0) return;

        float now = Time.time;
        if (now < _confirmCooldownUntil) return;

        if (awaitingSwipe)
        {
            HandleAwaitingSwipe(now);
            return;
        }

        SampleFrameIfDue(now);

        if (now >= _nextRecognitionTime)
        {
            _nextRecognitionTime = now + recognitionIntervalSeconds;
            TryRecognize(now);
        }
    }

    void SampleFrameIfDue(float now)
    {
        if (_lastSampleTime > 0f && (now - _lastSampleTime) < minFrameInterval) return;
        _lastSampleTime = now;

        float[] features = BuildFeatureFrame();
        if (features == null) return;

        _buffer.Add(new PoseFrame
        {
            time = now,
            features = features,
            indexTipPos = indexTip.position,
            thumbTipPos = thumbTip.position,
        });

        // Drop frames older than maxBufferSeconds.
        float cutoff = now - maxBufferSeconds;
        while (_buffer.Count > 0 && _buffer[0].time < cutoff) _buffer.RemoveAt(0);

        bufferFrameCount = _buffer.Count;
    }

    void TryRecognize(float now)
    {
        if (_buffer.Count < minFramesForRecognition) return;
        if (translateRecognizer == null) return;

        var trajectory = new List<float[]>(_buffer.Count);
        foreach (var f in _buffer) trajectory.Add(f.features);

        string label = null;
        try { label = translateRecognizer.Recognize(trajectory); }
        catch (Exception e) { Debug.LogError($"[Translate] Jackknife threw: {e}"); }

        if (label != translateGestureName) return;

        // Match. Lock corners from buffer extents.
        _cornerA = _buffer[0].indexTipPos;
        _cornerB = _buffer[_buffer.Count - 1].thumbTipPos;

        awaitingSwipe = true;
        _awaitingSwipeStartTime = now;
        _lastSwipeSample = GetPalmPosition();
        _lastSwipeSampleTime = now;
        ClearBuffer();

        Debug.Log($"[Translate] Jackknife matched -> AwaitingSwipe (A={_cornerA} B={_cornerB})");

        try { OnTranslateStarted?.Invoke(translateGestureName); } catch (Exception e) { Debug.LogError(e); }
        try { OnTranslateAreaDefined?.Invoke(translateGestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void HandleAwaitingSwipe(float now)
    {
        if (now - _awaitingSwipeStartTime > palmSwipeWindowSeconds)
        {
            Debug.Log("[Translate] palm-swipe window expired");
            awaitingSwipe = false;
            try { OnTranslateCancelled?.Invoke(translateGestureName); } catch (Exception e) { Debug.LogError(e); }
            return;
        }
        if (PalmSwipeDetected(now))
        {
            Debug.Log("[Translate] CONFIRMED via palm swipe");
            awaitingSwipe = false;
            _confirmCooldownUntil = now + confirmCooldownSeconds;
            try { OnTranslateConfirmed?.Invoke(translateGestureName); } catch (Exception e) { Debug.LogError(e); }
        }
    }

    bool PalmSwipeDetected(float now)
    {
        Vector3 palmPos = GetPalmPosition();
        float dt = now - _lastSwipeSampleTime;
        if (dt <= 0f) { _lastSwipeSample = palmPos; _lastSwipeSampleTime = now; return false; }
        Vector3 vel = (palmPos - _lastSwipeSample) / dt;
        _lastSwipeSample = palmPos;
        _lastSwipeSampleTime = now;
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector3 right = cam != null ? cam.transform.right : Vector3.right;
        float horizontalSpeed = Mathf.Abs(Vector3.Dot(vel, right));
        return horizontalSpeed > palmSwipeMinSpeed;
    }

    void ClearBuffer()
    {
        _buffer.Clear();
        bufferFrameCount = 0;
        _lastSampleTime = -1f;
        _nextRecognitionTime = 0f;
    }

    Vector3 GetPalmPosition()
    {
        if (palm != null) return palm.position;
        if (handAnchor != null) return handAnchor.position;
        return (thumbTip.position + indexTip.position) * 0.5f;
    }
}
