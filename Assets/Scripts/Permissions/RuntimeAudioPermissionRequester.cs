using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public class RuntimeAudioPermissionRequester : MonoBehaviour
{
    public bool requestOnStart = true;
    public bool verboseLogging = true;

    public bool HasPermission
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
            return true;
#endif
        }
    }

    void Start()
    {
        if (requestOnStart) RequestPermission();
    }

    public bool EnsurePermission()
    {
        if (HasPermission) return true;
        RequestPermission();
        return false;
    }

    public void RequestPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            if (verboseLogging) Debug.Log("[AudioPermission] RECORD_AUDIO already granted.");
            return;
        }

        if (verboseLogging) Debug.Log("[AudioPermission] requesting RECORD_AUDIO runtime permission.");
        Permission.RequestUserPermission(Permission.Microphone);
#else
        if (verboseLogging) Debug.Log("[AudioPermission] RECORD_AUDIO runtime permission not required on this platform.");
#endif
    }
}
