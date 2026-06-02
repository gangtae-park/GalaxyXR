using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lightweight world-space note card. It can be used from a prefab, but also
/// builds a complete note UI at runtime so the gesture can work before art/UI
/// prefabs exist.
/// </summary>
public class StoreNoteCard : MonoBehaviour
{
    public TMP_Text titleText;
    public TMP_InputField noteInput;
    public Button saveButton;
    public Button closeButton;

    [Header("Behavior")]
    public bool billboard = true;
    public Camera billboardCamera;
    public string defaultTitle = "Store note";
    public string placeholderText = "Write a note...";

    public string SavedText { get; private set; }

    bool _builtRuntimeUi;

    public void Initialize(Camera camera, string title)
    {
        billboardCamera = camera;

        if (titleText == null || noteInput == null || saveButton == null || closeButton == null)
        {
            BuildRuntimeUi();
        }

        if (titleText != null) titleText.text = string.IsNullOrEmpty(title) ? defaultTitle : title;
        if (noteInput != null)
        {
            noteInput.text = "";
            noteInput.Select();
            noteInput.ActivateInputField();
        }
    }

    void BuildRuntimeUi()
    {
        if (_builtRuntimeUi) return;
        _builtRuntimeUi = true;

        Canvas canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 50;

        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 20f;

        gameObject.AddComponent<GraphicRaycaster>();

        RectTransform root = gameObject.GetComponent<RectTransform>();
        if (root == null) root = gameObject.AddComponent<RectTransform>();
        root.sizeDelta = new Vector2(420f, 300f);
        root.localScale = Vector3.one * 0.0015f;

        Image background = gameObject.AddComponent<Image>();
        background.color = new Color(0.08f, 0.1f, 0.12f, 0.94f);

        titleText = CreateText("Title", root, new Vector2(24f, -22f), new Vector2(300f, 44f), 28f, FontStyles.Bold);
        titleText.alignment = TextAlignmentOptions.Left;

        closeButton = CreateButton("CloseButton", root, "X", new Vector2(-20f, -18f), new Vector2(44f, 44f), Close);
        RectTransform closeRect = closeButton.GetComponent<RectTransform>();
        closeRect.anchorMin = new Vector2(1f, 1f);
        closeRect.anchorMax = new Vector2(1f, 1f);
        closeRect.pivot = new Vector2(1f, 1f);

        RectTransform inputRoot = CreateRect("InputRoot", root, new Vector2(24f, -82f), new Vector2(372f, 136f));
        Image inputBg = inputRoot.gameObject.AddComponent<Image>();
        inputBg.color = new Color(1f, 1f, 1f, 0.92f);

        noteInput = inputRoot.gameObject.AddComponent<TMP_InputField>();
        noteInput.lineType = TMP_InputField.LineType.MultiLineNewline;
        noteInput.targetGraphic = inputBg;
        noteInput.textViewport = inputRoot;

        TMP_Text inputText = CreateText("Text", inputRoot, new Vector2(14f, -10f), new Vector2(344f, 108f), 22f, FontStyles.Normal);
        inputText.color = new Color(0.06f, 0.07f, 0.08f, 1f);
        inputText.alignment = TextAlignmentOptions.TopLeft;
        noteInput.textComponent = inputText;

        TMP_Text placeholder = CreateText("Placeholder", inputRoot, new Vector2(14f, -10f), new Vector2(344f, 108f), 22f, FontStyles.Italic);
        placeholder.color = new Color(0.25f, 0.28f, 0.32f, 0.55f);
        placeholder.text = placeholderText;
        placeholder.alignment = TextAlignmentOptions.TopLeft;
        noteInput.placeholder = placeholder;

        saveButton = CreateButton("SaveButton", root, "Save", new Vector2(-86f, 32f), new Vector2(120f, 48f), Save);
    }

    static RectTransform CreateRect(string name, RectTransform parent, Vector2 anchoredPosition, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        return rect;
    }

    static TMP_Text CreateText(string name, RectTransform parent, Vector2 anchoredPosition, Vector2 size, float fontSize, FontStyles style)
    {
        RectTransform rect = CreateRect(name, parent, anchoredPosition, size);
        TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = Color.white;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    static Button CreateButton(string name, RectTransform parent, string label, Vector2 anchoredPosition, Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        RectTransform rect = CreateRect(name, parent, anchoredPosition, size);
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);

        Image image = rect.gameObject.AddComponent<Image>();
        image.color = new Color(0.18f, 0.48f, 0.95f, 1f);

        Button button = rect.gameObject.AddComponent<Button>();
        button.onClick.AddListener(onClick);

        TMP_Text text = CreateText("Label", rect, new Vector2(0f, 0f), size, 20f, FontStyles.Bold);
        RectTransform textRect = text.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.pivot = new Vector2(0.5f, 0.5f);
        textRect.anchoredPosition = Vector2.zero;
        textRect.sizeDelta = Vector2.zero;
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;

        return button;
    }

    public void Save()
    {
        SavedText = noteInput != null ? noteInput.text : "";
        if (titleText != null) titleText.text = string.IsNullOrWhiteSpace(SavedText) ? "Saved empty note" : "Saved";
        Debug.Log($"[StoreNoteCard] Saved note: {SavedText}");
    }

    public void Close()
    {
        Destroy(gameObject);
    }

    void LateUpdate()
    {
        if (!billboard) return;

        Camera cam = billboardCamera != null ? billboardCamera : Camera.main;
        if (cam == null) return;

        Vector3 toCam = transform.position - cam.transform.position;
        if (toCam.sqrMagnitude > 0.000001f)
        {
            transform.rotation = Quaternion.LookRotation(toCam, cam.transform.up);
        }
    }
}
