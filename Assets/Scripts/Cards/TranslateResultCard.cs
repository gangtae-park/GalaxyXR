using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
TranslateResultCard

Card prefab component for the Translate referent. Same lifetime semantics as
SearchResultCard / AskResultCard (30-second auto-destroy that keeps ticking
regardless of grab / move, plus a Close button that destroys immediately) but
with a single body text field for the Korean translation -- no title.
*/

public class TranslateResultCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text bodyText;
    public Button closeButton;

    [Header("Lifetime")]
    public float autoDestroySeconds = 30f;

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

    public void SetContent(string koreanTranslation)
    {
        if (bodyText != null)
            bodyText.text = string.IsNullOrEmpty(koreanTranslation)
                ? "(no translation)"
                : koreanTranslation;
    }

    public void Close()
    {
        Destroy(gameObject);
    }
}
