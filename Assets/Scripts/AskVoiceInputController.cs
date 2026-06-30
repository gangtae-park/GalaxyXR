using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/*
Drives the Ask flow's voice input using on-device Android STT.

  1) Watches ResultCardSpawner.PendingAskQuestion. When it transitions
     null -> non-null (i.e. an AskQuestionCard was just spawned by a gesture
     Ask), the controller asks AndroidSpeechRecognizerBridge to start listening.

  2) On the bridge's OnFinalTranscript, the resulting string is:
       - applied locally to the AskQuestionCard via SubmitLocal(transcript)
         (status flips from "Listening..." to "Thinking...")
       - POSTed to MacProgram's /ask_voice endpoint as JSON
         { transcript, request_id }
     MacProgram then runs the Ask pipeline against the cached Ask target.

  3) If the AskQuestionCard is destroyed before the transcript arrives (user
     closed it, mode changed, etc.), the STT session is cancelled.

Android requires the RECORD_AUDIO permission. The bridge handles the JNI side;
this component is just the policy layer that pairs STT with the Ask card.
*/

public class AskVoiceInputController : MonoBehaviour
{
    [Header("Refs")]
    public ResultCardSpawner spawner;
    public AndroidSpeechRecognizerBridge speechBridge;

    [Header("Network")]
    [Tooltip("MacProgram endpoint that accepts {transcript, request_id} JSON and dispatches process_ask_question.")]
    public string voiceServerUrl = "http://192.168.0.3:5007/ask_voice";

    [Header("Behavior")]
    public bool autoResolveReferences = true;
    public bool verboseLogging = true;

    [Header("Status (read-only)")]
    [SerializeField] private bool isListening;
    [SerializeField] private string lastTranscript;
    [SerializeField] private string activeRequestId;

    private AskQuestionCard _activeCard;

    void Awake() { ResolveReferences(); }

    void OnEnable()
    {
        ResolveReferences();
        SubscribeBridge();
    }

    void OnDisable()
    {
        CancelListening("controller disabled");
        UnsubscribeBridge();
    }

    void Update()
    {
        AskQuestionCard current = spawner != null ? spawner.PendingAskQuestion : null;
        if (current == _activeCard) return;

        if (_activeCard != null) CancelListening("active card changed");
        _activeCard = current;
        if (current != null) StartListening();
    }

    void StartListening()
    {
        if (speechBridge == null)
        {
            Debug.LogWarning("[AskVoice] AndroidSpeechRecognizerBridge not assigned; cannot start STT.");
            return;
        }

        if (speechBridge.IsListening)
        {
            if (verboseLogging) Debug.Log("[AskVoice] bridge already listening; piggy-backing on existing session.");
            isListening = true;
            return;
        }

        activeRequestId = Guid.NewGuid().ToString("N");
        speechBridge.StartListening();
        isListening = true;
        Debug.Log($"[AskVoice] STT START request_id={activeRequestId}");
    }

    void CancelListening(string reason)
    {
        if (!isListening) return;
        if (speechBridge != null && speechBridge.IsListening) speechBridge.CancelListening();
        isListening = false;
        activeRequestId = null;
        Debug.Log($"[AskVoice] STT CANCEL ({reason})");
    }

    void HandleFinalTranscript(string transcript)
    {
        if (!isListening) return;
        isListening = false;

        string text = transcript == null ? "" : transcript.Trim();
        lastTranscript = text;
        string requestId = activeRequestId ?? "";
        activeRequestId = null;

        Debug.Log($"[AskVoice] STT FINAL request_id={requestId} transcript='{text}'");

        if (string.IsNullOrEmpty(text))
        {
            Debug.LogWarning("[AskVoice] empty transcript; nothing to send.");
            return;
        }

        if (_activeCard != null) _activeCard.SubmitLocal(text);
        StartCoroutine(PostTranscript(text, requestId));
    }

    void HandleSpeechError(int code, string message)
    {
        if (!isListening) return;
        isListening = false;
        activeRequestId = null;
        Debug.LogWarning($"[AskVoice] STT ERROR code={code} msg={message}");
    }

    IEnumerator PostTranscript(string transcript, string requestId)
    {
        AskTranscriptPayload payload = new AskTranscriptPayload
        {
            transcript = transcript ?? "",
            request_id = requestId ?? "",
            requestId = requestId ?? "",
        };
        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest(voiceServerUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");

            if (verboseLogging)
                Debug.Log($"[AskVoice] POST transcript request_id={requestId} bytes={body.Length} url={voiceServerUrl}");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
                Debug.Log($"[AskVoice] POST SUCCESS code={request.responseCode} body={request.downloadHandler.text}");
            else
                Debug.LogError($"[AskVoice] POST FAILED: {request.error} ({request.result})");
        }
    }

    void SubscribeBridge()
    {
        if (speechBridge == null) return;
        speechBridge.OnFinalTranscript -= HandleFinalTranscript;
        speechBridge.OnError -= HandleSpeechError;
        speechBridge.OnFinalTranscript += HandleFinalTranscript;
        speechBridge.OnError += HandleSpeechError;
    }

    void UnsubscribeBridge()
    {
        if (speechBridge == null) return;
        speechBridge.OnFinalTranscript -= HandleFinalTranscript;
        speechBridge.OnError -= HandleSpeechError;
    }

    void ResolveReferences()
    {
        if (!autoResolveReferences) return;
        if (spawner == null) spawner = FindObjectOfType<ResultCardSpawner>();
        if (speechBridge == null) speechBridge = FindObjectOfType<AndroidSpeechRecognizerBridge>();
    }

    [Serializable]
    private class AskTranscriptPayload
    {
        public string transcript;
        public string request_id;
        public string requestId;
    }
}
