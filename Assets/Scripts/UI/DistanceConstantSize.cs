using UnityEngine;

/*
Keeps a world-space card / bubble looking roughly the same size on screen no
matter how far it is from the user.

  - At Awake we capture the prefab's authored localScale as the "base" scale
    that's correct at `referenceDistanceMeters` (default 1.2 m).
  - In LateUpdate we measure the actual camera-to-self distance and multiply
    the base scale by (distance / referenceDistance). Twice the distance ->
    twice the world scale -> same apparent size on the display.

Knobs the user typically tunes from the inspector:

  - referenceDistanceMeters : the distance at which the prefab's authored
      size should be considered "correct". Match this to the typical card
      placement depth (e.g. the depth Python sends back for a desk-distance
      object) so existing card sizing doesn't change at that range.
  - floorAtAuthoredSize     : when true the card NEVER shrinks below its
      authored scale (helpful when the user moves *closer* to the object than
      the reference distance and you don't want tiny labels).
  - maxScaleMultiplier      : cap so very distant cards don't become giants.
*/

[DefaultExecutionOrder(900)]
public class DistanceConstantSize : MonoBehaviour
{
    public Camera referenceCamera;
    [Tooltip("Distance (m) at which the prefab's authored scale is considered correct.")]
    public float referenceDistanceMeters = 1.2f;
    [Tooltip("When true the card never shrinks below its authored size even if the user is closer than referenceDistance.")]
    public bool floorAtAuthoredSize = true;
    [Tooltip("Hard upper cap on the per-axis scale multiplier so far cards don't blow up.")]
    [Range(1.0f, 10.0f)] public float maxScaleMultiplier = 4.0f;
    [Tooltip("Extra multiplier applied ON TOP of the distance compensation. Used by hover feedback (bubble grows while gazed/ray-hovered). 1 = neutral.")]
    [HideInInspector] public float externalMultiplier = 1f;

    Vector3 _baseScale = Vector3.one;
    bool _captured;

    void Awake()
    {
        CaptureBaseScale();
    }

    void OnEnable()
    {
        if (!_captured) CaptureBaseScale();
    }

    void LateUpdate()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return;

        float d = Vector3.Distance(transform.position, cam.transform.position);
        float refD = Mathf.Max(0.01f, referenceDistanceMeters);
        float mult = d / refD;
        if (floorAtAuthoredSize && mult < 1f) mult = 1f;
        mult = Mathf.Clamp(mult, 0.01f, maxScaleMultiplier);
        transform.localScale = _baseScale * (mult * Mathf.Max(0.01f, externalMultiplier));
    }

    void CaptureBaseScale()
    {
        _baseScale = transform.localScale;
        _captured = true;
    }
}
