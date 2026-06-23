using UnityEngine;

/*
Rotates the attached transform to face the reference camera every LateUpdate.
Pairs with an XR Grab Interactable that has `Track Rotation` disabled so the billboard isn't fighting the grab system.
Position can still come from the grab (Track Position = true)
  lockUpright = true  : only yaw rotates (canvas stays vertical, no tilt)
  lockUpright = false : full rotation toward the camera (incl. pitch / roll)
*/

[DefaultExecutionOrder(1000)]
public class CanvasBillboard : MonoBehaviour
{
    public Camera referenceCamera;
    public bool lockUpright = true;
    [Range(0f, 0.95f)] public float smoothing = 0f;

    void LateUpdate()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return;

        Vector3 toSelf = transform.position - cam.transform.position;
        if (toSelf.sqrMagnitude < 0.000001f) return;

        Vector3 forward;
        Vector3 up;
        if (lockUpright)
        {
            toSelf.y = 0f;
            if (toSelf.sqrMagnitude < 0.000001f) return;
            forward = toSelf.normalized;
            up = Vector3.up;
        }
        else
        {
            forward = toSelf.normalized;
            up = cam.transform.up;
        }

        Quaternion target = Quaternion.LookRotation(forward, up);
        transform.rotation = smoothing > 0f
            ? Quaternion.Slerp(transform.rotation, target, 1f - smoothing)
            : target;
    }
}
