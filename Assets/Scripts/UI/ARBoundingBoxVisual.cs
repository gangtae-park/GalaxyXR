using UnityEngine;

public class ARBoundingBoxVisual : MonoBehaviour
{
    public Camera referenceCamera;
    public Vector2 size = new Vector2(0.45f, 0.35f);
    public Color lineColor = new Color(0.45f, 0.70f, 1.0f, 0.9f);
    public float lineWidth = 0.006f;
    public float lifetimeSeconds = 30f;
    public float pullTowardCamera = 0.02f;

    private LineRenderer _line;
    private Material _lineMaterial;
    private float _destroyAt;

    public static ARBoundingBoxVisual Create(
        Vector3 center,
        Camera referenceCamera,
        Vector2 size,
        Color color,
        float lineWidth,
        float lifetimeSeconds)
    {
        GameObject go = new GameObject("AR Target Bounds");
        go.transform.position = center;
        ARBoundingBoxVisual visual = go.AddComponent<ARBoundingBoxVisual>();
        visual.referenceCamera = referenceCamera;
        visual.size = size;
        visual.lineColor = color;
        visual.lineWidth = lineWidth;
        visual.lifetimeSeconds = lifetimeSeconds;
        visual.Initialize();
        return visual;
    }

    void Awake()
    {
        Initialize();
    }

    void Initialize()
    {
        if (_line == null)
        {
            _line = gameObject.GetComponent<LineRenderer>();
            if (_line == null) _line = gameObject.AddComponent<LineRenderer>();
        }

        _line.useWorldSpace = true;
        _line.loop = true;
        _line.positionCount = 4;
        _line.startWidth = lineWidth;
        _line.endWidth = lineWidth;
        _line.startColor = lineColor;
        _line.endColor = lineColor;
        if (_lineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null) _lineMaterial = new Material(shader);
        }
        if (_lineMaterial != null) _line.material = _lineMaterial;

        _destroyAt = Time.time + lifetimeSeconds;
        UpdateCorners();
    }

    void OnDestroy()
    {
        if (_lineMaterial != null)
        {
            Destroy(_lineMaterial);
            _lineMaterial = null;
        }
    }

    void LateUpdate()
    {
        if (lifetimeSeconds > 0f && Time.time >= _destroyAt)
        {
            Destroy(gameObject);
            return;
        }
        UpdateCorners();
    }

    void UpdateCorners()
    {
        if (_line == null) return;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector3 right = cam != null ? cam.transform.right : Vector3.right;
        Vector3 up = cam != null ? cam.transform.up : Vector3.up;
        Vector3 center = transform.position;

        if (cam != null && pullTowardCamera > 0f)
        {
            Vector3 toCamera = (cam.transform.position - center);
            if (toCamera.sqrMagnitude > 0.0001f)
                center += toCamera.normalized * pullTowardCamera;
        }

        Vector3 halfRight = right.normalized * (size.x * 0.5f);
        Vector3 halfUp = up.normalized * (size.y * 0.5f);

        _line.SetPosition(0, center - halfRight - halfUp);
        _line.SetPosition(1, center - halfRight + halfUp);
        _line.SetPosition(2, center + halfRight + halfUp);
        _line.SetPosition(3, center + halfRight - halfUp);
    }
}
