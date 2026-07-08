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
    }

    public void SetTranslation(string originalText, string koreanTranslation)
    {
        if (sourceText != null) sourceText.text = originalText ?? "";
        if (bodyText != null)
            bodyText.text = string.IsNullOrEmpty(koreanTranslation)
                ? "(no translation)"
                : koreanTranslation;
        _destroyAt = Time.time + autoDestroySeconds;
    }

    public void SetContent(string koreanTranslation) => SetTranslation("", koreanTranslation);

    public void Close()
    {
        Destroy(gameObject);
    }
}
