using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/*
VlmResultReceiver

Listens on a UDP port for "VLM_RESULT|{json}" datagrams from the MacProgram
and re-emits each parsed payload on the main thread via the OnResult event.

Per-gesture spawners (SearchResultCardSpawner, etc.) subscribe to OnResult,
filter by payload.gesture, and instantiate their own card prefab.
*/

public class VlmResultReceiver : MonoBehaviour
{
    [Serializable]
    public class VlmResponse
    {
        public string name;
        public string description;
        public string typical_use;
        public string info;
        public string answer;
        public string translation;
        public string result_search;
        public string error;
        public string raw;
        public string finish_reason;
        public string refusal;
    }

    [Serializable]
    public class VlmTargetMeta
    {
        public string source;
        public int[] bbox;
        public float best_overlap;
        public float best_iou;
        public string class_name;
        public float conf;
        public float sam_score;
        public int area;
        public int[] crop_bbox;
        public int[] gaze_bbox;
        public string user_question;
    }

    [Serializable]
    public class VlmResultPayload
    {
        public string timestamp;
        public string gesture;
        public string model;
        public VlmTargetMeta target_meta;
        public VlmResponse response;
    }

    [Header("Network")]
    public int port = 5006;
    public bool verboseLogging = true;

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
            try { OnResult?.Invoke(payload); }
            catch (Exception e) { Debug.LogError($"[VlmResultReceiver] OnResult subscriber threw: {e}"); }
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
            Debug.LogError($"[VlmResultReceiver] bind UDP {port} failed: {e.Message}");
            _client = null;
            return;
        }

        _running = true;
        _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "VlmResultReceiver" };
        _listenerThread.Start();
        Debug.Log($"[VlmResultReceiver] listening on UDP {port} for {PACKET_PREFIX}");
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
            catch (Exception e) { Debug.LogWarning($"[VlmResultReceiver] receive: {e.Message}"); continue; }

            if (data == null || data.Length == 0) continue;

            string text;
            try { text = Encoding.UTF8.GetString(data); }
            catch (Exception e) { Debug.LogWarning($"[VlmResultReceiver] decode: {e.Message}"); continue; }

            int sep = text.IndexOf('|');
            if (sep <= 0) continue;
            if (text.Substring(0, sep) != PACKET_PREFIX) continue;

            string body = text.Substring(sep + 1);
            VlmResultPayload payload;
            try { payload = JsonUtility.FromJson<VlmResultPayload>(body); }
            catch (Exception e) { Debug.LogWarning($"[VlmResultReceiver] json: {e.Message}"); continue; }
            if (payload == null) continue;

            _queue.Enqueue(payload);
            if (verboseLogging)
            {
                Debug.Log($"[VlmResultReceiver] received gesture={payload.gesture} name={payload.response?.name}");
            }
        }
    }
}
