using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum ObjectActionMenuAction
{
    Search,
    Ask,
    Compare,
    Translate,
    Summarize,
    Details,
    Cancel
}

public class ObjectActionRadialMenu : MonoBehaviour
{
    public static readonly ObjectActionMenuAction[] Actions =
    {
        ObjectActionMenuAction.Search,
        ObjectActionMenuAction.Ask,
        ObjectActionMenuAction.Compare,
        ObjectActionMenuAction.Translate,
        ObjectActionMenuAction.Summarize,
        ObjectActionMenuAction.Details,
        ObjectActionMenuAction.Cancel
    };

    public static readonly string[] Labels = { "Search", "Ask", "Compare", "Translate", "Summarize", "Details", "Cancel" };

    [Header("Style")]
    public float radius = 180f;
    public float innerRadius = 54f;
    public Color segmentColor = new Color(0.20f, 0.45f, 1.0f, 0.34f);
    public Color segmentHoverColor = new Color(0.28f, 0.58f, 1.0f, 0.58f);
    public Color segmentPressedColor = new Color(0.12f, 0.32f, 0.95f, 0.72f);
    public Color dividerColor = new Color(0.72f, 0.88f, 1.0f, 0.72f);
    public Color labelColor = Color.white;
    public float labelRadius = 118f;
    public string requestIdForLogs = "";

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
            CreateLabel(i, startAngle + sweep * 0.5f);
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
        image.color = new Color(0.03f, 0.08f, 0.16f, 0.40f);
        image.raycastTarget = false;
    }

    void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);
    }
}
