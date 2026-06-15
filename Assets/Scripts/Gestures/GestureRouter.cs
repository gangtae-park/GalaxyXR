using System;
using UnityEngine;


/*
Single owner of "Unity -> Python" gesture event traffic.

Two input channels are stitched together here so MsgSender only ever sees
one consistent stream of GESTURE_EVENT packets:

  1) PinchStrokeCapture -> JackknifeGestureRecognizer (Search/Find Info, Ask)
       stroke start    -> START   (gestureName = pendingReferentName)
       stroke complete -> END     (gestureName = recognized name)  + RECOGNIZED
                      OR  END+FAIL (gestureName = pendingReferentName) on reject
       stroke cancel   -> FAIL    (gestureName = pendingReferentName)

  2) TranslateGestureDetector (Translate via V band + sweep + palm swipe)
       OnTranslateStarted     -> START
       OnTranslateAreaDefined -> AREA_DEFINED
       OnTranslateConfirmed   -> END + RECOGNIZED
       OnTranslateCancelled   -> FAIL

Detectors themselves do not touch MsgSender.
*/

public class GestureRouter : MonoBehaviour
{
    [Header("References")]
    public PinchStrokeCapture strokeCapture;
    public TranslateGestureDetector translateDetector;
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
            strokeCapture.OnStrokeStarted   += HandleStrokeStarted;
            strokeCapture.OnStrokeCompleted += HandleStrokeCompleted;
            strokeCapture.OnStrokeCancelled += HandleStrokeCancelled;
        }
        if (translateDetector != null)
        {
            translateDetector.OnTranslateStarted     += HandleTranslateStarted;
            translateDetector.OnTranslateAreaDefined += HandleTranslateAreaDefined;
            translateDetector.OnTranslateConfirmed   += HandleTranslateConfirmed;
            translateDetector.OnTranslateCancelled   += HandleTranslateCancelled;
        }
    }

    void OnDisable()
    {
        if (strokeCapture != null)
        {
            strokeCapture.OnStrokeStarted   -= HandleStrokeStarted;
            strokeCapture.OnStrokeCompleted -= HandleStrokeCompleted;
            strokeCapture.OnStrokeCancelled -= HandleStrokeCancelled;
        }
        if (translateDetector != null)
        {
            translateDetector.OnTranslateStarted     -= HandleTranslateStarted;
            translateDetector.OnTranslateAreaDefined -= HandleTranslateAreaDefined;
            translateDetector.OnTranslateConfirmed   -= HandleTranslateConfirmed;
            translateDetector.OnTranslateCancelled   -= HandleTranslateCancelled;
        }
    }

    // ---------- Pinch stroke pipeline (Search / Ask) ----------

    void HandleStrokeStarted(Stroke stroke)
    {
        SendEvent(pendingReferentName, "START");
    }

    void HandleStrokeCompleted(Stroke stroke)
    {
        if (jackknifeRecognizer == null)
        {
            Debug.LogError("[GestureRouter] Jackknife recognizer not assigned");
            return;
        }

        string referentName = null;
        try { referentName = jackknifeRecognizer.Recognize(stroke); }
        catch (Exception e) { Debug.LogError($"[GestureRouter] Jackknife threw: {e}"); }

        if (!string.IsNullOrEmpty(referentName))
        {
            Debug.Log($"[GestureRouter] RECOGNIZED: {referentName}");
            SendEvent(referentName, "END");
            SendEvent(referentName, "RECOGNIZED");
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

    void HandleStrokeCancelled()
    {
        SendEvent(pendingReferentName, "FAIL");
        try { OnGestureFailed?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // ---------- Translate detector pipeline ----------

    void HandleTranslateStarted(string gestureName)
    {
        SendEvent(gestureName, "START");
    }

    void HandleTranslateAreaDefined(string gestureName)
    {
        SendEvent(gestureName, "AREA_DEFINED");
    }

    void HandleTranslateConfirmed(string gestureName)
    {
        Debug.Log($"[GestureRouter] TRANSLATE CONFIRMED: {gestureName}");
        SendEvent(gestureName, "END");
        SendEvent(gestureName, "RECOGNIZED");
        try { OnGestureRecognized?.Invoke(gestureName); } catch (Exception e) { Debug.LogError(e); }
    }

    void HandleTranslateCancelled(string gestureName)
    {
        SendEvent(gestureName, "FAIL");
        try { OnGestureFailed?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    // ---------- Wire output ----------

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
}
