using UnityEngine;

public class ObjectActionCommandBridge : MonoBehaviour
{
    [Header("Refs")]
    public MsgSender msgSender;
    public InteractionLogger interactionLogger;

    [Header("Behavior")]
    public bool autoFindReferences = true;

    DetectionResult _pendingCompareFirst;

    void Awake()
    {
        ResolveRefs();
    }

    public bool Route(ObjectActionMenuAction action, DetectedObjectAnchor anchor)
    {
        ResolveRefs();

        string gestureName = ToGestureName(action);
        Debug.Log($"[ObjectActionMenu] routing action={action} gesture='{gestureName}' label='{anchor?.detection?.label}'");

        if (action == ObjectActionMenuAction.Compare)
        {
            if (_pendingCompareFirst == null)
            {
                _pendingCompareFirst = anchor != null ? anchor.detection : null;
                Log(action, "ComparePending", true, "first object selected");
                Debug.Log("[ObjectActionMenu] Compare first object stored; select another object and press Compare again.");
                return true;
            }

            _pendingCompareFirst = null;
        }

        if (msgSender == null)
        {
            Debug.LogWarning("[ObjectActionMenu] command routed failure: MsgSender missing.");
            Log(action, gestureName, false, "MsgSender missing");
            return false;
        }

        SendGestureLikeCommand(gestureName);
        Log(action, gestureName, true, "");
        Debug.Log($"[ObjectActionMenu] command routed success action={action} gesture='{gestureName}'");
        return true;
    }

    void SendGestureLikeCommand(string gestureName)
    {
        msgSender.SendGestureEvent(new GestureEventPayload { gestureName = gestureName, eventType = "END" });
        msgSender.SendGestureEvent(new GestureEventPayload { gestureName = gestureName, eventType = "RECOGNIZED" });
    }

    string ToGestureName(ObjectActionMenuAction action)
    {
        switch (action)
        {
            case ObjectActionMenuAction.Search: return "Search/Find Info";
            case ObjectActionMenuAction.Ask: return "Ask";
            case ObjectActionMenuAction.Compare: return "Compare";
            case ObjectActionMenuAction.Translate: return "Translate";
            case ObjectActionMenuAction.Summarize: return "Summarize";
            case ObjectActionMenuAction.Details: return "Details";
            case ObjectActionMenuAction.Cancel: return "Cancel";
            default: return action.ToString();
        }
    }

    void ResolveRefs()
    {
        if (!autoFindReferences) return;
        if (msgSender == null) msgSender = FindObjectOfType<MsgSender>();
        if (interactionLogger == null) interactionLogger = FindObjectOfType<InteractionLogger>();
    }

    void Log(ObjectActionMenuAction action, string command, bool success, string error)
    {
        if (interactionLogger == null) return;
        interactionLogger.LogInteraction(new InteractionLogEntry
        {
            input_mode = "UIOnly",
            source = "object_action_menu",
            parsed_command = command,
            target_strategy = "detection_anchor",
            sent_packet_type = "GESTURE_EVENT",
            success = success,
            error_message = error
        });
    }
}
