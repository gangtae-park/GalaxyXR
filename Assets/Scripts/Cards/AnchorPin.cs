using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[DisallowMultipleComponent]
public class AnchorPin : MonoBehaviour
{
    [Header("Long-pinch to delete")]
    public float holdToDeleteDuration = 2f;
    public bool shrinkWhileHolding = true;
    public float shrinkRatio = 0.8f;
    public float autoColliderSizeMultiplier = 1.2f;

    [Header("Pinch color feedback")]
    [Tooltip("Darken the pin while it is being pinched, as feedback.")]
    public bool darkenWhileHolding = true;
    [Tooltip("RGB brightness at full hold (0 = black, 1 = unchanged). Lower = deeper/darker.")]
    [Range(0f, 1f)] public float darkenFactor = 0.4f;

    public string ObjectName { get; private set; }

    private XRSimpleInteractable _interactable;
    private Coroutine _holdRoutine;
    private Vector3 _initialScale;

    // Per-instance material colors captured at spawn, so the pinch tint only
    // affects this pin and can be restored when released.
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"); // URP/Lit
    private static readonly int ColorId = Shader.PropertyToID("_Color");         // built-in / legacy
    private struct MatColor { public Material mat; public int prop; public Color baseColor; }
    private readonly List<MatColor> _matColors = new List<MatColor>();

    void Awake()
    {
        _initialScale = transform.localScale;
        EnsureInteractable();
        CacheMaterialColors();
    }

    void OnEnable()
    {
        if (_interactable != null)
        {
            _interactable.selectEntered.AddListener(OnSelectEntered);
            _interactable.selectExited.AddListener(OnSelectExited);
        }
    }

    void OnDisable()
    {
        CancelHold();
        transform.localScale = _initialScale;
        RestoreColors();
        if (_interactable != null)
        {
            _interactable.selectEntered.RemoveListener(OnSelectEntered);
            _interactable.selectExited.RemoveListener(OnSelectExited);
        }
    }

    public void SetContent(string objectName)
    {
        ObjectName = objectName ?? "";
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        CancelHold();
        _holdRoutine = StartCoroutine(HoldToDelete());
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        // Released before the hold completed -> cancel and restore.
        CancelHold();
        transform.localScale = _initialScale;
        RestoreColors();
    }

    private IEnumerator HoldToDelete()
    {
        float elapsed = 0f;
        while (elapsed < holdToDeleteDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / holdToDeleteDuration);

            if (shrinkWhileHolding)
                transform.localScale = Vector3.Lerp(_initialScale, _initialScale * shrinkRatio, t);

            if (darkenWhileHolding)
                ApplyHoldColor(t);

            yield return null;
        }

        _holdRoutine = null;
        Destroy(gameObject);
    }

    private void CancelHold()
    {
        if (_holdRoutine != null)
        {
            StopCoroutine(_holdRoutine);
            _holdRoutine = null;
        }
    }

    // ---------- color feedback ----------

    private void CacheMaterialColors()
    {
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            // .materials returns per-renderer instances, so edits stay local to this pin.
            foreach (var m in r.materials)
            {
                int prop = m.HasProperty(BaseColorId) ? BaseColorId
                         : (m.HasProperty(ColorId) ? ColorId : 0);
                if (prop == 0) continue;
                _matColors.Add(new MatColor { mat = m, prop = prop, baseColor = m.GetColor(prop) });
            }
        }
    }

    private void ApplyHoldColor(float t)
    {
        foreach (var mc in _matColors)
        {
            Color dark = mc.baseColor * darkenFactor;
            dark.a = mc.baseColor.a; // keep transparency, only deepen RGB
            mc.mat.SetColor(mc.prop, Color.Lerp(mc.baseColor, dark, t));
        }
    }

    private void RestoreColors()
    {
        foreach (var mc in _matColors)
            if (mc.mat != null) mc.mat.SetColor(mc.prop, mc.baseColor);
    }

    // ---------- setup ----------

    private void EnsureInteractable()
    {
        if (GetComponentInChildren<Collider>() == null)
            AddFittedCollider();

        _interactable = GetComponent<XRSimpleInteractable>();
        if (_interactable == null)
            _interactable = gameObject.AddComponent<XRSimpleInteractable>();

        _interactable.allowGazeInteraction = false;
    }

    private void AddFittedCollider()
    {
        var box = gameObject.AddComponent<BoxCollider>();

        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        Bounds world = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            world.Encapsulate(renderers[i].bounds);

        box.center = transform.InverseTransformPoint(world.center);

        Vector3 ls = transform.lossyScale;
        Vector3 localSize = new Vector3(
            Mathf.Approximately(ls.x, 0f) ? world.size.x : world.size.x / Mathf.Abs(ls.x),
            Mathf.Approximately(ls.y, 0f) ? world.size.y : world.size.y / Mathf.Abs(ls.y),
            Mathf.Approximately(ls.z, 0f) ? world.size.z : world.size.z / Mathf.Abs(ls.z));
        box.size = localSize * Mathf.Max(0.01f, autoColliderSizeMultiplier);
    }
}
