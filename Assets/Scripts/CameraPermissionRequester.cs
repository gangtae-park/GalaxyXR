using UnityEngine;
using UnityEngine.Android;

public class CameraPermissionRequester : MonoBehaviour
{
    private void Start()
    {
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Permission.RequestUserPermission(Permission.Camera);
        }
    }
}