using UnityEngine;

public class SemanticCommandRouter : MonoBehaviour
{
    [Header("Refs")]
    public MsgSender msgSender;
    public ResultCardSpawner resultCardSpawner;
    public InteractionLogger interactionLogger;

    [Header("Synthetic gesture packets")]
    public bool sendSyntheticStartEvent = true;
    public string pendingGestureName = "Pending";
    public string searchGestureName = "Search/Find Info";
    public string askGestureName = "Ask";
    public bool captureGazeSnapshotForGazeVoice = true;

    [Header("MVP behavior")]
    [Tooltip("Direct Ask without a pending AskQuestionCard needs server-side target context. Keep off until the Python side supports it.")]
    public bool allowDirectAskWithoutPendingCard = false;

    [Header("Logging")]
    public bool autoResolveReferences = true;
    public bool verboseLogging = true;

    private bool _voiceCaptureOpen;

    void Awake()
    {
        ResolveReferences();
    }

    public bool BeginVoiceCapture(InputMode inputMode)
    {
        ResolveReferences();

        if (HasPendingAskQuestion())
        {
            if (verboseLogging)
                Debug.Log("[SemanticCommandRouter] Pending AskQuestionCard is active; voice transcript will be routed as ASK_QUESTION, not a new gesture capture.");
            return false;
        }

        if (_voiceCaptureOpen) return true;
        if (msgSender == null)
        {
            Log(null, inputMode, "GESTURE_EVENT/PendingStart", false, "MsgSender is missing.");
            return false;
        }

        SendGestureEvent(pendingGestureName, "START");
        _voiceCaptureOpen = true;

        if (verboseLogging)
            Debug.Log($"[SemanticCommandRouter] voice capture START mode={inputMode}");

        Log(null, inputMode, "GESTURE_EVENT/PendingStart", true, "");
        return true;
    }

    public bool FailVoiceCapture(InputMode inputMode, string reason)
    {
        ResolveReferences();

        if (!_voiceCaptureOpen) return false;
        if (msgSender == null)
        {
            _voiceCaptureOpen = false;
            Log(null, inputMode, "GESTURE_EVENT/PendingFail", false, "MsgSender is missing.");
            return false;
        }

        SendGestureEvent(pendingGestureName, "END");
        SendGestureEvent(pendingGestureName, "FAIL");
        _voiceCaptureOpen = false;

        if (verboseLogging)
            Debug.Log($"[SemanticCommandRouter] voice capture FAIL reason='{reason}'");

        Log(null, inputMode, "GESTURE_EVENT/PendingFail", true, reason ?? "");
        return true;
    }

    public void RouteCommand(ParsedVoiceCommand command, InputMode inputMode)
    {
        ResolveReferences();

        if (command == null)
        {
            FailVoiceCapture(inputMode, "Parsed command is null.");
            Log(null, inputMode, "UNKNOWN", false, "Parsed command is null.");
            return;
        }

        if (string.IsNullOrEmpty(command.requestId))
        {
            command.requestId = interactionLogger != null
                ? interactionLogger.NewRequestId()
                : System.Guid.NewGuid().ToString("N");
        }

        if (TryRoutePendingAskQuestion(command, inputMode)) return;

        switch (command.type)
        {
            case SemanticCommandType.SearchFindInfo:
                RouteSearch(command, inputMode);
                break;
            case SemanticCommandType.Ask:
                RouteAsk(command, inputMode);
                break;
            case SemanticCommandType.Compare:
                Debug.LogWarning("[SemanticCommandRouter] Compare detected, but Compare result UI/schema is not implemented yet.");
                FailVoiceCapture(inputMode, "Compare parser path exists; result UI/router is TODO.");
                Log(command, inputMode, "TODO_COMPARE", false, "Compare parser path exists; result UI/router is TODO.");
                break;
            default:
                Debug.LogWarning($"[SemanticCommandRouter] Unknown voice command transcript='{command.rawTranscript}'");
                FailVoiceCapture(inputMode, "Parser returned Unknown.");
                Log(command, inputMode, "UNKNOWN", false, "Parser returned Unknown.");
                break;
        }
    }

    void RouteSearch(ParsedVoiceCommand command, InputMode inputMode)
    {
        if (inputMode == InputMode.GazeVoice && captureGazeSnapshotForGazeVoice && resultCardSpawner != null)
        {
            resultCardSpawner.CaptureCurrentGazeSnapshot("voice Search/Find Info");
        }

        bool sent = SendSyntheticGestureSequence(searchGestureName);
        if (verboseLogging)
        {
            Debug.Log($"[SemanticCommandRouter] source={command.source} transcript='{command.rawTranscript}' -> synthetic '{searchGestureName}' sent={sent}");
        }
        Log(command, inputMode, "GESTURE_EVENT/SearchFindInfo", sent, sent ? "" : "MsgSender is missing.");
    }

    void RouteAsk(ParsedVoiceCommand command, InputMode inputMode)
    {
        if (allowDirectAskWithoutPendingCard)
        {
            if (inputMode == InputMode.GazeVoice && captureGazeSnapshotForGazeVoice && resultCardSpawner != null)
            {
                resultCardSpawner.CaptureCurrentGazeSnapshot("voice Ask");
            }
            bool sent = SendSyntheticGestureSequence(askGestureName);
            Log(command, inputMode, "GESTURE_EVENT/Ask", sent,
                sent ? "Direct Ask target context still depends on server-side support." : "MsgSender is missing.");
            return;
        }

        Debug.LogWarning("[SemanticCommandRouter] Ask transcript received, but no PendingAskQuestion card is active. Direct voice Ask is TODO until server target context is defined.");
        FailVoiceCapture(inputMode, "No PendingAskQuestion card. Direct voice Ask is TODO.");
        Log(command, inputMode, "TODO_DIRECT_ASK", false, "No PendingAskQuestion card. Direct voice Ask is TODO.");
    }

    bool TryRoutePendingAskQuestion(ParsedVoiceCommand command, InputMode inputMode)
    {
        AskQuestionCard pendingCard = resultCardSpawner != null ? resultCardSpawner.PendingAskQuestion : null;
        if (pendingCard == null) return false;

        if (string.IsNullOrWhiteSpace(command.rawTranscript))
        {
            FailVoiceCapture(inputMode, "Active AskQuestionCard received an empty transcript.");
            Log(command, inputMode, "ASK_QUESTION", false, "Active AskQuestionCard received an empty transcript.");
            return true;
        }

        FailVoiceCapture(inputMode, "Voice transcript routed to the active AskQuestionCard instead of a new gesture command.");
        pendingCard.Submit(command.rawTranscript);
        Log(command, inputMode, "ASK_QUESTION", true, "");
        return true;
    }

    bool SendSyntheticGestureSequence(string gestureName)
    {
        if (msgSender == null) return false;

        if (sendSyntheticStartEvent && !_voiceCaptureOpen)
        {
            SendGestureEvent(pendingGestureName, "START");
        }

        SendGestureEvent(gestureName, "END");
        SendGestureEvent(gestureName, "RECOGNIZED");
        _voiceCaptureOpen = false;

        return true;
    }

    void SendGestureEvent(string gestureName, string eventType)
    {
        msgSender.SendGestureEvent(new GestureEventPayload
        {
            gestureName = gestureName,
            eventType = eventType
        });
    }

    bool HasPendingAskQuestion()
    {
        return resultCardSpawner != null && resultCardSpawner.PendingAskQuestion != null;
    }

    void ResolveReferences()
    {
        if (!autoResolveReferences) return;
        if (msgSender == null) msgSender = FindObjectOfType<MsgSender>();
        if (resultCardSpawner == null) resultCardSpawner = FindObjectOfType<ResultCardSpawner>();
        if (interactionLogger == null) interactionLogger = FindObjectOfType<InteractionLogger>();
    }

    void Log(ParsedVoiceCommand command, InputMode inputMode, string packetType, bool success, string error)
    {
        if (interactionLogger == null) return;

        interactionLogger.LogInteraction(new InteractionLogEntry
        {
            input_mode = inputMode.ToString(),
            source = command != null ? command.source : GetSource(inputMode),
            raw_transcript = command != null ? command.rawTranscript : "",
            parsed_command = command != null ? command.type.ToString() : "Unknown",
            target_strategy = command != null ? command.targetStrategy : GetTargetStrategy(inputMode),
            sent_packet_type = packetType,
            request_id = command != null ? command.requestId : "",
            success = success,
            error_message = error
        });
    }

    static string GetSource(InputMode inputMode)
    {
        return inputMode == InputMode.GazeVoice ? "gaze_voice" : "voice";
    }

    static string GetTargetStrategy(InputMode inputMode)
    {
        if (inputMode == InputMode.GazeVoice) return "gaze";
        if (inputMode == InputMode.VoiceOnly) return "screen_center_or_server_context";
        return "gesture_area";
    }
}
