using System;
using UnityEngine;

public class GestureRouter : MonoBehaviour
{
    [Header("References")]
    public PinchStrokeCapture strokeCapture;
    public MsgSender msgSender;
    [Tooltip("Optional unified recognizer from the newer gt_work gesture flow.")]
    public JackknifeUnifiedRecognizer jackknifeRecognizer;

    [Header("Routing")]
    public string pendingReferentName = "Pending";
    public string searchGestureName = "Search/Find Info";
    public bool sendLocalOnlyGestureEvents = false;

    public event Action<string> OnGestureRecognized;
    public event Action OnGestureFailed;

    void OnEnable()
    {
        if (strokeCapture != null)
        {
            strokeCapture.OnStrokeStarted   += HandleStarted;
            strokeCapture.OnStrokeCompleted += HandleCompleted;
            strokeCapture.OnStrokeCancelled += HandleCancelled;
        }
    }

    void OnDisable()
    {
        if (strokeCapture != null)
        {
            strokeCapture.OnStrokeStarted   -= HandleStarted;
            strokeCapture.OnStrokeCompleted -= HandleCompleted;
            strokeCapture.OnStrokeCancelled -= HandleCancelled;
        }
    }

    void HandleStarted(Stroke stroke)
    {
        SendEvent(pendingReferentName, "START");
    }

    void HandleCompleted(Stroke stroke)
    {
        Recognize(searchGestureName);
    }

    void HandleCancelled()
    {
        SendEvent(pendingReferentName, "FAIL");
        try { OnGestureFailed?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
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

    void Recognize(string gestureName)
    {
        Debug.Log($"[GestureRouter] RECOGNIZED: {gestureName}");

        if (!IsLocalOnlyGesture(gestureName) || sendLocalOnlyGestureEvents)
        {
            SendEvent(gestureName, "END");
            SendEvent(gestureName, "RECOGNIZED");
        }
        else
        {
            Debug.Log($"[GestureRouter] Local-only gesture recognized: {gestureName}. Skipping Python/VLM routing.");
        }

        try { OnGestureRecognized?.Invoke(gestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    bool IsLocalOnlyGesture(string gestureName)
    {
        return string.Equals(gestureName, searchGestureName, StringComparison.Ordinal);
    }
}
