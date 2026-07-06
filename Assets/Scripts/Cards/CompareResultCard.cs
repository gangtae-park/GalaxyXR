using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
CompareResultCard

Drives the CompareResultCard prefab:

  CompareResultCard           <- canvasRect (height grows with row count)
   └ Panel                    <- rowsContainer (VerticalLayoutGroup parent)
      ├ TitleRow
      │   ├ TitleText_1       <- titleText_1   (object 1's name)
      │   ├ vs
      │   └ TitleText_2       <- titleText_2   (object 2's name)
      ├ EmptyRow              (a small spacer; stays in place)
      └ CategoryRow           <- categoryRowTemplate  (CLONED per category)
          ├ Category          <- row.categoryText
          ├ BodyText_1        <- row.bodyText_1
          └ BodyText_2        <- row.bodyText_2

The template `CategoryRow` is reused as the FIRST visible row and cloned
(N - 1) times for subsequent rows. The card root's height is recalculated to
fit (auto-adjust on row count).
*/

public class CompareResultCard : MonoBehaviour
{
    [Header("Title")]
    public TMP_Text titleText_1;
    public TMP_Text titleText_2;

    [Header("Rows")]
    [Tooltip("The CategoryRow already inside the prefab. Used as the first row AND as the template for additional rows.")]
    public CategoryRowRefs categoryRowTemplate;
    [Tooltip("Container that holds the rows (typically Panel). If empty, uses the template's parent at runtime.")]
    public RectTransform rowsContainer;

    [Header("Close")]
    public Button closeButton;

    [Header("Sizing")]
    [Tooltip("Root RectTransform whose height is recomputed when rows change. If empty, uses this GameObject's RectTransform.")]
    public RectTransform canvasRect;
    [Tooltip("Extra height (m or px in the canvas's units) added per row beyond the first. Use this to fine-tune in inspector if rows look cramped or too spaced.")]
    public float extraHeightPerRow = 0f;

    [Header("Lifetime")]
    public float autoDestroySeconds = 45f;

    [System.Serializable]
    public class CategoryRowRefs
    {
        public GameObject root;       // the CategoryRow GameObject itself
        public TMP_Text categoryText; // Category
        public TMP_Text bodyText_1;   // BodyText_1
        public TMP_Text bodyText_2;   // BodyText_2
    }

    readonly List<GameObject> _spawnedRows = new List<GameObject>();
    float _baseHeight;     // height before any rows are added beyond the template
    float _templateRowHeight;
    float _layoutSpacing;
    bool _measured;

    float _destroyAt;

    void Awake()
    {
        _destroyAt = Time.time + autoDestroySeconds;
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (canvasRect == null) canvasRect = GetComponent<RectTransform>();
    }

    void OnDestroy()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(Close);
    }

    void Update()
    {
        if (autoDestroySeconds > 0f && Time.time >= _destroyAt) Destroy(gameObject);
    }

    public void Close() { Destroy(gameObject); }

    /// <summary>Fill the card. Pass the two object names and one entry per
    /// category. Resizes the card height to fit the number of rows.</summary>
    public void SetContent(string nameA, string nameB, IList<VlmResultReceiver.CompareRow> rows)
    {
        if (titleText_1 != null) titleText_1.text = string.IsNullOrEmpty(nameA) ? "" : nameA;
        if (titleText_2 != null) titleText_2.text = string.IsNullOrEmpty(nameB) ? "" : nameB;

        ResolveContainer();
        EnsureMeasured();
        ClearSpawned();

        if (categoryRowTemplate == null || categoryRowTemplate.root == null)
        {
            Debug.LogWarning("[CompareResultCard] categoryRowTemplate not assigned; cannot lay out rows.");
            return;
        }

        int n = rows != null ? rows.Count : 0;
        if (n <= 0)
        {
            categoryRowTemplate.root.SetActive(false);
            ApplyHeight(0);
            return;
        }

        // Reuse the template as row 0, then clone for rows 1..N-1.
        FillRow(categoryRowTemplate.root, categoryRowTemplate, rows[0]);
        categoryRowTemplate.root.SetActive(true);

        for (int i = 1; i < n; i++)
        {
            GameObject clone = Instantiate(categoryRowTemplate.root, rowsContainer);
            clone.name = $"CategoryRow_{i}";
            CategoryRowRefs cloneRefs = BindRowFromClone(clone);
            FillRow(clone, cloneRefs, rows[i]);
            clone.SetActive(true);
            _spawnedRows.Add(clone);
        }

        ApplyHeight(n);
    }

    void ResolveContainer()
    {
        if (rowsContainer == null && categoryRowTemplate != null && categoryRowTemplate.root != null)
            rowsContainer = categoryRowTemplate.root.transform.parent as RectTransform;
    }

    void EnsureMeasured()
    {
        if (_measured) return;
        if (canvasRect != null) _baseHeight = canvasRect.rect.height;
        if (categoryRowTemplate != null && categoryRowTemplate.root != null)
        {
            RectTransform rt = categoryRowTemplate.root.transform as RectTransform;
            if (rt != null) _templateRowHeight = rt.rect.height;
        }
        if (rowsContainer != null)
        {
            VerticalLayoutGroup vlg = rowsContainer.GetComponent<VerticalLayoutGroup>();
            if (vlg != null) _layoutSpacing = vlg.spacing;
        }
        _measured = true;
    }

    void ClearSpawned()
    {
        for (int i = 0; i < _spawnedRows.Count; i++)
            if (_spawnedRows[i] != null) Destroy(_spawnedRows[i]);
        _spawnedRows.Clear();
    }

    static void FillRow(GameObject rowRoot, CategoryRowRefs refs, VlmResultReceiver.CompareRow data)
    {
        if (refs == null || data == null) return;
        if (refs.categoryText != null) refs.categoryText.text = data.category ?? "";
        if (refs.bodyText_1   != null) refs.bodyText_1.text   = data.value_a  ?? "";
        if (refs.bodyText_2   != null) refs.bodyText_2.text   = data.value_b  ?? "";
    }

    // For cloned rows, find the three TMP texts by name inside the clone.
    // The names ("Category", "BodyText_1", "BodyText_2") come from the prefab
    // hierarchy and must stay in sync with it.
    static CategoryRowRefs BindRowFromClone(GameObject clone)
    {
        CategoryRowRefs r = new CategoryRowRefs { root = clone };
        TMP_Text[] texts = clone.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            string n = texts[i].gameObject.name;
            if (n == "Category" && r.categoryText == null) r.categoryText = texts[i];
            else if (n == "BodyText_1" && r.bodyText_1 == null) r.bodyText_1 = texts[i];
            else if (n == "BodyText_2" && r.bodyText_2 == null) r.bodyText_2 = texts[i];
        }
        return r;
    }

    void ApplyHeight(int rowCount)
    {
        if (canvasRect == null) return;
        // Each row beyond the first adds (templateRowHeight + layoutSpacing + extraHeightPerRow).
        int extraRows = Mathf.Max(0, rowCount - 1);
        float added = extraRows * (_templateRowHeight + _layoutSpacing + extraHeightPerRow);
        Vector2 sd = canvasRect.sizeDelta;
        sd.y = _baseHeight + added;
        canvasRect.sizeDelta = sd;
    }
}
