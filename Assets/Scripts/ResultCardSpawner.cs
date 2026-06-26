using System.Collections.Generic;
using UnityEngine;

/*
SearchResultCardSpawner

Single dispatcher for every referent's result card. Despite the legacy class
name (kept to avoid breaking scene references), this now handles ALL gestures
that produce a card -- not just Search/Find Info.

Responsibilities:

  1) On every gesture END from PinchPosegestureRouter, snapshot the user's
     gaze world position. The card spawn position is THIS snapshot + a fixed
     right-and-up offset, so cards always appear where the user was looking
     plus a bit upper-right, never moving once placed.

  2) On every VlmResultPayload from VlmResultReceiver, dispatch by payload.gesture:

       "Search/Find Info"  -> spawn SearchResultCard with (name, result_search)
       "Ask"  + no answer  -> spawn AskQuestionCard with name; wait for the
                              user's voice-question via card.OnQuestionSubmitted;
                              pass the captured question to MsgSender.SendAskQuestion
       "Ask"  + with answer-> destroy any AskQuestionCard, spawn AskResultCard
                              with (name, question, answer)
       (other gestures)    -> hook later (Translate, Compare, ...)

  3) replacePreviousCard = true ensures only one card is alive at a time, so a
     new gesture cleanly replaces the previous result on screen.
*/

public class ResultCardSpawner : MonoBehaviour
{
    [Header("Refs")]
    public VlmResultReceiver receiver;
    public GestureRouter gestureRouter;
    public EyeGazeReader eyeGazeReader;
    public MsgSender msgSender;
    public Camera referenceCamera;

    [Header("Card Prefabs")]
    public GameObject searchResultCardPrefab;
    public GameObject askQuestionCardPrefab;
    public GameObject askResultCardPrefab;
    public GameObject translateResultCardPrefab;
    public GameObject anchorPinPrefab;

    [Header("Note flow (Save)")]
    [Tooltip("Owns the SaveNoteCard / StickyNote / ViewNoteCard lifecycle. Required to handle Save VLM_RESULT.")]
    public NoteManager noteManager;

    [Header("Anchor management")]
    [Tooltip("Maximum simultaneous anchor pins. 0 = unlimited. When exceeded the oldest is destroyed (FIFO).")]
    public int maxAnchors = 0;

    [Header("Spawn position (relative to gaze)")]
    [Tooltip("Distance from the camera along the gaze direction where the card 'anchor' lands.")]
    public float gazeProjectionDistance = 1.2f;
    [Tooltip("Offset to the right (in meters, relative to camera-right at spawn time).")]
    public float horizontalOffset = 0.25f;
    [Tooltip("Offset upward (in meters, relative to camera-up at spawn time).")]
    public float verticalOffset = 0.15f;

    [Header("Behavior")]
    public bool replacePreviousCard = true;
    public bool verboseLogging = true;

    private GameObject _currentCard;
    private AskQuestionCard _pendingAskQuestion;
    private Vector3 _lastGazeWorldPos;
    private bool _haveGazeSnapshot;
    private readonly Queue<GameObject> _spawnedAnchors = new Queue<GameObject>();

    /// <summary>The AskQuestionCard currently waiting for the user's voice question,
    /// or null if no Ask flow is in progress. Voice recognition components can read
    /// this and call .Submit(text) when they have the transcript.</summary>
    public AskQuestionCard PendingAskQuestion => _pendingAskQuestion;

    void OnEnable()
    {
        if (receiver != null) receiver.OnResult += HandleResult;
        if (gestureRouter != null) gestureRouter.OnCaptureRecognized += HandleGestureRecognized;
    }

    void OnDisable()
    {
        if (receiver != null) receiver.OnResult -= HandleResult;
        if (gestureRouter != null) gestureRouter.OnCaptureRecognized -= HandleGestureRecognized;
    }

    // ---------- gaze snapshot at gesture END ----------

    void HandleGestureRecognized(string gestureName)
    {
        _lastGazeWorldPos = ComputeGazeWorldPosition();
        _haveGazeSnapshot = true;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] gesture END '{gestureName}' -> gaze snapshot {_lastGazeWorldPos}");
    }

    Vector3 ComputeGazeWorldPosition()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return transform.position;

        Vector3 gazeDir = cam.transform.forward;
        if (eyeGazeReader != null && eyeGazeReader.LatestIsTracked
            && eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f)
        {
            gazeDir = eyeGazeReader.LatestGazeDirection.normalized;
        }
        return cam.transform.position + gazeDir * gazeProjectionDistance;
    }

    Vector3 ComputeSpawnPosition()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector3 basePos = _haveGazeSnapshot ? _lastGazeWorldPos
                        : (cam != null ? cam.transform.position + cam.transform.forward * gazeProjectionDistance
                                       : transform.position);
        if (cam == null) return basePos;
        return basePos
            + cam.transform.right * horizontalOffset
            + cam.transform.up    * verticalOffset;
    }

    // ---------- result dispatch ----------

    void HandleResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (payload == null) return;
        string gesture = payload.gesture;
        if (string.IsNullOrEmpty(gesture)) return;

        // Anchor handles fail at the spawner level (status=="fail" -> skip).
        // The other cards typically still want to render something on fail
        // (e.g. an error message) so we only fail-gate Anchor here.
        if (gesture == "Anchor")
        {
            SpawnAnchor(payload);
            return;
        }

        // Save is fail-gated just like Anchor: a fail status means Python
        // couldn't identify the object, so we don't open the note UI at all.
        if (gesture == "Save")
        {
            DispatchSave(payload);
            return;
        }

        if (payload.response == null) return;

        switch (gesture)
        {
            case "Search/Find Info":
                SpawnSearchResult(payload);
                break;

            case "Ask":
                DispatchAsk(payload);
                break;

            case "Translate":
                SpawnTranslateResult(payload);
                break;

            default:
                if (verboseLogging)
                    Debug.Log($"[ResultCardSpawner] gesture '{gesture}' has no card handler yet.");
                break;
        }
    }

    // ---------- Save ----------

    void DispatchSave(VlmResultReceiver.VlmResultPayload payload)
    {
        bool failed =
            (payload.status != null && payload.status.Equals("fail", System.StringComparison.OrdinalIgnoreCase))
            || (payload.response != null && !string.IsNullOrEmpty(payload.response.error));
        if (failed)
        {
            if (verboseLogging)
                Debug.Log($"[ResultCardSpawner] Save REJECTED: status={payload.status} reason='{payload.reason}'.");
            return;
        }
        if (payload.response == null) return;
        if (noteManager == null)
        {
            Debug.LogWarning("[ResultCardSpawner] noteManager not assigned; cannot open SaveNoteCard.");
            return;
        }

        Vector3 pos = ComputeSpawnPosition();
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Quaternion rot = (cam != null)
            ? Quaternion.LookRotation(cam.transform.forward, Vector3.up)
            : Quaternion.identity;

        noteManager.BeginNote(
            payload.response.object_id ?? "",
            payload.response.name ?? "",
            pos,
            rot
        );
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] Save -> NoteManager.BeginNote(name='{payload.response.name}', id='{payload.response.object_id}').");
    }

    // ---------- Anchor ----------

    void SpawnAnchor(VlmResultReceiver.VlmResultPayload payload)
    {
        // Python signals a fail (DB mismatch, etc.) via status="fail" / stage="ack"
        // (network.py:send_gesture_fail_to_unity), or via response.error on legacy
        // paths. Either way -> skip spawn.
        bool failed =
            (payload.status != null && payload.status.Equals("fail", System.StringComparison.OrdinalIgnoreCase))
            || (payload.response != null && !string.IsNullOrEmpty(payload.response.error));
        if (failed)
        {
            if (verboseLogging)
            {
                string why = !string.IsNullOrEmpty(payload.reason)
                    ? payload.reason
                    : (payload.response != null ? payload.response.error : "(no reason)");
                Debug.Log($"[ResultCardSpawner] anchor REJECTED: status={payload.status} stage={payload.stage} reason='{why}' -- no pin spawned.");
            }
            return;
        }

        if (anchorPinPrefab == null)
        {
            Debug.LogWarning("[ResultCardSpawner] anchorPinPrefab not assigned.");
            return;
        }

        Vector3 pos = ComputeSpawnPosition();
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Quaternion rot = (cam != null)
            ? Quaternion.LookRotation(cam.transform.forward, Vector3.up)
            : Quaternion.identity;

        GameObject go = Instantiate(anchorPinPrefab, pos, rot);
        var pin = go.GetComponent<AnchorPin>();
        if (pin != null && payload.response != null)
            pin.SetContent(payload.response.name);

        _spawnedAnchors.Enqueue(go);
        EnforceMaxAnchors();

        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned AnchorPin name='{payload.response?.name}' (total {_spawnedAnchors.Count})");
    }

    void EnforceMaxAnchors()
    {
        if (maxAnchors <= 0) return;
        while (_spawnedAnchors.Count > maxAnchors)
        {
            GameObject oldest = _spawnedAnchors.Dequeue();
            if (oldest != null) Destroy(oldest);
        }
    }

    [ContextMenu("Clear All Anchors")]
    public void ClearAllAnchors()
    {
        while (_spawnedAnchors.Count > 0)
        {
            GameObject go = _spawnedAnchors.Dequeue();
            if (go != null) Destroy(go);
        }
    }

    // ---------- Translate ----------

    void SpawnTranslateResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (translateResultCardPrefab == null)
        {
            Debug.LogWarning("[ResultCardSpawner] translateResultCardPrefab not assigned.");
            return;
        }
        ReplaceCurrentCard();

        GameObject go = Instantiate(translateResultCardPrefab, ComputeSpawnPosition(), Quaternion.identity);
        var card = go.GetComponent<TranslateResultCard>();
        if (card != null)
            card.SetContent(payload.response.translation);
        _currentCard = go;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned TranslateResultCard translation='{payload.response.translation}'");
    }

    // ---------- Search ----------

    void SpawnSearchResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (searchResultCardPrefab == null)
        {
            Debug.LogWarning("[ResultCardSpawner] searchResultCardPrefab not assigned.");
            return;
        }
        ReplaceCurrentCard();

        GameObject go = Instantiate(searchResultCardPrefab, ComputeSpawnPosition(), Quaternion.identity);
        var card = go.GetComponent<SearchResultCard>();
        if (card != null)
            card.SetContent(payload.response.name, payload.response.result_search);
        _currentCard = go;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned SearchResultCard name='{payload.response.name}'");
    }

    // ---------- Ask (two-step) ----------

    void DispatchAsk(VlmResultReceiver.VlmResultPayload payload)
    {
        bool hasAnswer = !string.IsNullOrEmpty(payload.response.answer);
        if (!hasAnswer) SpawnAskQuestion(payload);
        else            SpawnAskResult(payload);
    }

    void SpawnAskQuestion(VlmResultReceiver.VlmResultPayload payload)
    {
        if (askQuestionCardPrefab == null)
        {
            Debug.LogWarning("[ResultCardSpawner] askQuestionCardPrefab not assigned.");
            return;
        }
        ReplaceCurrentCard();

        GameObject go = Instantiate(askQuestionCardPrefab, ComputeSpawnPosition(), Quaternion.identity);
        var card = go.GetComponent<AskQuestionCard>();
        if (card != null)
        {
            card.SetObjectName(payload.response.name);
            card.OnQuestionSubmitted += HandleQuestionSubmitted;
            _pendingAskQuestion = card;
        }
        _currentCard = go;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned AskQuestionCard name='{payload.response.name}'");
    }

    void HandleQuestionSubmitted(string question)
    {
        if (msgSender == null)
        {
            Debug.LogWarning("[ResultCardSpawner] msgSender not assigned; can't forward question.");
            return;
        }
        msgSender.SendAskQuestion(question);
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] forwarded question to Python: '{question}'");
    }

    void SpawnAskResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (askResultCardPrefab == null)
        {
            Debug.LogWarning("[ResultCardSpawner] askResultCardPrefab not assigned.");
            return;
        }

        // Pull the user's question from the AskQuestionCard if still around,
        // otherwise from target_meta.user_question (Python echoes it back).
        string question = "";
        if (_pendingAskQuestion != null && !string.IsNullOrEmpty(_pendingAskQuestion.SubmittedQuestion))
            question = _pendingAskQuestion.SubmittedQuestion;
        else if (payload.target_meta != null && !string.IsNullOrEmpty(payload.target_meta.user_question))
            question = payload.target_meta.user_question;

        ReplaceCurrentCard();
        _pendingAskQuestion = null;

        GameObject go = Instantiate(askResultCardPrefab, ComputeSpawnPosition(), Quaternion.identity);
        var card = go.GetComponent<AskResultCard>();
        if (card != null)
            card.SetContent(payload.response.name, question, payload.response.answer);
        _currentCard = go;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned AskResultCard name='{payload.response.name}'");
    }

    // ---------- helpers ----------

    void ReplaceCurrentCard()
    {
        if (!replacePreviousCard) return;
        if (_pendingAskQuestion != null)
        {
            _pendingAskQuestion.OnQuestionSubmitted -= HandleQuestionSubmitted;
            _pendingAskQuestion = null;
        }
        if (_currentCard != null)
        {
            Destroy(_currentCard);
            _currentCard = null;
        }
    }
}
