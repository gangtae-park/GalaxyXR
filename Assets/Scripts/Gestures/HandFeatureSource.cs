using UnityEngine;

/*
HandFeatureSource

Single source of truth for the hand pose feature layout. Both the recording
scene (TemplateRecordController) and the inference scene reference this
component, so templates saved at record time match the runtime input.

Each call to BuildFeatureFrame() snapshots every joint in `handJoints` and
emits a flat float[] of length handJoints.Length * 3, with:
  - position taken RELATIVE to `jointOrigin` (or handJoints[0] if origin is null)
  - optionally rotated into the reference camera's local frame
    (normalizeToCameraOrientation = true) for head-rotation invariance.
*/

public class HandFeatureSource : MonoBehaviour
{
    [Header("Hand joints")]
    [Tooltip("Joints sampled each frame. Order MUST match between recording and inference.")]
    public Transform[] handJoints;
    [Tooltip("Per-frame normalisation origin. If null, handJoints[0] is used.")]
    public Transform jointOrigin;

    [Header("Normalisation")]
    [Tooltip("Rotate joint vectors into the reference camera's local frame so the feature " +
             "vector is invariant to head orientation.")]
    public bool normalizeToCameraOrientation = true;
    [Tooltip("If null, Camera.main is used at runtime.")]
    public Camera referenceCamera;

    public int FeatureDim => (handJoints != null ? handJoints.Length : 0) * 3;

    /// <summary>Snapshot the current hand pose into a flat float[].
    /// Returns null when handJoints is empty.</summary>
    public float[] BuildFeatureFrame()
    {
        if (handJoints == null || handJoints.Length == 0) return null;
        int n = handJoints.Length;

        Vector3 origin = jointOrigin != null
            ? jointOrigin.position
            : (handJoints[0] != null ? handJoints[0].position : Vector3.zero);

        Quaternion invCam = Quaternion.identity;
        if (normalizeToCameraOrientation)
        {
            Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
            if (cam != null) invCam = Quaternion.Inverse(cam.transform.rotation);
        }

        float[] f = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            if (handJoints[i] == null) continue;
            Vector3 p = handJoints[i].position - origin;
            if (normalizeToCameraOrientation) p = invCam * p;
            f[3 * i + 0] = p.x;
            f[3 * i + 1] = p.y;
            f[3 * i + 2] = p.z;
        }
        return f;
    }
}
