using TMPro;
using UnityEngine;

/*
Head-locked text for the pilot study (DataCollectionScene): the 3-2-1
countdown, "GO!" and "Saved!" messages float directly in front of the user's
eyes instead of living on the world-space control Canvas.

Everything is created at runtime -- add this component to any GameObject
(e.g. next to PilotStudyController) and assign it in the controller; no scene
canvas wiring needed. Optionally assign `font` (e.g. a Pretendard SDF asset),
otherwise the TMP default is used.

Behaviour:
    - Follows the head with a soft lerp (hard-snaps when it (re)appears so it
      never sweeps across the view from its previous spot).
    - Show()/Hide() fade the text over fadeSeconds; while fully faded out the
      canvas is disabled entirely so nothing obstructs the gesture window.
*/

public class PilotHudText : MonoBehaviour
{
    [Header("Optional refs (auto-created when empty)")]
    public TMP_FontAsset font;

    [Header("Placement")]
    [Tooltip("Meters in front of the eyes.")]
    public float distance = 1.4f;
    [Tooltip("Vertical offset in meters at `distance`. Slightly negative keeps the text just under the natural gaze line.")]
    public float verticalOffset = -0.08f;
    [Tooltip("Follow smoothing. Higher = snappier; 0 = hard head-lock.")]
    public float followLerp = 10f;

    [Header("Look")]
    public float fontSize = 150f;
    public float fadeSeconds = 0.15f;

    private Canvas _canvas;
    private CanvasGroup _group;
    private TMP_Text _text;
    private float _targetAlpha;

    public bool IsVisible => _targetAlpha > 0f;

    public void Show(string message, Color color)
    {
        EnsureCreated();
        bool wasHidden = _group.alpha <= 0.001f;
        _text.text = message;
        _text.color = color;
        _targetAlpha = 1f;
        _canvas.gameObject.SetActive(true);
        if (wasHidden) SnapToHead();
    }

    public void Hide()
    {
        _targetAlpha = 0f;
    }

    void LateUpdate()
    {
        if (_canvas == null || !_canvas.gameObject.activeSelf) return;

        _group.alpha = fadeSeconds <= 0f
            ? _targetAlpha
            : Mathf.MoveTowards(_group.alpha, _targetAlpha, Time.deltaTime / fadeSeconds);

        if (_targetAlpha <= 0f && _group.alpha <= 0.001f)
        {
            _canvas.gameObject.SetActive(false);
            return;
        }

        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null) return;

        Vector3 targetPos = TargetPosition(cam);
        Transform t = _canvas.transform;
        t.position = followLerp <= 0f
            ? targetPos
            : Vector3.Lerp(t.position, targetPos, 1f - Mathf.Exp(-followLerp * Time.deltaTime));
        t.rotation = Quaternion.LookRotation(t.position - cam.position);
    }

    Vector3 TargetPosition(Transform cam)
    {
        return cam.position + cam.forward * distance + cam.up * verticalOffset;
    }

    void SnapToHead()
    {
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null) return;
        _canvas.transform.position = TargetPosition(cam);
        _canvas.transform.rotation = Quaternion.LookRotation(_canvas.transform.position - cam.position);
    }

    void EnsureCreated()
    {
        if (_canvas != null) return;

        var go = new GameObject("PilotHudCanvas");
        go.transform.SetParent(null, false);
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;

        var rt = _canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(800f, 400f);
        rt.localScale = Vector3.one * 0.002f;   // 800px -> 1.6 m wide at world scale

        _group = go.AddComponent<CanvasGroup>();
        _group.interactable = false;
        _group.blocksRaycasts = false;
        _group.alpha = 0f;
        _targetAlpha = 0f;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = fontSize;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;
        if (font != null) tmp.font = font;
        _text = tmp;

        go.SetActive(false);
    }

    void OnDestroy()
    {
        if (_canvas != null) Destroy(_canvas.gameObject);
    }
}
