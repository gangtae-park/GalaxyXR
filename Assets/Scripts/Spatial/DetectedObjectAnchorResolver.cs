using UnityEngine;

public class DetectedObjectAnchorResolver : MonoBehaviour
{
    [Header("Refs")]
    public CaptureContextRegistry registry;
    public YoloScreenToWorldMapper mapper;

    [Header("Raycast")]
    public bool usePhysicsRaycast = true;
    public LayerMask raycastMask = ~0;
    public float maxRaycastDistance = 5f;

    [Header("Depth")]
    public bool enableDepthSampling = false;

    [Header("Fallback")]
    public float fixedFallbackDistance = 1.2f;

    void Awake()
    {
        ResolveRefs();
    }

    public bool TryResolveAnchor(DetectionResult detection, out DetectedObjectAnchor anchor)
    {
        ResolveRefs();
        anchor = null;

        if (detection == null || !detection.HasValidBbox())
        {
            Debug.LogWarning("[AnchorResolver] invalid detection; anchor skipped.");
            return false;
        }

        if (string.IsNullOrEmpty(detection.requestId))
        {
            Debug.LogWarning("[AnchorResolver] detection skipped: request_id is empty; refusing latest-context fallback.");
            return false;
        }

        CaptureContextRegistry.CaptureContext context;
        if (registry == null || !registry.TryGet(detection.requestId, out context))
        {
            if (detection.requireExactRequestContext && !string.IsNullOrEmpty(detection.requestId))
            {
                Debug.LogWarning($"[AnchorResolver] exact capture context missing for request_id={detection.requestId}; object UI anchor skipped to avoid current-camera fallback.");
                return false;
            }

            if (registry == null || !registry.TryGetLatest(out context))
            {
                Camera cam = Camera.main;
                if (registry == null) registry = CaptureContextRegistry.EnsureInstance();
                string requestId = registry.Register(detection.requestId, detection.imageWidth, detection.imageHeight, cam, fixedFallbackDistance);
                registry.TryGet(requestId, out context);
                detection.requestId = requestId;
                Debug.LogWarning("[AnchorResolver] no matching capture context; registered current camera as fallback.");
            }
            else
            {
                Debug.LogWarning($"[AnchorResolver] request_id='{detection.requestId}' not found; using latest request_id={context.requestId}.");
                detection.requestId = context.requestId;
            }
        }

        Vector2 viewport = mapper != null ? mapper.GetViewportPoint(detection, context) : new Vector2(0.5f, 0.5f);
        Ray ray = BuildRay(context, viewport);
        string method = "FixedDistance";
        Vector3 world = ray.origin + ray.direction * Mathf.Max(0.05f, context != null ? context.fallbackDistance : fixedFallbackDistance);

        if (usePhysicsRaycast && Physics.Raycast(ray, out RaycastHit hit, maxRaycastDistance, raycastMask))
        {
            world = hit.point;
            method = "Raycast";
        }
        else if (enableDepthSampling)
        {
            Debug.LogWarning("[AnchorResolver] Depth sampling requested but no XR depth provider is wired yet; falling back to fixed distance.");
        }

        if (method == "FixedDistance")
            Debug.LogWarning($"[AnchorResolver] anchor resolve method used: FixedDistance distance={(context != null ? context.fallbackDistance : fixedFallbackDistance):F2}");
        else
            Debug.Log($"[AnchorResolver] anchor resolve method used: {method}");

        anchor = new DetectedObjectAnchor
        {
            detection = detection,
            context = context,
            viewportPoint = viewport,
            worldPosition = world,
            rayOrigin = ray.origin,
            rayDirection = ray.direction,
            resolveMethod = method
        };
        return true;
    }

    Ray BuildRay(CaptureContextRegistry.CaptureContext context, Vector2 viewport)
    {
        if (context == null)
        {
            Camera cam = Camera.main;
            if (cam != null) return cam.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
            return new Ray(transform.position, transform.forward);
        }

        float nx = viewport.x * 2f - 1f;
        float ny = viewport.y * 2f - 1f;

        if (context.orthographic)
        {
            float halfHeight = context.orthographicSize;
            float halfWidth = halfHeight * Mathf.Max(0.01f, context.aspect);
            Vector3 localOrigin = new Vector3(nx * halfWidth, ny * halfHeight, 0f);
            Vector3 worldOrigin = context.cameraPosition + context.cameraRotation * localOrigin;
            Vector3 worldDirection = context.cameraRotation * Vector3.forward;
            return new Ray(worldOrigin, worldDirection.normalized);
        }

        float tanHalfFov = Mathf.Tan(context.verticalFov * Mathf.Deg2Rad * 0.5f);
        Vector3 localDirection = new Vector3(
            nx * tanHalfFov * Mathf.Max(0.01f, context.aspect),
            ny * tanHalfFov,
            1f).normalized;
        Vector3 direction = context.cameraRotation * localDirection;
        return new Ray(context.cameraPosition, direction.normalized);
    }

    void ResolveRefs()
    {
        if (registry == null) registry = CaptureContextRegistry.EnsureInstance();
        if (mapper == null)
        {
            mapper = GetComponent<YoloScreenToWorldMapper>();
            if (mapper == null) mapper = gameObject.AddComponent<YoloScreenToWorldMapper>();
        }
    }
}
