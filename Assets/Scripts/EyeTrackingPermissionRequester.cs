using UnityEngine;
using UnityEngine.Android;

public class EyeTrackingPermissionRequester : MonoBehaviour
{
    const string EyeTrackingFinePermission = "android.permission.EYE_TRACKING_FINE";
    const string ScenePermission = "android.permission.SCENE_UNDERSTANDING_FINE";

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        RequestEyeTrackingPermission();
#endif
    }

    void RequestEyeTrackingPermission()
    {
        if (Permission.HasUserAuthorizedPermission(EyeTrackingFinePermission))
        {
            Debug.Log("Eye tracking permission already granted");
            RequestSceneUnderstandingPermission();
            return;
        }

        Debug.Log("Requesting EYE_TRACKING_FINE...");

        var callbacks = new PermissionCallbacks();

        callbacks.PermissionGranted += permission =>
        {
            Debug.Log("Eye tracking permission granted");
            RequestSceneUnderstandingPermission();
        };

        callbacks.PermissionDenied += permission =>
        {
            Debug.Log("Eye tracking permission denied");
        };

        callbacks.PermissionDeniedAndDontAskAgain += permission =>
        {
            Debug.Log("Eye tracking permission denied permanently");
        };

        Permission.RequestUserPermission(EyeTrackingFinePermission, callbacks);
    }

    void RequestSceneUnderstandingPermission()
    {
        if (Permission.HasUserAuthorizedPermission(ScenePermission))
        {
            Debug.Log("Scene permission already granted");
            return;
        }

        Debug.Log("Requesting SCENE_UNDERSTANDING_FINE...");

        var callbacks = new PermissionCallbacks();

        callbacks.PermissionGranted += permission =>
        {
            Debug.Log("Scene permission granted");
        };

        callbacks.PermissionDenied += permission =>
        {
            Debug.Log("Scene permission denied");
        };

        callbacks.PermissionDeniedAndDontAskAgain += permission =>
        {
            Debug.Log("Scene permission denied permanently");
        };

        Permission.RequestUserPermission(ScenePermission, callbacks);
    }
}