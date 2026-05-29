using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class PinchToCalibration : MonoBehaviour
{
    public CalibrationManager calibrationManager;
    public Transform gazeTransform;

    private XRBaseInteractor interactor;

    void Awake()
    {
        interactor = GetComponent<XRBaseInteractor>();

        if (interactor == null)
        {
            Debug.LogError("[PinchToCalibration] XRBaseInteractor not found on this GameObject.");
        }
    }

    void OnEnable()
    {
        if (interactor != null)
        {
            interactor.selectEntered.AddListener(OnSelectEntered);
            interactor.selectExited.AddListener(OnSelectExited);
        }
    }

    void OnDisable()
    {
        if (interactor != null)
        {
            interactor.selectEntered.RemoveListener(OnSelectEntered);
            interactor.selectExited.RemoveListener(OnSelectExited);
        }
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        Debug.Log($"[PinchToCalibration] Select Entered on {gameObject.name}");
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        Debug.Log($"[PinchToCalibration] Select Exited on {gameObject.name}");
    }

    public void TriggerCalibrationStep()
    {
        Debug.Log("[PinchToCalibration] TriggerCalibrationStep called (unused in dot-selection mode)");
    }
}