using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ObjectDetectionBubbleSpawner : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] ObjectDetectionBubble bubblePrefab;
    [SerializeField] Transform bubbleRoot;
    [SerializeField] ObjectActionRadialMenuSpawner radialMenuSpawner;
    [SerializeField] DetectedObjectAnchorResolver anchorResolver;
    [SerializeField] Camera referenceCamera;

    [Header("Bubble placement")]
    [SerializeField] bool filterByConfidence = false;
    [SerializeField, Range(0f, 1f)] float minConfidence = 0.25f;
    [SerializeField] float bubbleDistanceMeters = 1.2f;
    [SerializeField] float bubbleVerticalOffsetMeters = 0.04f;
    [SerializeField] float bubbleForwardOffsetMeters = 0.03f;
    [SerializeField] bool clearPreviousOnShow = true;
    [SerializeField] bool autoCreateRuntimeBubblePrefab = true;
    [SerializeField] bool verboseLogging = true;
    
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

    ObjectDetectionBubble CreateRuntimeBubblePrefab()
    {
        GameObject root = new GameObject("ObjectDetectionBubbleRuntimePrefab", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        root.transform.SetParent(transform, false);
        root.SetActive(false);

        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(150f, 72f);
        root.transform.localScale = Vector3.one * 0.0012f;

        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = referenceCamera != null ? referenceCamera : Camera.main;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        AddTrackedDeviceGraphicRaycasterIfAvailable(root);

        GameObject buttonGo = new GameObject("BubbleButton", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonGo.transform.SetParent(root.transform, false);
        RectTransform buttonRect = buttonGo.GetComponent<RectTransform>();
        buttonRect.anchorMin = Vector2.zero;
        buttonRect.anchorMax = Vector2.one;
        buttonRect.offsetMin = Vector2.zero;
        buttonRect.offsetMax = Vector2.zero;

        Image image = buttonGo.GetComponent<Image>();
        image.color = new Color(0.08f, 0.43f, 1f, 0.88f);

        Button button = buttonGo.GetComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.08f, 0.43f, 1f, 0.88f);
        colors.highlightedColor = new Color(0.24f, 0.58f, 1f, 0.96f);
        colors.pressedColor = new Color(0.02f, 0.24f, 0.82f, 1f);
        colors.selectedColor = colors.highlightedColor;
        button.colors = colors;

        Text label = CreateRuntimeText(buttonGo.transform, "Label", new Vector2(0f, 10f), 20, FontStyle.Bold);
        Text confidence = CreateRuntimeText(buttonGo.transform, "Confidence", new Vector2(0f, -16f), 14, FontStyle.Normal);

        ObjectDetectionBubble bubble = root.AddComponent<ObjectDetectionBubble>();
        bubble.Button = button;
        bubble.LabelText = label;
        bubble.ConfidenceText = confidence;
        bubble.ReferenceCamera = referenceCamera != null ? referenceCamera : Camera.main;

        if (verboseLogging)
            Debug.Log("[OBJECT_BUBBLE] created runtime default bubble prefab.");

        return bubble;
    }

    static Text CreateRuntimeText(Transform parent, string name, Vector2 anchoredPosition, int fontSize, FontStyle style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = new Vector2(136f, 24f);

        Text text = go.GetComponent<Text>();
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 8;
        text.resizeTextMaxSize = fontSize;
        text.raycastTarget = false;
        // Arial.ttf was retired from Unity's built-in resources; the new name is LegacyRuntime.ttf.
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return text;
    }

    void OnBubbleClicked(
        DetectionResult detection,
        VlmResultReceiver.VlmResultPayload payload,
        string requestId)
    {
        if (detection == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE][WARN] click ignored because detection is null.");
            return;
        }

        Debug.Log($"[UIInteraction] Bubble clicked: {detection.label}");
        Debug.Log($"[OBJECT_BUBBLE] clicked label={detection.label} conf={detection.confidence:F3} request_id={requestId}");
        ClearBubbles();
        ResolveReferences();

        if (radialMenuSpawner == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE][WARN] radialMenuSpawner is not assigned.");
            return;
        }

        Debug.Log("[UIInteraction] Opening UI panel from bubble click");
        Debug.Log("[OBJECT_BUBBLE] calling radial menu spawner...");
        bool spawned = radialMenuSpawner.HandleDetectionResult(detection, payload, requestId);
        if (!spawned)
            Debug.LogWarning($"[OBJECT_BUBBLE][WARN] radial menu spawn failed label={detection.label} request_id={requestId}");
    }

    Vector3 ComputeBubbleWorldPosition(DetectionResult detection)
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE][WARN] No camera found. Using spawner forward fallback.");
            return transform.position + transform.forward * Mathf.Max(0.05f, bubbleDistanceMeters);
        }

        Vector2 viewportCenter = GetDetectionViewportCenter(detection);

        Ray ray = cam.ViewportPointToRay(new Vector3(viewportCenter.x, viewportCenter.y, 0f));

        if (usePhysicsRaycastPlacement)
        {
            if (TryGetRaycastWorldPosition(ray, out Vector3 raycastPosition))
            {
                if (verboseLogging)
                {
                    Debug.Log($"[OBJECT_BUBBLE] raycast placement success viewport=({viewportCenter.x:F3},{viewportCenter.y:F3}) world=({raycastPosition.x:F3},{raycastPosition.y:F3},{raycastPosition.z:F3})");
                }

                return raycastPosition;
            }

            if (verboseLogging)
            {
                Debug.Log("[OBJECT_BUBBLE] raycast placement failed. Falling back to fixed distance.");
            }
        }

        Vector3 fixedPosition = GetFixedDistanceWorldPosition(cam, ray);

        if (verboseLogging)
        {
            Debug.Log($"[OBJECT_BUBBLE] fixed placement viewport=({viewportCenter.x:F3},{viewportCenter.y:F3}) distance={bubbleDistanceMeters:F2} world=({fixedPosition.x:F3},{fixedPosition.y:F3},{fixedPosition.z:F3})");
        }

        return fixedPosition;
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

    void AddTrackedDeviceGraphicRaycasterIfAvailable(GameObject root)
    {
        System.Type raycasterType = System.Type.GetType(
            "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit");
        if (raycasterType == null || root.GetComponent(raycasterType) != null) return;
        root.AddComponent(raycasterType);
        if (verboseLogging)
            Debug.Log("[OBJECT_BUBBLE] XR tracked device graphic raycaster attached to bubble.");
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
        if (referenceCamera == null) referenceCamera = Camera.main;
        if (radialMenuSpawner == null) radialMenuSpawner = FindObjectOfType<ObjectActionRadialMenuSpawner>();
        if (radialMenuSpawner == null)
            radialMenuSpawner = ObjectActionRadialMenuSpawner.CreateRuntimeDefault();
        if (anchorResolver == null && radialMenuSpawner != null)
            anchorResolver = radialMenuSpawner.anchorResolver;
        if (anchorResolver == null) anchorResolver = FindObjectOfType<DetectedObjectAnchorResolver>();
    }
}
