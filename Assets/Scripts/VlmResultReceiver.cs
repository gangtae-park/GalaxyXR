using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

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
        public string error;             // populated if Python reports vlm_call_failed
        public string raw;               // populated if model returned non-JSON
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
        public string user_question;   // populated by Python when Ask uses voice transcript
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

    public event Action<VlmResultPayload> OnVlmResult;
    private const string PACKET_PREFIX = "VLM_RESULT";

    private UdpClient _client;
    private Thread _listenerThread;
    private volatile bool _running;
    private readonly ConcurrentQueue<VlmResultPayload> _resultQueue =
        new ConcurrentQueue<VlmResultPayload>();

    void OnEnable()
    {
        StartListening();
    }

    void OnDisable()
    {
        StopListening();
    }

    void OnApplicationQuit()
    {
        StopListening();
    }

    void Update()
    {
        while (_resultQueue.TryDequeue(out var payload))
        {
            try
            {
                OnVlmResult?.Invoke(payload);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VlmResultReceiver] OnVlmResult subscriber threw: {e}");
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
            Debug.LogError($"[VlmResultReceiver] Failed to bind UDP port {port}: {e.Message}");
            _client = null;
            return;
        }

        _running = true;
        _listenerThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "VlmResultReceiver"
        };
        _listenerThread.Start();

        Debug.Log($"[VlmResultReceiver] Listening on UDP {port} for {PACKET_PREFIX}");
    }

    void StopListening()
    {
        _running = false;

        if (_client != null)
        {
            try { _client.Close(); }
            catch { /* ignored */ }
            _client = null;
        }

        if (_listenerThread != null)
        {
            try
            {
                if (_listenerThread.IsAlive && !_listenerThread.Join(500))
                {
                    // Background thread, will be torn down by process exit if needed.
                }
            }
            catch { /* ignored */ }
            _listenerThread = null;
        }
    }

    void ListenLoop()
    {
        IPEndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            byte[] data;
            try
            {
                data = _client.Receive(ref anyEp);
            }
            catch (SocketException)
            {
                // Socket closed during shutdown.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VlmResultReceiver] Receive error: {e.Message}");
                continue;
            }

            if (data == null || data.Length == 0) continue;

            string text;
            try
            {
                text = Encoding.UTF8.GetString(data);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VlmResultReceiver] UTF-8 decode failed: {e.Message}");
                continue;
            }

            int sep = text.IndexOf('|');
            if (sep <= 0)
            {
                if (verboseLogging)
                    Debug.LogWarning($"[VlmResultReceiver] Packet has no '|' separator: {Trim(text)}");
                continue;
            }

            string prefix = text.Substring(0, sep);
            if (prefix != PACKET_PREFIX)
            {
                if (verboseLogging)
                    Debug.LogWarning($"[VlmResultReceiver] Unknown prefix '{prefix}'.");
                continue;
            }

            string jsonBody = text.Substring(sep + 1);
            VlmResultPayload payload = TryParse(jsonBody);
            if (payload == null) continue;

            _resultQueue.Enqueue(payload);

            if (verboseLogging)
            {
                Debug.Log(
                    $"[VlmResultReceiver] Received from {anyEp.Address}:{anyEp.Port}  " +
                    $"gesture={payload.gesture} model={payload.model} name={payload.response.name}"
                );
            }
        }
    }

    static VlmResultPayload TryParse(string json)
    {
        try
        {
            return JsonUtility.FromJson<VlmResultPayload>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VlmResultReceiver] JSON parse failed: {e.Message}\n{Trim(json)}");
            return null;
        }
    }

    static string Trim(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return s.Length <= max ? s : (s.Substring(0, max) + "...");
    }
}
