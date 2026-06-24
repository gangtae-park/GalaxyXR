using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ObjectActionRadialMenuSpawner : MonoBehaviour
{
    [Header("Refs")]
    public DetectedObjectAnchorResolver anchorResolver;
    public ObjectActionCommandBridge commandBridge;
    public Camera referenceCamera;

    [Header("Filtering")]
    public float confidenceThreshold = 0.25f;
    public float minBboxSize = 0.005f;
    public bool replaceExistingMenu = true;

    [Header("Placement")]
    public float cameraForwardOffset = 0.15f;
    public float menuScale = 0.0012f;
    public float menuLifetimeSeconds = 20f;

    [Header("Debug")]
    public bool verboseLogging = true;

    GameObject _currentMenuRoot;
    DetectedObjectAnchor _currentAnchor;
    float _destroyAt;

    void Awake()
    {
        EnsureDependencies();
    }

    void Update()
    {
        if (_currentMenuRoot != null && menuLifetimeSeconds > 0f && Time.time >= _destroyAt)
            CloseCurrentMenu();
    }

    public static ObjectActionRadialMenuSpawner CreateRuntimeDefault()
    {
        GameObject go = new GameObject("ObjectActionRadialMenuRuntime");
        ObjectActionRadialMenuSpawner spawner = go.AddComponent<ObjectActionRadialMenuSpawner>();
        spawner.EnsureDependencies();
        return spawner;
    }

    public bool HandleDetectionResult(DetectionResult detection, VlmResultReceiver.VlmResultPayload payload = null, string activeObjectUiRequestId = null)
    {
        EnsureDependencies();

        if (payload == null || payload.gesture != "ObjectUI")
        {
            Debug.LogWarning($"[ObjectActionMenu][WARN] detection ignored because payload.gesture is not ObjectUI. gesture={payload?.gesture} request_id={PayloadRequestId(payload)}");
            return false;
        }

        if (detection == null)
        {
            Debug.LogWarning("[ObjectActionMenu] detection result received but detection is null.");
            return false;
        }

        string payloadRequestId = PayloadRequestId(payload);
        if (string.IsNullOrEmpty(payloadRequestId) || string.IsNullOrEmpty(detection.requestId) || string.IsNullOrEmpty(activeObjectUiRequestId))
        {
            Debug.LogWarning($"[ObjectActionMenu][WARN] detection ignored because request_id is empty. payload_request_id={payloadRequestId} detection_request_id={detection.requestId} active_request_id={activeObjectUiRequestId}");
            return false;
        }

        if (payloadRequestId != detection.requestId || payloadRequestId != activeObjectUiRequestId)
        {
            Debug.LogWarning($"[ObjectActionMenu][WARN] detection ignored because request_id mismatch. payload_request_id={payloadRequestId} detection_request_id={detection.requestId} active_request_id={activeObjectUiRequestId}");
            return false;
        }

        Debug.Log($"[ObjectActionMenu] detection result received request_id={detection.requestId} label={detection.label} confidence={detection.confidence:F3} bbox=[{detection.x1:F2},{detection.y1:F2},{detection.x2:F2},{detection.y2:F2}]");

        if (detection.confidence < confidenceThreshold)
        {
            Debug.LogWarning($"[ObjectActionMenu] detection skipped: confidence {detection.confidence:F3} < threshold {confidenceThreshold:F3}");
            return false;
        }

        Vector2 bboxSize = detection.Size;
        if (bboxSize.x < minBboxSize || bboxSize.y < minBboxSize)
        {
            Debug.LogWarning($"[ObjectActionMenu] detection skipped: bbox too small size={bboxSize}");
            return false;
        }

        if (anchorResolver == null || !anchorResolver.TryResolveAnchor(detection, out DetectedObjectAnchor anchor))
        {
            Debug.LogWarning("[ObjectActionMenu] detection skipped: anchor resolve failed.");
            return false;
        }

        Spawn(anchor, detection.requestId);
        return true;
    }

    public void SpawnFallback(Vector3 worldPosition, string requestId, string label = "view target")
    {
        DetectionResult detection = new DetectionResult
        {
            requestId = requestId ?? "",
            label = string.IsNullOrEmpty(label) ? "view target" : label,
            confidence = 1f,
            x1 = 0f,
            y1 = 0f,
            x2 = 1f,
            y2 = 1f,
            imageWidth = 1,
            imageHeight = 1
        };

        DetectedObjectAnchor anchor = new DetectedObjectAnchor
        {
            detection = detection,
            viewportPoint = new Vector2(0.5f, 0.5f),
            worldPosition = worldPosition,
            rayOrigin = worldPosition,
            rayDirection = Vector3.forward,
            resolveMethod = "PanelFallback"
        };
        Spawn(anchor, requestId);
    }

    public void Spawn(DetectedObjectAnchor anchor)
    {
        Spawn(anchor, anchor != null && anchor.detection != null ? anchor.detection.requestId : "");
    }

    public void Spawn(DetectedObjectAnchor anchor, string requestId)
    {
        if (anchor == null) return;
        if (replaceExistingMenu) CloseCurrentMenu();

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector3 position = anchor.worldPosition;
        if (cam != null && anchor.resolveMethod != "PanelFallback")
        {
            Vector3 toCamera = cam.transform.position - position;
            if (toCamera.sqrMagnitude > 0.0001f)
                position += toCamera.normalized * cameraForwardOffset;
        }

        GameObject root = new GameObject("Object Action Radial Menu", typeof(RectTransform));
        root.transform.position = position;
        root.transform.localScale = Vector3.one * menuScale;

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;
        root.AddComponent<GraphicRaycaster>();
        AddTrackedDeviceGraphicRaycasterIfAvailable(root);
        CanvasGroup group = root.AddComponent<CanvasGroup>();
        group.alpha = 0.94f;
        group.interactable = true;
        group.blocksRaycasts = true;

        CanvasBillboard billboard = root.AddComponent<CanvasBillboard>();
        billboard.referenceCamera = cam;
        billboard.lockUpright = false;

        ObjectActionRadialMenu menu = root.AddComponent<ObjectActionRadialMenu>();
        menu.requestIdForLogs = requestId ?? "";
        menu.Build();
        menu.OnActionClicked += action =>
        {
            if (action == ObjectActionMenuAction.Cancel)
            {
                Debug.Log($"[RADIAL_UI] Cancel selected request_id={requestId}");
                CloseCurrentMenu();
                return;
            }

            if (commandBridge != null)
                commandBridge.Route(action, _currentAnchor);
        };

        AddObjectLabel(root.transform, anchor);

        _currentMenuRoot = root;
        _currentAnchor = anchor;
        _destroyAt = Time.time + menuLifetimeSeconds;

        Debug.Log($"[RADIAL_UI] Spawn requested request_id={requestId} target={anchor.detection?.label}");
        Debug.Log($"[RADIAL_UI] Anchor world_pos=({position.x:F3},{position.y:F3},{position.z:F3})");
        Debug.Log($"[OBJECT_UI] anchor_world=({anchor.worldPosition.x:F3},{anchor.worldPosition.y:F3},{anchor.worldPosition.z:F3})");
        Debug.Log($"[OBJECT_UI] anchor_source={ToObjectUiAnchorSource(anchor.resolveMethod)}");
        Debug.Log($"[RADIAL_UI] Spawn complete visible={_currentMenuRoot != null}");

        if (verboseLogging)
            Debug.Log($"[ObjectActionMenu] radial menu spawned position={position} label={anchor.detection?.label} method={anchor.resolveMethod}");
    }

    static string ToObjectUiAnchorSource(string resolveMethod)
    {
        if (resolveMethod == "Raycast") return "raycast_hit";
        if (resolveMethod == "PanelFallback") return "search_panel_fallback";
        return "bbox_screen_ray_fixed_depth";
    }

    static string PayloadRequestId(VlmResultReceiver.VlmResultPayload payload)
    {
        if (payload == null) return "";
        if (!string.IsNullOrEmpty(payload.request_id)) return payload.request_id;
        if (!string.IsNullOrEmpty(payload.requestId)) return payload.requestId;
        return "";
    }

    void AddTrackedDeviceGraphicRaycasterIfAvailable(GameObject root)
    {
        System.Type raycasterType = System.Type.GetType(
            "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit");
        if (raycasterType == null) return;
        if (root.GetComponent(raycasterType) != null) return;
        root.AddComponent(raycasterType);
        if (verboseLogging)
            Debug.Log("[ObjectActionMenu] XR tracked device graphic raycaster attached to radial menu.");
    }

    public void CloseCurrentMenu()
    {
        if (_currentMenuRoot != null)
        {
            Destroy(_currentMenuRoot);
            _currentMenuRoot = null;
            _currentAnchor = null;
        }
    }

    void AddObjectLabel(Transform parent, DetectedObjectAnchor anchor)
    {
        GameObject go = new GameObject("ObjectLabel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, -225f);
        rect.sizeDelta = new Vector2(280f, 42f);

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        string label = anchor.detection != null ? anchor.detection.label : "detected object";
        text.text = $"{label}  {anchor.resolveMethod}";
        text.fontSize = 16f;
        text.color = new Color(0.90f, 0.97f, 1f, 0.95f);
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
    }

    void EnsureDependencies()
    {
        if (referenceCamera == null) referenceCamera = Camera.main;
        if (anchorResolver == null)
        {
            anchorResolver = GetComponent<DetectedObjectAnchorResolver>();
            if (anchorResolver == null) anchorResolver = gameObject.AddComponent<DetectedObjectAnchorResolver>();
        }
        if (commandBridge == null)
        {
            commandBridge = GetComponent<ObjectActionCommandBridge>();
            if (commandBridge == null) commandBridge = gameObject.AddComponent<ObjectActionCommandBridge>();
        }
    }
}
