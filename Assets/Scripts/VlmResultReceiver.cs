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

    [Header("Network")]
    public int port = 5006;
    public bool verboseLogging = true;

    [Header("Object action menu")]
    public bool enableObjectActionMenu = true;
    public bool autoCreateObjectActionMenuSpawner = true;
    public ObjectActionRadialMenuSpawner objectActionMenuSpawner;

    public event Action<VlmResultPayload> OnResult;
    private const string PACKET_PREFIX = "VLM_RESULT";

    private UdpClient _client;
    private Thread _listenerThread;
    private volatile bool _running;
    private readonly ConcurrentQueue<VlmResultPayload> _queue = new ConcurrentQueue<VlmResultPayload>();

    void OnEnable() { StartListening(); }
    void OnDisable() { StopListening(); }
    void OnApplicationQuit() { StopListening(); }

    void Update()
    {
        while (_queue.TryDequeue(out var payload))
        {
            if (verboseLogging) LogReceivedPayload(payload);

            try { OnResult?.Invoke(payload); }
            catch (Exception e) { Debug.LogError($"[Study Log][VlmResultReceiver] OnResult subscriber threw: {e}"); }

            if (enableObjectActionMenu && payload.gesture != "ObjectUI" && PayloadContainsDetectionLikeData(payload))
            {
                Debug.LogWarning($"[ObjectActionMenu][WARN] ignoring non-ObjectUI detection-like payload gesture={payload.gesture} request_id={FirstNonEmpty(payload.request_id, payload.requestId)}; route via ResultCardSpawner only.");
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
        Debug.Log($"[Study Log][VlmResultReceiver] listening on UDP {port} for {PACKET_PREFIX}");
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
            if (text.Substring(0, sep) != PACKET_PREFIX) continue;

            string body = text.Substring(sep + 1);
            VlmResultPayload payload;
            try { payload = JsonUtility.FromJson<VlmResultPayload>(body); }
            catch (Exception e) { Debug.LogWarning($"[Study Log][VlmResultReceiver] json: {e.Message}"); continue; }
            if (payload == null) continue;

            _queue.Enqueue(payload);
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
