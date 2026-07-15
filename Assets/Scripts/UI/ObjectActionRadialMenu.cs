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
    public Color segmentHoverColor = new Color(0.22f, 0.27f, 0.42f, 0.95f);
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

    // One pinch can fire both the gaze-pinch path and a Button click; only
    // the first within the window is routed.
    float _lastActionTime = -999f;
    const float ActionDebounceSeconds = 0.4f;

    bool ConsumeActionGate()
    {
        if (Time.unscaledTime - _lastActionTime < ActionDebounceSeconds) return false;
        _lastActionTime = Time.unscaledTime;
        return true;
    }

    /// <summary>Geometric wedge hit-test for gaze-pinch selection: `local` is
    /// a point in this canvas's LOCAL units (radius/innerRadius space, i.e.
    /// transform.InverseTransformPoint of a world point on the menu plane).
    /// Mirrors RadialMenuSegmentGraphic's clockwise layout: wedge i spans
    /// ((i-1)*sweep, i*sweep] with its centre at i*sweep - sweep/2.</summary>
    public bool TryGetActionAtLocalPoint(Vector2 local, out ObjectActionMenuAction action)
    {
        action = ObjectActionMenuAction.Cancel;
        if (!TryGetIndexAtLocalPoint(local, out int index)) return false;
        action = Actions[index];
        return true;
    }

    public bool TryGetIndexAtLocalPoint(Vector2 local, out int index)
    {
        index = -1;
        float r = local.magnitude;
        if (r < innerRadius || r > radius * 1.15f) return false;

        float sweep = 360f / Actions.Length;
        float theta = Mathf.Repeat(Mathf.Atan2(local.y, local.x) * Mathf.Rad2Deg, 360f);
        index = Mathf.CeilToInt(theta / sweep) % Actions.Length;
        return true;
    }

    // -------- Gaze hover highlight --------
    // The Button ColorBlock only reacts to EventSystem pointers (hand ray);
    // the gaze path bypasses it, so the hovered wedge is tinted directly on
    // its graphic. Both feedbacks can coexist (canvasRenderer tint multiplies
    // over graphic.color).
    RadialMenuSegmentGraphic[] _segmentGraphics;
    int _gazeHoverIndex = -1;

    public void SetGazeHoverIndex(int index)
    {
        if (_segmentGraphics == null || index == _gazeHoverIndex) return;
        for (int i = 0; i < _segmentGraphics.Length; i++)
        {
            if (_segmentGraphics[i] == null) continue;
            _segmentGraphics[i].color = i == index ? segmentHoverColor : segmentColor;
        }
        _gazeHoverIndex = index;
    }

    /// <summary>Programmatic wedge activation (gaze-pinch path). Fires the
    /// same OnActionClicked event a Button click would.</summary>
    public void ClickAction(ObjectActionMenuAction action)
    {
        if (!ConsumeActionGate())
        {
            Debug.Log($"[ObjectActionMenu] duplicate gaze-pinch action ignored action={action}");
            return;
        }
        Debug.Log($"[ObjectActionMenu] action selected via gaze-pinch action={action}");
        OnActionClicked?.Invoke(action);
    }

    public void Build()
    {
        ClearChildren();
        _segmentGraphics = new RadialMenuSegmentGraphic[Actions.Length];
        _gazeHoverIndex = -1;

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
        _segmentGraphics[index] = graphic;

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
            if (!ConsumeActionGate())
            {
                Debug.Log($"[ObjectActionMenu] duplicate button click ignored action={action}");
                return;
            }
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
