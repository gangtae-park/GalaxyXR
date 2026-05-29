using UnityEngine;

public class FollowHeadUI : MonoBehaviour
{
    public Transform cameraTransform;

    void LateUpdate()
    {
        transform.position = cameraTransform.position + cameraTransform.forward * 1.0f;
        transform.rotation = Quaternion.LookRotation(transform.position - cameraTransform.position);
    }
}