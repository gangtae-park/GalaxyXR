using UnityEngine;

/*
Single source of truth for the hand pose feature layout.
Both the recording scene and the inference scene reference this component, so templates saved at record time match the runtime input.

Each call to BuildFeatureFrame() snapshots every joint in `handJoints` and emits a flat float[] of length handJoints.Length * 3, with:
    - position taken relative to jointOrigin
    - optionally rotated into the reference camera's local frame for head-rotation invariance.
*/

public class HandFeatureSource : MonoBehaviour
{
    [Header("Hand joints")]
    public Transform[] handJoints;
    public Transform jointOrigin;

    [Header("Normalisation")]
    public bool normalizeToCameraOrientation = true;
    public Camera referenceCamera;

    public int FeatureDim => (handJoints != null ? handJoints.Length : 0) * 3;

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
