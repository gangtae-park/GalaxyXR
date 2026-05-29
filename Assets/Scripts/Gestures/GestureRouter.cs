using System;
using System.Collections.Generic;
using UnityEngine;


/*
Listens to a PinchStrokeCapture and runs every registered classifier on each
completed stroke. The classifier with the best confidence above the global
threshold wins; its GestureName is sent to Python via MsgSender.
*/

public class GestureRouter : MonoBehaviour
{
    [Header("References")]
    public PinchStrokeCapture strokeCapture;
    public MsgSender msgSender;
    public JackknifeGestureRecognizer jackknifeRecognizer;

    [Header("Routing")]
    public string pendingReferentName = "Pending";

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
        string referentName = null;

        if (jackknifeRecognizer != null)
        {
            try { referentName = jackknifeRecognizer.Recognize(stroke); }
            catch (Exception e) { Debug.LogError($"[GestureRouter] Jackknife threw: {e}"); }
        }
        else
        {
            Debug.LogError($"[GestureRouter] Can't find Jackknife recognizer");
            return;
        }

        if (!string.IsNullOrEmpty(referentName))
        {
            Debug.Log($"[GestureRouter] RECOGNIZED: {referentName}");
            // Send END with the final name so Python's handler dispatches correctly.
            SendEvent(referentName, "END");
            // Then send a RECOGNIZED packet (informational; Python END already triggers VLM).
            SendRecognized(referentName);
            try { OnGestureRecognized?.Invoke(referentName); } catch (Exception e) { Debug.LogError(e); }
        }
        else
        {
            Debug.Log("[GestureRouter] FAIL");
            SendEvent(pendingReferentName, "END");
            SendEvent(pendingReferentName, "FAIL");
            try { OnGestureFailed?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
        }
    }

    void HandleCancelled()
    {
        SendEvent(pendingReferentName, "FAIL");
        try { OnGestureFailed?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void SendEvent(string gestureName, string eventType)
    {
        if (msgSender == null) return;
        var payload = new CircleGestureRecognizer.CircleGesturePayload
        {
            gestureName = gestureName,
            eventType = eventType,
        };
        msgSender.SendGestureEvent(payload);
    }

    void SendRecognized(string gestureName)
    {
        if (msgSender == null) return;
        var payload = new CircleGestureRecognizer.CircleGesturePayload
        {
            gestureName = gestureName,
            eventType = "RECOGNIZED",
        };
        msgSender.SendCircleGesture(payload);
    }
}
