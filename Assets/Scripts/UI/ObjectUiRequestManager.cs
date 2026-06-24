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
    public EyeGazeReader eyeGazeReader;
    public Camera referenceCamera;

    [Header("Object UI request")]
    public string objectUiServerUrl = "http://192.168.0.3:5007/object_ui";
    public float captureDelaySeconds = 3.0f;
    [Range(1, 100)] public int jpegQuality = 65;
    public bool autoResolveReferences = true;
    public bool verboseLogging = true;
    public float objectUiContextMaxAgeSeconds = 120f;

    [Header("Selection")]
    public bool preferGazeNearestTarget = true;
    public bool preferScreenCenterWhenGazeUnavailable = true;
    public bool spawnAtSearchPanelFallback = true;
    public float fallbackProjectionDistance = 1.2f;
    public float fallbackHorizontalOffset = 0.25f;
    public float fallbackVerticalOffset = 0.15f;

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
        if (_requestRoutine != null || !string.IsNullOrEmpty(activeRequestId))
            CancelObjectUiRequest();
    }

    [ContextMenu("Begin Object UI Request")]
    public void BeginObjectUiRequest()
    {
        ResolveReferences();
        if (_requestRoutine != null)
        {
            Debug.LogWarning($"[OBJECT_UI][WARN] request already active request_id={activeRequestId}");
            return;
        }

        activeRequestId = Guid.NewGuid().ToString("N");
        state = RequestState.WaitingForSceneCapture;
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

    IEnumerator CaptureAndSend(string requestId)
    {
        ResolveReferences();
        state = RequestState.SendingToServer;
        Debug.Log($"[OBJECT_UI] Capture started request_id={requestId}");

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        CapturePoseSnapshot capturePose = CapturePoseSnapshot.From(cam);
        _activeCapturePose = capturePose;
        _hasActiveCapturePose = true;
        CaptureGazeViewport(cam, out _captureGazeViewport, out _hasCaptureGazeViewport);
        Debug.Log($"[OBJECT_UI] capture_gaze tracked={_hasCaptureGazeViewport} viewport=({_captureGazeViewport.x:F3},{_captureGazeViewport.y:F3}) request_id={requestId}");

        yield return new WaitForEndOfFrame();

        Texture2D texture = null;
        byte[] jpg = null;
        try
        {
            texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null || texture.width <= 0 || texture.height <= 0)
            {
                Debug.LogError($"[OBJECT_UI][ERROR] capture failed image=0x0 request_id={requestId}");
                state = RequestState.Idle;
                activeRequestId = "";
                yield break;
            }

            jpg = ImageConversion.EncodeToJPG(texture, Mathf.Clamp(jpegQuality, 1, 100));
            Debug.Log($"[OBJECT_UI] Captured image={texture.width}x{texture.height} bytes={jpg.Length} request_id={requestId}");
            RegisterCapturePose(requestId, texture.width, texture.height, capturePose);

            ObjectUiImagePayload payload = BuildPayload(requestId, texture.width, texture.height, jpg, capturePose);
            yield return PostPayload(payload);
        }
        finally
        {
            if (texture != null) Destroy(texture);
        }
    }

    ObjectUiImagePayload BuildPayload(string requestId, int imageWidth, int imageHeight, byte[] jpg, CapturePoseSnapshot capturePose)
    {
        return new ObjectUiImagePayload
        {
            request_id = requestId,
            requestId = requestId,
            mode = "object_ui",
            capture_time = Time.unscaledTime,
            screen_width = capturePose.screenWidth,
            screen_height = capturePose.screenHeight,
            image_width = imageWidth,
            image_height = imageHeight,
            image_mime = "image/jpeg",
            image_base64 = Convert.ToBase64String(jpg),
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
        Debug.Log($"[OBJECT_UI] YOLO response request_id={responseRequestId} detections={count}");
        if (count == 0)
        {
            Debug.LogWarning("[OBJECT_UI][WARN] no detections; using search-panel fallback position");
            SpawnSearchPanelFallback(responseRequestId, "no_detections");
            FinishRequest();
            return;
        }

        int selectedIndex = SelectDetection(payload.detections);
        VlmResultReceiver.VlmDetection selected = payload.detections[selectedIndex];
        DetectionResult detection = ToDetectionResult(selected, payload, responseRequestId);
        if (detection == null)
        {
            Debug.LogWarning("[OBJECT_UI][WARN] selected detection is invalid; using search-panel fallback position");
            SpawnSearchPanelFallback(responseRequestId, "invalid_detection");
            FinishRequest();
            return;
        }
        detection.requireExactRequestContext = true;

        Debug.Log($"[OBJECT_UI] selected detection index={selectedIndex} class={detection.label} conf={detection.confidence:F3} bbox=[{detection.x1:F1},{detection.y1:F1},{detection.x2:F1},{detection.y2:F1}]");
        Vector2 center = detection.Center;
        Vector2 normalized = new Vector2(
            center.x / Mathf.Max(1, detection.imageWidth),
            center.y / Mathf.Max(1, detection.imageHeight));
        Debug.Log($"[OBJECT_UI] bbox_center_px=({center.x:F1},{center.y:F1}) normalized=({normalized.x:F3},{normalized.y:F3})");

        ObjectActionRadialMenuSpawner spawner = ResolveRadialMenuSpawner();
        if (spawner == null)
        {
            Debug.LogWarning("[OBJECT_UI][WARN] radial menu spawner unavailable.");
            FinishRequest();
            return;
        }

        bool spawnedOnDetection = spawner.HandleDetectionResult(detection, payload, activeRequestId);
        if (!spawnedOnDetection)
            SpawnSearchPanelFallback(responseRequestId, "detection_anchor_failed");
        FinishRequest();
    }

    void SpawnSearchPanelFallback(string requestId, string reason)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            Debug.LogWarning($"[OBJECT_UI][WARN] fallback radial UI ignored because request_id is empty. reason={reason}");
            return;
        }

        if (!spawnAtSearchPanelFallback)
        {
            Debug.LogWarning($"[OBJECT_UI][WARN] fallback disabled reason={reason} request_id={requestId}");
            return;
        }

        ObjectActionRadialMenuSpawner spawner = ResolveRadialMenuSpawner();
        if (spawner == null)
        {
            Debug.LogWarning($"[OBJECT_UI][WARN] radial menu spawner unavailable for fallback reason={reason} request_id={requestId}");
            return;
        }

        Vector3 position = ComputeSearchPanelFallbackPosition();
        spawner.SpawnFallback(position, requestId, "view target");
        Debug.Log($"[OBJECT_UI] fallback_spawn reason={reason} request_id={requestId} world=({position.x:F3},{position.y:F3},{position.z:F3})");
    }

    Vector3 ComputeSearchPanelFallbackPosition()
    {
        CapturePoseSnapshot pose = _hasActiveCapturePose
            ? _activeCapturePose
            : CapturePoseSnapshot.From(referenceCamera != null ? referenceCamera : Camera.main);

        Vector2 viewport = _hasCaptureGazeViewport ? _captureGazeViewport : new Vector2(0.5f, 0.5f);
        Ray ray = BuildRay(pose, viewport);
        Vector3 right = pose.cameraRotation * Vector3.right;
        Vector3 up = pose.cameraRotation * Vector3.up;
        Vector3 basePos = ray.origin + ray.direction * Mathf.Max(0.05f, fallbackProjectionDistance);
        return basePos + right * fallbackHorizontalOffset + up * fallbackVerticalOffset;
    }

    Ray BuildRay(CapturePoseSnapshot pose, Vector2 viewport)
    {
        float nx = viewport.x * 2f - 1f;
        float ny = viewport.y * 2f - 1f;

        if (pose.orthographic)
        {
            float halfHeight = pose.orthographicSize;
            float halfWidth = halfHeight * Mathf.Max(0.01f, pose.aspect);
            Vector3 localOrigin = new Vector3(nx * halfWidth, ny * halfHeight, 0f);
            Vector3 worldOrigin = pose.cameraPosition + pose.cameraRotation * localOrigin;
            Vector3 worldDirection = pose.cameraRotation * Vector3.forward;
            return new Ray(worldOrigin, worldDirection.normalized);
        }

        float tanHalfFov = Mathf.Tan(pose.verticalFov * Mathf.Deg2Rad * 0.5f);
        Vector3 localDirection = new Vector3(
            nx * tanHalfFov * Mathf.Max(0.01f, pose.aspect),
            ny * tanHalfFov,
            1f).normalized;
        Vector3 direction = pose.cameraRotation * localDirection;
        return new Ray(pose.cameraPosition, direction.normalized);
    }

    int SelectDetection(VlmResultReceiver.VlmDetection[] detections)
    {
        bool useGaze = preferGazeNearestTarget && _hasCaptureGazeViewport;
        bool useScreenCenter = !useGaze && preferScreenCenterWhenGazeUnavailable;
        string policy = useGaze ? "gaze_nearest" : (useScreenCenter ? "screen_center_nearest" : "confidence_highest");
        Debug.Log($"[OBJECT_UI] target selection policy={policy}");

        int bestIndex = 0;
        float bestScore = (useGaze || useScreenCenter) ? float.PositiveInfinity : float.NegativeInfinity;
        Vector2 targetViewport = useGaze ? _captureGazeViewport : new Vector2(0.5f, 0.5f);

        for (int i = 0; i < detections.Length; i++)
        {
            VlmResultReceiver.VlmDetection det = detections[i];
            if (det == null || det.bbox == null || det.bbox.Length < 4) continue;
            float conf = det.confidence > 0f ? det.confidence : det.conf;

            if (useGaze || useScreenCenter)
            {
                Vector2 center = DetectionCenter01(det);
                float distance = Vector2.SqrMagnitude(center - targetViewport);
                if (distance < bestScore || (Mathf.Approximately(distance, bestScore) && conf > DetectionConfidence(detections[bestIndex])))
                {
                    bestScore = distance;
                    bestIndex = i;
                }
            }
            else
            {
                if (conf > bestScore)
                {
                    bestScore = conf;
                    bestIndex = i;
                }
            }
        }

        return bestIndex;
    }

    float DetectionConfidence(VlmResultReceiver.VlmDetection det)
    {
        if (det == null) return -1f;
        return det.confidence > 0f ? det.confidence : det.conf;
    }

    Vector2 DetectionCenter01(VlmResultReceiver.VlmDetection det)
    {
        int width = FirstPositive(det.image_width, det.imageWidth, Screen.width);
        int height = FirstPositive(det.image_height, det.imageHeight, Screen.height);
        float cx = (det.bbox[0] + det.bbox[2]) * 0.5f / Mathf.Max(1, width);
        float cy = (det.bbox[1] + det.bbox[3]) * 0.5f / Mathf.Max(1, height);
        return new Vector2(Mathf.Clamp01(cx), Mathf.Clamp01(1f - cy));
    }

    DetectionResult ToDetectionResult(VlmResultReceiver.VlmDetection det, VlmResultReceiver.VlmResultPayload payload, string requestId)
    {
        if (det == null || det.bbox == null || det.bbox.Length < 4) return null;
        int width = FirstPositive(det.image_width, det.imageWidth, payload.image_width, payload.imageWidth, Screen.width);
        int height = FirstPositive(det.image_height, det.imageHeight, payload.image_height, payload.imageHeight, Screen.height);
        return DetectionResult.FromXYXY(
            requestId,
            FirstNonEmpty(det.label, det.class_name),
            det.confidence > 0f ? det.confidence : det.conf,
            det.bbox,
            width,
            height);
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
        if (!string.IsNullOrWhiteSpace(objectUiServerUrl))
            return objectUiServerUrl;
        string host = msgSender != null && !string.IsNullOrWhiteSpace(msgSender.serverIP)
            ? msgSender.serverIP
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
