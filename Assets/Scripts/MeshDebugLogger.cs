using UnityEngine;
using UnityEngine.XR.ARFoundation;

public class MeshDebugLogger : MonoBehaviour
{
    public ARMeshManager meshManager;

    void Start()
    {
        Debug.Log($"[MESH DEBUG] meshManager null? {meshManager == null}");
        if (meshManager != null)
        {
            Debug.Log($"[MESH DEBUG] meshManager enabled = {meshManager.enabled}");
            meshManager.meshesChanged += OnMeshesChanged;
        }
    }

    void OnDestroy()
    {
        if (meshManager != null)
            meshManager.meshesChanged -= OnMeshesChanged;
    }

    void OnMeshesChanged(ARMeshesChangedEventArgs args)
    {
        Debug.Log($"[MESH DEBUG] added={args.added.Count}, updated={args.updated.Count}, removed={args.removed.Count}");
    }
}