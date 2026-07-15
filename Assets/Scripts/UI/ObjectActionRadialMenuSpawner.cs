using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ObjectActionRadialMenuSpawner : MonoBehaviour
{
    [Header("Refs")]
    public DetectedObjectAnchorResolver anchorResolver;
    public ObjectActionCommandBridge commandBridge;
    public Camera referenceCamera;
    [Tooltip("TMP font used for menu wedge labels + the bottom object label. Assign a Korean-capable SDF (e.g. Pretendard-Medium SDF) so DB names render. If left empty, the spawner tries to auto-find any loaded Pretendard asset at first spawn.")]
    public TMP_FontAsset menuFontAsset;

    [Header("Filtering")]
    public bool filterByConfidence = false;
    public float confidenceThreshold = 0.25f;
    public float minBboxSize = 0.005f;
    public bool replaceExistingMenu = true;

    [Header("Placement")]
    [Tooltip("When ON (default) the menu faces the camera once at spawn and stays FIXED -- head tilt no longer rotates it. Turn OFF to restore the old per-frame billboard behaviour.")]
    public bool freezeOrientationAtSpawn = true;
    public float cameraForwardOffset = 0.15f;
    public float menuScale = 0.0012f;
    public float menuLifetimeSeconds = 20f;

    [Header("Constant-apparent-size scaling")]
    [Tooltip("When true, the spawned menu gets a DistanceConstantSize so it stays the same screen size at any depth.")]
    public bool enforceConstantApparentSize = true;
    [Tooltip("Distance (m) at which the menuScale value is considered correct; cards at exactly this depth keep their authored size.")]
    public float constantSizeReferenceDistance = 1.2f;
    [Tooltip("If true, the menu never shrinks below its authored size even if the user is closer than the reference distance.")]
    public bool constantSizeFloorAtAuthored = true;
    [Range(1f, 10f)] public float constantSizeMaxMultiplier = 4.0f;

    [Header("Debug")]
    public bool verboseLogging = true;

    GameObject _currentMenuRoot;
    ObjectActionRadialMenu _currentMenu;
    DetectedObjectAnchor _currentAnchor;
    float _destroyAt;

    /// <summary>The menu currently on screen (null when closed). Used by the
    /// gaze-pinch selector to hit-test wedges geometrically.</summary>
    public ObjectActionRadialMenu CurrentMenu => _currentMenuRoot != null ? _currentMenu : null;

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
        return HandleDetectionResult(detection, payload, activeObjectUiRequestId, overrideWorldPosition: null);
    }

    // Overload used by the bubble click path: when overrideWorldPosition is
    // provided, the menu lands at exactly that point, skipping the anchor
    // resolver's reprojection and the cameraForwardOffset pull-back. This is
    // how "menu at the bubble's position" is enforced.
    public bool HandleDetectionResult(
        DetectionResult detection,
        VlmResultReceiver.VlmResultPayload payload,
        string activeObjectUiRequestId,
        Vector3 overrideWorldPosition)
    {
        return HandleDetectionResult(detection, payload, activeObjectUiRequestId, (Vector3?)overrideWorldPosition);
    }

    bool HandleDetectionResult(
        DetectionResult detection,
        VlmResultReceiver.VlmResultPayload payload,
        string activeObjectUiRequestId,
        Vector3? overrideWorldPosition)
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

        if (filterByConfidence && detection.confidence < confidenceThreshold)
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

        DetectedObjectAnchor anchor;
        if (overrideWorldPosition.HasValue)
        {
            // Trusted position from the bubble — bypass the anchor resolver
            // entirely so the menu lands at the exact bubble location.
            anchor = new DetectedObjectAnchor
            {
                detection = detection,
                viewportPoint = new Vector2(0.5f, 0.5f),
                worldPosition = overrideWorldPosition.Value,
                rayOrigin = overrideWorldPosition.Value,
                rayDirection = Vector3.forward,
                resolveMethod = "BubblePosition",
            };
            Debug.Log($"[ObjectActionMenu] using bubble override world position ({anchor.worldPosition.x:F3},{anchor.worldPosition.y:F3},{anchor.worldPosition.z:F3})");
        }
        else
        {
            if (anchorResolver == null || !anchorResolver.TryResolveAnchor(detection, out anchor))
            {
                Debug.LogWarning("[ObjectActionMenu] detection skipped: anchor resolve failed.");
                return false;
            }
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
        // BubblePosition / PanelFallback anchors are already at the desired
        // spawn point -- the bubble path explicitly wants the menu AT the
        // bubble, so we skip the pull-back-toward-camera step.
        bool skipForwardPull =
            anchor.resolveMethod == "PanelFallback"
            || anchor.resolveMethod == "BubblePosition";
        if (cam != null && !skipForwardPull)
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

        if (freezeOrientationAtSpawn)
        {
            // Face the camera ONCE, with world up so the menu spawns level
            // regardless of head tilt, then never rotate again.
            if (cam != null)
            {
                Vector3 toMenu = position - cam.transform.position;
                if (toMenu.sqrMagnitude > 1e-6f)
                    root.transform.rotation = Quaternion.LookRotation(toMenu.normalized, Vector3.up);
            }
        }
        else
        {
            CanvasBillboard billboard = root.AddComponent<CanvasBillboard>();
            billboard.referenceCamera = cam;
            billboard.lockUpright = false;
        }

        if (enforceConstantApparentSize)
        {
            DistanceConstantSize sizer = root.AddComponent<DistanceConstantSize>();
            sizer.referenceCamera = cam;
            sizer.referenceDistanceMeters = constantSizeReferenceDistance;
            sizer.floorAtAuthoredSize = constantSizeFloorAtAuthored;
            sizer.maxScaleMultiplier = constantSizeMaxMultiplier;
        }

        ObjectActionRadialMenu menu = root.AddComponent<ObjectActionRadialMenu>();
        menu.requestIdForLogs = requestId ?? "";
        menu.fontAsset = ResolveMenuFont();
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
            // Close the menu after any successful action selection so the user
            // can keep interacting with the bubbles (which stay visible).
            // Compare's two-step flow is handled inside Route(): the first
            // Compare click stores pending state, the second fires the request.
            // Either way we close here -- the user just clicks another bubble
            // to re-open the menu.
            CloseCurrentMenu();
        };

        AddObjectLabel(root.transform, anchor);

        _currentMenuRoot = root;
        _currentMenu = menu;
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
        if (resolveMethod == "BubblePosition") return "bubble_world_position";
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
            _currentMenu = null;
            _currentAnchor = null;
        }
    }

    /// <summary>Returns true if a menu is currently displayed for the given
    /// detection. Used by the bubble spawner to implement "re-click bubble
    /// closes the menu" — no Cancel wedge needed.</summary>
    public bool IsMenuOpenFor(DetectionResult detection)
    {
        if (_currentMenuRoot == null || _currentAnchor == null) return false;
        return ReferenceEquals(_currentAnchor.detection, detection);
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
        TMP_FontAsset font = ResolveMenuFont();
        if (font != null) text.font = font;
        string label = anchor.detection != null ? anchor.detection.label : "detected object";
        // resolveMethod is debug noise for the user; show just the DB name.
        text.text = label;
        text.fontSize = 16f;
        text.color = new Color(0.92f, 0.95f, 1f, 0.95f);
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
    }

    // Returns the configured Pretendard-style font when assigned, otherwise
    // looks for any loaded Pretendard TMP_FontAsset in memory (works once a
    // card prefab has been instantiated since cards reference Pretendard).
    // Returns null if nothing matches; TMP falls back to default in that case.
    TMP_FontAsset ResolveMenuFont()
    {
        if (menuFontAsset != null) return menuFontAsset;
        TMP_FontAsset[] all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            if (all[i].name.IndexOf("Pretendard", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                menuFontAsset = all[i]; // cache so subsequent menus skip the search
                if (verboseLogging)
                    Debug.Log($"[ObjectActionMenu] auto-resolved menu font from loaded assets: {all[i].name}");
                return all[i];
            }
        }
        if (verboseLogging)
            Debug.LogWarning("[ObjectActionMenu] no Pretendard TMP_FontAsset loaded yet; Korean labels may show as boxes. Assign Spawner.menuFontAsset in the inspector.");
        return null;
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
