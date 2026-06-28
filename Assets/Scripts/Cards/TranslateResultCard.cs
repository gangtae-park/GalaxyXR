using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
TranslateResultCard

Card prefab component for the Translate referent. Two-stage display:
  - SetOcrOnly(text)               -> stage 1, after Jackknife recognition
                                      and Python OCR. Shows the source text and
                                      a "translating..." placeholder.
  - SetTranslation(src, ko)        -> stage 2, after the user's confirming
                                      palm-forward swipe and Python GPT call.

30 s auto-destroy that resets on each Set* call, plus a Close button.
*/

public class TranslateResultCard : MonoBehaviour
{
    [Header("UI")]
    [Tooltip("Original (OCR) text. Optional -- omit if you only want to show the translation.")]
    public TMP_Text sourceText;
    [Tooltip("Korean translation. Shows the translating placeholder between stages 1 and 2.")]
    public TMP_Text bodyText;
    public Button closeButton;

    [Header("Lifetime")]
    public float autoDestroySeconds = 30f;
    [Tooltip("Text shown in bodyText after OCR but before the GPT translation arrives.")]
    public string translatingPlaceholder = "번역 중...";

    private float _destroyAt;

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

    /// <summary>Stage 1 -- show OCR'd source text and a "translating" placeholder.</summary>
    public void SetOcrOnly(string originalText)
    {
        if (sourceText != null) sourceText.text = originalText ?? "";
        if (bodyText   != null) bodyText.text = translatingPlaceholder;
        _destroyAt = Time.time + autoDestroySeconds;
    }

    /// <summary>Stage 2 -- replace the placeholder with the final translation.</summary>
    public void SetTranslation(string originalText, string koreanTranslation)
    {
        if (sourceText != null) sourceText.text = originalText ?? "";
        if (bodyText != null)
            bodyText.text = string.IsNullOrEmpty(koreanTranslation)
                ? "(no translation)"
                : koreanTranslation;
        _destroyAt = Time.time + autoDestroySeconds;
    }

    /// <summary>Legacy single-arg shim for callers that only have the translation.</summary>
    public void SetContent(string koreanTranslation) => SetTranslation("", koreanTranslation);

    public void Close()
    {
        Destroy(gameObject);
    }
}
