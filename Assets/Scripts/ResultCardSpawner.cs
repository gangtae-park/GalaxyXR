using System.Collections.Generic;
using System.Text;
using UnityEngine;

/*
SearchResultCardSpawner

Single dispatcher for every referent's result card. Despite the legacy class
name (kept to avoid breaking scene references), this now handles ALL gestures
that produce a card -- not just Search/Find Info.

Responsibilities:

  1) On every gesture END from GestureRouter, snapshot the user's
     gaze world position. The card spawn position is THIS snapshot + a fixed
     right-and-up offset, so cards always appear where the user was looking
     plus a bit upper-right, never moving once placed.

  2) On every VlmResultPayload from VlmResultReceiver, dispatch by payload.gesture:

       "Search/Find Info"  -> spawn SearchResultCard with (name, result_search)
       "Ask"  + no answer  -> spawn AskQuestionCard with name; wait for the
                              user's voice-question via card.OnQuestionSubmitted;
                              pass the captured question to MsgSender.SendAskQuestion
       "Ask"  + with answer-> destroy any AskQuestionCard, spawn AskResultCard
       "VoiceAsk"         -> treated as "Ask" for compatibility with voice-side servers
                              with (name, question, answer)
       other gestures     -> spawn compact generic cards using the mapping in
                              HandleResult without changing backend semantics

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

    [Header("Anchor management")]
    [Tooltip("Maximum simultaneous anchor pins. 0 = unlimited. When exceeded the oldest is destroyed (FIFO).")]
    public int maxAnchors = 0;

    [Header("Reference AR style")]
    public bool applyReferencePanelStyle = true;
    public Color panelBackgroundColor = new Color(0.20f, 0.45f, 1.0f, 0.55f);
    public Color panelBorderColor = new Color(0.72f, 0.88f, 1.0f, 0.78f);
    public Color panelTextColor = Color.white;
    public Color panelSecondaryTextColor = new Color(0.92f, 0.97f, 1.0f, 0.92f);
    public Vector2 infoPanelSize = new Vector2(380f, 160f);
    public Vector2 askPanelSize = new Vector2(430f, 150f);
    public Vector2 answerPanelSize = new Vector2(500f, 240f);
    public Vector2 comparePanelSize = new Vector2(440f, 190f);
    public Vector2 translationPanelSize = new Vector2(380f, 150f);
    public Vector2 statusPanelSize = new Vector2(320f, 95f);
    public Vector2 sensorPanelSize = new Vector2(350f, 190f);
    public bool showTargetBoundingBox = true;
    public Vector2 targetBoundingBoxSize = new Vector2(0.45f, 0.35f);
    public Color targetBoundingBoxColor = new Color(0.45f, 0.70f, 1.0f, 0.9f);
    public float targetBoundingBoxLineWidth = 0.006f;

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
    private ARBoundingBoxVisual _currentTargetBounds;
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
        CaptureCurrentGazeSnapshot($"gesture END '{gestureName}'");
    }

    public void CaptureCurrentGazeSnapshot(string reason)
    {
        _lastGazeWorldPos = ComputeGazeWorldPosition();
        _haveGazeSnapshot = true;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] {reason} -> gaze snapshot {_lastGazeWorldPos}");
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
        if (gesture == "ObjectUI") return;

        // Anchor handles fail at the spawner level (status=="fail" -> skip).
        // The other cards typically still want to render something on fail
        // (e.g. an error message) so we only fail-gate Anchor here.
        if (gesture == "Anchor")
        {
            SpawnAnchor(payload);
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

            case "VoiceAsk":
                DispatchAsk(payload);
                break;

            case "Translate":
                SpawnTranslateResult(payload);
                break;

            case "Compare":
                SpawnGenericResult(payload, ARPanelLayoutKind.CompareCard);
                break;

            case "Capture":
                SpawnGenericResult(payload, ARPanelLayoutKind.StatusCard);
                break;

            case "Save":
            case "Store":
                SpawnGenericResult(payload, ARPanelLayoutKind.NoteCard);
                break;

            case "Mark":
            case "Anchor":
                SpawnGenericResult(payload, ARPanelLayoutKind.AnchorCard);
                break;

            case "Activate":
            case "Deactivate":
                SpawnGenericResult(payload, ARPanelLayoutKind.StatusCard);
                break;

            case "Set":
            case "Change":
                SpawnGenericResult(payload, ARPanelLayoutKind.ControlCard);
                break;

            case "Read":
            case "Sense":
                SpawnGenericResult(payload, ARPanelLayoutKind.SensorCard);
                break;

            default:
                if (verboseLogging)
                    Debug.Log($"[ResultCardSpawner] gesture '{gesture}' has no card handler yet.");
                break;
        }
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
        ApplyPanelLayout(go, ARPanelLayoutKind.TranslationCard);
        var card = go.GetComponent<TranslateResultCard>();
        if (card != null)
            card.SetContent(payload.response.translation);
        _currentCard = go;
        ShowTargetBoundsIfAvailable(payload);
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
        ApplyPanelLayout(go, ARPanelLayoutKind.InfoCard);
        var card = go.GetComponent<SearchResultCard>();
        if (card != null)
            card.SetContent(payload.response.name, payload.response.result_search);
        _currentCard = go;
        ShowTargetBoundsIfAvailable(payload);
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
        ApplyPanelLayout(go, ARPanelLayoutKind.AskCard);
        var card = go.GetComponent<AskQuestionCard>();
        if (card != null)
        {
            card.SetObjectName(payload.response.name);
            card.OnQuestionSubmitted += HandleQuestionSubmitted;
            _pendingAskQuestion = card;
        }
        _currentCard = go;
        ShowTargetBoundsIfAvailable(payload);
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
        ApplyPanelLayout(go, ARPanelLayoutKind.AnswerCard);
        var card = go.GetComponent<AskResultCard>();
        if (card != null)
            card.SetContent(payload.response.name, question, payload.response.answer);
        _currentCard = go;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned AskResultCard name='{payload.response.name}'");
    }

    // ---------- Generic mapped panels ----------

    void SpawnGenericResult(VlmResultReceiver.VlmResultPayload payload, ARPanelLayoutKind layoutKind)
    {
        if (searchResultCardPrefab == null)
        {
            Debug.LogWarning("[ResultCardSpawner] generic panel fallback requires searchResultCardPrefab.");
            return;
        }
        ReplaceCurrentCard();

        GameObject go = Instantiate(searchResultCardPrefab, ComputeSpawnPosition(), Quaternion.identity);
        ApplyPanelLayout(go, layoutKind);
        var card = go.GetComponent<SearchResultCard>();
        if (card != null)
            card.SetContent(BuildGenericTitle(payload, layoutKind), BuildGenericBody(payload, layoutKind));
        _currentCard = go;

        if (layoutKind == ARPanelLayoutKind.CompareCard || layoutKind == ARPanelLayoutKind.SensorCard)
            ShowTargetBoundsIfAvailable(payload);

        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned generic {layoutKind} for gesture='{payload.gesture}'");
    }

    static string BuildGenericTitle(VlmResultReceiver.VlmResultPayload payload, ARPanelLayoutKind layoutKind)
    {
        string name = payload != null && payload.response != null ? payload.response.name : "";
        if (!string.IsNullOrEmpty(name)) return name;

        string gesture = payload != null ? payload.gesture : "";
        switch (layoutKind)
        {
            case ARPanelLayoutKind.CompareCard: return "Compare";
            case ARPanelLayoutKind.NoteCard: return "Note";
            case ARPanelLayoutKind.AnchorCard: return "Marker anchored";
            case ARPanelLayoutKind.SensorCard: return "Read / Sense";
            case ARPanelLayoutKind.ControlCard: return "Set / Change";
            case ARPanelLayoutKind.StatusCard:
                return string.IsNullOrEmpty(gesture) ? "Status" : gesture;
            default:
                return string.IsNullOrEmpty(gesture) ? "Status" : gesture;
        }
    }

    static string BuildGenericBody(VlmResultReceiver.VlmResultPayload payload, ARPanelLayoutKind layoutKind)
    {
        VlmResultReceiver.VlmResponse response = payload != null ? payload.response : null;
        string best = FirstNonEmpty(
            response != null ? response.answer : "",
            response != null ? response.info : "",
            response != null ? response.description : "",
            response != null ? response.result_search : "",
            response != null ? response.translation : "",
            response != null ? response.raw : ""
        );

        if (!string.IsNullOrEmpty(best))
            return layoutKind == ARPanelLayoutKind.CompareCard ? CompactPlainText(best) : best;

        string gesture = payload != null ? payload.gesture : "";
        switch (layoutKind)
        {
            case ARPanelLayoutKind.StatusCard:
                if (gesture == "Activate") return "Activated";
                if (gesture == "Deactivate") return "Deactivated";
                return "Image captured and saved";
            case ARPanelLayoutKind.NoteCard:
                return "Saved memo or extracted information will appear here.";
            case ARPanelLayoutKind.AnchorCard:
                return "Marker anchored";
            case ARPanelLayoutKind.SensorCard:
                return "CO2      --\nHCHO     --\nTVOC     --\nTEMP     --\nHUMI     --";
            case ARPanelLayoutKind.ControlCard:
                return "Control panel placeholder";
            case ARPanelLayoutKind.CompareCard:
                return "Comparison result unavailable.";
            default:
                return "";
        }
    }

    static string CompactPlainText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string[] lines = text.Split('\n');
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }
        return builder.ToString();
    }

    static string FirstNonEmpty(params string[] values)
    {
        if (values == null) return "";
        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i])) return values[i];
        }
        return "";
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
        if (_currentTargetBounds != null)
        {
            Destroy(_currentTargetBounds.gameObject);
            _currentTargetBounds = null;
        }
    }

    void ApplyPanelLayout(GameObject go, ARPanelLayoutKind layoutKind)
    {
        if (!applyReferencePanelStyle || go == null) return;
        ARPanelStyle style = ARPanelStyle.ApplyTo(go, layoutKind);
        if (style == null) return;

        style.backgroundColor = panelBackgroundColor;
        style.borderColor = panelBorderColor;
        style.textColor = panelTextColor;
        style.secondaryTextColor = panelSecondaryTextColor;
        style.infoSize = infoPanelSize;
        style.askSize = askPanelSize;
        style.answerSize = answerPanelSize;
        style.compareSize = comparePanelSize;
        style.translationSize = translationPanelSize;
        style.statusSize = statusPanelSize;
        style.sensorSize = sensorPanelSize;
        style.Apply();
    }

    void ShowTargetBoundsIfAvailable(VlmResultReceiver.VlmResultPayload payload)
    {
        if (!showTargetBoundingBox || payload == null || !HasTargetBounds(payload.target_meta)) return;

        if (_currentTargetBounds != null)
            Destroy(_currentTargetBounds.gameObject);

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector3 center = _haveGazeSnapshot ? _lastGazeWorldPos : ComputeGazeWorldPosition();
        _currentTargetBounds = ARBoundingBoxVisual.Create(
            center,
            cam,
            targetBoundingBoxSize,
            targetBoundingBoxColor,
            targetBoundingBoxLineWidth,
            30f
        );
    }

    static bool HasTargetBounds(VlmResultReceiver.VlmTargetMeta meta)
    {
        if (meta == null) return false;
        return HasBox(meta.bbox) || HasBox(meta.crop_bbox) || HasBox(meta.gaze_bbox);
    }

    static bool HasBox(float[] box)
    {
        return box != null && box.Length >= 4;
    }
}
