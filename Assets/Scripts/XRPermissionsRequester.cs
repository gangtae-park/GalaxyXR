using System.Collections.Generic;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/*
XRPermissionsRequester

Single-shot permission requester targeting Galaxy XR (Android XR / Snapdragon
Spaces). Replaces both EyeTrackingPermissionRequester and the Oculus-flavoured
XRI Starter Asset PermissionsManager.

Key design choices:

  1) Awake-only. NEVER re-requests from OnApplicationFocus / Update.
     Re-requesting on focus regain was the cause of the previous loop --
     Android XR fires focus events frequently (system overlays, permission
     dialogs themselves dropping focus, etc.) and re-issuing
     Permission.RequestUserPermission on each focus regain triggers a
     permission-dialog / OpenXR-action-rebinding storm that shows up as
     graphics flicker and dropped frame rate.

  2) Static `s_AlreadyPrompted` set. Even if this component is re-enabled
     or the scene reloads, each permission ID is prompted at most ONCE per
     process lifetime. Resets only on app restart.

  3) Galaxy XR / Android XR permission IDs use the `android.permission.*`
     namespace. (Meta Quest's `com.oculus.permission.*` IDs do NOT exist on
     Galaxy XR.) The default list covers eye gaze, hand tracking, and mic.

Required pairing:
  Every permission listed here must ALSO be declared in
  Assets/Plugins/Android/AndroidManifest.xml with
    <uses-permission android:name="..." />
  otherwise Permission.RequestUserPermission is a no-op.
*/

public class XRPermissionsRequester : MonoBehaviour
{
    [Header("Permissions to request once at startup")]
    [Tooltip("Android permission IDs. Galaxy XR uses 'android.permission.*' " +
             "(Android XR), NOT 'com.oculus.permission.*'.")]
    public List<string> permissions = new List<string>
    {
        "android.permission.EYE_TRACKING",
        "android.permission.EYE_TRACKING_FINE",
        "android.permission.HAND_TRACKING",
        "android.permission.RECORD_AUDIO",
    };

    [Tooltip("Log status for already-granted permissions too (useful while wiring).")]
    public bool verboseLogging = true;

    // Static so each permission is requested EXACTLY ONCE per process. This
    // survives scene reloads and component re-enables; only an app restart
    // clears it.
    static readonly HashSet<string> s_AlreadyPrompted = new HashSet<string>();

    void Awake()
    {
        if (permissions == null) return;
        for (int i = 0; i < permissions.Count; i++)
        {
            string id = permissions[i];
            if (string.IsNullOrEmpty(id)) continue;
            if (!s_AlreadyPrompted.Add(id)) continue;   // false = was already in set
            TryRequest(id);
        }
    }

    void TryRequest(string id)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(id))
        {
            if (verboseLogging) Debug.Log($"[XRPermissions] already granted: {id}");
            return;
        }

        var cb = new PermissionCallbacks();
        cb.PermissionGranted += s => Debug.Log($"[XRPermissions] GRANTED: {s}");
        cb.PermissionDenied  += s => Debug.LogWarning($"[XRPermissions] DENIED: {s}");
        cb.PermissionDeniedAndDontAskAgain += s =>
            Debug.LogError($"[XRPermissions] DENIED (don't ask again): {s} -- enable in system settings.");

        Permission.RequestUserPermission(id, cb);
        if (verboseLogging) Debug.Log($"[XRPermissions] requesting: {id}");
#else
        if (verboseLogging) Debug.Log($"[XRPermissions] non-Android build; skip {id}");
#endif
    }
}
