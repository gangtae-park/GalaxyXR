using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// Supplies the current camera frame to YOLO.
/// Galaxy XR builds use the on-device Camera2 plugin; Editor Play Mode uses
/// a PC webcam while keeping the same gesture-to-inference pipeline.
/// </summary>
public class Camera2FrameReceiver : MonoBehaviour
{
    [Header("Camera Capture")]
    public int width = 1280;
    public int height = 720;

    [Header("YOLO Processing")]
    public int inferenceIntervalMs = 1000;
    [Tooltip("When disabled, YOLO runs only after a recognized Search gesture.")]
    public bool runInferenceContinuously = false;

    [Header("Editor Play Mode")]
    [Tooltip("Use a PC webcam as the frame source in Editor Play Mode.")]
    public bool useEditorWebcam = true;
    public int editorWebcamIndex = 0;

    [Header("Debug Stream")]
    public bool enableDebugStream = false;
    public string debugStreamIp = "192.168.0.2";
    public int debugStreamPort = 5005;
    public int debugStreamFps = 60;
    public int debugMaxWidth = 480;
    public int debugMaxHeight = 270;
    [Range(1, 100)] public int jpegQuality = 50;

    public YoloSegLogger yoloLogger;
    public int FrameWidth => latestTexture != null ? latestTexture.width : width;
    public int FrameHeight => latestTexture != null ? latestTexture.height : height;
    public bool HasFrame { get; private set; }
    public string ActiveSource { get; private set; } = "None";
    public event Action<string> OnCameraStatusChanged;

    private AndroidJavaObject plugin;
    private Texture2D latestTexture;
    private Texture2D debugTexture;
    private WebCamTexture editorWebcam;
    private byte[] latestRgbaBytes;
    private sbyte[] latestRgbaSBytes;
    private float lastInferenceTime;
    private float lastDebugStreamTime;
    private int debugWidth;
    private int debugHeight;
    private UdpClient udpClient;
    private IPEndPoint debugStreamEndPoint;
    private bool isProcessing;

    private void Start()
    {
        latestTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        InitializeDebugStream();

#if UNITY_ANDROID && !UNITY_EDITOR
        StartAndroidCamera();
#elif UNITY_EDITOR
        StartEditorWebcam();
#else
        ReportStatus("No camera provider is available on this platform");
#endif
    }

    private void Update()
    {
#if UNITY_EDITOR
        UpdateEditorWebcam();
#endif
    }

    /// <summary>Called by the Android Camera2 plugin through UnitySendMessage.</summary>
    public void OnFrameAvailable(string message)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        PullAndroidFrame();
#endif
    }

    /// <summary>
    /// Runs YOLO against the latest frame. Search gestures use this entry point
    /// on both Android builds and Editor Play Mode.
    /// </summary>
    public void RequestInference(Action<bool> onComplete)
    {
        if (!HasFrame || latestTexture == null)
        {
            ReportStatus($"No frame available from {ActiveSource}");
            onComplete?.Invoke(false);
            return;
        }

        RunYolo(Time.realtimeSinceStartup, onComplete, true);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void StartAndroidCamera()
    {
        ActiveSource = "Galaxy XR Camera2";
        using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        AndroidJavaObject activity =
            unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

        plugin = new AndroidJavaObject(
            "com.example.camera2plugin.Camera2StreamPlugin",
            activity,
            gameObject.name,
            width,
            height);

        plugin.Call("startCamera");
        ReportStatus($"Started {ActiveSource}");
    }

    private void PullAndroidFrame()
    {
        latestRgbaSBytes = plugin?.Call<sbyte[]>("getLatestRgbaFrame");
        if (latestRgbaSBytes == null || latestRgbaSBytes.Length == 0)
            return;

        if (latestRgbaBytes == null || latestRgbaBytes.Length != latestRgbaSBytes.Length)
            latestRgbaBytes = new byte[latestRgbaSBytes.Length];

        Buffer.BlockCopy(
            latestRgbaSBytes,
            0,
            latestRgbaBytes,
            0,
            latestRgbaSBytes.Length);

        if (latestRgbaBytes.Length != width * height * 4)
        {
            Debug.LogWarning(
                $"[CameraFrame] Invalid Camera2 frame size: {latestRgbaBytes.Length}, " +
                $"expected={width * height * 4}");
            return;
        }

        latestTexture.LoadRawTextureData(latestRgbaBytes);
        latestTexture.Apply(false);
        HasFrame = true;

        float now = Time.realtimeSinceStartup;
        TrySendDebugFrame(now);
        if (runInferenceContinuously)
            RunYolo(now, null);
    }
#endif

#if UNITY_EDITOR
    private void StartEditorWebcam()
    {
        if (!useEditorWebcam)
        {
            ActiveSource = "Editor webcam disabled";
            ReportStatus(ActiveSource);
            return;
        }

        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            ActiveSource = "No PC webcam";
            ReportStatus(ActiveSource);
            return;
        }

        int index = Mathf.Clamp(editorWebcamIndex, 0, devices.Length - 1);
        ActiveSource = $"PC webcam: {devices[index].name}";
        editorWebcam = new WebCamTexture(devices[index].name, width, height, 30);
        editorWebcam.Play();
        ReportStatus($"Starting {ActiveSource}");
    }

    private void UpdateEditorWebcam()
    {
        if (editorWebcam == null ||
            !editorWebcam.isPlaying ||
            !editorWebcam.didUpdateThisFrame)
        {
            return;
        }

        int frameWidth = editorWebcam.width;
        int frameHeight = editorWebcam.height;
        if (frameWidth <= 16 || frameHeight <= 16)
            return;

        if (latestTexture == null ||
            latestTexture.width != frameWidth ||
            latestTexture.height != frameHeight)
        {
            if (latestTexture != null)
                Destroy(latestTexture);

            latestTexture =
                new Texture2D(frameWidth, frameHeight, TextureFormat.RGBA32, false);
        }

        latestTexture.SetPixels32(editorWebcam.GetPixels32());
        latestTexture.Apply(false);
        HasFrame = true;

        float now = Time.realtimeSinceStartup;
        TrySendDebugFrame(now);
        if (runInferenceContinuously)
            RunYolo(now, null);
    }
#endif

    private void RunYolo(
        float now,
        Action<bool> onComplete,
        bool ignoreInterval = false)
    {
        if (isProcessing)
        {
            onComplete?.Invoke(false);
            return;
        }

        if (!ignoreInterval &&
            (now - lastInferenceTime) * 1000f < inferenceIntervalMs)
        {
            onComplete?.Invoke(false);
            return;
        }

        if (yoloLogger == null)
        {
            ReportStatus("YoloSegLogger is not assigned");
            onComplete?.Invoke(false);
            return;
        }

        lastInferenceTime = now;
        isProcessing = true;
        ReportStatus($"Running YOLO from {ActiveSource}");

        yoloLogger.RunAndLog(latestTexture, () =>
        {
            isProcessing = false;
            onComplete?.Invoke(true);
        });
    }

    private void InitializeDebugStream()
    {
        if (!enableDebugStream)
            return;

        CalculateDebugStreamSize();
        debugTexture =
            new Texture2D(debugWidth, debugHeight, TextureFormat.RGB24, false);
        udpClient = new UdpClient();
        debugStreamEndPoint =
            new IPEndPoint(IPAddress.Parse(debugStreamIp), debugStreamPort);
    }

    private void CalculateDebugStreamSize()
    {
        float scale = Mathf.Min(
            debugMaxWidth / (float)width,
            debugMaxHeight / (float)height,
            1f);

        debugWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
        debugHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
    }

    private void TrySendDebugFrame(float now)
    {
        if (!enableDebugStream ||
            udpClient == null ||
            debugStreamEndPoint == null ||
            debugTexture == null ||
            latestTexture == null)
        {
            return;
        }

        float intervalMs = 1000f / Mathf.Max(1, debugStreamFps);
        if ((now - lastDebugStreamTime) * 1000f < intervalMs)
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
        int sourceWidth = latestTexture.width;
        int sourceHeight = latestTexture.height;

        for (int y = 0; y < debugHeight; y++)
        {
            int srcY = Mathf.Clamp(
                Mathf.FloorToInt((y + 0.5f) * sourceHeight / debugHeight),
                0,
                sourceHeight - 1);
            int flippedSrcY = sourceHeight - 1 - srcY;

            for (int x = 0; x < debugWidth; x++)
            {
                int srcX = Mathf.Clamp(
                    Mathf.FloorToInt((x + 0.5f) * sourceWidth / debugWidth),
                    0,
                    sourceWidth - 1);
                dst[y * debugWidth + x] =
                    src[flippedSrcY * sourceWidth + srcX];
            }
        }

        debugTexture.SetPixels32(dst);
        debugTexture.Apply(false);
    }

    private void ReportStatus(string status)
    {
        Debug.Log($"[CameraFrame] {status}");
        try { OnCameraStatusChanged?.Invoke(status); }
        catch (Exception e)
        {
            Debug.LogError($"[CameraFrame] Status subscriber threw: {e}");
        }
    }

    private void OnDestroy()
    {
#if UNITY_EDITOR
        if (editorWebcam != null)
        {
            editorWebcam.Stop();
            editorWebcam = null;
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        plugin?.Call("stopCamera");
#endif

        udpClient?.Close();
        udpClient = null;

        if (latestTexture != null)
            Destroy(latestTexture);
        if (debugTexture != null)
            Destroy(debugTexture);
    }
}
