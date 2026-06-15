using UnityEngine;

/*
TranslateAreaVisualizer

Shows a translucent quad spanning TranslateGestureDetector.AreaCornerA <-> AreaCornerB
during the AreaDefined state, then clears it on Confirm or Cancel. The quad faces
the reference camera so it reads as a screen-aligned highlight.

Wiring: assign the TranslateGestureDetector reference; optionally set overlayPrefab
to a custom mesh / material. When overlayPrefab is null, a Unity Quad is created at
runtime and tinted with overlayColor.
*/

public class TranslateAreaVisualizer : MonoBehaviour
{
    [Header("References")]
    public TranslateGestureDetector translateDetector;
    public Camera referenceCamera;

    [Header("Appearance")]
    public GameObject overlayPrefab;
    public Color overlayColor = new Color(0.2f, 0.85f, 1f, 0.35f);
    public float minDimensionMeters = 0.05f;
    public float padMeters = 0.02f;
    public float leftPadMeters = 0.06f;

    [Header("Placement")]
    public float placementDistance = 0f;

    private GameObject _overlay;

    void OnEnable()
    {
        if (translateDetector == null)
        {
            Debug.LogWarning("[TranslateAreaVisualizer] translateDetector is not assigned.");
            return;
        }
        translateDetector.OnTranslateAreaDefined += HandleAreaDefined;
        translateDetector.OnTranslateConfirmed   += HandleClear;
        translateDetector.OnTranslateCancelled   += HandleClear;
    }

    void OnDisable()
    {
        if (translateDetector != null)
        {
            translateDetector.OnTranslateAreaDefined -= HandleAreaDefined;
            translateDetector.OnTranslateConfirmed   -= HandleClear;
            translateDetector.OnTranslateCancelled   -= HandleClear;
        }
        DestroyOverlay();
    }

    void HandleAreaDefined(string gestureName)
    {
        // Build the transform once at AreaDefined and never touch it again.
        // The overlay then stays locked to the world while the user moves their head
        // between AreaDefined and the palm swipe.
        if (_overlay == null) SpawnOverlay();
        UpdateOverlayTransform();
    }

    void HandleClear(string gestureName)
    {
        DestroyOverlay();
    }

    void SpawnOverlay()
    {
        if (overlayPrefab != null)
        {
            _overlay = Instantiate(overlayPrefab);
        }
        else
        {
            _overlay = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _overlay.name = "TranslateAreaOverlay";
            var col = _overlay.GetComponent<Collider>();
            if (col != null) Destroy(col);
            ApplyFallbackMaterial(_overlay);
        }
    }

    void UpdateOverlayTransform()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return;

        Vector3 A = translateDetector.AreaCornerA;
        Vector3 B = translateDetector.AreaCornerB;

        // Project the hand corners from camera-relative direction onto a plane at
        // `placementDistance` so the box covers what the user "framed" with their
        // hand, not the spot where the hand itself was (which is right in front of
        // the user's face). The angular extent of the box is preserved.
        if (placementDistance > 0f)
        {
            Vector3 camPos = cam.transform.position;
            Vector3 dirA = A - camPos;
            Vector3 dirB = B - camPos;
            float depthA = Mathf.Max(0.05f, Vector3.Dot(dirA, cam.transform.forward));
            float depthB = Mathf.Max(0.05f, Vector3.Dot(dirB, cam.transform.forward));
            A = camPos + dirA * (placementDistance / depthA);
            B = camPos + dirB * (placementDistance / depthB);
        }

        Vector3 mid = (A + B) * 0.5f;
        Vector3 right = cam.transform.right;
        Vector3 up = cam.transform.up;
        Vector3 d = B - A;

        // Padding: padMeters is added to both width and height (all four sides),
        // leftPadMeters is added only to the left edge to compensate for the
        // armed-start being slightly to the right of where the text actually begins.
        float w = Mathf.Abs(Vector3.Dot(d, right)) + padMeters + leftPadMeters;
        float h = Mathf.Abs(Vector3.Dot(d, up)) + padMeters;
        w = Mathf.Max(minDimensionMeters, w);
        h = Mathf.Max(minDimensionMeters, h);

        // Shift the midpoint left by half of leftPadMeters so the extra padding
        // lands on the left edge only (right edge stays at the captured thumb tip).
        mid -= right * (leftPadMeters * 0.5f);

        _overlay.transform.position = mid;
        // Unity's Quad faces -Z. LookRotation(cam.forward, cam.up) rotates -Z to
        // point along -cam.forward, i.e. toward the camera at THIS instant.
        // We never recompute this, so the overlay stays fixed in world space.
        _overlay.transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
        _overlay.transform.localScale = new Vector3(w, h, 1f);
    }

    void ApplyFallbackMaterial(GameObject go)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        Shader shader =
            Shader.Find("Universal Render Pipeline/Unlit") ??
            Shader.Find("Unlit/Transparent") ??
            Shader.Find("Sprites/Default");
        Material mat = shader != null ? new Material(shader) : new Material(r.sharedMaterial.shader);

        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", overlayColor);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color", overlayColor);
        mat.color = overlayColor;

        // Standard-shader transparency setup. Harmless on URP Unlit (it ignores).
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
        if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_ZWrite"))   mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.renderQueue = 3000;

        r.material = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    void DestroyOverlay()
    {
        if (_overlay != null)
        {
            Destroy(_overlay);
            _overlay = null;
        }
    }
}
