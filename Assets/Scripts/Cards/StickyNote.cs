using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[DisallowMultipleComponent]
public class StickyNote : MonoBehaviour
{
    [Header("Auto-interactable")]
    public float autoColliderSizeMultiplier = 1.4f;
    public float minColliderThickness = 0.03f;

    [Header("Debug")]
    public bool verboseLogging = true;

    public string NoteId { get; private set; }
    public string ObjectId { get; private set; }

    public event Action<StickyNote> OnPinched;

    private XRSimpleInteractable _interactable;

    void Awake()
    {
        EnsureInteractable();
    }

    void OnEnable()
    {
        if (_interactable != null)
            _interactable.selectEntered.AddListener(OnSelectEntered);
    }

    void OnDisable()
    {
        if (_interactable != null)
            _interactable.selectEntered.RemoveListener(OnSelectEntered);
    }

    public void Bind(string noteId, string objectId)
    {
        NoteId = noteId ?? "";
        ObjectId = objectId ?? "";
    }

    /// <summary>Gaze-pinch entry: same effect as an XRI select.</summary>
    public void PinchFromGaze()
    {
        if (verboseLogging)
            Debug.Log($"[StickyNote] pinched via gaze (noteId={NoteId})");
        try { OnPinched?.Invoke(this); } catch (Exception e) { Debug.LogError(e); }
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (verboseLogging)
            Debug.Log($"[StickyNote] pinched (noteId={NoteId}, by {args.interactorObject?.transform?.name ?? "?"})");
        try { OnPinched?.Invoke(this); } catch (Exception e) { Debug.LogError(e); }
    }

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
        box.isTrigger = false;

        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            float d = Mathf.Max(0.03f, minColliderThickness);
            box.size = new Vector3(d, d, d);
            if (verboseLogging) Debug.LogWarning("[StickyNote] no Renderer found; using default collider size.");
            return;
        }

        Bounds world = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            world.Encapsulate(renderers[i].bounds);

        box.center = transform.InverseTransformPoint(world.center);

        Vector3 ls = transform.lossyScale;
        Vector3 localSize = new Vector3(
            Mathf.Approximately(ls.x, 0f) ? world.size.x : world.size.x / Mathf.Abs(ls.x),
            Mathf.Approximately(ls.y, 0f) ? world.size.y : world.size.y / Mathf.Abs(ls.y),
            Mathf.Approximately(ls.z, 0f) ? world.size.z : world.size.z / Mathf.Abs(ls.z));
        localSize *= Mathf.Max(0.01f, autoColliderSizeMultiplier);

        float minT = Mathf.Max(0f, minColliderThickness);
        localSize.x = Mathf.Max(localSize.x, minT);
        localSize.y = Mathf.Max(localSize.y, minT);
        localSize.z = Mathf.Max(localSize.z, minT);
        box.size = localSize;

        if (verboseLogging)
            Debug.Log($"[StickyNote] auto-fitted BoxCollider size={box.size} (renderer bounds world={world.size}, lossyScale={ls})");
    }
}
