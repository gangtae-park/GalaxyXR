using System;
using System.Collections.Generic;
using UnityEngine;

public class CaptureContextRegistry : MonoBehaviour
{
    [Serializable]
    public class CaptureContext
    {
        public string requestId;
        public double registeredRealtime;
        public int imageWidth;
        public int imageHeight;
        public int screenWidth;
        public int screenHeight;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public Matrix4x4 projectionMatrix;
        public float verticalFov;
        public float aspect;
        public bool orthographic;
        public float orthographicSize;
        public float fallbackDistance;
    }

    public static CaptureContextRegistry Instance { get; private set; }

    [Header("Defaults")]
    public float maxContextAgeSeconds = 120f;
    public float fallbackDistance = 1.2f;
    public int defaultImageWidth = 0;
    public int defaultImageHeight = 0;

    readonly Dictionary<string, CaptureContext> _contexts = new Dictionary<string, CaptureContext>();
    string _latestRequestId;

    public string LatestRequestId => _latestRequestId;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Debug.LogWarning("[CaptureContext] multiple registries exist; newest one will still function locally.");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public static CaptureContextRegistry EnsureInstance()
    {
        if (Instance != null) return Instance;

        CaptureContextRegistry found = FindObjectOfType<CaptureContextRegistry>();
        if (found != null)
        {
            Instance = found;
            return found;
        }

        GameObject go = new GameObject("CaptureContextRegistry");
        CaptureContextRegistry registry = go.AddComponent<CaptureContextRegistry>();
        Instance = registry;
        return registry;
    }

    public string Register(string requestId, int imageWidth, int imageHeight, Camera cam, float? overrideFallbackDistance = null)
    {
        if (string.IsNullOrEmpty(requestId))
            requestId = NewRequestId();
        if (cam == null) cam = Camera.main;

        return RegisterSnapshot(
            requestId,
            imageWidth,
            imageHeight,
            Screen.width,
            Screen.height,
            cam != null ? cam.transform.position : Vector3.zero,
            cam != null ? cam.transform.rotation : Quaternion.identity,
            cam != null ? cam.projectionMatrix : Matrix4x4.identity,
            cam != null ? cam.fieldOfView : 60f,
            cam != null ? cam.aspect : (Screen.height > 0 ? Screen.width / (float)Screen.height : 1.777f),
            cam != null && cam.orthographic,
            cam != null ? cam.orthographicSize : 5f,
            overrideFallbackDistance ?? fallbackDistance);
    }

    public string RegisterSnapshot(
        string requestId,
        int imageWidth,
        int imageHeight,
        int screenWidth,
        int screenHeight,
        Vector3 cameraPosition,
        Quaternion cameraRotation,
        Matrix4x4 projectionMatrix,
        float verticalFov,
        float aspect,
        bool orthographic,
        float orthographicSize,
        float contextFallbackDistance)
    {
        if (string.IsNullOrEmpty(requestId))
            requestId = NewRequestId();

        CaptureContext context = new CaptureContext
        {
            requestId = requestId,
            registeredRealtime = Time.realtimeSinceStartupAsDouble,
            imageWidth = imageWidth > 0 ? imageWidth : defaultImageWidth,
            imageHeight = imageHeight > 0 ? imageHeight : defaultImageHeight,
            screenWidth = screenWidth > 0 ? screenWidth : Screen.width,
            screenHeight = screenHeight > 0 ? screenHeight : Screen.height,
            cameraPosition = cameraPosition,
            cameraRotation = cameraRotation,
            projectionMatrix = projectionMatrix,
            verticalFov = verticalFov > 0f ? verticalFov : 60f,
            aspect = aspect > 0f ? aspect : (Screen.height > 0 ? Screen.width / (float)Screen.height : 1.777f),
            orthographic = orthographic,
            orthographicSize = orthographicSize > 0f ? orthographicSize : 5f,
            fallbackDistance = Mathf.Max(0.05f, contextFallbackDistance)
        };

        _contexts[requestId] = context;
        _latestRequestId = requestId;
        PruneOld();

        Debug.Log($"[CaptureContext] registered request_id={requestId} image={context.imageWidth}x{context.imageHeight} screen={context.screenWidth}x{context.screenHeight} camera={context.cameraPosition}");
        return requestId;
    }

    public bool TryGet(string requestId, out CaptureContext context)
    {
        context = null;
        PruneOld();

        if (!string.IsNullOrEmpty(requestId) && _contexts.TryGetValue(requestId, out context))
            return true;

        return false;
    }

    public bool TryGetLatest(out CaptureContext context)
    {
        context = null;
        if (!string.IsNullOrEmpty(_latestRequestId) && TryGet(_latestRequestId, out context))
            return true;
        return false;
    }

    void PruneOld()
    {
        if (maxContextAgeSeconds <= 0f || _contexts.Count == 0) return;

        double now = Time.realtimeSinceStartupAsDouble;
        List<string> stale = null;
        foreach (var kv in _contexts)
        {
            if (now - kv.Value.registeredRealtime <= maxContextAgeSeconds) continue;
            if (stale == null) stale = new List<string>();
            stale.Add(kv.Key);
        }

        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++)
        {
            _contexts.Remove(stale[i]);
            if (_latestRequestId == stale[i]) _latestRequestId = "";
            Debug.Log($"[CaptureContext] pruned stale request_id={stale[i]}");
        }
    }

    static string NewRequestId()
    {
        return Guid.NewGuid().ToString("N");
    }
}
