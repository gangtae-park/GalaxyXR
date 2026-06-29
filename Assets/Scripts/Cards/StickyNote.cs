using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/*
StickyNote

The floating emoji that marks a saved note next to a target object. Mirrors
AnchorPin's runtime collider + XRSimpleInteractable auto-setup so the prefab
doesn't need any interactable wiring in the editor.

Behaviour difference vs AnchorPin: this fires on the FIRST short pinch
(selectEntered) -- no long-hold-to-delete. The owning NoteManager opens a
ViewNoteCard in response.
*/
[DisallowMultipleComponent]
public class StickyNote : MonoBehaviour
{
    [Header("Auto-interactable")]
    [Tooltip("Padding on the auto-fitted BoxCollider (1 = exact renderer bounds).")]
    public float autoColliderSizeMultiplier = 1.4f;
    [Tooltip("Minimum thickness (metres, local space) for any axis of the auto-fitted BoxCollider. Prevents zero-thickness colliders when the renderer is a flat sprite/quad -- those would otherwise be unhittable by XR rays.")]
    public float minColliderThickness = 0.03f;

    [Header("Debug")]
    public bool verboseLogging = true;

    public string NoteId { get; private set; }
    public string ObjectId { get; private set; }

    /// <summary>Fired once each time the sticky is pinched (selectEntered).</summary>
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

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (verboseLogging)
            Debug.Log($"[StickyNote] pinched (noteId={NoteId}, by {args.interactorObject?.transform?.name ?? "?"})");
        try { OnPinched?.Invoke(this); } catch (Exception e) { Debug.LogError(e); }
    }

    // Same auto-collider + interactable pattern as AnchorPin so prefabs without
    // editor-time setup still receive pinches. Gaze selection is disabled --
    // a real pinch is required, looking at the sticky alone shouldn't open it.
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
            // No renderer to size against -- give the box a sane default so XR rays
            // still have something to hit (otherwise the box has size 0 in local).
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

        // Flat renderers (SpriteRenderer, Quad facing +Z) produce one axis = 0,
        // which gives a degenerate collider XR rays cannot hit. Clamp every
        // axis to at least minColliderThickness so the box is always volumetric.
        float minT = Mathf.Max(0f, minColliderThickness);
        localSize.x = Mathf.Max(localSize.x, minT);
        localSize.y = Mathf.Max(localSize.y, minT);
        localSize.z = Mathf.Max(localSize.z, minT);
        box.size = localSize;

        if (verboseLogging)
            Debug.Log($"[StickyNote] auto-fitted BoxCollider size={box.size} (renderer bounds world={world.size}, lossyScale={ls})");
    }
}
