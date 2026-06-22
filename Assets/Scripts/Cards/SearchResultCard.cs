using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
SearchResultCard

Attached to the root of the SearchResultCard prefab. Responsibilities:
  - Populate TitleText / BodyText via SetContent(displayName, searchResult).
  - Wire CloseButton.onClick to destroy the card.
  - Auto-destroy after `autoDestroySeconds` (default 30s). The timer is real
    wall-clock time -- it keeps ticking regardless of the card being grabbed,
    moved, or reparented by an XR interactor.
*/

public class SearchResultCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text titleText;
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

    public void SetContent(string displayName, string resultSearch)
    {
        if (titleText != null)
            titleText.text = string.IsNullOrEmpty(displayName) ? "Unknown" : displayName;
        if (bodyText != null)
            bodyText.text = string.IsNullOrEmpty(resultSearch) ? "(no result)" : resultSearch;
    }

    /// <summary>Wired to CloseButton.OnClick by Awake. Also callable externally.</summary>
    public void Close()
    {
        Destroy(gameObject);
    }
}
