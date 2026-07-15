using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/*
Listens on a UDP port for "VLM_RESULT|{json}" datagrams from the MacProgram and re-emits each parsed payload on the main thread via the OnResult event.
Per-gesture spawners subscribe to OnResult, filter by payload.gesture, and instantiate their own card prefab.
*/

public class VlmResultReceiver : MonoBehaviour
{
    [Serializable]
    public class CompareRow
    {
        public string category;
        public string value_a;
        public string value_b;
    }

    [Serializable]
    public class VlmResponse
    {
        public string name;
        public string object_id;
        public string description;
        public string typical_use;
        public string info;
        public string answer;
        public string result;
        public string text;
        public string translation;
        public string result_search;
        public string message;
        public string error;
        public string raw;
        public string finish_reason;
        public string refusal;
        // Voice Save shortcut: when Python's voice classifier extracts a note
        // body from the transcript (e.g. "3시 회의라고 저장해줘"), it forwards
        // the body here so NoteManager can commit a StickyNote directly and
        // skip the SaveNoteCard input UI. Empty for gesture Save.
        public string note_text;
        // Compare-specific: when the gesture is "Compare", these carry the two
        // object names and the per-category table that CompareResultCard renders.
        public string name_a;
        public string name_b;
        public CompareRow[] compare_rows;
        // Anchor info for distance-aware card placement. gaze_dir is the
        // head-space unit vector to the target (from Python inverse calibration
        // applied to the YOLO bbox centre); depth_meters comes from Depth
        // Anything V2. Zero values mean Python didn't supply anchor data and
        // the spawner should fall back to the legacy fixed-distance placement.
        public float gaze_dir_x;
        public float gaze_dir_y;
        public float gaze_dir_z;
        public float depth_meters;
        public string depth_source;  // "mask" | "bbox" | "none"
    }

    [Serializable]
    public class VlmTargetMeta
    {
        public string source;
        public int[] bbox;
        public int[] frame_size;   // [width, height] of the source frame; lets Unity convert bbox pixels to viewport fractions
        public float best_overlap;
        public float best_iou;
        public string class_name;
        public string label;
        public float conf;
        public float confidence;
        public float sam_score;
        public int area;
        public float[] crop_bbox;
        public float[] gaze_bbox;
        public string user_question;
    }

    [Serializable]
    public class VlmDetection
    {
        public string request_id;
        public string requestId;
        public string label;          // human-readable name (DB name after CLIP filter)
        public string class_name;
        public string object_id;      // DB key from CLIP match, e.g. "object_a"
        public float confidence;
        public float conf;
        public float[] bbox;
        public int image_width;
        public int image_height;
        public int imageWidth;
        public int imageHeight;
        // Bubble world-space anchoring: gaze direction (head-space unit vector)
        // computed via inverse gaze calibration applied to the bbox centre, and
        // metric depth from Depth Anything V2. Together they let Unity place the
        // bubble at camera + R_capture * gaze_dir * depth, no raycast required.
        public float gaze_dir_x;
        public float gaze_dir_y;
        public float gaze_dir_z;
        public float depth_meters;
        public string depth_source;  // "mask" | "bbox" | "none"
    }

    [Serializable]
    public class VlmResultPayload
    {
        public string request_id;
        public string requestId;
        public string timestamp;
        public string gesture;
        public string model;
        public string status;   // "ok" | "fail"
        public string stage;    // "ack" (early gesture-handler fail) | "answer" (post-VLM)
        public string reason;   // free-form reason text when status == "fail"
        public string label;
        public string class_name;
        public float confidence;
        public float conf;
        public float[] bbox;
        public int image_width;
        public int image_height;
        public int imageWidth;
        public int imageHeight;
        public VlmDetection[] detections;
        public VlmTargetMeta target_meta;
        public VlmResponse response;
    }

    // One incremental chunk of a streaming LLM answer. Emitted per OpenAI
    // content chunk (Python's on_delta callback). Cards append `delta` to
    // whatever they've already rendered.
    [Serializable]
    public class VlmStreamDeltaPayload
    {
        public string stream_id;
        public string request_id;
        public string requestId;
        public string gesture;
        public string stage;   // "answer" (Ask) | "translation" (Translate)
        public int seq;
        public string delta;
        public VlmTargetMeta target_meta;  // usually only on seq==0
    }

    // Terminator for a stream. Carries the full assembled response so the
    // spawner can commit anchor / metadata / final answer text (the deltas
    // alone are enough to render, but END lets us do things like write the
    // canonical name field once).
    [Serializable]
    public class VlmStreamEndPayload
    {
        public string stream_id;
        public string request_id;
        public string requestId;
        public string gesture;
        public string stage;
        public string status;   // "ok" | "fail"
        public string error;
        public VlmResponse response;
        public VlmTargetMeta target_meta;
    }

    // Discriminated queue entry so the listener thread can push all three
    // packet kinds into one queue and Update() dispatches to the right event.
    private enum QueueKind { Result, Delta, End }
    private class QueueEntry
    {
        public QueueKind kind;
        public VlmResultPayload result;
        public VlmStreamDeltaPayload delta;
        public VlmStreamEndPayload end;
    }

    [Header("Network")]
    [Tooltip("Optional explicit asset; when null, Resources/NetworkSettings.asset is used. The listen port comes ONLY from NetworkSettings (resultUdpPort).")]
    public NetworkSettings networkSettings;
    private int port = 5006;
    public bool verboseLogging = true;

    [Header("Object action menu")]
    public bool enableObjectActionMenu = true;
    public bool autoCreateObjectActionMenuSpawner = true;
    public ObjectActionRadialMenuSpawner objectActionMenuSpawner;

    public event Action<VlmResultPayload> OnResult;
    public event Action<VlmStreamDeltaPayload> OnStreamDelta;
    public event Action<VlmStreamEndPayload> OnStreamEnd;

    private const string PACKET_PREFIX = "VLM_RESULT";
    private const string STREAM_DELTA_PREFIX = "VLM_STREAM_DELTA";
    private const string STREAM_END_PREFIX = "VLM_STREAM_END";

    private UdpClient _client;
    private Thread _listenerThread;
    private volatile bool _running;
    private readonly ConcurrentQueue<QueueEntry> _queue = new ConcurrentQueue<QueueEntry>();

    void OnEnable()
    {
        ApplyNetworkSettings();
        StartListening();
    }

    void ApplyNetworkSettings()
    {
        NetworkSettings settings = networkSettings != null ? networkSettings : NetworkSettings.Instance;
        if (settings == null)
        {
            Debug.LogWarning($"[VlmResultReceiver] NetworkSettings asset missing -- falling back to built-in default listen port {port}.");
            return;
        }
        if (settings.resultUdpPort > 0) port = settings.resultUdpPort;
    }
    void OnDisable() { StopListening(); }
    void OnApplicationQuit() { StopListening(); }

    void Update()
    {
        while (_queue.TryDequeue(out var entry))
        {
            switch (entry.kind)
            {
                case QueueKind.Result:
                    if (verboseLogging) LogReceivedPayload(entry.result);
                    try { OnResult?.Invoke(entry.result); }
                    catch (Exception e) { Debug.LogError($"[Study Log][VlmResultReceiver] OnResult subscriber threw: {e}"); }
                    if (enableObjectActionMenu && entry.result.gesture != "ObjectUI" && PayloadContainsDetectionLikeData(entry.result))
                    {
                        Debug.LogWarning($"[ObjectActionMenu][WARN] ignoring non-ObjectUI detection-like payload gesture={entry.result.gesture} request_id={FirstNonEmpty(entry.result.request_id, entry.result.requestId)}; route via ResultCardSpawner only.");
                    }
                    break;
                case QueueKind.Delta:
                    if (verboseLogging)
                        Debug.Log($"[VLM_STREAM_DELTA] gesture={entry.delta.gesture} stage={entry.delta.stage} seq={entry.delta.seq} len={entry.delta.delta?.Length ?? 0}");
                    try { OnStreamDelta?.Invoke(entry.delta); }
                    catch (Exception e) { Debug.LogError($"[Study Log][VlmResultReceiver] OnStreamDelta subscriber threw: {e}"); }
                    break;
                case QueueKind.End:
                    if (verboseLogging)
                        Debug.Log($"[VLM_STREAM_END] gesture={entry.end.gesture} stage={entry.end.stage} status={entry.end.status}");
                    try { OnStreamEnd?.Invoke(entry.end); }
                    catch (Exception e) { Debug.LogError($"[Study Log][VlmResultReceiver] OnStreamEnd subscriber threw: {e}"); }
                    break;
            }
        }
    }

    void StartListening()
    {
        if (_running) return;
        try
        {
            _client = new UdpClient(port);
            _client.Client.ReceiveBufferSize = 1 << 18;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Study Log][VlmResultReceiver] bind UDP {port} failed: {e.Message}");
            _client = null;
            return;
        }

        _running = true;
        _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "VlmResultReceiver" };
        _listenerThread.Start();
        Debug.Log($"[Study Log][VlmResultReceiver] listening on UDP {port} for {PACKET_PREFIX} + {STREAM_DELTA_PREFIX}/{STREAM_END_PREFIX}");
    }

    void StopListening()
    {
        _running = false;
        if (_client != null) { try { _client.Close(); } catch { } _client = null; }
        if (_listenerThread != null)
        {
            try { if (_listenerThread.IsAlive) _listenerThread.Join(500); } catch { }
            _listenerThread = null;
        }
    }

    void ListenLoop()
    {
        IPEndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            byte[] data;
            try { data = _client.Receive(ref anyEp); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e) { Debug.LogWarning($"[Study Log][VlmResultReceiver] receive: {e.Message}"); continue; }

            if (data == null || data.Length == 0) continue;

            string text;
            try { text = Encoding.UTF8.GetString(data); }
            catch (Exception e) { Debug.LogWarning($"[Study Log][VlmResultReceiver] decode: {e.Message}"); continue; }

            int sep = text.IndexOf('|');
            if (sep <= 0) continue;
            string prefix = text.Substring(0, sep);
            string body = text.Substring(sep + 1);

            try
            {
                if (prefix == PACKET_PREFIX)
                {
                    var p = JsonUtility.FromJson<VlmResultPayload>(body);
                    if (p != null) _queue.Enqueue(new QueueEntry { kind = QueueKind.Result, result = p });
                }
                else if (prefix == STREAM_DELTA_PREFIX)
                {
                    var p = JsonUtility.FromJson<VlmStreamDeltaPayload>(body);
                    if (p != null) _queue.Enqueue(new QueueEntry { kind = QueueKind.Delta, delta = p });
                }
                else if (prefix == STREAM_END_PREFIX)
                {
                    var p = JsonUtility.FromJson<VlmStreamEndPayload>(body);
                    if (p != null) _queue.Enqueue(new QueueEntry { kind = QueueKind.End, end = p });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Study Log][VlmResultReceiver] json {prefix}: {e.Message}");
            }
        }
    }

    void LogReceivedPayload(VlmResultPayload payload)
    {
        if (payload == null) return;

        VlmResponse response = payload.response;
        string requestId = FirstNonEmpty(payload.request_id, payload.requestId);
        string name = response != null ? response.name : "";
        string text = FirstNonEmpty(
            response != null ? response.text : "",
            payload.target_meta != null ? payload.target_meta.user_question : "");
        string transcript = payload.target_meta != null ? payload.target_meta.user_question : "";
        string answer = FirstNonEmpty(
            response != null ? response.answer : "",
            response != null ? response.result : "",
            response != null ? response.result_search : "",
            response != null ? response.info : "",
            response != null ? response.description : "",
            response != null ? response.raw : "",
            response != null ? response.error : "",
            payload.reason);

        Debug.Log($"[VLM_RESULT] received raw gesture={payload.gesture} name={name} text={text} answer={answer} request_id={requestId}");

        if (payload.gesture == "VoiceAsk")
        {
            Debug.Log($"[VOICE_RESULT] server response received request_id={requestId}");
            Debug.Log($"[VOICE_RESULT] transcript='{transcript}'");
            Debug.Log($"[VOICE_RESULT] answer='{answer}'");
        }
    }

    static bool PayloadContainsDetectionLikeData(VlmResultPayload payload)
    {
        if (payload == null) return false;
        if (payload.detections != null && payload.detections.Length > 0) return true;
        if (payload.bbox != null && payload.bbox.Length >= 4) return true;
        if (payload.target_meta != null && payload.target_meta.bbox != null && payload.target_meta.bbox.Length >= 4) return true;
        return false;
    }

    static string FirstNonEmpty(params string[] values)
    {
        if (values == null) return "";
        for (int i = 0; i < values.Length; i++)
            if (!string.IsNullOrEmpty(values[i])) return values[i];
        return "";
    }
}
