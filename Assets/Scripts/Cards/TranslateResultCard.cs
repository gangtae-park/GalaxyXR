using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;


public class TranslateResultCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text sourceText;
    public TMP_Text bodyText;
    public Button closeButton;

    [Header("Lifetime")]
    public float autoDestroySeconds = 30f;
    public string translatingPlaceholder = "번역 중...";

    private float _destroyAt;
    private StringBuilder _streamingBody;

    void Awake()
    {
        _destroyAt = Time.time + autoDestroySeconds;
        if (closeButton != null) closeButton.onClick.AddListener(Close);
    }

    void OnDestroy()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(Close);
    }

    void Update()
    {
        if (Time.time >= _destroyAt) Destroy(gameObject);
    }

    public void SetOcrOnly(string originalText)
    {
        if (sourceText != null) sourceText.text = originalText ?? "";
        if (bodyText   != null) bodyText.text = translatingPlaceholder;
        _destroyAt = Time.time + autoDestroySeconds;
        _streamingBody = null;
    }

    public void SetTranslation(string originalText, string koreanTranslation)
    {
        if (sourceText != null) sourceText.text = originalText ?? "";
        if (bodyText != null)
            bodyText.text = string.IsNullOrEmpty(koreanTranslation)
                ? "(no translation)"
                : koreanTranslation;
        _destroyAt = Time.time + autoDestroySeconds;
        _streamingBody = null;
    }

    public void SetContent(string koreanTranslation) => SetTranslation("", koreanTranslation);

    // Streaming lifecycle. The OCR-stage payload already spawned the card via
    // SetOcrOnly (source text + placeholder body). BeginStreamingTranslation()
    // clears the placeholder so deltas visibly grow.
    public void BeginStreamingTranslation(string originalText = null)
    {
        if (!string.IsNullOrEmpty(originalText) && sourceText != null)
            sourceText.text = originalText;
        _streamingBody = new StringBuilder(256);
        if (bodyText != null) bodyText.text = "";
        _destroyAt = Time.time + autoDestroySeconds;
    }

    public void AppendTranslationDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        if (_streamingBody == null) _streamingBody = new StringBuilder(256);
        _streamingBody.Append(delta);
        if (bodyText != null) bodyText.text = _streamingBody.ToString();
        _destroyAt = Time.time + autoDestroySeconds;
    }

    public void EndStreamingTranslation(string finalTranslation = null)
    {
        string shown =
            !string.IsNullOrEmpty(finalTranslation) ? finalTranslation
            : (_streamingBody != null ? _streamingBody.ToString() : "");
        if (bodyText != null)
            bodyText.text = string.IsNullOrEmpty(shown) ? "(no translation)" : shown;
        _streamingBody = null;
        _destroyAt = Time.time + autoDestroySeconds;
    }

    public void Close()
    {
        Destroy(gameObject);
    }
}
