using UnityEngine;

/// <summary>
/// Spawns an AskQuestionCard when the "Ask" gesture is recognized, wires the card's
/// Submit button to send the question to Python via MsgSender, and pipes the VLM
/// answer (when it comes back as a VLM_RESULT) into the same card.
/// </summary>
public class AskCardSpawner : MonoBehaviour
{
    [Header("Refs")]
    public VlmResultReceiver receiver;
    public GestureAnchorTracker anchorTracker;
    public MsgSender msgSender;
    public GameObject cardPrefab;
    [Tooltip("If null, Camera.main is used for distance clamping.")]
    public Camera referenceCamera;

    [Header("Filter")]
    [Tooltip("Only handle anchors / VLM results for this gesture name.")]
    public string gestureNameFilter = "Ask";

    [Header("Card placement (camera-relative offset from gaze anchor, in meters)")]
    public float offsetRight = 0.18f;
    public float offsetUp = 0.12f;
    public float pullToward = 0.05f;
    public float minCameraDistance = 0.4f;

    [Header("Behavior")]
    public bool replacePreviousCard = true;
    public bool verboseLogging = true;

    private AskQuestionCard _currentCard;

    void OnEnable()
    {
        if (anchorTracker != null) anchorTracker.OnGestureEndAnchor += HandleAnchor;
        if (receiver != null)      receiver.OnVlmResult += HandleVlmResult;
    }

    void OnDisable()
    {
        if (anchorTracker != null) anchorTracker.OnGestureEndAnchor -= HandleAnchor;
        if (receiver != null)      receiver.OnVlmResult -= HandleVlmResult;
    }

    void HandleAnchor(GestureAnchorTracker.AnchorPose pose)
    {
        if (!string.IsNullOrEmpty(gestureNameFilter) && pose.gestureName != gestureNameFilter) return;

        if (cardPrefab == null)
        {
            Debug.LogWarning("[AskCardSpawner] cardPrefab is not assigned.");
            return;
        }

        if (replacePreviousCard && _currentCard != null)
        {
            // Unsubscribe before destroying so we don't get stale callbacks.
            _currentCard.OnSubmit -= HandleSubmit;
            Destroy(_currentCard.gameObject);
            _currentCard = null;
        }

        Vector3 spawnPos = pose.worldPosition
            + pose.cameraRight * offsetRight
            + pose.cameraUp    * offsetUp
            - pose.cameraForward * pullToward;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam != null)
        {
            Vector3 toSpawn = spawnPos - cam.transform.position;
            float dist = toSpawn.magnitude;
            if (dist < minCameraDistance && dist > 0.0001f)
            {
                spawnPos = cam.transform.position + toSpawn.normalized * minCameraDistance;
            }
        }

        GameObject go = Instantiate(cardPrefab, spawnPos, Quaternion.identity);
        AskQuestionCard card = go.GetComponent<AskQuestionCard>();
        if (card == null)
        {
            Debug.LogWarning("[AskCardSpawner] cardPrefab missing AskQuestionCard component.");
            return;
        }
        card.OnSubmit += HandleSubmit;
        _currentCard = card;

        if (verboseLogging)
            Debug.Log($"[AskCardSpawner] Spawned Ask card at {spawnPos}");
    }

    void HandleSubmit(string question)
    {
        if (msgSender == null)
        {
            Debug.LogWarning("[AskCardSpawner] msgSender not assigned; cannot send question.");
            if (_currentCard != null) _currentCard.ShowError("Network error", "MsgSender not wired.");
            return;
        }
        msgSender.SendAskQuestion(question);
        if (verboseLogging)
            Debug.Log($"[AskCardSpawner] Forwarded question to Python: '{question}'");
    }

    void HandleVlmResult(VlmResultReceiver.VlmResultPayload payload)
    {
        // Ask response is { "name": ..., "answer": ... }.  We render it into the same
        // AskQuestionCard that's already on screen (answer panel) and put the transcribed
        // voice question back into the QuestionInput field so the user sees what was
        // recognized.
        if (!string.IsNullOrEmpty(gestureNameFilter) &&
            payload != null && !string.IsNullOrEmpty(payload.gesture) &&
            payload.gesture != gestureNameFilter)
        {
            return;
        }

        if (_currentCard == null)
        {
            if (verboseLogging)
                Debug.LogWarning("[AskCardSpawner] Ask VLM result arrived but no active card.");
            return;
        }

        if (payload == null || payload.response == null)
        {
            _currentCard.ShowError("Error", "VLM response missing.");
            return;
        }

        var r = payload.response;
        string transcribed = payload.target_meta != null ? payload.target_meta.user_question : null;

        if (!string.IsNullOrEmpty(r.error))
        {
            _currentCard.ShowError("Error", r.error, transcribed);
            return;
        }

        // Ask schema: name + answer. Fallback to raw text if model didn't return clean JSON.
        string title = string.IsNullOrEmpty(r.name) ? "Answer" : r.name;
        string body  = !string.IsNullOrEmpty(r.answer) ? r.answer
                       : (!string.IsNullOrEmpty(r.raw) ? r.raw : "(empty response)");

        _currentCard.ShowAnswer(title, body, transcribed);

        if (verboseLogging)
            Debug.Log($"[AskCardSpawner] Ask card populated. title='{title}', transcript='{transcribed}'");
    }
}
