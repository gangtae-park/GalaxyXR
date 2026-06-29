using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class VoiceStatusPanel : MonoBehaviour
{
    [Header("Refs")]
    public VoiceInputManager voiceInputManager;
    public Camera referenceCamera;

    [Header("Placement")]
    public float distanceFromCamera = 0.9f;
    public float verticalOffset = -0.22f;
    public float horizontalOffset = 0f;
    public Vector2 panelSize = new Vector2(420f, 110f);
    public float worldScale = 0.0012f;

    [Header("Messages")]
    public string listeningMessage = "Listening... speak now";
    public string partialPrefix = "Heard: ";
    public string finalPrefix = "Transcript: ";
    public string errorPrefix = "Voice error: ";
    public float hideAfterFinalSeconds = 1.5f;
    public float hideAfterErrorSeconds = 2.5f;

    [Header("Style")]
    public Color backgroundColor = new Color(0.02f, 0.02f, 0.02f, 0.86f);
    public Color listeningColor = new Color(0.38f, 0.82f, 1f, 1f);
    public Color finalColor = new Color(0.75f, 1f, 0.75f, 1f);
    public Color errorColor = new Color(1f, 0.55f, 0.55f, 1f);
    public int fontSize = 34;
    public TMP_FontAsset messageFont;

    private GameObject _panelRoot;
    private TMP_Text _messageText;
    private Coroutine _hideRoutine;

    void Awake()
    {
        BuildPanel();
    }

    void OnEnable()
    {
        ResolveReferences();
        Subscribe();
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void LateUpdate()
    {
        if (_panelRoot == null || !_panelRoot.activeSelf) return;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return;

        Transform camTransform = cam.transform;
        Transform panelTransform = _panelRoot.transform;
        panelTransform.position = camTransform.position
            + camTransform.forward * distanceFromCamera
            + camTransform.right * horizontalOffset
            + camTransform.up * verticalOffset;
        panelTransform.rotation = Quaternion.LookRotation(panelTransform.position - camTransform.position);
    }

    public void ShowListening()
    {
        Show(listeningMessage, listeningColor, 0f);
    }

    public void Hide()
    {
        StopHideRoutine();
        if (_panelRoot != null) _panelRoot.SetActive(false);
    }

    void HandleListeningStarted(InputMode mode)
    {
        ShowListening();
    }

    void HandleListeningStopped(string reason)
    {
        if (reason == "final" || reason == "error" || reason == "timeout") return;
        Hide();
    }

    void HandlePartialTranscript(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return;
        Show(partialPrefix + transcript, listeningColor, 0f);
    }

    void HandleFinalTranscript(string transcript)
    {
        Show(finalPrefix + transcript, finalColor, hideAfterFinalSeconds);
    }

    void HandleVoiceError(string error)
    {
        Show(errorPrefix + error, errorColor, hideAfterErrorSeconds);
    }

    void Show(string message, Color color, float hideAfterSeconds)
    {
        BuildPanel();
        StopHideRoutine();
        _messageText.text = message ?? "";
        _messageText.color = color;
        _panelRoot.SetActive(true);
        if (hideAfterSeconds > 0f) _hideRoutine = StartCoroutine(HideAfterDelay(hideAfterSeconds));
    }

    IEnumerator HideAfterDelay(float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (_panelRoot != null) _panelRoot.SetActive(false);
        _hideRoutine = null;
    }

    void StopHideRoutine()
    {
        if (_hideRoutine == null) return;
        StopCoroutine(_hideRoutine);
        _hideRoutine = null;
    }

    void ResolveReferences()
    {
        if (voiceInputManager == null) voiceInputManager = FindObjectOfType<VoiceInputManager>();
        if (referenceCamera == null) referenceCamera = Camera.main;
    }

    void Subscribe()
    {
        if (voiceInputManager == null) return;
        voiceInputManager.OnListeningStarted -= HandleListeningStarted;
        voiceInputManager.OnListeningStopped -= HandleListeningStopped;
        voiceInputManager.OnPartialTranscript -= HandlePartialTranscript;
        voiceInputManager.OnFinalTranscript -= HandleFinalTranscript;
        voiceInputManager.OnVoiceError -= HandleVoiceError;

        voiceInputManager.OnListeningStarted += HandleListeningStarted;
        voiceInputManager.OnListeningStopped += HandleListeningStopped;
        voiceInputManager.OnPartialTranscript += HandlePartialTranscript;
        voiceInputManager.OnFinalTranscript += HandleFinalTranscript;
        voiceInputManager.OnVoiceError += HandleVoiceError;
    }

    void Unsubscribe()
    {
        if (voiceInputManager == null) return;
        voiceInputManager.OnListeningStarted -= HandleListeningStarted;
        voiceInputManager.OnListeningStopped -= HandleListeningStopped;
        voiceInputManager.OnPartialTranscript -= HandlePartialTranscript;
        voiceInputManager.OnFinalTranscript -= HandleFinalTranscript;
        voiceInputManager.OnVoiceError -= HandleVoiceError;
    }

    void BuildPanel()
    {
        if (_panelRoot != null) return;

        _panelRoot = new GameObject("VoiceStatusPanelRuntime", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        _panelRoot.transform.SetParent(transform, false);

        RectTransform rootRect = _panelRoot.GetComponent<RectTransform>();
        rootRect.sizeDelta = panelSize;
        rootRect.localScale = Vector3.one * worldScale;

        Canvas canvas = _panelRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        CanvasScaler scaler = _panelRoot.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        GameObject background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(_panelRoot.transform, false);
        RectTransform bgRect = background.GetComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;
        Image image = background.GetComponent<Image>();
        image.color = backgroundColor;
        image.raycastTarget = false;

        GameObject textObject = new GameObject("Message", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(_panelRoot.transform, false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(24f, 14f);
        textRect.offsetMax = new Vector2(-24f, -14f);

        _messageText = textObject.GetComponent<TMP_Text>();
        if (messageFont != null) _messageText.font = messageFont;
        _messageText.fontSize = fontSize;
        _messageText.alignment = TextAlignmentOptions.Center;
        _messageText.enableWordWrapping = true;
        _messageText.raycastTarget = false;

        _panelRoot.SetActive(false);
    }
}
