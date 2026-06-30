using UnityEngine;

/*
Routes a radial menu wedge click into a backend command targeting the *exact*
bubble that the user clicked on.

Old behaviour: faked a GESTURE_EVENT (END + RECOGNIZED) so Python's gesture
handlers would re-run YOLO/CLIP from the current gaze. That mis-targeted in
the bubble-menu flow because the user's gaze had moved by then.

New behaviour: send an OBJECT_ACTION packet containing the action name, the
object_id captured at bubble-spawn time, and the original /object_ui
request_id so Python correlates with its cached detection. Python's
object_action handler short-circuits detection and uses the DB record
directly, then ships back a VLM_RESULT shaped exactly like the corresponding
gesture would.

Compare needs two objects. We cache the first selection (object_id +
anchor.worldPosition + request_id) and ship the pair to Python on the second
click. The result card lands at the midpoint of the two bubble positions.

Before sending, the bridge also stashes the bubble's world position on the
ResultCardSpawner so the next card it spawns lands exactly on the bubble
instead of using its own gaze-based projection.
*/

public class ObjectActionCommandBridge : MonoBehaviour
{
    [Header("Refs")]
    public MsgSender msgSender;
    public InteractionLogger interactionLogger;
    public ResultCardSpawner resultCardSpawner;

    [Header("Behavior")]
    public bool autoFindReferences = true;
    public bool verboseLogging = true;

    // Compare two-step: first click stores; second click fires.
    string _pendingComparePrimaryId;
    string _pendingComparePrimaryRequestId;
    string _pendingComparePrimaryLabel;
    Vector3 _pendingComparePrimaryWorld;
    bool _pendingComparePrimaryValid;

    void Awake()
    {
        ResolveRefs();
    }

    public bool Route(ObjectActionMenuAction action, DetectedObjectAnchor anchor)
    {
        ResolveRefs();

        if (anchor == null || anchor.detection == null)
        {
            Debug.LogWarning("[ObjectActionMenu] route ignored: anchor or detection is null.");
            return false;
        }

        if (action == ObjectActionMenuAction.Cancel)
        {
            ClearComparePending("cancel");
            Log(action, "Cancel", true, "");
            return true;
        }

        DetectionResult det = anchor.detection;
        // Prefer the CLIP-matched DB key. If for some reason it didn't propagate,
        // fall back to the visible label (Python's lookup tolerates either).
        string objectId = !string.IsNullOrEmpty(det.objectId) ? det.objectId : (det.label ?? "");
        string requestId = det.requestId ?? "";

        if (msgSender == null)
        {
            Debug.LogWarning("[ObjectActionMenu] route failed: MsgSender missing.");
            Log(action, ToActionName(action), false, "MsgSender missing");
            return false;
        }

        if (action == ObjectActionMenuAction.Compare)
            return HandleCompare(det, anchor, requestId);

        // Single-object actions. Override the next card spawn position with
        // the bubble's world position so the result lands at the bubble.
        if (resultCardSpawner != null)
            resultCardSpawner.OverrideNextSpawnPosition(anchor.worldPosition);

        string actionName = ToActionName(action);
        msgSender.SendObjectAction(actionName, objectId, "", requestId);
        Log(action, actionName, true, "");
        Debug.Log($"[ObjectActionMenu] OBJECT_ACTION sent action={actionName} object_id={objectId} request_id={requestId}");
        return true;
    }

    bool HandleCompare(DetectionResult det, DetectedObjectAnchor anchor, string requestId)
    {
        string objectId = !string.IsNullOrEmpty(det.objectId) ? det.objectId : (det.label ?? "");

        if (!_pendingComparePrimaryValid)
        {
            _pendingComparePrimaryId = objectId;
            _pendingComparePrimaryRequestId = requestId;
            _pendingComparePrimaryLabel = det.label;
            _pendingComparePrimaryWorld = anchor.worldPosition;
            _pendingComparePrimaryValid = true;
            Debug.Log($"[ObjectActionMenu] Compare primary stored object_id={objectId} request_id={requestId}. Select another bubble + Compare to run.");
            Log(ObjectActionMenuAction.Compare, "ComparePending", true, "first object selected");
            return true;
        }

        if (string.Equals(_pendingComparePrimaryId, objectId, System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"[ObjectActionMenu] Compare ignored: same object selected twice (id={objectId}). Cancel and try again.");
            ClearComparePending("same-object");
            return false;
        }

        string primaryId = _pendingComparePrimaryId;
        Vector3 midpoint = (_pendingComparePrimaryWorld + anchor.worldPosition) * 0.5f;
        ClearComparePending("dispatched");

        if (resultCardSpawner != null)
            resultCardSpawner.OverrideNextSpawnPosition(midpoint);

        string effectiveRequestId = !string.IsNullOrEmpty(requestId) ? requestId : _pendingComparePrimaryRequestId;
        msgSender.SendObjectAction("Compare", primaryId, objectId, effectiveRequestId);
        Debug.Log($"[ObjectActionMenu] OBJECT_ACTION Compare sent a={primaryId} b={objectId} midpoint=({midpoint.x:F3},{midpoint.y:F3},{midpoint.z:F3}) request_id={effectiveRequestId}");
        Log(ObjectActionMenuAction.Compare, "Compare", true, "");
        return true;
    }

    void ClearComparePending(string reason)
    {
        if (!_pendingComparePrimaryValid) return;
        if (verboseLogging) Debug.Log($"[ObjectActionMenu] Compare primary cleared ({reason}).");
        _pendingComparePrimaryValid = false;
        _pendingComparePrimaryId = null;
        _pendingComparePrimaryRequestId = null;
        _pendingComparePrimaryLabel = null;
        _pendingComparePrimaryWorld = Vector3.zero;
    }

    /// <summary>External cancel — used by ObjectUiRequestManager on mode change
    /// so the Compare two-step doesn't survive a UI -> Voice / Gesture switch.</summary>
    public void ResetPendingState(string reason = "external_reset")
    {
        ClearComparePending(reason);
    }

    static string ToActionName(ObjectActionMenuAction action)
    {
        // These names are matched by Python's object_action handler.
        switch (action)
        {
            case ObjectActionMenuAction.Search: return "Search";
            case ObjectActionMenuAction.Ask: return "Ask";
            case ObjectActionMenuAction.Translate: return "Translate";
            case ObjectActionMenuAction.Compare: return "Compare";
            case ObjectActionMenuAction.Anchor: return "Anchor";
            case ObjectActionMenuAction.Save: return "Save";
            case ObjectActionMenuAction.Capture: return "Capture";
            case ObjectActionMenuAction.Cancel: return "Cancel";
            default: return action.ToString();
        }
    }

    void ResolveRefs()
    {
        if (!autoFindReferences) return;
        if (msgSender == null) msgSender = FindObjectOfType<MsgSender>();
        if (interactionLogger == null) interactionLogger = FindObjectOfType<InteractionLogger>();
        if (resultCardSpawner == null) resultCardSpawner = FindObjectOfType<ResultCardSpawner>();
    }

    void Log(ObjectActionMenuAction action, string command, bool success, string error)
    {
        if (interactionLogger == null) return;
        interactionLogger.LogInteraction(new InteractionLogEntry
        {
            input_mode = "UIOnly",
            source = "object_action_menu",
            parsed_command = command,
            target_strategy = "bubble_object_id",
            sent_packet_type = "OBJECT_ACTION",
            success = success,
            error_message = error
        });
    }
}
