using UnityEngine;
using UnityEngine.XR.ARFoundation;

[DefaultExecutionOrder(-10000)]
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(ARCameraManager))]
public sealed class ARCameraBackgroundBootstrapper : MonoBehaviour
{
    private void Awake()
    {
        EnsureCameraBackground();
    }

    private void EnsureCameraBackground()
    {
        if (TryGetComponent<ARCameraBackground>(out _))
            return;

        gameObject.AddComponent<ARCameraBackground>();
    }
}
