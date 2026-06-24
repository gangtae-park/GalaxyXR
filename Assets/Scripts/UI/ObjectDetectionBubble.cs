using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class ObjectDetectionBubble : MonoBehaviour, IPointerClickHandler
{
    [Header("Refs")]
    [SerializeField] Button button;
    [SerializeField] Text labelText;
    [SerializeField] Text confidenceText;
    [SerializeField] Camera referenceCamera;

    [Header("Billboard")]
    [SerializeField] bool faceCamera = true;
    [SerializeField] bool keepUpright = false;

    DetectionResult detection;
    VlmResultReceiver.VlmResultPayload payload;
    string requestId;
    Action<DetectionResult, VlmResultReceiver.VlmResultPayload, string> onClicked;

    public Button Button
    {
        get => button;
        set => button = value;
    }

    public Text LabelText
    {
        get => labelText;
        set => labelText = value;
    }

    public Text ConfidenceText
    {
        get => confidenceText;
        set => confidenceText = value;
    }

    public Camera ReferenceCamera
    {
        get => referenceCamera;
        set => referenceCamera = value;
    }

    public void Initialize(
        DetectionResult detection,
        VlmResultReceiver.VlmResultPayload payload,
        string requestId,
        Action<DetectionResult, VlmResultReceiver.VlmResultPayload, string> onClicked)
    {
        this.detection = detection;
        this.payload = payload;
        this.requestId = requestId ?? "";
        this.onClicked = onClicked;

        ResolveReferences();
        UpdateText();

        if (button != null)
        {
            button.onClick.RemoveListener(HandleClick);
            button.onClick.AddListener(HandleClick);
        }
        else
        {
            Debug.LogWarning("[OBJECT_BUBBLE][WARN] Button is not assigned on ObjectDetectionBubble.");
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (button == null || !button.interactable)
            HandleClick();
    }

    void OnDestroy()
    {
        if (button != null)
            button.onClick.RemoveListener(HandleClick);
    }

    void LateUpdate()
    {
        if (!faceCamera) return;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return;

        Vector3 toCamera = cam.transform.position - transform.position;
        if (toCamera.sqrMagnitude < 0.0001f) return;

        if (keepUpright)
        {
            Vector3 flatForward = Vector3.ProjectOnPlane(-toCamera.normalized, Vector3.up);
            if (flatForward.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
        }
        else
        {
            transform.rotation = Quaternion.LookRotation(-toCamera.normalized, cam.transform.up);
        }
    }

    void HandleClick()
    {
        string label = detection != null ? detection.label : "";
        Debug.Log($"[OBJECT_BUBBLE] clicked label={label} request_id={requestId}");
        onClicked?.Invoke(detection, payload, requestId);
    }

    void ResolveReferences()
    {
        if (button == null) button = GetComponentInChildren<Button>(true);
        if (labelText == null)
        {
            Text[] texts = GetComponentsInChildren<Text>(true);
            if (texts.Length > 0) labelText = texts[0];
            if (texts.Length > 1) confidenceText = texts[1];
        }
        if (referenceCamera == null) referenceCamera = Camera.main;
    }

    void UpdateText()
    {
        string label = detection != null && !string.IsNullOrEmpty(detection.label)
            ? detection.label
            : "object";
        if (labelText != null)
            labelText.text = label;

        if (confidenceText != null)
            confidenceText.text = detection != null ? detection.confidence.ToString("0.00") : "";
    }
}
