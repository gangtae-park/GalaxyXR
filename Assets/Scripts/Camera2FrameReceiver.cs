using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

public class Camera2FrameReceiver : MonoBehaviour
{
    [Header("Camera2 Capture")]
    public int width = 1280;
    public int height = 720;

    [Header("YOLO Processing")]
    public int inferenceIntervalMs = 1000;

    [Header("Debug Stream to Mac")]
    public bool enableDebugStream = false;
    public string debugStreamIp = "192.168.0.2";
    public int debugStreamPort = 5005;
    public int debugStreamFps = 60;
    public int debugMaxWidth = 480;
    public int debugMaxHeight = 270;
    [Range(1, 100)] public int jpegQuality = 50;

    public YoloSegLogger yoloLogger;

    private AndroidJavaObject plugin;
    private Texture2D latestTexture;
    private Texture2D debugTexture;
    private byte[] latestRgbaBytes;
    private float lastInferenceTime;
    private float lastDebugStreamTime;
    private int debugWidth;
    private int debugHeight;

    private UdpClient udpClient;
    private IPEndPoint debugStreamEndPoint;

    private bool isProcessing = false;

    private void Start()
    {
        latestTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);

        if (enableDebugStream)
        {
            CalculateDebugStreamSize();
            debugTexture = new Texture2D(debugWidth, debugHeight, TextureFormat.RGB24, false);
            udpClient = new UdpClient();
            debugStreamEndPoint = new IPEndPoint(IPAddress.Parse(debugStreamIp), debugStreamPort);
            Debug.Log($"[DebugStream] UDP stream enabled: {debugStreamIp}:{debugStreamPort}, fps={debugStreamFps}, size={debugWidth}x{debugHeight}, jpegQuality={jpegQuality}");
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

        plugin = new AndroidJavaObject(
            "com.example.camera2plugin.Camera2StreamPlugin",
            activity,
            gameObject.name,
            width,
            height
        );

        plugin.Call("startCamera");
#endif
    }

    // Android plugin에서 UnitySendMessage로 호출
    public void OnFrameAvailable(string message)
    {
        // message 예: "frame_ready"
        // 실제 frame byte[] 전달은 UnitySendMessage로는 비효율적이라,
        // Unity가 plugin.getLatestRgbaFrame()을 pull 하는 구조를 사용
        TryRunInference();
    }

    private void TryRunInference()
    {
        float now = Time.realtimeSinceStartup;

#if UNITY_ANDROID && !UNITY_EDITOR
        latestRgbaBytes = plugin.Call<byte[]>("getLatestRgbaFrame");
        if (latestRgbaBytes == null || latestRgbaBytes.Length == 0)
            return;

        if (latestRgbaBytes.Length != width * height * 4)
        {
            Debug.LogWarning($"[Camera2] Invalid frame size: {latestRgbaBytes.Length}, expected={width * height * 4}");
            return;
        }

        latestTexture.LoadRawTextureData(latestRgbaBytes);
        latestTexture.Apply(false);

        TrySendDebugFrame(now);
        TryRunYolo(now);
#endif
    }

    private void TryRunYolo(float now)
    {
        if (isProcessing)
            return;

        if ((now - lastInferenceTime) * 1000f < inferenceIntervalMs)
            return;

        lastInferenceTime = now;

        if (yoloLogger == null)
        {
            Debug.LogWarning("[Camera2] YoloSegLogger is not assigned.");
            return;
        }

        isProcessing = true;
        yoloLogger.RunAndLog(latestTexture, () =>
        {
            isProcessing = false;
        });
    }

    private void CalculateDebugStreamSize()
    {
        float scale = Mathf.Min(
            debugMaxWidth / (float)width,
            debugMaxHeight / (float)height,
            1f
        );

        debugWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
        debugHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
    }

    private void TrySendDebugFrame(float now)
    {
        if (!enableDebugStream || udpClient == null || debugStreamEndPoint == null || debugTexture == null)
            return;

        int fps = Mathf.Max(1, debugStreamFps);
        float streamIntervalMs = 1000f / fps;
        if ((now - lastDebugStreamTime) * 1000f < streamIntervalMs)
            return;

        lastDebugStreamTime = now;

        try
        {
            DownsampleAndFlipForDebugStream();
            byte[] jpgBytes = debugTexture.EncodeToJPG(jpegQuality);
            udpClient.Send(jpgBytes, jpgBytes.Length, debugStreamEndPoint);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DebugStream] Failed to send frame: {e.Message}");
        }
    }

    private void DownsampleAndFlipForDebugStream()
    {
        Color32[] src = latestTexture.GetPixels32();
        Color32[] dst = new Color32[debugWidth * debugHeight];

        for (int y = 0; y < debugHeight; y++)
        {
            int srcY = Mathf.Clamp(Mathf.FloorToInt((y + 0.5f) * height / debugHeight), 0, height - 1);
            int flippedSrcY = height - 1 - srcY;

            for (int x = 0; x < debugWidth; x++)
            {
                int srcX = Mathf.Clamp(Mathf.FloorToInt((x + 0.5f) * width / debugWidth), 0, width - 1);
                dst[y * debugWidth + x] = src[flippedSrcY * width + srcX];
            }
        }

        debugTexture.SetPixels32(dst);
        debugTexture.Apply(false);
    }

    private void OnDestroy()
    {
        udpClient?.Close();
        udpClient = null;
        if (debugTexture != null)
        {
            Destroy(debugTexture);
            debugTexture = null;
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        plugin?.Call("stopCamera");
#endif
    }
}