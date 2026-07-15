using System.Collections.Generic;
using UnityEngine;

public class ObjectDetectionBubbleSpawner : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] ObjectDetectionBubble bubblePrefab;
    [SerializeField] Transform bubbleRoot;
    [SerializeField] ObjectActionRadialMenuSpawner radialMenuSpawner;
    [SerializeField] DetectedObjectAnchorResolver anchorResolver;
    [SerializeField] CaptureContextRegistry captureContextRegistry;
    [SerializeField] Camera referenceCamera;

    [Header("Bubble placement")]
    [SerializeField] bool filterByConfidence = false;
    [SerializeField, Range(0f, 1f)] float minConfidence = 0.25f;
    [SerializeField] float bubbleDistanceMeters = 1.2f;
    [SerializeField] float bubbleVerticalOffsetMeters = 0.04f;
    [SerializeField] float bubbleForwardOffsetMeters = 0.03f;
    [Tooltip("Multiplied with Depth Anything's metric estimate before bubble placement. <1.0 pulls bubbles closer to the user (good if depth feels too far), >1.0 pushes them away. 1.0 leaves the raw estimate alone.")]
    [SerializeField, Range(0.1f, 2.0f)] float depthScaleFactor = 1.0f;
    [SerializeField] bool clearPreviousOnShow = true;
    [SerializeField] bool autoCreateRuntimeBubblePrefab = true;
    [SerializeField] bool verboseLogging = true;

    [Header("Constant-apparent-size scaling")]
    [SerializeField] bool enforceConstantApparentSize = true;
    [SerializeField] float constantSizeReferenceDistance = 1.2f;
    [SerializeField] bool constantSizeFloorAtAuthored = true;
    [SerializeField, Range(1f, 10f)] float constantSizeMaxMultiplier = 4.0f;
    
    [Header("Gaze-pinch selection")]
    [Tooltip("Auto-attach a GazePinchUiSelector so bubbles and the radial menu can be selected by LOOKING at them and pinching (right hand), in addition to ray/poke.")]
    [SerializeField] bool enableGazePinchSelection = true;

    [Header("Optional depth / raycast placement")]
    public bool usePhysicsRaycastPlacement = false;
    public LayerMask raycastMask = ~0;
    public float raycastMaxDistanceMeters = 5.0f;
    public float surfaceOffsetMeters = 0.03f;

    readonly List<ObjectDetectionBubble> activeBubbles = new List<ObjectDetectionBubble>();

    public ObjectDetectionBubble BubblePrefab
    {
        get => bubblePrefab;
        set => bubblePrefab = value;
    }

    public Transform BubbleRoot
    {
        get => bubbleRoot;
        set => bubbleRoot = value;
    }

    public ObjectActionRadialMenuSpawner RadialMenuSpawner
    {
        get => radialMenuSpawner;
        set => radialMenuSpawner = value;
    }

    public DetectedObjectAnchorResolver AnchorResolver
    {
        get => anchorResolver;
        set => anchorResolver = value;
    }

    public Camera ReferenceCamera
    {
        get => referenceCamera;
        set => referenceCamera = value;
    }

    void Awake()
    {
        // Attach the gaze-pinch selector at scene start (not only on first
        // ShowBubbles) so it is armed and logging from the beginning.
        ResolveReferences();
    }

    public static ObjectDetectionBubbleSpawner CreateRuntimeDefault(
        ObjectActionRadialMenuSpawner radialMenuSpawner = null,
        Camera referenceCamera = null)
    {
        GameObject go = new GameObject("ObjectDetectionBubbleRuntime");
        ObjectDetectionBubbleSpawner spawner = go.AddComponent<ObjectDetectionBubbleSpawner>();
        spawner.RadialMenuSpawner = radialMenuSpawner;
        spawner.ReferenceCamera = referenceCamera != null ? referenceCamera : Camera.main;
        spawner.BubbleRoot = go.transform;
        spawner.ResolveReferences();
        return spawner;
    }

    public int ShowBubbles(
        DetectionResult[] detections,
        VlmResultReceiver.VlmResultPayload payload,
        string requestId)
    {
        ResolveReferences();

        if (clearPreviousOnShow)
            ClearBubbles();

        if (bubblePrefab == null)
        {
            if (autoCreateRuntimeBubblePrefab)
                bubblePrefab = CreateRuntimeBubblePrefab();

            if (bubblePrefab == null)
            {
                Debug.LogWarning("[OBJECT_BUBBLE][WARN] bubblePrefab is not assigned.");
                return 0;
            }
        }

        if (detections == null || detections.Length == 0)
        {
            Debug.LogWarning($"[OBJECT_BUBBLE][WARN] no detections to show request_id={requestId}");
            return 0;
        }

        Transform parent = bubbleRoot != null ? bubbleRoot : transform;
        int spawned = 0;
        for (int i = 0; i < detections.Length; i++)
        {
            DetectionResult detection = detections[i];
            if (detection == null)
                continue;

            if (filterByConfidence && detection.confidence < minConfidence)
            {
                if (verboseLogging)
                    Debug.Log($"[OBJECT_BUBBLE] skip low confidence index={i} label={detection.label} conf={detection.confidence:F3}");
                continue;
            }

            Vector2 center = detection.Center;
            Vector3 worldPosition = ComputeBubbleWorldPosition(detection);
            ObjectDetectionBubble bubble = Instantiate(bubblePrefab, worldPosition, Quaternion.identity, parent);
            ApplyConstantSize(bubble.gameObject);
            EnsureXrInteractableIfAvailable(bubble.gameObject);
            bubble.gameObject.SetActive(true);
            bubble.ReferenceCamera = referenceCamera != null ? referenceCamera : Camera.main;
            bubble.Initialize(detection, payload, requestId, OnBubbleClicked);
            activeBubbles.Add(bubble);
            spawned++;

            Debug.Log($"[UIInteraction] Bubble created: {detection.label}, {detection.confidence:F3}, bbox center=({center.x:F1},{center.y:F1})");
            if (verboseLogging)
                Debug.Log($"[OBJECT_BUBBLE] spawned index={i} label={detection.label} conf={detection.confidence:F3} world=({worldPosition.x:F3},{worldPosition.y:F3},{worldPosition.z:F3}) request_id={requestId}");
        }

        return spawned;
    }

    public int ShowBubbles(
        IList<DetectionResult> detections,
        VlmResultReceiver.VlmResultPayload payload,
        string requestId)
    {
        if (detections == null) return ShowBubbles((DetectionResult[])null, payload, requestId);
        DetectionResult[] array = new DetectionResult[detections.Count];
        detections.CopyTo(array, 0);
        return ShowBubbles(array, payload, requestId);
    }

    public void ClearBubbles()
    {
        int cleared = activeBubbles.Count;
        for (int i = 0; i < activeBubbles.Count; i++)
        {
            if (activeBubbles[i] != null)
                Destroy(activeBubbles[i].gameObject);
        }
        activeBubbles.Clear();
        if (cleared > 0)
            Debug.Log("[UIInteraction] Cleared bubbles");
    }

    // Runtime fallback when bubblePrefab is not assigned. Creates a small blue
    // sphere with a SphereCollider so EventSystem / XR pointer can click it.
    // No Canvas, no label, no confidence -- just a marker.
    ObjectDetectionBubble CreateRuntimeBubblePrefab()
    {
        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        root.name = "ObjectDetectionBubbleRuntimePrefab";
        root.transform.SetParent(transform, false);
        root.transform.localScale = Vector3.one * 0.05f; // 5 cm diameter
        root.SetActive(false);

        // CreatePrimitive already gives us MeshFilter + MeshRenderer + SphereCollider.
        MeshRenderer mr = root.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"));
            mat.color = new Color(0.08f, 0.43f, 1f, 0.88f);
            mr.sharedMaterial = mat;
        }

        ObjectDetectionBubble bubble = root.AddComponent<ObjectDetectionBubble>();
        bubble.ReferenceCamera = referenceCamera != null ? referenceCamera : Camera.main;

        if (verboseLogging)
            Debug.Log("[OBJECT_BUBBLE] created runtime default sphere bubble prefab.");

        return bubble;
    }

    void OnBubbleClicked(
        DetectionResult detection,
        VlmResultReceiver.VlmResultPayload payload,
        string requestId,
        Vector3 bubbleWorldPosition)
    {
        if (detection == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE][WARN] click ignored because detection is null.");
            return;
        }

        Debug.Log($"[UIInteraction] Bubble clicked: {detection.label}");
        Debug.Log($"[OBJECT_BUBBLE] clicked label={detection.label} conf={detection.confidence:F3} request_id={requestId} bubble_world=({bubbleWorldPosition.x:F3},{bubbleWorldPosition.y:F3},{bubbleWorldPosition.z:F3})");
        // NOTE: Bubbles intentionally NOT cleared here. They stay visible
        // through menu interaction; they're only swapped out by another long
        // pinch (which calls ShowBubbles with clearPreviousOnShow=true) or
        // torn down on mode change (ObjectUiRequestManager.OnDisable).
        ResolveReferences();

        if (radialMenuSpawner == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE][WARN] radialMenuSpawner is not assigned.");
            return;
        }

        // Re-clicking the same bubble while its menu is open should toggle the
        // menu off (no Cancel wedge -- toggle UX is simpler).
        if (radialMenuSpawner.IsMenuOpenFor(detection))
        {
            Debug.Log($"[OBJECT_BUBBLE] re-click on bubble whose menu is open -> close menu label={detection.label}");
            radialMenuSpawner.CloseCurrentMenu();
            return;
        }

        Debug.Log("[UIInteraction] Opening UI panel from bubble click");
        Debug.Log("[OBJECT_BUBBLE] calling radial menu spawner at bubble world position...");
        // Hand the actual bubble world position to the menu spawner so it
        // anchors there directly (no anchor-resolver reprojection, no
        // camera-forward pull-back).
        bool spawned = radialMenuSpawner.HandleDetectionResult(detection, payload, requestId, bubbleWorldPosition);
        if (!spawned)
            Debug.LogWarning($"[OBJECT_BUBBLE][WARN] radial menu spawn failed label={detection.label} request_id={requestId}");
    }

    Vector3 ComputeBubbleWorldPosition(DetectionResult detection)
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;

        // Preferred path: head-space gaze direction (from Python inverse gaze
        // calibration applied to the bbox centre) + metric depth (Depth Anything
        // V2). We MUST use the capture-time camera pose -- not the current one --
        // because the user may have moved their head between capture and spawn.
        if (detection != null && detection.HasGazeAnchor())
        {
            float scaledDepth = Mathf.Max(0.05f, detection.depthMeters * depthScaleFactor);
            if (TryGetCapturePose(detection.requestId, out Vector3 captureOrigin, out Quaternion captureRotation))
            {
                Vector3 headDir = detection.gazeDir.normalized;
                Vector3 worldDir = captureRotation * headDir;
                Vector3 worldPos = captureOrigin + worldDir * scaledDepth;
                if (verboseLogging)
                {
                    Debug.Log($"[OBJECT_BUBBLE] gaze+depth anchor head=({headDir.x:F3},{headDir.y:F3},{headDir.z:F3}) depth_raw={detection.depthMeters:F2}m depth_scaled={scaledDepth:F2}m (x{depthScaleFactor:F2}) source={detection.depthSource} world=({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3}) request_id={detection.requestId}");
                }
                return worldPos;
            }

            // Capture pose not registered -- last-resort: current camera (still
            // better than viewport ray, but head drift will show).
            if (cam != null)
            {
                Vector3 worldDir = cam.transform.TransformDirection(detection.gazeDir.normalized);
                Vector3 worldPos = cam.transform.position + worldDir * scaledDepth;
                if (verboseLogging)
                    Debug.LogWarning($"[OBJECT_BUBBLE] gaze+depth anchor used CURRENT camera pose (no capture pose for request_id={detection.requestId}); head drift may show. depth_scaled={scaledDepth:F2}m");
                return worldPos;
            }
        }

        if (cam == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE][WARN] No camera found. Using spawner forward fallback.");
            return transform.position + transform.forward * Mathf.Max(0.05f, bubbleDistanceMeters);
        }

        // Legacy fallback: viewport ray at fixed distance. Used only when Python
        // didn't supply gaze_dir/depth (older payloads or model unavailable).
        Vector2 viewportCenter = GetDetectionViewportCenter(detection);
        Ray ray = cam.ViewportPointToRay(new Vector3(viewportCenter.x, viewportCenter.y, 0f));

        if (usePhysicsRaycastPlacement && TryGetRaycastWorldPosition(ray, out Vector3 raycastPosition))
        {
            if (verboseLogging)
                Debug.Log($"[OBJECT_BUBBLE] raycast placement viewport=({viewportCenter.x:F3},{viewportCenter.y:F3}) world=({raycastPosition.x:F3},{raycastPosition.y:F3},{raycastPosition.z:F3})");
            return raycastPosition;
        }

        Vector3 fixedPosition = GetFixedDistanceWorldPosition(cam, ray);
        if (verboseLogging)
            Debug.Log($"[OBJECT_BUBBLE] fixed placement viewport=({viewportCenter.x:F3},{viewportCenter.y:F3}) distance={bubbleDistanceMeters:F2} world=({fixedPosition.x:F3},{fixedPosition.y:F3},{fixedPosition.z:F3})");
        return fixedPosition;
    }

    bool TryGetCapturePose(string requestId, out Vector3 origin, out Quaternion rotation)
    {
        origin = Vector3.zero;
        rotation = Quaternion.identity;
        if (string.IsNullOrEmpty(requestId)) return false;
        if (captureContextRegistry == null)
            captureContextRegistry = CaptureContextRegistry.EnsureInstance();
        if (captureContextRegistry == null) return false;

        if (!captureContextRegistry.TryGet(requestId, out CaptureContextRegistry.CaptureContext context) || context == null)
            return false;

        origin = context.cameraPosition;
        rotation = context.cameraRotation;
        return true;
    }

    Vector2 GetDetectionViewportCenter(DetectionResult detection)
    {
        if (detection == null)
        {
            return new Vector2(0.5f, 0.5f);
        }

        Vector2 center = detection.Center;

        float imageWidth = Mathf.Max(1, detection.imageWidth);
        float imageHeight = Mathf.Max(1, detection.imageHeight);

        float viewportX = center.x / imageWidth;

        // YOLO/image bbox는 보통 왼쪽 위가 원점이고,
        // Unity viewport는 왼쪽 아래가 원점이라 y를 뒤집는다.
        float viewportY = 1f - (center.y / imageHeight);

        viewportX = Mathf.Clamp01(viewportX);
        viewportY = Mathf.Clamp01(viewportY);

        return new Vector2(viewportX, viewportY);
    }

    bool TryGetRaycastWorldPosition(Ray ray, out Vector3 worldPosition)
    {
        worldPosition = Vector3.zero;

        if (Physics.Raycast(ray, out RaycastHit hit, raycastMaxDistanceMeters, raycastMask, QueryTriggerInteraction.Ignore))
        {
            worldPosition = hit.point + hit.normal * surfaceOffsetMeters;
            worldPosition += Vector3.up * bubbleVerticalOffsetMeters;
            return true;
        }

        return false;
    }

    Vector3 GetFixedDistanceWorldPosition(Camera cam, Ray ray)
    {
        float distance = Mathf.Max(0.05f, bubbleDistanceMeters);

        Vector3 pos = ray.origin + ray.direction.normalized * distance;
        pos += cam.transform.up * bubbleVerticalOffsetMeters;

        return pos;
    }

    bool TryResolveAnchorPosition(DetectionResult detection, out Vector3 position)
    {
        position = Vector3.zero;
        ResolveReferences();

        if (anchorResolver == null)
            return false;

        if (!anchorResolver.TryResolveAnchor(detection, out DetectedObjectAnchor anchor) || anchor == null)
            return false;

        position = anchor.worldPosition;
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam != null)
        {
            position += cam.transform.up * bubbleVerticalOffsetMeters;
            Vector3 toCamera = cam.transform.position - position;
            if (toCamera.sqrMagnitude > 0.0001f)
                position += toCamera.normalized * bubbleForwardOffsetMeters;
        }

        return true;
    }

    void ResolveReferences()
    {
        if (enableGazePinchSelection && GetComponent<GazePinchUiSelector>() == null)
            gameObject.AddComponent<GazePinchUiSelector>();
        if (referenceCamera == null) referenceCamera = Camera.main;
        if (radialMenuSpawner == null) radialMenuSpawner = FindObjectOfType<ObjectActionRadialMenuSpawner>();
        if (radialMenuSpawner == null)
            radialMenuSpawner = ObjectActionRadialMenuSpawner.CreateRuntimeDefault();
        if (anchorResolver == null && radialMenuSpawner != null)
            anchorResolver = radialMenuSpawner.anchorResolver;
        if (anchorResolver == null) anchorResolver = FindObjectOfType<DetectedObjectAnchorResolver>();
        if (captureContextRegistry == null) captureContextRegistry = CaptureContextRegistry.EnsureInstance();
    }

    void ApplyConstantSize(GameObject go)
    {
        if (!enforceConstantApparentSize || go == null) return;
        DistanceConstantSize comp = go.GetComponent<DistanceConstantSize>();
        if (comp == null) comp = go.AddComponent<DistanceConstantSize>();
        comp.referenceCamera = referenceCamera != null ? referenceCamera : Camera.main;
        comp.referenceDistanceMeters = constantSizeReferenceDistance;
        comp.floorAtAuthoredSize = constantSizeFloorAtAuthored;
        comp.maxScaleMultiplier = constantSizeMaxMultiplier;
    }

    // Safety net: if XR Interaction Toolkit is installed and the prefab lacks
    // an XRSimpleInteractable, add one at runtime so XR Ray Interactor pinch
    // can select the sphere. ObjectDetectionBubble.Initialize then hooks
    // selectEntered. No-op if XRI isn't present.
    static System.Type s_xrSimpleInteractableType;
    static bool s_xrTypeProbed;
    void EnsureXrInteractableIfAvailable(GameObject go)
    {
        if (go == null) return;
        if (!s_xrTypeProbed)
        {
            s_xrTypeProbed = true;
            s_xrSimpleInteractableType = System.Type.GetType(
                "UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable, Unity.XR.Interaction.Toolkit")
                ?? System.Type.GetType(
                    "UnityEngine.XR.Interaction.Toolkit.XRSimpleInteractable, Unity.XR.Interaction.Toolkit");
        }
        if (s_xrSimpleInteractableType == null) return;
        if (go.GetComponent(s_xrSimpleInteractableType) != null) return;
        go.AddComponent(s_xrSimpleInteractableType);
        if (verboseLogging)
            Debug.Log("[OBJECT_BUBBLE] auto-added XRSimpleInteractable so pinch can select the bubble.");
    }
}
