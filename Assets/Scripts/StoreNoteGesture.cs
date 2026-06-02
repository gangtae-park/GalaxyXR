using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Detects a Store/Save gesture: keep the left palm open, then draw a short
/// trajectory over that palm with a fingertip. When the stroke completes, a
/// world-space note card appears near the palm.
/// </summary>
public class StoreNoteGesture : MonoBehaviour
{
    [Header("Left hand references")]
    public Transform leftPalm;
    public Transform leftWrist;
    public Transform leftThumbTip;
    public Transform leftIndexTip;
    public Transform leftMiddleTip;
    public Transform leftRingTip;
    public Transform leftLittleTip;

    [Header("Drawing finger")]
    [Tooltip("Usually the opposite-hand index tip.")]
    public Transform drawingFingerTip;

    [Header("UI")]
    public StoreNoteCard noteCardPrefab;
    public Camera referenceCamera;
    public Vector3 noteOffsetFromPalm = new Vector3(0.08f, 0.08f, 0.05f);
    public bool replacePreviousNote = false;

    [Header("Open palm detection")]
    [Tooltip("Minimum distance from palm to each fingertip for the palm to count as open.")]
    public float minFingerSpreadFromPalm = 0.075f;
    [Tooltip("If true, palm normal should roughly face the camera.")]
    public bool requirePalmFacingCamera = true;
    [Range(-1f, 1f)] public float minPalmFacingDot = 0.25f;

    [Header("Drawing detection")]
    public float maxFingerDistanceFromPalmPlane = 0.08f;
    public float maxFingerDistanceFromPalmCenter = 0.22f;
    public float minSampleDistance = 0.008f;
    public int minPointCount = 8;
    public float minStrokeLength = 0.08f;
    public float maxPauseBeforeComplete = 0.35f;
    public float cooldownSeconds = 1f;

    [Header("Preview")]
    public LineRenderer strokePreviewLine;

    public event Action<StoreNoteCard> OnNoteOpened;

    readonly List<Vector3> _points = new List<Vector3>();
    bool _drawing;
    float _lastPointTime;
    float _cooldownUntil;
    StoreNoteCard _currentNote;

    void OnEnable()
    {
        SetPreviewVisible(false);
    }

    void Update()
    {
        if (!ReferencesReady())
        {
            CancelDrawing();
            return;
        }

        if (Time.time < _cooldownUntil) return;

        bool palmOpen = IsLeftPalmOpen();
        bool fingerOnPalm = palmOpen && IsDrawingFingerOnPalm();

        if (fingerOnPalm)
        {
            AddPoint(drawingFingerTip.position);
        }
        else if (_drawing && Time.time - _lastPointTime >= maxPauseBeforeComplete)
        {
            CompleteOrCancel();
        }

        if (!palmOpen && _drawing)
        {
            CompleteOrCancel();
        }
    }

    bool ReferencesReady()
    {
        return leftPalm != null &&
               leftThumbTip != null &&
               leftIndexTip != null &&
               leftMiddleTip != null &&
               leftRingTip != null &&
               leftLittleTip != null &&
               drawingFingerTip != null;
    }

    bool IsLeftPalmOpen()
    {
        if (DistanceFromPalm(leftThumbTip) < minFingerSpreadFromPalm) return false;
        if (DistanceFromPalm(leftIndexTip) < minFingerSpreadFromPalm) return false;
        if (DistanceFromPalm(leftMiddleTip) < minFingerSpreadFromPalm) return false;
        if (DistanceFromPalm(leftRingTip) < minFingerSpreadFromPalm) return false;
        if (DistanceFromPalm(leftLittleTip) < minFingerSpreadFromPalm) return false;

        if (!requirePalmFacingCamera) return true;

        Camera cam = CurrentCamera();
        if (cam == null) return true;

        Vector3 palmNormal = PalmNormal();
        Vector3 toCamera = (cam.transform.position - leftPalm.position).normalized;
        return Mathf.Abs(Vector3.Dot(palmNormal, toCamera)) >= minPalmFacingDot;
    }

    float DistanceFromPalm(Transform tip)
    {
        return Vector3.Distance(leftPalm.position, tip.position);
    }

    bool IsDrawingFingerOnPalm()
    {
        Vector3 palmNormal = PalmNormal();
        Vector3 palmToFinger = drawingFingerTip.position - leftPalm.position;
        float planeDistance = Mathf.Abs(Vector3.Dot(palmToFinger, palmNormal));
        float centerDistance = palmToFinger.magnitude;

        return planeDistance <= maxFingerDistanceFromPalmPlane &&
               centerDistance <= maxFingerDistanceFromPalmCenter;
    }

    Vector3 PalmNormal()
    {
        if (leftWrist != null)
        {
            Vector3 acrossPalm = leftIndexTip.position - leftLittleTip.position;
            Vector3 upPalm = leftMiddleTip.position - leftWrist.position;
            Vector3 normal = Vector3.Cross(acrossPalm, upPalm);
            if (normal.sqrMagnitude > 0.000001f) return normal.normalized;
        }

        Camera cam = CurrentCamera();
        if (cam != null) return -cam.transform.forward;
        return transform.forward;
    }

    void AddPoint(Vector3 point)
    {
        if (!_drawing)
        {
            _drawing = true;
            _points.Clear();
            SetPreviewVisible(true);
        }

        if (_points.Count == 0 || Vector3.Distance(_points[_points.Count - 1], point) >= minSampleDistance)
        {
            _points.Add(point);
            _lastPointTime = Time.time;
            UpdatePreview();
        }
    }

    void CompleteOrCancel()
    {
        if (!_drawing) return;

        bool valid = _points.Count >= minPointCount && StrokeLength() >= minStrokeLength;
        if (valid)
        {
            OpenNoteCard();
            _cooldownUntil = Time.time + cooldownSeconds;
        }

        CancelDrawing();
    }

    float StrokeLength()
    {
        float length = 0f;
        for (int i = 1; i < _points.Count; i++)
        {
            length += Vector3.Distance(_points[i - 1], _points[i]);
        }
        return length;
    }

    void OpenNoteCard()
    {
        if (replacePreviousNote && _currentNote != null)
        {
            Destroy(_currentNote.gameObject);
            _currentNote = null;
        }

        Camera cam = CurrentCamera();
        Vector3 palmRight = (leftIndexTip.position - leftLittleTip.position).normalized;
        Vector3 palmUp = leftMiddleTip.position - (leftWrist != null ? leftWrist.position : leftPalm.position);
        if (palmUp.sqrMagnitude < 0.000001f) palmUp = Vector3.up;
        palmUp.Normalize();

        Vector3 spawnPosition = leftPalm.position
            + palmRight * noteOffsetFromPalm.x
            + palmUp * noteOffsetFromPalm.y
            + PalmNormal() * noteOffsetFromPalm.z;

        StoreNoteCard card;
        if (noteCardPrefab != null)
        {
            card = Instantiate(noteCardPrefab, spawnPosition, Quaternion.identity);
        }
        else
        {
            GameObject go = new GameObject("StoreNoteCard", typeof(RectTransform));
            go.transform.position = spawnPosition;
            card = go.AddComponent<StoreNoteCard>();
        }

        card.Initialize(cam, "Store note");
        _currentNote = card;

        try { OnNoteOpened?.Invoke(card); }
        catch (Exception e) { Debug.LogError($"[StoreNoteGesture] subscriber threw: {e}"); }

        Debug.Log("[StoreNoteGesture] Store note opened.");
    }

    void CancelDrawing()
    {
        _drawing = false;
        _points.Clear();
        SetPreviewVisible(false);
    }

    void UpdatePreview()
    {
        if (strokePreviewLine == null) return;

        strokePreviewLine.useWorldSpace = true;
        strokePreviewLine.positionCount = _points.Count;
        for (int i = 0; i < _points.Count; i++)
        {
            strokePreviewLine.SetPosition(i, _points[i]);
        }
    }

    void SetPreviewVisible(bool visible)
    {
        if (strokePreviewLine != null) strokePreviewLine.enabled = visible;
    }

    Camera CurrentCamera()
    {
        return referenceCamera != null ? referenceCamera : Camera.main;
    }
}
