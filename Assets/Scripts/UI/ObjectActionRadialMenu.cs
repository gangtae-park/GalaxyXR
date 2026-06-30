using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum ObjectActionMenuAction
{
    Search,
    Ask,
    Translate,
    Compare,
    Anchor,
    Save,
    Capture,
    // Cancel is no longer a wedge -- re-clicking the same bubble closes the menu.
    // The enum value is kept so legacy code that compares against it still compiles.
    Cancel
}

public class ObjectActionRadialMenu : MonoBehaviour
{
    public static readonly ObjectActionMenuAction[] Actions =
    {
        ObjectActionMenuAction.Search,
        ObjectActionMenuAction.Ask,
        ObjectActionMenuAction.Translate,
        ObjectActionMenuAction.Compare,
        ObjectActionMenuAction.Anchor,
        ObjectActionMenuAction.Save,
        ObjectActionMenuAction.Capture
    };

    public static readonly string[] Labels = { "Search", "Ask", "Translate", "Compare", "Anchor", "Save", "Capture" };

    [Header("Style")]
    public float radius = 180f;
    public float innerRadius = 54f;
    // Semi-transparent black to match the other card prefabs.
    public Color segmentColor = new Color(0.04f, 0.04f, 0.06f, 0.78f);
    public Color segmentHoverColor = new Color(0.10f, 0.10f, 0.14f, 0.92f);
    public Color segmentPressedColor = new Color(0.00f, 0.00f, 0.00f, 0.96f);
    public Color dividerColor = new Color(0.85f, 0.88f, 0.95f, 0.55f);
    public Color labelColor = Color.white;
    public float labelRadius = 118f;
    public string requestIdForLogs = "";

    // Optional Pretendard (or any Korean-glyph) font asset. Assigned by the
    // spawner so the wedge labels render Korean correctly when the menu also
    // shows the matched DB name. When null, falls back to TMP defaults.
    public TMPro.TMP_FontAsset fontAsset;

    public event Action<ObjectActionMenuAction> OnActionClicked;

    public void Build()
    {
        ClearChildren();

        RectTransform rect = GetComponent<RectTransform>();
        if (rect != null) rect.sizeDelta = new Vector2(radius * 2f, radius * 2f);

        const float fullCircle = 360f;
        float sweep = fullCircle / Actions.Length;

        for (int i = 0; i < Actions.Length; i++)
        {
            float startAngle = i * sweep;
            CreateSegment(i, startAngle, sweep);
            // RadialMenuSegmentGraphic draws clockwise from startAngle (a0 = startAngle - step*i),
            // so the visual centre of wedge i is at startAngle - sweep/2, NOT +sweep/2.
            // The old +sweep/2 placed each label on top of the *next* wedge, which is why every
            // click fired the action one slot over.
            CreateLabel(i, startAngle - sweep * 0.5f);
            Debug.Log($"[RADIAL_UI] Segment[{i}] angle_start={startAngle:F3} angle_size={sweep:F3} label={Labels[i]}");
        }

        CreateCenterDot();
        Debug.Log($"[RADIAL_UI] Segment count={Actions.Length} request_id={requestIdForLogs}");
    }

    void CreateSegment(int index, float startAngle, float sweepAngle)
    {
        GameObject go = new GameObject("Segment_" + Labels[index], typeof(RectTransform));
        go.transform.SetParent(transform, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        RadialMenuSegmentGraphic graphic = go.AddComponent<RadialMenuSegmentGraphic>();
        graphic.startAngle = startAngle;
        graphic.sweepAngle = sweepAngle;
        graphic.innerRadius = innerRadius;
        graphic.outerRadius = radius;
        graphic.color = segmentColor;

        Button button = go.AddComponent<Button>();
        button.targetGraphic = graphic;
        ColorBlock colors = button.colors;
        colors.normalColor = segmentColor;
        colors.highlightedColor = segmentHoverColor;
        colors.pressedColor = segmentPressedColor;
        colors.selectedColor = segmentHoverColor;
        colors.disabledColor = new Color(segmentColor.r, segmentColor.g, segmentColor.b, 0.12f);
        button.colors = colors;

        ObjectActionMenuAction action = Actions[index];
        button.onClick.AddListener(() =>
        {
            Debug.Log($"[ObjectActionMenu] action button clicked action={action}");
            OnActionClicked?.Invoke(action);
        });
    }

    void CreateLabel(int index, float angleDegrees)
    {
        GameObject go = new GameObject("Label_" + Labels[index], typeof(RectTransform));
        go.transform.SetParent(transform, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(92f, 30f);

        float rad = angleDegrees * Mathf.Deg2Rad;
        rect.anchoredPosition = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * labelRadius;

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) text.font = fontAsset;
        text.text = Labels[index];
        text.fontSize = 15f;
        text.fontStyle = FontStyles.Bold;
        text.color = labelColor;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
    }

    void CreateCenterDot()
    {
        GameObject go = new GameObject("Center", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(innerRadius * 1.45f, innerRadius * 1.45f);

        Image image = go.AddComponent<Image>();
        image.color = new Color(0.02f, 0.02f, 0.03f, 0.55f);
        image.raycastTarget = false;
    }

    void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);
    }
}
