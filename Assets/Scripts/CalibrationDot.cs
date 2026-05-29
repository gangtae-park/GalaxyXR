using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections;
using UnityEngine.XR.Interaction.Toolkit.Interactables; 

public class CalibrationDot : MonoBehaviour
{
    [Header("Pinch Hold Config")]
    public float holdDuration = 1.0f;
    public float targetScaleMultiplier = 1.8f;

    public CalibrationManager manager;
    public CalibSender calibSender;
    public int dotIndex;

    private XRSimpleInteractable interactable;
    private Coroutine holdCoroutine;
    private Vector3 initialScale;
    private bool isConfirmed = false;
    
    void Awake()
    {
        interactable = GetComponent<XRSimpleInteractable>();
        initialScale = transform.localScale;
        if (calibSender == null) calibSender = CalibSender.Instance;
    }

    void OnEnable()
    {   
        if (calibSender == null) calibSender = CalibSender.Instance;

        isConfirmed = false;
        ResetVisual();

        if (interactable != null)
        {
            interactable.selectEntered.AddListener(OnSelectEntered);
            interactable.selectExited.AddListener(OnSelectExited);
        }
    }

    void OnDisable()
    {
        CancelHold();
        calibSender?.CancelCalibrationHold(dotIndex);
        ResetVisual();

        if (interactable != null)
        {
            interactable.selectEntered.RemoveListener(OnSelectEntered);
            interactable.selectExited.RemoveListener(OnSelectExited);
        }
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (isConfirmed) return;
        Debug.Log($"[CalibrationDot] {dotIndex}-th dot select entered by {args.interactorObject.transform.name}");
        calibSender?.BeginCalibrationHold(dotIndex);

        CancelHold();
        holdCoroutine = StartCoroutine(HoldToConfirm(args));
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        if (isConfirmed) return;
        Debug.Log($"[CalibrationDot] {dotIndex}-th dot select exited by {args.interactorObject.transform.name}");
        calibSender?.CancelCalibrationHold(dotIndex);

        CancelHold();
        ResetVisual();
    }

    private IEnumerator HoldToConfirm(SelectEnterEventArgs args)
    {
        float elapsed = 0f;
        Vector3 targetScale = initialScale * targetScaleMultiplier;

        while (elapsed < holdDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / holdDuration);
            transform.localScale = Vector3.Lerp(initialScale, targetScale, t);
            yield return null;
        }

        transform.localScale = targetScale;
        isConfirmed = true;
        holdCoroutine = null;

        Debug.Log($"[CalibrationDot] {dotIndex}-th dot hold complete by {args.interactorObject.transform.name}");
        calibSender?.CompleteCalibrationHold(dotIndex);
        manager.OnDotSelected(dotIndex);
    }

    private void CancelHold()
    {
        if (holdCoroutine != null)
        {
            StopCoroutine(holdCoroutine);
            holdCoroutine = null;
        }
    }

    private void ResetVisual()
    {
        transform.localScale = initialScale;
    }

}