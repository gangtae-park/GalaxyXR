using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Captures the Translate interaction:
///   1. Short stationary pinch chooses a reference point.
///   2. Release the pinch and draw an open-air rectangle-ish path.
///   3. Pinch again to confirm the region.
///
/// The final region is the viewport-space bounding box of the drawn points,
/// which is more forgiving than requiring a perfectly rectangular stroke.
/// </summary>
public class TranslateRegionCapture : MonoBehaviour
{
    enum CaptureState
    {
        Idle,
        AnchorPinch,
        Drawing
    }

    [Header("References")]
    public Transform indexTip;
    public InputActionReference pinchAction;
    public MsgSender msgSender;
    public Camera referenceCamera;

    [Header("Pinch")]
    [Range(0f, 1f)] public float pinchValueThreshold = 0.7f;
    [Tooltip("The first pinch must stay mostly still to become a Translate anchor.")]
    public float maxAnchorMoveDistance = 0.035f;
    [Tooltip("Reject accidental taps that are too short to be intentional.")]
    public float minAnchorHoldSeconds = 0.08f;
    [Tooltip("Reject long holds so ordinary held-pinch gestures keep feeling distinct.")]
    public float maxAnchorHoldSeconds = 0.75f;

    [Header("Drawing")]
    public float minSampleDistance = 0.008f;
    public int minDrawPointCount = 6;
    [Tooltip("Minimum normalized viewport width/height before a region can be confirmed.")]
    public Vector2 minViewportSize = new Vector2(0.03f, 0.03f);
    [Tooltip("Seconds before an unfinished Translate selection resets.")]
    public float drawTimeoutSeconds = 6f;

    [Header("Preview")]
    [Tooltip("Optional LineRenderer used to preview the selected rectangle.")]
    public LineRenderer previewLine;
    public bool hidePreviewWhenIdle = true;

    [Header("Events")]
    public string gestureName = "Translate";

    public event Action<Rect> OnRegionConfirmed;
    public event Action OnRegionCancelled;

    CaptureState _state = CaptureState.Idle;
    bool _wasPinching;
    float _anchorStartTime;
    float _drawingStartTime;
    float _maxAnchorTravel;
    Vector3 _anchorWorld;
    Vector2 _anchorViewport;
    readonly List<Vector3> _drawWorldPoints = new List<Vector3>();
    readonly List<Vector2> _drawViewportPoints = new List<Vector2>();

    void OnEnable()
    {
        pinchAction?.action.Enable();
        SetPreviewVisible(false);
    }

    void OnDisable()
    {
        pinchAction?.action.Disable();
        CancelSelection("disabled");
    }

    void Update()
    {
        if (pinchAction == null || pinchAction.action == null)
        {
            CancelSelection("pinch action missing");
            return;
        }

        if (indexTip == null)
        {
            CancelSelection("index tip missing");
            return;
        }

        bool isPinching = pinchAction.action.ReadValue<float>() >= pinchValueThreshold;

        switch (_state)
        {
            case CaptureState.Idle:
                if (isPinching && !_wasPinching) BeginAnchorPinch();
                break;

            case CaptureState.AnchorPinch:
                UpdateAnchorPinch(isPinching);
                break;

            case CaptureState.Drawing:
                UpdateDrawing(isPinching);
                break;
        }

        _wasPinching = isPinching;
    }

    void BeginAnchorPinch()
    {
        _state = CaptureState.AnchorPinch;
        _anchorStartTime = Time.time;
        _maxAnchorTravel = 0f;
        _anchorWorld = indexTip.position;
        _drawWorldPoints.Clear();
        _drawViewportPoints.Clear();

        Camera cam = CurrentCamera();
        _anchorViewport = cam != null
            ? ToViewport2D(cam, _anchorWorld)
            : Vector2.zero;

        Debug.Log("[TranslateRegionCapture] Anchor pinch started");
    }

    void UpdateAnchorPinch(bool isPinching)
    {
        _maxAnchorTravel = Mathf.Max(_maxAnchorTravel, Vector3.Distance(_anchorWorld, indexTip.position));

        if (!isPinching && _wasPinching)
        {
            float held = Time.time - _anchorStartTime;
            bool validAnchor = held >= minAnchorHoldSeconds &&
                               held <= maxAnchorHoldSeconds &&
                               _maxAnchorTravel <= maxAnchorMoveDistance;

            if (validAnchor)
            {
                BeginDrawing();
            }
            else
            {
                ResetToIdle();
                Debug.Log(
                    $"[TranslateRegionCapture] Anchor ignored. held={held:F2}s travel={_maxAnchorTravel:F3}m"
                );
            }
        }
    }

    void BeginDrawing()
    {
        _state = CaptureState.Drawing;
        _drawingStartTime = Time.time;
        _drawWorldPoints.Clear();
        _drawViewportPoints.Clear();
        AddDrawPoint(_anchorWorld, force: true);
        AddDrawPoint(indexTip.position, force: true);
        SetPreviewVisible(true);

        SendGestureEvent("START");
        Debug.Log("[TranslateRegionCapture] Drawing started");
    }

    void UpdateDrawing(bool isPinching)
    {
        if (Time.time - _drawingStartTime > drawTimeoutSeconds)
        {
            CancelSelection("drawing timed out");
            return;
        }

        if (isPinching && !_wasPinching)
        {
            ConfirmRegion();
            return;
        }

        AddDrawPoint(indexTip.position, force: false);
        UpdatePreview();
    }

    void AddDrawPoint(Vector3 worldPoint, bool force)
    {
        Camera cam = CurrentCamera();
        if (cam == null) return;

        if (!force && _drawWorldPoints.Count > 0 &&
            Vector3.Distance(_drawWorldPoints[_drawWorldPoints.Count - 1], worldPoint) < minSampleDistance)
        {
            return;
        }

        _drawWorldPoints.Add(worldPoint);
        _drawViewportPoints.Add(ToViewport2D(cam, worldPoint));
    }

    void ConfirmRegion()
    {
        AddDrawPoint(indexTip.position, force: true);

        if (!TryGetViewportRect(out Rect rect))
        {
            CancelSelection("region too small or incomplete");
            return;
        }

        msgSender?.SendTranslateRegion(rect, _anchorViewport);
        SendGestureEvent("END");

        try { OnRegionConfirmed?.Invoke(rect); }
        catch (Exception e) { Debug.LogError($"[TranslateRegionCapture] subscriber threw: {e}"); }

        Debug.Log($"[TranslateRegionCapture] Region confirmed: {rect}");
        ResetToIdle();
    }

    bool TryGetViewportRect(out Rect rect)
    {
        rect = Rect.zero;
        if (_drawViewportPoints.Count < minDrawPointCount) return false;

        float xMin = float.MaxValue;
        float yMin = float.MaxValue;
        float xMax = float.MinValue;
        float yMax = float.MinValue;

        for (int i = 0; i < _drawViewportPoints.Count; i++)
        {
            Vector2 p = _drawViewportPoints[i];
            xMin = Mathf.Min(xMin, p.x);
            yMin = Mathf.Min(yMin, p.y);
            xMax = Mathf.Max(xMax, p.x);
            yMax = Mathf.Max(yMax, p.y);
        }

        rect = Rect.MinMaxRect(
            Mathf.Clamp01(xMin),
            Mathf.Clamp01(yMin),
            Mathf.Clamp01(xMax),
            Mathf.Clamp01(yMax)
        );

        return rect.width >= minViewportSize.x && rect.height >= minViewportSize.y;
    }

    void UpdatePreview()
    {
        if (previewLine == null) return;
        if (!TryGetViewportRect(out Rect rect)) return;

        Camera cam = CurrentCamera();
        if (cam == null) return;

        float depth = Mathf.Max(0.15f, Vector3.Dot(_anchorWorld - cam.transform.position, cam.transform.forward));
        Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(rect.xMin, rect.yMin, depth));
        Vector3 topLeft = cam.ViewportToWorldPoint(new Vector3(rect.xMin, rect.yMax, depth));
        Vector3 topRight = cam.ViewportToWorldPoint(new Vector3(rect.xMax, rect.yMax, depth));
        Vector3 bottomRight = cam.ViewportToWorldPoint(new Vector3(rect.xMax, rect.yMin, depth));

        previewLine.positionCount = 5;
        previewLine.useWorldSpace = true;
        previewLine.SetPosition(0, bottomLeft);
        previewLine.SetPosition(1, topLeft);
        previewLine.SetPosition(2, topRight);
        previewLine.SetPosition(3, bottomRight);
        previewLine.SetPosition(4, bottomLeft);
    }

    void CancelSelection(string reason)
    {
        if (_state == CaptureState.Idle) return;

        Debug.Log($"[TranslateRegionCapture] Cancelled: {reason}");
        SendGestureEvent("FAIL");

        try { OnRegionCancelled?.Invoke(); }
        catch (Exception e) { Debug.LogError($"[TranslateRegionCapture] subscriber threw: {e}"); }

        ResetToIdle();
    }

    void ResetToIdle()
    {
        _state = CaptureState.Idle;
        _drawWorldPoints.Clear();
        _drawViewportPoints.Clear();
        if (hidePreviewWhenIdle) SetPreviewVisible(false);
    }

    void SendGestureEvent(string eventType)
    {
        if (msgSender == null) return;

        var payload = new CircleGestureRecognizer.CircleGesturePayload
        {
            gestureName = gestureName,
            eventType = eventType,
        };
        msgSender.SendGestureEvent(payload);
    }

    Camera CurrentCamera()
    {
        return referenceCamera != null ? referenceCamera : Camera.main;
    }

    static Vector2 ToViewport2D(Camera cam, Vector3 worldPoint)
    {
        Vector3 p = cam.WorldToViewportPoint(worldPoint);
        return new Vector2(p.x, p.y);
    }

    void SetPreviewVisible(bool visible)
    {
        if (previewLine != null) previewLine.enabled = visible;
    }
}
