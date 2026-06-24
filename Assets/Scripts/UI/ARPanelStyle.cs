using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class ARPanelStyle : MonoBehaviour
{
    [Header("Layout")]
    public ARPanelLayoutKind layoutKind = ARPanelLayoutKind.InfoCard;
    public bool applyOnAwake = true;
    public bool resizeCanvas = true;
    public bool applyWorldScale = false;
    public float worldScale = 0.001f;

    [Header("Colors")]
    public Color backgroundColor = new Color(0.20f, 0.45f, 1.0f, 0.55f);
    public Color borderColor = new Color(0.72f, 0.88f, 1.0f, 0.78f);
    public Color textColor = Color.white;
    public Color secondaryTextColor = new Color(0.92f, 0.97f, 1.0f, 0.92f);
    public Color buttonColor = new Color(0.25f, 0.50f, 1.0f, 0.88f);

    [Header("Typography")]
    public float titleFontSize = 26f;
    public float bodyFontSize = 17f;
    public float compactFontSize = 15f;

    [Header("Panel sizes")]
    public Vector2 infoSize = new Vector2(380f, 160f);
    public Vector2 askSize = new Vector2(430f, 150f);
    public Vector2 answerSize = new Vector2(500f, 240f);
    public Vector2 compareSize = new Vector2(440f, 190f);
    public Vector2 translationSize = new Vector2(380f, 150f);
    public Vector2 statusSize = new Vector2(320f, 95f);
    public Vector2 sensorSize = new Vector2(350f, 190f);

    private static Sprite _roundedSprite;

    void Awake()
    {
        if (applyOnAwake) Apply();
    }

    public static ARPanelStyle ApplyTo(GameObject root, ARPanelLayoutKind kind)
    {
        if (root == null) return null;
        ARPanelStyle style = root.GetComponent<ARPanelStyle>();
        if (style == null) style = root.AddComponent<ARPanelStyle>();
        style.layoutKind = kind;
        style.Apply();
        return style;
    }

    public void Apply()
    {
        Canvas canvas = GetComponentInChildren<Canvas>(true);
        if (canvas != null)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            if (canvasRect != null && resizeCanvas)
                canvasRect.sizeDelta = GetSize(layoutKind);
        }

        if (applyWorldScale)
            transform.localScale = Vector3.one * worldScale;

        EnsureBillboard();

        Image panelImage = FindPanelImage();
        if (panelImage != null)
        {
            panelImage.color = backgroundColor;
            panelImage.raycastTarget = false;
            panelImage.sprite = GetRoundedSprite();
            panelImage.type = Image.Type.Sliced;
            panelImage.pixelsPerUnitMultiplier = 1f;

            Outline outline = panelImage.GetComponent<Outline>();
            if (outline == null) outline = panelImage.gameObject.AddComponent<Outline>();
            outline.effectColor = borderColor;
            outline.effectDistance = new Vector2(2f, -2f);
            outline.useGraphicAlpha = true;
        }

        ApplyLayoutPadding();
        ApplyTextStyle();
        ApplyButtonStyle();
    }

    Vector2 GetSize(ARPanelLayoutKind kind)
    {
        switch (kind)
        {
            case ARPanelLayoutKind.AskCard: return askSize;
            case ARPanelLayoutKind.AnswerCard: return answerSize;
            case ARPanelLayoutKind.CompareCard: return compareSize;
            case ARPanelLayoutKind.TranslationCard: return translationSize;
            case ARPanelLayoutKind.StatusCard:
            case ARPanelLayoutKind.NoteCard:
            case ARPanelLayoutKind.AnchorCard:
            case ARPanelLayoutKind.ControlCard:
                return statusSize;
            case ARPanelLayoutKind.SensorCard: return sensorSize;
            default: return infoSize;
        }
    }

    Image FindPanelImage()
    {
        Image[] images = GetComponentsInChildren<Image>(true);
        Image fallback = null;
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i] == null) continue;
            if (images[i].gameObject.name == "Panel") return images[i];
            if (fallback == null && !images[i].raycastTarget) fallback = images[i];
        }
        return fallback;
    }

    void EnsureBillboard()
    {
        CanvasBillboard billboard = GetComponent<CanvasBillboard>();
        if (billboard == null) billboard = gameObject.AddComponent<CanvasBillboard>();

        if (billboard.referenceCamera == null)
            billboard.referenceCamera = Camera.main;
    }

    void ApplyLayoutPadding()
    {
        VerticalLayoutGroup[] verticalGroups = GetComponentsInChildren<VerticalLayoutGroup>(true);
        for (int i = 0; i < verticalGroups.Length; i++)
        {
            VerticalLayoutGroup group = verticalGroups[i];
            group.padding = new RectOffset(18, 18, 14, 14);
            group.spacing = layoutKind == ARPanelLayoutKind.StatusCard ? 4f : 8f;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
        }

        HorizontalLayoutGroup[] horizontalGroups = GetComponentsInChildren<HorizontalLayoutGroup>(true);
        for (int i = 0; i < horizontalGroups.Length; i++)
        {
            HorizontalLayoutGroup group = horizontalGroups[i];
            group.padding = new RectOffset(0, 0, 0, 0);
            group.spacing = 8f;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
        }
    }

    void ApplyTextStyle()
    {
        TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            TMP_Text text = texts[i];
            if (text == null) continue;

            string name = text.gameObject.name;
            bool title = name.Contains("Title") || name.Contains("AskText");
            bool secondary = name.Contains("Question") || name.Contains("Status") || name.Contains("Lang");

            text.color = secondary ? secondaryTextColor : textColor;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.fontSize = title ? titleFontSize : (secondary ? compactFontSize : bodyFontSize);
            text.fontStyle = title ? FontStyles.Normal : text.fontStyle;
            text.alignment = title ? TextAlignmentOptions.Left : text.alignment;
        }
    }

    void ApplyButtonStyle()
    {
        Button[] buttons = GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (button == null) continue;

            Image image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = buttonColor;
                image.sprite = GetRoundedSprite();
                image.type = Image.Type.Sliced;
            }

            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.color = textColor;
                label.fontSize = compactFontSize;
            }
        }
    }

    static Sprite GetRoundedSprite()
    {
        if (_roundedSprite != null) return _roundedSprite;

        const int size = 64;
        const int radius = 14;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.hideFlags = HideFlags.HideAndDontSave;

        Color clear = new Color(1f, 1f, 1f, 0f);
        Color white = Color.white;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside =
                    IsInsideCorner(x, y, radius, radius, radius) &&
                    IsInsideCorner(size - 1 - x, y, radius, radius, radius) &&
                    IsInsideCorner(x, size - 1 - y, radius, radius, radius) &&
                    IsInsideCorner(size - 1 - x, size - 1 - y, radius, radius, radius);
                texture.SetPixel(x, y, inside ? white : clear);
            }
        }
        texture.Apply();

        _roundedSprite = Sprite.Create(
            texture,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius)
        );
        _roundedSprite.hideFlags = HideFlags.HideAndDontSave;
        return _roundedSprite;
    }

    static bool IsInsideCorner(int x, int y, int radius, int cornerX, int cornerY)
    {
        if (x >= radius || y >= radius) return true;
        float dx = x - cornerX + 0.5f;
        float dy = y - cornerY + 0.5f;
        return dx * dx + dy * dy <= radius * radius;
    }
}
