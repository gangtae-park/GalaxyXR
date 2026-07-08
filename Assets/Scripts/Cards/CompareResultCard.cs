using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CompareResultCard : MonoBehaviour
{
    [Header("Title")]
    public TMP_Text titleText_1;
    public TMP_Text titleText_2;

    [Header("Rows")]
    public CategoryRowRefs categoryRowTemplate;
    public RectTransform rowsContainer;

    [Header("Close")]
    public Button closeButton;

    [Header("Sizing")]
    public RectTransform canvasRect;
    public float extraHeightPerRow = 0f;

    [Header("Lifetime")]
    public float autoDestroySeconds = 45f;

    [System.Serializable]
    public class CategoryRowRefs
    {
        public GameObject root;
        public TMP_Text categoryText;
        public TMP_Text bodyText_1;
        public TMP_Text bodyText_2;
    }

    readonly List<GameObject> _spawnedRows = new List<GameObject>();

    float _baseChromeHeight;
    bool _measured;

    [SerializeField] bool logHeightCalculations = false;

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

    public void RefreshHeight()
    {
        ApplyHeight(_spawnedRows.Count + (categoryRowTemplate != null
            && categoryRowTemplate.root != null
            && categoryRowTemplate.root.activeSelf ? 1 : 0));
    }

    void ResolveContainer()
    {
        if (rowsContainer == null && categoryRowTemplate != null && categoryRowTemplate.root != null)
            rowsContainer = categoryRowTemplate.root.transform.parent as RectTransform;
    }

    void EnsureMeasured()
    {
        if (_measured) return;
        if (canvasRect == null || rowsContainer == null)
        {
            _measured = true;
            return;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRect);

        float canvasHeight = canvasRect.rect.height;
        float rowsHeight = rowsContainer.rect.height;
        _baseChromeHeight = Mathf.Max(0f, canvasHeight - rowsHeight);
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
        if (canvasRect == null || rowsContainer == null) return;

        LayoutRebuilder.ForceRebuildLayoutImmediate(rowsContainer);
        float requiredRowsHeight = LayoutUtility.GetPreferredHeight(rowsContainer);
        if (requiredRowsHeight <= 0f)
            requiredRowsHeight = rowsContainer.rect.height;

        if (extraHeightPerRow != 0f)
            requiredRowsHeight += Mathf.Max(0, rowCount) * extraHeightPerRow;

        float target = _baseChromeHeight + requiredRowsHeight;
        Vector2 sd = canvasRect.sizeDelta;
        float previous = sd.y;
        sd.y = target;
        canvasRect.sizeDelta = sd;

        LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRect);

        if (logHeightCalculations)
            Debug.Log(
                $"[CompareResultCard] ApplyHeight rows={rowCount} " +
                $"required_rows={requiredRowsHeight:F1} chrome={_baseChromeHeight:F1} " +
                $"canvas {previous:F1} -> {target:F1}");
    }
}
