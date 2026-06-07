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
    [Tooltip("Used when Jackknife has no saved templates or rejects the stroke.")]
    public GestureClassifierComponent[] fallbackClassifiers;

    [Header("Routing")]
    public string pendingReferentName = "Pending";
    public string[] localOnlyGestureNames = new[] { "Search/Find Info" };
    public bool sendLocalOnlyGestureEvents = false;
    public bool useFallbackClassifiersWhenJackknifeRejects = true;

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

        if (jackknifeRecognizer != null && jackknifeRecognizer.IsReady)
        {
            try { referentName = jackknifeRecognizer.Recognize(stroke); }
            catch (Exception e) { Debug.LogError($"[GestureRouter] Jackknife threw: {e}"); }
        }

        if (string.IsNullOrEmpty(referentName) && useFallbackClassifiersWhenJackknifeRejects)
        {
            referentName = RecognizeWithFallbacks(stroke);
        }

        if (!string.IsNullOrEmpty(referentName))
        {
            Debug.Log($"[GestureRouter] RECOGNIZED: {referentName}");

            if (IsLocalOnlyGesture(referentName) && !sendLocalOnlyGestureEvents)
            {
                Debug.Log($"[GestureRouter] Local-only gesture recognized: {referentName}. Skipping Python/VLM routing.");
            }
            else
            {
                // Send END with the final name so Python's handler dispatches correctly.
                SendEvent(referentName, "END");
                // Then send a RECOGNIZED packet (informational; Python END already triggers VLM).
                SendRecognized(referentName);
            }

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

    string RecognizeWithFallbacks(Stroke stroke)
    {
        if (fallbackClassifiers == null || fallbackClassifiers.Length == 0)
        {
            if (jackknifeRecognizer == null || !jackknifeRecognizer.IsReady)
                Debug.LogWarning("[GestureRouter] No trained Jackknife model or fallback classifiers are available.");
            return null;
        }

        string bestName = null;
        float bestConfidence = float.MinValue;

        foreach (GestureClassifierComponent classifier in fallbackClassifiers)
        {
            if (classifier == null) continue;

            try
            {
                if (classifier.TryClassify(stroke, out float confidence) && confidence > bestConfidence)
                {
                    bestName = classifier.GestureName;
                    bestConfidence = confidence;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GestureRouter] Fallback classifier {classifier.GetType().Name} threw: {e}");
            }
        }

        if (!string.IsNullOrEmpty(bestName))
            Debug.Log($"[GestureRouter] Fallback recognized '{bestName}' (confidence={bestConfidence:F2})");

        return bestName;
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

    bool IsLocalOnlyGesture(string gestureName)
    {
        if (localOnlyGestureNames == null)
            return false;

        foreach (var localOnlyGestureName in localOnlyGestureNames)
        {
            if (string.Equals(localOnlyGestureName, gestureName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
