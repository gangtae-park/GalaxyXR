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
       "VoiceAsk"         -> spawn a voice result card directly; never spawn
                              the listening AskQuestionCard from a server response
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
    public GameObject compareResultCardPrefab;
    public GameObject anchorPinPrefab;

    [Header("Note flow (Save)")]
    [Tooltip("Owns the SaveNoteCard / StickyNote / ViewNoteCard lifecycle. Required to handle Save VLM_RESULT.")]
    public NoteManager noteManager;

    [Header("Capture flow")]
    [Tooltip("Owns the CaptureControlCard lifecycle. Required to handle Capture VLM_RESULT.")]
    public CaptureManager captureManager;

    [Header("Anchor management")]
    [Tooltip("Maximum simultaneous anchor pins. 0 = unlimited. When exceeded the oldest is destroyed (FIFO).")]
    public int maxAnchors = 0;

    [Header("Spawn position (legacy fallback)")]
    [Tooltip("Fallback object distance (metres) when Python doesn't provide depth_meters. Used to compute the object world position from the gaze ray.")]
    public float gazeProjectionDistance = 1.2f;
    [Tooltip("Multiplied with Depth Anything's metric depth_meters before card placement. <1.0 brings cards closer to the user.")]
    [Range(0.1f, 2f)] public float objectDepthScale = 1.0f;

    [Header("Group A placement (Search / Ask / AskResult / VoiceResult / Note)")]
    [Tooltip("Horizontal offset (m) along capture-time camera-right. Positive = right of the object.")]
    public float groupAHorizontalOffset = 0.12f;
    [Tooltip("Vertical offset (m) along capture-time camera-up. Positive = above the object.")]
    public float groupAVerticalOffset = 0.08f;
    [Tooltip("Distance (m) the card is pulled back along the gaze direction toward the user, so it doesn't sit ON the object.")]
    public float groupAPullTowardUser = 0.15f;

    [Header("Group B placement (Translate / Capture)")]
    [Tooltip("Tiny pull toward the user so the card never z-fights with the actual surface. Usually 0.")]
    public float groupBPullTowardUser = 0.0f;

    [Header("Anchor placement (above-object variant of group B)")]
    [Tooltip("Vertical lift (m) applied to the Anchor pin so it floats just above the object. Bigger value -> further above; matches typical bubble radius of ~2.5 cm.")]
    public float anchorAboveOffset = 0.12f;

    [Header("Constant-apparent-size scaling")]
    [Tooltip("When true, every spawned card gets a DistanceConstantSize component so it stays roughly the same screen size at any depth.")]
    public bool enforceConstantApparentSize = true;
    [Tooltip("Distance (m) at which the prefab's authored scale is considered correct. Cards at exactly this depth keep their authored size.")]
    public float constantSizeReferenceDistance = 1.2f;
    [Tooltip("If true, cards closer to the user than the reference distance keep their authored size rather than shrinking.")]
    public bool constantSizeFloorAtAuthored = true;
    [Range(1f, 10f)]
    public float constantSizeMaxMultiplier = 4.0f;

    [Header("Behavior")]
    public bool replacePreviousCard = true;
    public bool verboseLogging = true;

    // Used by ComputeCardSpawnPosition to know which offset profile to apply.
    enum CardPlacementPolicy
    {
        OffsetUpperRightCloser,   // Group A: cards live up/right of the object, slightly closer to user
        AtObject,                 // Group B: cards sit on the object (Translate / Capture)
        AtObjectAbove,            // Anchor: same as AtObject but lifted upward a bit
    }

    struct GazeSnapshot
    {
        public Vector3 cameraPos;
        public Quaternion cameraRot;
        public Vector3 gazeDirWorld;   // unit, world space, at capture time
        public bool isValid;
    }

    private GameObject _currentCard;
    private AskQuestionCard _pendingAskQuestion;
    private Vector3? _overrideNextSpawnPosition;
    private GazeSnapshot _lastGazeSnapshot;
    private readonly Queue<GameObject> _spawnedAnchors = new Queue<GameObject>();

    /// <summary>The AskQuestionCard currently waiting for the user's voice question,
    /// or null if no Ask flow is in progress. Voice recognition components can read
    /// this and call .Submit(text) when they have the transcript.</summary>
    public AskQuestionCard PendingAskQuestion => _pendingAskQuestion;
    public bool HasPendingVoiceListeningCard => _pendingAskQuestion != null && IsVoiceListeningCard(_pendingAskQuestion);

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
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null)
        {
            _lastGazeSnapshot = new GazeSnapshot { isValid = false };
            return;
        }

        Vector3 gazeDir = cam.transform.forward;
        if (eyeGazeReader != null && eyeGazeReader.LatestIsTracked
            && eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f)
        {
            gazeDir = eyeGazeReader.LatestGazeDirection.normalized;
        }

        _lastGazeSnapshot = new GazeSnapshot
        {
            cameraPos = cam.transform.position,
            cameraRot = cam.transform.rotation,
            gazeDirWorld = gazeDir,
            isValid = true,
        };

        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] {reason} -> gaze snapshot cam={_lastGazeSnapshot.cameraPos} dir={_lastGazeSnapshot.gazeDirWorld}");
    }

    /// <summary>Consumed once by the next ComputeObjectWorldPosition call so the
    /// upcoming card spawns exactly at the supplied world position (used by the
    /// bubble-menu flow: cards should land at the clicked bubble, not where the
    /// payload's gaze_dir + depth happen to project to).
    ///
    /// Also synthesises a GazeSnapshot from the current camera so the per-policy
    /// offset (group A right/up/toward-user, Anchor above) still has well-defined
    /// directions. Without this, the spawn collapses to the raw override point
    /// and follow-ups like sticky notes / anchor pins visually overlap the bubble.</summary>
    public void OverrideNextSpawnPosition(Vector3 worldPos)
    {
        _overrideNextSpawnPosition = worldPos;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam != null)
        {
            Vector3 toObject = worldPos - cam.transform.position;
            Vector3 gazeDir = toObject.sqrMagnitude > 0.0001f ? toObject.normalized : cam.transform.forward;
            _lastGazeSnapshot = new GazeSnapshot
            {
                cameraPos = cam.transform.position,
                cameraRot = cam.transform.rotation,
                gazeDirWorld = gazeDir,
                isValid = true,
            };
        }

        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] next spawn override = ({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3}) (synthetic snapshot from current camera)");
    }

    /// <summary>Compute the object's world position from the gesture-time snapshot and (if available)
    /// the Python-supplied head-space gaze direction + metric depth. Falls back to the legacy
    /// fixed-distance gaze ray when those payload fields are zero.</summary>
    Vector3 ComputeObjectWorldPosition(VlmResultReceiver.VlmResultPayload payload)
    {
        if (_overrideNextSpawnPosition.HasValue)
        {
            Vector3 pos = _overrideNextSpawnPosition.Value;
            _overrideNextSpawnPosition = null;
            if (verboseLogging)
                Debug.Log($"[ResultCardSpawner] using one-shot override position {pos}");
            return pos;
        }

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (!_lastGazeSnapshot.isValid)
        {
            if (cam != null)
                return cam.transform.position + cam.transform.forward * gazeProjectionDistance;
            return transform.position;
        }

        bool hasPayloadAnchor =
            payload != null
            && payload.response != null
            && payload.response.depth_meters > 0.01f;

        if (hasPayloadAnchor)
        {
            Vector3 headDir = new Vector3(
                payload.response.gaze_dir_x,
                payload.response.gaze_dir_y,
                payload.response.gaze_dir_z);
            // Python supplies a unit-length vector when calibration is loaded; if it's
            // zero (no calibration), fall back to the snapshot's world gaze.
            if (headDir.sqrMagnitude < 0.0001f)
            {
                Vector3 worldDir = _lastGazeSnapshot.gazeDirWorld.normalized;
                return _lastGazeSnapshot.cameraPos + worldDir * Mathf.Max(0.05f, payload.response.depth_meters * objectDepthScale);
            }
            Vector3 dirWorld = _lastGazeSnapshot.cameraRot * headDir.normalized;
            return _lastGazeSnapshot.cameraPos + dirWorld * Mathf.Max(0.05f, payload.response.depth_meters * objectDepthScale);
        }

        // No depth from Python -> legacy fixed-distance fallback.
        return _lastGazeSnapshot.cameraPos + _lastGazeSnapshot.gazeDirWorld.normalized * gazeProjectionDistance;
    }

    /// <summary>Apply a prefab-specific placement policy on top of the raw object world position.</summary>
    Vector3 ComputeCardSpawnPosition(VlmResultReceiver.VlmResultPayload payload, CardPlacementPolicy policy)
    {
        Vector3 objectPos = ComputeObjectWorldPosition(payload);
        if (!_lastGazeSnapshot.isValid)
            return objectPos;

        // Capture-time right/up so the offset is "frozen" at the user's head pose
        // at gesture END, rather than tracking the user as they move.
        Vector3 rightDir = _lastGazeSnapshot.cameraRot * Vector3.right;
        Vector3 upDir = _lastGazeSnapshot.cameraRot * Vector3.up;
        Vector3 towardUser = -_lastGazeSnapshot.gazeDirWorld.normalized;

        switch (policy)
        {
            case CardPlacementPolicy.OffsetUpperRightCloser:
                return objectPos
                    + rightDir * groupAHorizontalOffset
                    + upDir * groupAVerticalOffset
                    + towardUser * groupAPullTowardUser;
            case CardPlacementPolicy.AtObjectAbove:
                return objectPos + Vector3.up * anchorAboveOffset + towardUser * groupBPullTowardUser;
            case CardPlacementPolicy.AtObject:
            default:
                return objectPos + towardUser * groupBPullTowardUser;
        }
    }

    // ---------- result dispatch ----------

    void HandleResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (payload == null) return;
        string gesture = payload.gesture;
        if (string.IsNullOrEmpty(gesture)) return;
        if (gesture == "ObjectUI") return;
        if (gesture == "VoiceAsk")
        {
            SpawnVoiceResultCard(payload);
            return;
        }

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

        // Capture is also fail-gated. Successful response opens a sizing card
        // anchored on the object; both-hand pinch (or 10 s timeout) closes it.
        if (gesture == "Capture")
        {
            DispatchCapture(payload);
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

            case "Compare":
                SpawnCompareResult(payload);
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

        Vector3 pos = ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser);
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

    // ---------- Capture ----------

    void DispatchCapture(VlmResultReceiver.VlmResultPayload payload)
    {
        bool failed =
            (payload.status != null && payload.status.Equals("fail", System.StringComparison.OrdinalIgnoreCase))
            || (payload.response != null && !string.IsNullOrEmpty(payload.response.error));
        if (failed)
        {
            if (verboseLogging)
                Debug.Log($"[ResultCardSpawner] Capture REJECTED: status={payload.status} reason='{payload.reason}'.");
            return;
        }
        if (payload.response == null) return;
        if (captureManager == null)
        {
            Debug.LogWarning("[ResultCardSpawner] captureManager not assigned; cannot open CaptureControlCard.");
            return;
        }

        // Capture centres on the object itself (AtObject policy).
        Vector3 pos = ComputeCardSpawnPosition(payload, CardPlacementPolicy.AtObject);
        int[] bbox = payload.target_meta != null ? payload.target_meta.bbox : null;
        int[] frameSize = payload.target_meta != null ? payload.target_meta.frame_size : null;

        captureManager.BeginCapture(
            payload.response.name ?? "",
            payload.response.object_id ?? "",
            pos,
            bbox,
            frameSize
        );

        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] Capture -> CaptureManager.BeginCapture(name='{payload.response.name}', bbox={(bbox != null ? string.Join(",", bbox) : "null")}, frame={(frameSize != null ? string.Join("x", frameSize) : "null")}).");
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

        Vector3 pos = ComputeCardSpawnPosition(payload, CardPlacementPolicy.AtObjectAbove);
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Quaternion rot = (cam != null)
            ? Quaternion.LookRotation(cam.transform.forward, Vector3.up)
            : Quaternion.identity;

        GameObject go = Instantiate(anchorPinPrefab, pos, rot);
        ApplyConstantSize(go);
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

        string stage = payload.stage ?? "";
        string srcText = payload.response.name ?? "";
        string translation = payload.response.translation ?? "";

        // Reuse the existing card across stages so the OCR text doesn't flash and the
        // translation visibly fills in. New card only if there's no current Translate
        // card (or the user replaced it via another gesture).
        TranslateResultCard card = _currentCard != null ? _currentCard.GetComponent<TranslateResultCard>() : null;
        if (card == null)
        {
            ReplaceCurrentCard();
            GameObject go = Instantiate(translateResultCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.AtObject), Quaternion.identity);
            ApplyConstantSize(go);
            _currentCard = go;
            card = go.GetComponent<TranslateResultCard>();
        }
        if (card == null) return;

        if (stage == "ocr")
        {
            card.SetOcrOnly(srcText);
            if (verboseLogging) Debug.Log($"[ResultCardSpawner] Translate OCR -> '{srcText}'");
        }
        else if (stage == "translation" || !string.IsNullOrEmpty(translation))
        {
            card.SetTranslation(srcText, translation);
            if (verboseLogging) Debug.Log($"[ResultCardSpawner] Translate KO -> '{translation}' (src='{srcText}')");
        }
        else
        {
            // Stage missing or unknown -- fall back to translation-only legacy path.
            card.SetContent(translation);
        }
    }

    // ---------- Compare ----------

    void SpawnCompareResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (compareResultCardPrefab == null)
        {
            // Graceful degrade: when the new prefab isn't wired yet, render as
            // a generic Search-style card using the legacy result_search text.
            Debug.LogWarning("[ResultCardSpawner] compareResultCardPrefab not assigned; falling back to SearchResultCard.");
            SpawnGenericResult(payload, ARPanelLayoutKind.CompareCard);
            return;
        }
        if (payload == null || payload.response == null) return;

        ReplaceCurrentCard();

        GameObject go = Instantiate(compareResultCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser), Quaternion.identity);
        ApplyConstantSize(go);

        CompareResultCard card = go.GetComponent<CompareResultCard>();
        if (card != null)
        {
            string nameA = !string.IsNullOrEmpty(payload.response.name_a)
                ? payload.response.name_a
                : SplitNameAOrFallback(payload.response.name);
            string nameB = !string.IsNullOrEmpty(payload.response.name_b)
                ? payload.response.name_b
                : SplitNameBOrFallback(payload.response.name);
            card.SetContent(nameA, nameB, payload.response.compare_rows);
        }

        _currentCard = go;
        if (verboseLogging)
        {
            int rowCount = payload.response.compare_rows != null ? payload.response.compare_rows.Length : 0;
            Debug.Log($"[ResultCardSpawner] spawned CompareResultCard name='{payload.response.name}' rows={rowCount}");
        }
    }

    // "A vs B" -> ("A", "B") split fallback when Python didn't populate name_a/name_b.
    static string SplitNameAOrFallback(string pairName)
    {
        if (string.IsNullOrEmpty(pairName)) return "";
        int idx = pairName.IndexOf(" vs ", System.StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? pairName.Substring(0, idx) : pairName;
    }

    static string SplitNameBOrFallback(string pairName)
    {
        if (string.IsNullOrEmpty(pairName)) return "";
        int idx = pairName.IndexOf(" vs ", System.StringComparison.OrdinalIgnoreCase);
        return idx > 0 && idx + 4 < pairName.Length ? pairName.Substring(idx + 4) : "";
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

        GameObject go = Instantiate(searchResultCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser), Quaternion.identity);
        ApplyConstantSize(go);
        var card = go.GetComponent<SearchResultCard>();
        if (card != null)
            card.SetContent(payload.response.name, payload.response.result_search);
        _currentCard = go;
        if (verboseLogging)
            Debug.Log($"[ResultCardSpawner] spawned SearchResultCard name='{payload.response.name}'");
    }

    // ---------- Ask (two-step) ----------

    void SpawnVoiceResultCard(VlmResultReceiver.VlmResultPayload payload)
    {
        ClearVoiceListeningCards("voice_response");
        ClearAskQuestionCardForVoiceResponse("voice_response");

        string requestId = FirstNonEmpty(
            payload != null ? payload.request_id : "",
            payload != null ? payload.requestId : "");
        string transcript = payload != null && payload.target_meta != null
            ? payload.target_meta.user_question
            : "";
        string answer = VoiceResultBody(payload);
        string title = VoiceResultTitle(payload);

        Debug.Log($"[VOICE_RESULT] server response received request_id={requestId}");
        Debug.Log($"[VOICE_RESULT] transcript='{transcript}'");
        Debug.Log($"[VOICE_RESULT] answer='{answer}'");

        if (string.IsNullOrWhiteSpace(answer))
        {
            string name = payload != null && payload.response != null ? payload.response.name : "";
            if (IsVoiceRequestName(name))
                Debug.LogWarning("[VOICE_RESULT][WARN] ignored placeholder name='Voice request'");
            else
                Debug.LogWarning($"[VOICE_RESULT][WARN] voice response has no displayable result request_id={requestId}");
            return;
        }

        Debug.Log($"[VOICE_RESULT] spawning result card title='{title}'");
        ReplaceCurrentCard();

        if (askResultCardPrefab != null)
        {
            GameObject go = Instantiate(askResultCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser), Quaternion.identity);
            ApplyConstantSize(go);
            var card = go.GetComponent<AskResultCard>();
            if (card != null) card.SetContent(title, transcript, answer);
            _currentCard = go;
            Debug.Log($"[RESULT_CARD] spawned VoiceResultCard request_id={requestId}");
            return;
        }

        if (searchResultCardPrefab != null)
        {
            GameObject go = Instantiate(searchResultCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser), Quaternion.identity);
            ApplyConstantSize(go);
            var card = go.GetComponent<SearchResultCard>();
            if (card != null) card.SetContent(title, answer);
            _currentCard = go;
            Debug.Log($"[RESULT_CARD] spawned VoiceResultCard request_id={requestId}");
            return;
        }

        Debug.LogWarning("[VOICE_RESULT][WARN] no result card prefab assigned for voice response.");
    }

    void DispatchAsk(VlmResultReceiver.VlmResultPayload payload)
    {
        bool hasAnswer = !string.IsNullOrEmpty(payload.response.answer);
        bool failed = payload.status != null
            && payload.status.Equals("fail", System.StringComparison.OrdinalIgnoreCase);

        if (failed && !hasAnswer)
        {
            payload.response.answer = FirstNonEmpty(
                payload.response != null ? payload.response.error : "",
                payload.reason,
                "No answer was returned.");
            hasAnswer = true;
        }

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

        GameObject go = Instantiate(askQuestionCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser), Quaternion.identity);
        ApplyConstantSize(go);
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

        GameObject go = Instantiate(askResultCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser), Quaternion.identity);
        ApplyConstantSize(go);
        var card = go.GetComponent<AskResultCard>();
        if (card != null)
            card.SetContent(DisplayNameForAskResult(payload), question, payload.response.answer);
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

        GameObject go = Instantiate(searchResultCardPrefab, ComputeCardSpawnPosition(payload, CardPlacementPolicy.OffsetUpperRightCloser), Quaternion.identity);
        ApplyConstantSize(go);
        var card = go.GetComponent<SearchResultCard>();
        if (card != null)
            card.SetContent(BuildGenericTitle(payload, layoutKind), BuildGenericBody(payload, layoutKind));
        _currentCard = go;

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

    static bool IsVoiceListeningCard(AskQuestionCard card)
    {
        return card != null && IsVoiceRequestName(card.ObjectName);
    }

    static bool IsVoiceRequestName(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Trim().Equals("Voice request", System.StringComparison.OrdinalIgnoreCase);
    }

    static string DisplayNameForAskResult(VlmResultReceiver.VlmResultPayload payload)
    {
        return payload != null && payload.response != null ? payload.response.name : "";
    }

    static string VoiceResultTitle(VlmResultReceiver.VlmResultPayload payload)
    {
        string name = payload != null && payload.response != null ? payload.response.name : "";
        if (!IsVoiceRequestName(name) && !string.IsNullOrWhiteSpace(name))
            return name;
        return "Voice result";
    }

    static string VoiceResultBody(VlmResultReceiver.VlmResultPayload payload)
    {
        VlmResultReceiver.VlmResponse response = payload != null ? payload.response : null;
        return FirstNonEmpty(
            response != null ? response.answer : "",
            response != null ? response.result : "",
            response != null ? response.text : "",
            response != null ? response.result_search : "",
            response != null ? response.info : "",
            response != null ? response.description : "",
            response != null ? response.raw : "",
            response != null ? response.error : "",
            payload != null ? payload.reason : "");
    }

    // ---------- helpers ----------

    public void ClearVoiceListeningCards(string reason = "cleanup")
    {
        bool removed = false;

        if (_pendingAskQuestion != null && IsVoiceListeningCard(_pendingAskQuestion))
        {
            _pendingAskQuestion.OnQuestionSubmitted -= HandleQuestionSubmitted;
            GameObject cardObject = _pendingAskQuestion.gameObject;
            if (cardObject != null) Destroy(cardObject);
            if (_currentCard == cardObject) _currentCard = null;
            _pendingAskQuestion = null;
            removed = true;
        }

        if (_currentCard != null)
        {
            AskQuestionCard card = _currentCard.GetComponent<AskQuestionCard>();
            if (card != null && IsVoiceListeningCard(card))
            {
                if (_pendingAskQuestion == card)
                {
                    card.OnQuestionSubmitted -= HandleQuestionSubmitted;
                    _pendingAskQuestion = null;
                }
                Destroy(_currentCard);
                _currentCard = null;
                removed = true;
            }
        }

        if (removed)
            Debug.Log($"[VOICE_UI] listening panel hidden reason={reason}");
    }

    bool ClearAskQuestionCardForVoiceResponse(string reason)
    {
        bool removed = false;

        if (_pendingAskQuestion != null)
        {
            _pendingAskQuestion.OnQuestionSubmitted -= HandleQuestionSubmitted;
            GameObject cardObject = _pendingAskQuestion.gameObject;
            if (cardObject != null) Destroy(cardObject);
            if (_currentCard == cardObject) _currentCard = null;
            _pendingAskQuestion = null;
            removed = true;
        }

        if (_currentCard != null)
        {
            AskQuestionCard card = _currentCard.GetComponent<AskQuestionCard>();
            if (card != null)
            {
                Destroy(_currentCard);
                _currentCard = null;
                removed = true;
            }
        }

        if (removed)
        {
            Debug.LogWarning("[VOICE_RESULT][WARN] response routed to listening card; forcing result mode");
            Debug.Log($"[VOICE_UI] listening panel hidden reason={reason}");
        }

        return removed;
    }

    public void RemoveCardsBySource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        if (source == "voice-listening" || source == "voice")
            ClearVoiceListeningCards(source);
    }

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

    /// <summary>Attach a DistanceConstantSize so the card keeps the same apparent
    /// screen size at any depth. Skipped when the spawner-level toggle is off
    /// or the prefab already includes its own size policy.</summary>
    void ApplyConstantSize(GameObject go)
    {
        if (!enforceConstantApparentSize || go == null) return;
        DistanceConstantSize comp = go.GetComponent<DistanceConstantSize>();
        if (comp == null) comp = go.AddComponent<DistanceConstantSize>();
        comp.referenceCamera = referenceCamera != null ? referenceCamera : Camera.main;
        comp.referenceDistanceMeters = constantSizeReferenceDistance;
        comp.floorAtAuthoredSize = constantSizeFloorAtAuthored;
        comp.maxScaleMultiplier = constantSizeMaxMultiplier;
    }
}
