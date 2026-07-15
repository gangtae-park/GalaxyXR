using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class ObjectUiRequestManager : MonoBehaviour
{
    enum RequestState
    {
        Idle,
        WaitingForSceneCapture,
        SendingToServer,
        WaitingForYoloResponse
    }

    [Serializable]
    class ObjectUiImagePayload
    {
        public string request_id;
        public string requestId;
        public string mode;
        public float capture_time;
        public int screen_width;
        public int screen_height;
        public int image_width;
        public int image_height;
        public string image_mime;
        public string image_base64;
        public bool gaze_tracked;
        public float gaze_viewport_x;
        public float gaze_viewport_y;
        public float camera_pos_x;
        public float camera_pos_y;
        public float camera_pos_z;
        public float camera_rot_x;
        public float camera_rot_y;
        public float camera_rot_z;
        public float camera_rot_w;
    }

    [Header("Refs")]
    public MsgSender msgSender;
    public VlmResultReceiver resultReceiver;
    public ObjectActionRadialMenuSpawner radialMenuSpawner;
    public ObjectDetectionBubbleSpawner bubbleSpawner;
    public EyeGazeReader eyeGazeReader;
    public Camera referenceCamera;

    [Header("Object UI request")]
    [Tooltip("Optional. When assigned (or Resources/NetworkSettings.asset exists), the endpoint is derived from that shared asset; objectUiServerUrl below is used only as a legacy fallback.")]
    public NetworkSettings networkSettings;
    public string objectUiServerUrl = "http://192.168.0.3:5007/object_ui";
    [Tooltip("Used to be the 3 s delay between dropdown selection and capture. Now the trigger is the left-hand pinch hold (ObjectUiHandTrigger), so we capture immediately. Kept as a knob in case manual debug delay is wanted.")]
    public float captureDelaySeconds = 0.0f;
    [Range(1, 100)] public int jpegQuality = 65;
    public bool autoResolveReferences = true;
    public bool verboseLogging = true;
    public float objectUiContextMaxAgeSeconds = 120f;

    [Header("Status")]
    [SerializeField] private RequestState state = RequestState.Idle;
    [SerializeField] private string activeRequestId = "";

    Coroutine _requestRoutine;
    Vector2 _captureGazeViewport;
    bool _hasCaptureGazeViewport;
    CapturePoseSnapshot _activeCapturePose;
    bool _hasActiveCapturePose;

    struct CapturePoseSnapshot
    {
        public int screenWidth;
        public int screenHeight;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public Matrix4x4 projectionMatrix;
        public float verticalFov;
        public float aspect;
        public bool orthographic;
        public float orthographicSize;

        public static CapturePoseSnapshot From(Camera cam)
        {
            return new CapturePoseSnapshot
            {
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                cameraPosition = cam != null ? cam.transform.position : Vector3.zero,
                cameraRotation = cam != null ? cam.transform.rotation : Quaternion.identity,
                projectionMatrix = cam != null ? cam.projectionMatrix : Matrix4x4.identity,
                verticalFov = cam != null ? cam.fieldOfView : 60f,
                aspect = cam != null ? cam.aspect : (Screen.height > 0 ? Screen.width / (float)Screen.height : 1.777f),
                orthographic = cam != null && cam.orthographic,
                orthographicSize = cam != null ? cam.orthographicSize : 5f
            };
        }
    }

    void Awake()
    {
        ResolveReferences();
    }

    void OnEnable()
    {
        ResolveReferences();
        if (resultReceiver != null)
        {
            resultReceiver.OnResult -= HandleYoloResult;
            resultReceiver.OnResult += HandleYoloResult;
        }
    }

    void OnDisable()
    {
        if (resultReceiver != null) resultReceiver.OnResult -= HandleYoloResult;
        // Mode change (UI -> Voice / Gesture) flips this component off. Tear
        // down EVERYTHING the UI mode put on screen so the next mode doesn't
        // see leftover bubbles, an open radial menu, or a half-finished
        // Compare two-step in the command bridge.
        CancelObjectUiRequest();
        if (radialMenuSpawner != null)
        {
            radialMenuSpawner.CloseCurrentMenu();
            if (radialMenuSpawner.commandBridge != null)
                radialMenuSpawner.commandBridge.ResetPendingState("mode_change");
        }
    }

    [ContextMenu("Begin Object UI Request")]
    public void BeginObjectUiRequest()
    {
        ResolveReferences();
        // Long-pinch refresh: wipe the previous bubble set AND any stale radial
        // menu so the user is looking at a clean slate before the new capture.
        if (bubbleSpawner != null) bubbleSpawner.ClearBubbles();
        if (radialMenuSpawner != null) radialMenuSpawner.CloseCurrentMenu();
        if (_requestRoutine != null)
        {
            Debug.LogWarning($"[OBJECT_UI][WARN] request already active request_id={activeRequestId}");
            return;
        }

        activeRequestId = Guid.NewGuid().ToString("N");
        state = RequestState.WaitingForSceneCapture;
        Debug.Log("[UIInteraction] Started");
        Debug.Log($"[OBJECT_UI] Button pressed request_id={activeRequestId}");
        Debug.Log($"[OBJECT_UI] Waiting {captureDelaySeconds:F1}s before capture request_id={activeRequestId}");
        _requestRoutine = StartCoroutine(RequestRoutine(activeRequestId));
    }

    public void CancelObjectUiRequest()
    {
        if (_requestRoutine != null)
        {
            StopCoroutine(_requestRoutine);
            _requestRoutine = null;
        }
        Debug.Log($"[OBJECT_UI] cancelled request_id={activeRequestId}");
        if (bubbleSpawner != null) bubbleSpawner.ClearBubbles();
        Debug.Log("[UIInteraction] Cleared bubbles");
        activeRequestId = "";
        state = RequestState.Idle;
        _hasActiveCapturePose = false;
    }

    IEnumerator RequestRoutine(string requestId)
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, captureDelaySeconds));
        yield return CaptureAndSend(requestId);
        _requestRoutine = null;
    }

    // We no longer capture the Unity screen and ship the JPG to Python --
    // Unity's ScreenCapture on Android XR only sees its own rendered output
    // (passthrough video is OS-composited and never reaches the framebuffer),
    // so YOLO always saw a blank image and detected nothing.
    //
    // Instead we send a minimal trigger; the Python /object_ui handler now
    // runs YOLO against state.latest_frame -- the ADB screenrecord stream the
    // MacProgram already pulls from the device, which captures the FINAL
    // composited display (passthrough + Unity overlays) and is the same
    // source the gesture handlers (Search/Anchor/Save/etc.) already use.
    IEnumerator CaptureAndSend(string requestId)
    {
        ResolveReferences();
        state = RequestState.SendingToServer;
        Debug.Log($"[OBJECT_UI] Trigger fired request_id={requestId}");

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        CapturePoseSnapshot capturePose = CapturePoseSnapshot.From(cam);
        _activeCapturePose = capturePose;
        _hasActiveCapturePose = true;
        CaptureGazeViewport(cam, out _captureGazeViewport, out _hasCaptureGazeViewport);
        Debug.Log($"[OBJECT_UI] capture_gaze tracked={_hasCaptureGazeViewport} viewport=({_captureGazeViewport.x:F3},{_captureGazeViewport.y:F3}) request_id={requestId}");

        // Register camera pose with image dims = 0 -- the actual frame size
        // comes from Python (ADB stream) and is echoed back in the response
        // (image_width / image_height in the VLM_RESULT). DetectedObjectAnchorResolver
        // uses the response dims to project bbox -> world.
        RegisterCapturePose(requestId, 0, 0, capturePose);

        ObjectUiImagePayload payload = BuildPayload(requestId, capturePose);
        yield return PostPayload(payload);
    }

    ObjectUiImagePayload BuildPayload(string requestId, CapturePoseSnapshot capturePose)
    {
        return new ObjectUiImagePayload
        {
            request_id = requestId,
            requestId = requestId,
            mode = "object_ui",
            capture_time = Time.unscaledTime,
            screen_width = capturePose.screenWidth,
            screen_height = capturePose.screenHeight,
            // image_width / image_height intentionally 0: server uses its own ADB-stream
            // frame and reports the actual dims in the response.
            image_width = 0,
            image_height = 0,
            image_mime = "",
            image_base64 = "",
            gaze_tracked = _hasCaptureGazeViewport,
            gaze_viewport_x = _captureGazeViewport.x,
            gaze_viewport_y = _captureGazeViewport.y,
            camera_pos_x = capturePose.cameraPosition.x,
            camera_pos_y = capturePose.cameraPosition.y,
            camera_pos_z = capturePose.cameraPosition.z,
            camera_rot_x = capturePose.cameraRotation.x,
            camera_rot_y = capturePose.cameraRotation.y,
            camera_rot_z = capturePose.cameraRotation.z,
            camera_rot_w = capturePose.cameraRotation.w
        };
    }

    void RegisterCapturePose(string requestId, int imageWidth, int imageHeight, CapturePoseSnapshot capturePose)
    {
        if (msgSender != null)
        {
            string registered = msgSender.RegisterCaptureSnapshotForRequest(
                requestId,
                "object_ui delayed capture",
                imageWidth,
                imageHeight,
                capturePose.screenWidth,
                capturePose.screenHeight,
                capturePose.cameraPosition,
                capturePose.cameraRotation,
                capturePose.projectionMatrix,
                capturePose.verticalFov,
                capturePose.aspect,
                capturePose.orthographic,
                capturePose.orthographicSize,
                objectUiContextMaxAgeSeconds);
            Debug.Log($"[OBJECT_UI] capture_pose_registered request_id={registered} camera_pos=({capturePose.cameraPosition.x:F3},{capturePose.cameraPosition.y:F3},{capturePose.cameraPosition.z:F3}) image={imageWidth}x{imageHeight}");
            return;
        }

        CaptureContextRegistry registry = CaptureContextRegistry.EnsureInstance();
        if (registry == null)
        {
            Debug.LogWarning("[OBJECT_UI][WARN] capture context registry unavailable; anchor may fall back to current camera.");
            return;
        }

        if (objectUiContextMaxAgeSeconds > 0f)
            registry.maxContextAgeSeconds = Mathf.Max(registry.maxContextAgeSeconds, objectUiContextMaxAgeSeconds);

        string fallbackRequestId = registry.RegisterSnapshot(
            requestId,
            imageWidth,
            imageHeight,
            capturePose.screenWidth,
            capturePose.screenHeight,
            capturePose.cameraPosition,
            capturePose.cameraRotation,
            capturePose.projectionMatrix,
            capturePose.verticalFov,
            capturePose.aspect,
            capturePose.orthographic,
            capturePose.orthographicSize,
            1.2f);
        Debug.Log($"[OBJECT_UI] capture_pose_registered request_id={fallbackRequestId} camera_pos=({capturePose.cameraPosition.x:F3},{capturePose.cameraPosition.y:F3},{capturePose.cameraPosition.z:F3}) image={imageWidth}x{imageHeight}");
    }

    IEnumerator PostPayload(ObjectUiImagePayload payload)
    {
        string url = ResolveObjectUiUrl();
        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");

            if (verboseLogging)
                Debug.Log($"[OBJECT_UI] Sending image to YOLO server request_id={payload.request_id} bytes={body.Length} url={url}");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                state = RequestState.WaitingForYoloResponse;
                Debug.Log("[UIInteraction] Image sent to YOLO");
                Debug.Log($"[OBJECT_UI] YOLO request accepted request_id={payload.request_id} code={request.responseCode} body={request.downloadHandler.text}");
            }
            else
            {
                Debug.LogError($"[OBJECT_UI][ERROR] failed to send image to YOLO server request_id={payload.request_id}: {request.error} ({request.result})");
                state = RequestState.Idle;
                activeRequestId = "";
            }
        }
    }

    void HandleYoloResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (payload == null || payload.gesture != "ObjectUI") return;

        string responseRequestId = FirstNonEmpty(payload.request_id, payload.requestId);
        if (string.IsNullOrEmpty(responseRequestId))
        {
            Debug.LogWarning($"[OBJECT_UI][WARN] ObjectUI response ignored because request_id is empty. active={activeRequestId}");
            return;
        }

        if (string.IsNullOrEmpty(activeRequestId) || responseRequestId != activeRequestId)
        {
            Debug.LogWarning($"[OBJECT_UI][WARN] request_id mismatch active={activeRequestId} response={responseRequestId}; ignoring response.");
            return;
        }

        int count = payload.detections != null ? payload.detections.Length : 0;
        Debug.Log($"[UIInteraction] YOLO result received: count = {count}");
        Debug.Log($"[OBJECT_UI] YOLO detections received: {count}");
        Debug.Log($"[OBJECT_UI] YOLO response request_id={responseRequestId} detections={count}");
        if (count == 0)
        {
            Debug.LogWarning("[OBJECT_UI][WARN] no detections; UI interaction panel will stay closed.");
            Debug.Log("[UIInteraction] Blocked auto panel open before bubble selection");
            FinishRequest();
            return;
        }

        ObjectDetectionBubbleSpawner spawner = ResolveBubbleSpawner();
        if (spawner == null)
        {
            Debug.LogWarning("[OBJECT_UI][WARN] bubble spawner unavailable; object bubbles cannot be shown.");
            Debug.Log("[UIInteraction] Blocked auto panel open before bubble selection");
            FinishRequest();
            return;
        }

        System.Collections.Generic.List<DetectionResult> validDetections = new System.Collections.Generic.List<DetectionResult>();
        for (int i = 0; i < payload.detections.Length; i++)
        {
            DetectionResult detection = ToDetectionResult(payload.detections[i], payload, responseRequestId);
            if (detection == null)
            {
                Debug.LogWarning($"[OBJECT_UI][WARN] detection[{i}] ignored because bbox/metadata is invalid.");
                continue;
            }

            detection.requireExactRequestContext = true;
            validDetections.Add(detection);

            Vector2 center = detection.Center;
            Vector2 normalized = new Vector2(
                center.x / Mathf.Max(1, detection.imageWidth),
                center.y / Mathf.Max(1, detection.imageHeight));
            Debug.Log($"[OBJECT_UI] detection[{i}] class={detection.label} conf={detection.confidence:F3} bbox=[{detection.x1:F1},{detection.y1:F1},{detection.x2:F1},{detection.y2:F1}] center_px=({center.x:F1},{center.y:F1}) normalized=({normalized.x:F3},{normalized.y:F3})");
        }

        Debug.Log("[UIInteraction] Showing object bubbles");
        int bubbleCount = spawner.ShowBubbles(validDetections.ToArray(), payload, responseRequestId);
        if (bubbleCount <= 0)
        {
            Debug.LogWarning("[OBJECT_UI][WARN] no bubbles spawned; UI interaction panel will stay closed.");
            Debug.Log("[UIInteraction] Blocked auto panel open before bubble selection");
        }
        else
        {
            Debug.Log($"[OBJECT_UI] spawned object bubbles count={bubbleCount} request_id={responseRequestId}");
        }

        // The server request is complete. The saved requestId is kept inside each bubble for later click handling.
        FinishRequest();
    }

    DetectionResult ToDetectionResult(VlmResultReceiver.VlmDetection det, VlmResultReceiver.VlmResultPayload payload, string requestId)
    {
        if (det == null || det.bbox == null || det.bbox.Length < 4) return null;
        int width = FirstPositive(det.image_width, det.imageWidth, payload.image_width, payload.imageWidth, Screen.width);
        int height = FirstPositive(det.image_height, det.imageHeight, payload.image_height, payload.imageHeight, Screen.height);
        DetectionResult result = DetectionResult.FromXYXY(
            requestId,
            FirstNonEmpty(det.label, det.class_name),
            det.confidence > 0f ? det.confidence : det.conf,
            det.bbox,
            width,
            height);
        if (result != null)
        {
            result.gazeDir = new Vector3(det.gaze_dir_x, det.gaze_dir_y, det.gaze_dir_z);
            result.depthMeters = det.depth_meters;
            result.depthSource = det.depth_source;
            result.objectId = det.object_id;
        }
        return result;
    }

    void CaptureGazeViewport(Camera cam, out Vector2 viewport, out bool hasViewport)
    {
        viewport = new Vector2(0.5f, 0.5f);
        hasViewport = false;
        if (cam == null || eyeGazeReader == null || !eyeGazeReader.LatestIsTracked)
            return;

        Vector3 gazeDir = eyeGazeReader.LatestGazeDirection;
        if (gazeDir.sqrMagnitude < 0.0001f) return;

        Vector3 world = cam.transform.position + gazeDir.normalized * 1.2f;
        Vector3 vp = cam.WorldToViewportPoint(world);
        if (vp.z <= 0f) return;

        viewport = new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));
        hasViewport = true;
    }

    ObjectDetectionBubbleSpawner ResolveBubbleSpawner()
    {
        if (bubbleSpawner != null) return bubbleSpawner;
        bubbleSpawner = GetComponent<ObjectDetectionBubbleSpawner>();
        if (bubbleSpawner != null) return bubbleSpawner;
        Transform parent = transform.parent;
        if (parent != null)
        {
            bubbleSpawner = parent.GetComponent<ObjectDetectionBubbleSpawner>();
            if (bubbleSpawner != null) return bubbleSpawner;
        }
        bubbleSpawner = FindObjectOfType<ObjectDetectionBubbleSpawner>();
        if (bubbleSpawner != null) return bubbleSpawner;

        ObjectActionRadialMenuSpawner radialSpawner = ResolveRadialMenuSpawner();
        bubbleSpawner = ObjectDetectionBubbleSpawner.CreateRuntimeDefault(
            radialSpawner,
            referenceCamera != null ? referenceCamera : Camera.main);
        Debug.Log("[OBJECT_UI] created runtime ObjectDetectionBubbleSpawner; detections will show as bubbles before action panel.");
        return bubbleSpawner;
    }

    ObjectActionRadialMenuSpawner ResolveRadialMenuSpawner()
    {
        if (radialMenuSpawner != null) return radialMenuSpawner;
        radialMenuSpawner = FindObjectOfType<ObjectActionRadialMenuSpawner>();
        if (radialMenuSpawner != null) return radialMenuSpawner;
        radialMenuSpawner = ObjectActionRadialMenuSpawner.CreateRuntimeDefault();
        return radialMenuSpawner;
    }

    string ResolveObjectUiUrl()
    {
        // 1) Shared NetworkSettings asset -- the intended single source of truth.
        NetworkSettings s = networkSettings != null ? networkSettings : NetworkSettings.Instance;
        if (s != null) return s.ObjectUiUrl;
        // 2) Legacy: explicit URL field on this component.
        if (!string.IsNullOrWhiteSpace(objectUiServerUrl))
            return objectUiServerUrl;
        // 3) Last-ditch: reuse MsgSender's serverIP with the default port/path.
        string host = msgSender != null && !string.IsNullOrWhiteSpace(msgSender.ServerIP)
            ? msgSender.ServerIP
            : "192.168.0.3";
        return $"http://{host}:5007/object_ui";
    }

    void FinishRequest()
    {
        activeRequestId = "";
        state = RequestState.Idle;
        _hasActiveCapturePose = false;
    }

    void ResolveReferences()
    {
        if (!autoResolveReferences) return;
        if (msgSender == null) msgSender = FindObjectOfType<MsgSender>();
        if (resultReceiver == null) resultReceiver = FindObjectOfType<VlmResultReceiver>();
        if (radialMenuSpawner == null) radialMenuSpawner = FindObjectOfType<ObjectActionRadialMenuSpawner>();
        if (bubbleSpawner == null) bubbleSpawner = FindObjectOfType<ObjectDetectionBubbleSpawner>();
        if (eyeGazeReader == null) eyeGazeReader = FindObjectOfType<EyeGazeReader>();
        if (referenceCamera == null) referenceCamera = Camera.main;
    }

    static int FirstPositive(params int[] values)
    {
        if (values == null) return 0;
        for (int i = 0; i < values.Length; i++)
            if (values[i] > 0) return values[i];
        return 0;
    }

    static string FirstNonEmpty(params string[] values)
    {
        if (values == null) return "";
        for (int i = 0; i < values.Length; i++)
            if (!string.IsNullOrEmpty(values[i])) return values[i];
        return "";
    }
}
