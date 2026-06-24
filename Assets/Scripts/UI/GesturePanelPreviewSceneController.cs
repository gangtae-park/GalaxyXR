using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class GesturePanelPreviewSceneController : MonoBehaviour
{
    enum PreviewStep
    {
        Search,
        AskListening,
        VoiceAnswer,
        Compare,
        Translate,
        Capture,
        Save,
        Anchor,
        Activate,
        Deactivate,
        SetChange,
        ReadSense
    }

    [Header("Refs")]
    public Camera referenceCamera;
    public VoiceInputManager voiceInputManager;

    [Header("Layout")]
    public float distanceFromCamera = 1.55f;
    public float worldCanvasScale = 0.0016f;
    public Vector2 mockImageSize = new Vector2(520f, 330f);
    public Vector3 imageOffset = new Vector3(-0.42f, -0.02f, 0f);
    public Vector3 panelOffset = new Vector3(0.43f, 0.02f, 0f);

    [Header("Panel Style")]
    public Color panelBackgroundColor = new Color(0.20f, 0.45f, 1.0f, 0.55f);
    public Color panelBorderColor = new Color(0.72f, 0.88f, 1.0f, 0.78f);
    public Color bboxColor = new Color(0.36f, 0.72f, 1.0f, 0.95f);

    [Header("Mock input")]
    public string actionInput = "search";
    public string transcriptInput = "\uc774 \ucd08\ub85d\uc0c9 \ubcd1\uc740 \ubb50\uc57c?";
    public bool autoFindVoiceInputManager = true;

    GameObject _imageRoot;
    GameObject _panelRoot;
    TextMeshProUGUI _captionText;
    Texture2D _mockTexture;
    PreviewStep _currentStep = PreviewStep.Search;

    void Awake()
    {
        if (referenceCamera == null) referenceCamera = Camera.main;
        if (autoFindVoiceInputManager && voiceInputManager == null)
            voiceInputManager = FindObjectOfType<VoiceInputManager>();
    }

    void OnEnable()
    {
        if (voiceInputManager != null)
        {
            voiceInputManager.OnFinalTranscript -= HandleVoiceTranscript;
            voiceInputManager.OnFinalTranscript += HandleVoiceTranscript;
        }
    }

    void OnDisable()
    {
        if (voiceInputManager != null)
            voiceInputManager.OnFinalTranscript -= HandleVoiceTranscript;
    }

    void OnDestroy()
    {
        if (_mockTexture != null)
        {
            Destroy(_mockTexture);
            _mockTexture = null;
        }
    }

    void Start()
    {
        ShowStep(PreviewStep.Search);
    }

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            RunAction(actionInput);
        if (keyboard.vKey.wasPressedThisFrame)
            StartVoiceOrMock();
        if (keyboard.nKey.wasPressedThisFrame)
            ShowNext();
        if (keyboard.pKey.wasPressedThisFrame)
            ShowPrevious();

        if (keyboard.digit1Key.wasPressedThisFrame) ShowStep(PreviewStep.Search);
        if (keyboard.digit2Key.wasPressedThisFrame) ShowStep(PreviewStep.AskListening);
        if (keyboard.digit3Key.wasPressedThisFrame) ShowStep(PreviewStep.VoiceAnswer);
        if (keyboard.digit4Key.wasPressedThisFrame) ShowStep(PreviewStep.Compare);
        if (keyboard.digit5Key.wasPressedThisFrame) ShowStep(PreviewStep.Translate);
        if (keyboard.digit6Key.wasPressedThisFrame) ShowStep(PreviewStep.Capture);
        if (keyboard.digit7Key.wasPressedThisFrame) ShowStep(PreviewStep.Save);
        if (keyboard.digit8Key.wasPressedThisFrame) ShowStep(PreviewStep.Anchor);
        if (keyboard.digit9Key.wasPressedThisFrame) ShowStep(PreviewStep.ReadSense);
    }

    void OnGUI()
    {
        const int width = 380;
        GUILayout.BeginArea(new Rect(16, 16, width, 300), GUI.skin.box);
        GUILayout.Label("Gesture Panel Preview");
        GUILayout.Label("Step: " + _currentStep);

        GUILayout.Space(6);
        GUILayout.Label("Action input");
        actionInput = GUILayout.TextField(actionInput);
        if (GUILayout.Button("Run Action")) RunAction(actionInput);

        GUILayout.Space(6);
        GUILayout.Label("Voice transcript / mock STT");
        transcriptInput = GUILayout.TextField(transcriptInput);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Mock Voice Final")) HandleVoiceTranscript(transcriptInput);
        if (GUILayout.Button("Start Voice")) StartVoiceOrMock();
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Previous")) ShowPrevious();
        if (GUILayout.Button("Next")) ShowNext();
        GUILayout.EndHorizontal();

        GUILayout.Label("Keys: Enter run, V voice, N/P next/prev, 1-9 panels");
        GUILayout.EndArea();
    }

    public void RunAction(string action)
    {
        string normalized = (action ?? "").Trim().ToLowerInvariant();
        if (normalized.Contains("search") || normalized.Contains("find")) ShowStep(PreviewStep.Search);
        else if (normalized.Contains("ask")) ShowStep(PreviewStep.AskListening);
        else if (normalized.Contains("voice")) HandleVoiceTranscript(transcriptInput);
        else if (normalized.Contains("compare")) ShowStep(PreviewStep.Compare);
        else if (normalized.Contains("translate")) ShowStep(PreviewStep.Translate);
        else if (normalized.Contains("capture")) ShowStep(PreviewStep.Capture);
        else if (normalized.Contains("save") || normalized.Contains("store")) ShowStep(PreviewStep.Save);
        else if (normalized.Contains("mark") || normalized.Contains("anchor")) ShowStep(PreviewStep.Anchor);
        else if (normalized.Contains("deactivate")) ShowStep(PreviewStep.Deactivate);
        else if (normalized.Contains("activate")) ShowStep(PreviewStep.Activate);
        else if (normalized.Contains("set") || normalized.Contains("change")) ShowStep(PreviewStep.SetChange);
        else if (normalized.Contains("read") || normalized.Contains("sense")) ShowStep(PreviewStep.ReadSense);
        else ShowStep(PreviewStep.Search);
    }

    public void StartVoiceOrMock()
    {
        if (voiceInputManager != null)
        {
            voiceInputManager.StartListening();
            ShowStep(PreviewStep.AskListening);
            return;
        }

        HandleVoiceTranscript(transcriptInput);
    }

    void HandleVoiceTranscript(string transcript)
    {
        transcriptInput = string.IsNullOrWhiteSpace(transcript) ? transcriptInput : transcript.Trim();
        ShowStep(PreviewStep.VoiceAnswer);
    }

    void ShowNext()
    {
        int count = Enum.GetValues(typeof(PreviewStep)).Length;
        ShowStep((PreviewStep)(((int)_currentStep + 1) % count));
    }

    void ShowPrevious()
    {
        int count = Enum.GetValues(typeof(PreviewStep)).Length;
        ShowStep((PreviewStep)(((int)_currentStep - 1 + count) % count));
    }

    void ShowStep(PreviewStep step)
    {
        _currentStep = step;
        BuildMockImage(step);
        BuildPanel(step);
    }

    void BuildMockImage(PreviewStep step)
    {
        if (_imageRoot != null) Destroy(_imageRoot);

        _imageRoot = CreateWorldCanvas("Mock Server Image", mockImageSize, GetWorldPosition(imageOffset));
        GameObject panel = CreateStretchChild("Panel", _imageRoot.transform);
        Image panelImage = panel.AddComponent<Image>();
        panelImage.color = new Color(0.04f, 0.07f, 0.11f, 0.88f);

        GameObject rawGo = CreateRectChild("MockImage", panel.transform, mockImageSize - new Vector2(30f, 58f), new Vector2(0f, 12f));
        RawImage raw = rawGo.AddComponent<RawImage>();
        if (_mockTexture == null) _mockTexture = CreateMockServerTexture();
        raw.texture = _mockTexture;
        raw.color = Color.white;

        AddBorder(panel.transform, mockImageSize - new Vector2(30f, 58f), new Vector2(0f, 12f), new Color(0.75f, 0.88f, 1f, 0.55f), 2f);
        AddTargetBox(panel.transform, step);

        _captionText = CreateText(panel.transform, "Caption", "", 18f, FontStyles.Bold, TextAlignmentOptions.Center);
        RectTransform captionRect = _captionText.GetComponent<RectTransform>();
        captionRect.anchorMin = new Vector2(0f, 0f);
        captionRect.anchorMax = new Vector2(1f, 0f);
        captionRect.pivot = new Vector2(0.5f, 0f);
        captionRect.anchoredPosition = new Vector2(0f, 10f);
        captionRect.sizeDelta = new Vector2(-24f, 34f);
        _captionText.text = GetImageCaption(step);
    }

    void AddTargetBox(Transform parent, PreviewStep step)
    {
        Vector2 pos = new Vector2(-116f, 30f);
        Vector2 size = new Vector2(96f, 150f);

        if (step == PreviewStep.Compare)
        {
            AddBorder(parent, new Vector2(96f, 150f), new Vector2(-116f, 30f), bboxColor, 4f);
            AddBorder(parent, new Vector2(130f, 120f), new Vector2(116f, -8f), bboxColor, 4f);
            return;
        }

        if (step == PreviewStep.Translate)
        {
            pos = new Vector2(112f, 44f);
            size = new Vector2(150f, 52f);
        }
        else if (step == PreviewStep.ReadSense)
        {
            pos = new Vector2(108f, -10f);
            size = new Vector2(132f, 120f);
        }
        else if (step == PreviewStep.Capture)
        {
            pos = Vector2.zero;
            size = new Vector2(395f, 225f);
        }

        AddBorder(parent, size, pos, bboxColor, 4f);
    }

    void BuildPanel(PreviewStep step)
    {
        if (_panelRoot != null) Destroy(_panelRoot);

        switch (step)
        {
            case PreviewStep.Search:
                _panelRoot = CreateInfoCard("Green Bottle", "Mock VLM Search result\nReusable bottle with a green cap.\nDetected from the current camera frame.", ARPanelLayoutKind.InfoCard);
                break;
            case PreviewStep.AskListening:
                _panelRoot = CreateAskCard("Green Bottle", "Listening for a Korean question...");
                break;
            case PreviewStep.VoiceAnswer:
                _panelRoot = CreateAnswerCard("Green Bottle", transcriptInput, "Mock answer: \ucd08\ub85d\uc0c9 \ubb3c\ubcd1\uc73c\ub85c \ubcf4\uc774\uba70, \ud604\uc7ac \uc120\ud0dd\ub41c \ub300\uc0c1\uc5d0 \ub300\ud55c \uc9c8\ubb38 \uc751\ub2f5 UI\uc785\ub2c8\ub2e4.");
                break;
            case PreviewStep.Compare:
                _panelRoot = CreateCompareCard();
                break;
            case PreviewStep.Translate:
                _panelRoot = CreateInfoCard("English -> Korean", "Honey Crunch\n\uae00\ub8e8\ud150 \ud504\ub9ac \uc2dc\ub9ac\uc5bc\n\uc2dc\uc5f0\uc6a9 \ubc88\uc5ed \uacb0\uacfc", ARPanelLayoutKind.TranslationCard);
                break;
            case PreviewStep.Capture:
                _panelRoot = CreateInfoCard("Capture", "Image captured and saved", ARPanelLayoutKind.StatusCard);
                break;
            case PreviewStep.Save:
                _panelRoot = CreateInfoCard("Note", "Saved memo:\nGreen bottle on the desk.\nMock extracted info is stored here.", ARPanelLayoutKind.NoteCard);
                break;
            case PreviewStep.Anchor:
                _panelRoot = CreateInfoCard("Marker anchored", "Anchor position saved near the selected object.", ARPanelLayoutKind.AnchorCard);
                break;
            case PreviewStep.Activate:
                _panelRoot = CreateInfoCard("Activate", "Activated", ARPanelLayoutKind.StatusCard);
                break;
            case PreviewStep.Deactivate:
                _panelRoot = CreateInfoCard("Deactivate", "Deactivated", ARPanelLayoutKind.StatusCard);
                break;
            case PreviewStep.SetChange:
                _panelRoot = CreateInfoCard("Set / Change", "Brightness      72%\nMode            Focus\nValue           Mock", ARPanelLayoutKind.ControlCard);
                break;
            case PreviewStep.ReadSense:
                _panelRoot = CreateInfoCard("Read / Sense", "CO2      421 ppm\nHCHO     0.03 mg/m3\nTVOC     0.18 mg/m3\nTEMP     24.1 C\nHUMI     47%", ARPanelLayoutKind.SensorCard);
                break;
        }
    }

    GameObject CreateInfoCard(string title, string body, ARPanelLayoutKind kind)
    {
        GameObject root = CreateStyledPanel("Preview " + kind, kind);
        Transform panel = root.transform.Find("Panel");
        CreateText(panel, "TitleText", title, 26f, FontStyles.Bold, TextAlignmentOptions.Left);
        CreateText(panel, "BodyText", body, 17f, FontStyles.Normal, TextAlignmentOptions.Left);
        ApplyStyle(root, kind);
        return root;
    }

    GameObject CreateAskCard(string objectName, string status)
    {
        GameObject root = CreateStyledPanel("Preview Ask", ARPanelLayoutKind.AskCard);
        Transform panel = root.transform.Find("Panel");
        CreateText(panel, "TitleText", "Ask about '" + objectName + "'", 26f, FontStyles.Bold, TextAlignmentOptions.Left);
        CreateText(panel, "StatusText", status, 15f, FontStyles.Normal, TextAlignmentOptions.Left);

        GameObject row = CreateLayoutChild("InputRow", panel, 48f);
        HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.padding = new RectOffset(0, 0, 0, 0);
        rowLayout.spacing = 8f;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = true;

        GameObject input = CreateLayoutChild("QuestionPreview", row.transform, 48f);
        LayoutElement inputLayout = input.GetComponent<LayoutElement>();
        inputLayout.flexibleWidth = 1f;
        Image inputBg = input.AddComponent<Image>();
        inputBg.color = new Color(0.04f, 0.10f, 0.20f, 0.42f);
        StretchToParent(CreateText(input.transform, "QuestionText", transcriptInput, 14f, FontStyles.Normal, TextAlignmentOptions.Left), 10f, 4f);

        GameObject send = CreateLayoutChild("SendButton", row.transform, 48f);
        LayoutElement sendLayout = send.GetComponent<LayoutElement>();
        sendLayout.preferredWidth = 82f;
        Image sendBg = send.AddComponent<Image>();
        sendBg.color = new Color(0.25f, 0.50f, 1.0f, 0.88f);
        StretchToParent(CreateText(send.transform, "SendText", "Send", 15f, FontStyles.Bold, TextAlignmentOptions.Center), 4f, 4f);

        ApplyStyle(root, ARPanelLayoutKind.AskCard);
        return root;
    }

    GameObject CreateAnswerCard(string objectName, string question, string answer)
    {
        GameObject root = CreateStyledPanel("Preview Answer", ARPanelLayoutKind.AnswerCard);
        Transform panel = root.transform.Find("Panel");
        CreateText(panel, "TitleText", objectName, 26f, FontStyles.Bold, TextAlignmentOptions.Left);
        CreateText(panel, "QuestionText", question, 15f, FontStyles.Italic, TextAlignmentOptions.Left);
        CreateText(panel, "AnswerText", answer, 17f, FontStyles.Normal, TextAlignmentOptions.Left);
        ApplyStyle(root, ARPanelLayoutKind.AnswerCard);
        return root;
    }

    GameObject CreateCompareCard()
    {
        GameObject root = CreateStyledPanel("Preview Compare", ARPanelLayoutKind.CompareCard);
        Transform panel = root.transform.Find("Panel");
        CreateText(panel, "TitleText", "Compare", 26f, FontStyles.Bold, TextAlignmentOptions.Left);

        GameObject row = CreateLayoutChild("CompareColumns", panel, 125f);
        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;

        CreateCompareColumn(row.transform, "Green Bottle", "Material: plastic\nUse: drink\nState: reusable");
        CreateCompareColumn(row.transform, "White Box", "Material: paper\nUse: package\nState: unopened");
        ApplyStyle(root, ARPanelLayoutKind.CompareCard);
        return root;
    }

    void CreateCompareColumn(Transform parent, string title, string body)
    {
        GameObject column = CreateLayoutChild(title, parent, 120f);
        Image bg = column.AddComponent<Image>();
        bg.color = new Color(0.05f, 0.11f, 0.22f, 0.32f);
        VerticalLayoutGroup layout = column.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 8, 8);
        layout.spacing = 4f;
        CreateText(column.transform, "HeaderText", title, 17f, FontStyles.Bold, TextAlignmentOptions.Left);
        CreateText(column.transform, "BodyText", body, 14f, FontStyles.Normal, TextAlignmentOptions.Left);
    }

    GameObject CreateStyledPanel(string name, ARPanelLayoutKind kind)
    {
        GameObject root = CreateWorldCanvas(name, GetPanelSize(kind), GetWorldPosition(panelOffset));
        GameObject panel = CreateStretchChild("Panel", root.transform);
        Image image = panel.AddComponent<Image>();
        image.color = panelBackgroundColor;
        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 14, 14);
        layout.spacing = 8f;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return root;
    }

    void ApplyStyle(GameObject root, ARPanelLayoutKind kind)
    {
        ARPanelStyle style = ARPanelStyle.ApplyTo(root, kind);
        if (style == null) return;
        style.backgroundColor = panelBackgroundColor;
        style.borderColor = panelBorderColor;
        style.textColor = Color.white;
        style.secondaryTextColor = new Color(0.92f, 0.97f, 1.0f, 0.92f);
        style.Apply();
    }

    Vector2 GetPanelSize(ARPanelLayoutKind kind)
    {
        switch (kind)
        {
            case ARPanelLayoutKind.AskCard: return new Vector2(430f, 170f);
            case ARPanelLayoutKind.AnswerCard: return new Vector2(500f, 240f);
            case ARPanelLayoutKind.CompareCard: return new Vector2(455f, 220f);
            case ARPanelLayoutKind.TranslationCard: return new Vector2(380f, 150f);
            case ARPanelLayoutKind.SensorCard: return new Vector2(360f, 210f);
            case ARPanelLayoutKind.StatusCard:
            case ARPanelLayoutKind.NoteCard:
            case ARPanelLayoutKind.AnchorCard:
            case ARPanelLayoutKind.ControlCard:
                return new Vector2(340f, 115f);
            default:
                return new Vector2(390f, 165f);
        }
    }

    GameObject CreateWorldCanvas(string name, Vector2 size, Vector3 position)
    {
        GameObject root = new GameObject(name, typeof(RectTransform));
        root.transform.position = position;
        root.transform.localScale = Vector3.one * worldCanvasScale;

        RectTransform rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = size;
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasBillboard>().referenceCamera = referenceCamera;
        root.AddComponent<GraphicRaycaster>();
        return root;
    }

    GameObject CreateStretchChild(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return go;
    }

    GameObject CreateRectChild(string name, Transform parent, Vector2 size, Vector2 anchoredPosition)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredPosition;
        return go;
    }

    GameObject CreateLayoutChild(string name, Transform parent, float preferredHeight)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        LayoutElement layout = go.AddComponent<LayoutElement>();
        layout.preferredHeight = preferredHeight;
        return go;
    }

    TextMeshProUGUI CreateText(Transform parent, string name, string text, float fontSize, FontStyles fontStyle, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, fontSize * 2f);
        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = fontStyle;
        tmp.color = Color.white;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = true;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.raycastTarget = false;
        return tmp;
    }

    void StretchToParent(TextMeshProUGUI text, float horizontalPadding, float verticalPadding)
    {
        if (text == null) return;
        RectTransform rect = text.GetComponent<RectTransform>();
        if (rect == null) return;

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(horizontalPadding, verticalPadding);
        rect.offsetMax = new Vector2(-horizontalPadding, -verticalPadding);
    }

    void AddBorder(Transform parent, Vector2 size, Vector2 anchoredPosition, Color color, float thickness)
    {
        GameObject root = CreateRectChild("BlueBounds", parent, size, anchoredPosition);
        CreateBorderLine(root.transform, "Top", new Vector2(size.x, thickness), new Vector2(0f, size.y * 0.5f), color);
        CreateBorderLine(root.transform, "Bottom", new Vector2(size.x, thickness), new Vector2(0f, -size.y * 0.5f), color);
        CreateBorderLine(root.transform, "Left", new Vector2(thickness, size.y), new Vector2(-size.x * 0.5f, 0f), color);
        CreateBorderLine(root.transform, "Right", new Vector2(thickness, size.y), new Vector2(size.x * 0.5f, 0f), color);
    }

    void CreateBorderLine(Transform parent, string name, Vector2 size, Vector2 anchoredPosition, Color color)
    {
        GameObject line = CreateRectChild(name, parent, size, anchoredPosition);
        Image image = line.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    Vector3 GetWorldPosition(Vector3 offset)
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return transform.position + offset;
        return cam.transform.position
            + cam.transform.forward * distanceFromCamera
            + cam.transform.right * offset.x
            + cam.transform.up * offset.y;
    }

    string GetImageCaption(PreviewStep step)
    {
        switch (step)
        {
            case PreviewStep.Search: return "Mock server frame: Search result target";
            case PreviewStep.AskListening: return "Mock server frame: Ask target selected";
            case PreviewStep.VoiceAnswer: return "Mock server frame + final transcript";
            case PreviewStep.Compare: return "Mock server frame: two selected objects";
            case PreviewStep.Translate: return "Mock server frame: text region";
            case PreviewStep.ReadSense: return "Mock server frame: sensor panel region";
            case PreviewStep.Capture: return "Mock captured frame";
            default: return "Mock server frame";
        }
    }

    Texture2D CreateMockServerTexture()
    {
        const int width = 512;
        const int height = 320;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float t = y / (float)height;
                Color color = Color.Lerp(new Color(0.06f, 0.08f, 0.11f), new Color(0.18f, 0.21f, 0.25f), t);
                texture.SetPixel(x, y, color);
            }
        }

        FillRect(texture, 78, 72, 80, 172, new Color(0.12f, 0.62f, 0.40f));
        FillRect(texture, 95, 220, 46, 28, new Color(0.25f, 0.85f, 0.52f));
        FillRect(texture, 78, 66, 80, 10, new Color(0.05f, 0.22f, 0.16f));

        FillRect(texture, 320, 88, 116, 92, new Color(0.84f, 0.84f, 0.78f));
        FillRect(texture, 314, 176, 128, 36, new Color(0.96f, 0.86f, 0.42f));
        FillRect(texture, 330, 196, 96, 12, new Color(0.20f, 0.12f, 0.05f));

        FillRect(texture, 32, 38, 448, 14, new Color(0.32f, 0.35f, 0.36f));
        FillRect(texture, 46, 28, 128, 10, new Color(0.16f, 0.17f, 0.18f));
        FillRect(texture, 260, 28, 178, 10, new Color(0.16f, 0.17f, 0.18f));

        texture.Apply();
        texture.hideFlags = HideFlags.HideAndDontSave;
        return texture;
    }

    void FillRect(Texture2D texture, int left, int bottom, int width, int height, Color color)
    {
        for (int y = bottom; y < bottom + height; y++)
        {
            if (y < 0 || y >= texture.height) continue;
            for (int x = left; x < left + width; x++)
            {
                if (x < 0 || x >= texture.width) continue;
                texture.SetPixel(x, y, color);
            }
        }
    }
}
