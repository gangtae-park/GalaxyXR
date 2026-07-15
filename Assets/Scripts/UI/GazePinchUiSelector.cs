using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/*
GazePinchUiSelector

Gaze-pinch selection for the ObjectUI layer: look at a detection bubble or a
radial-menu wedge and pinch (right hand) to click it, without having to reach
out with the ray/poke interactors.

Auto-attached by ObjectDetectionBubbleSpawner (enableGazePinchSelection).
On the pinch RISING edge:

  1. Radial menu first: intersect the eye-gaze ray with the open menu's
     canvas plane, convert to local units, and hit-test the wedge ring
     geometrically (ObjectActionRadialMenu.TryGetActionAtLocalPoint). No
     EventSystem / raycaster involvement, so it works regardless of pointer
     setup and of the menu's frozen orientation.
  2. Otherwise bubbles: a forgiving SphereCast along the gaze ray; the first
     ObjectDetectionBubble hit gets the same HandleClick path as a pointer
     click (so re-click-to-close-menu and the Compare flow behave normally).

Only active in UIOnly input mode by default -- the same right pinch drives
Jackknife gestures in Gesture mode.
*/

public class GazePinchUiSelector : MonoBehaviour
{
    [Header("Refs (auto-resolved when empty)")]
    public EyeGazeReader eyeGazeReader;
    public ObjectActionRadialMenuSpawner menuSpawner;
    public InputModeManager modeManager;
    public GestureRouter feedbackRouter;

    [Header("Pinch")]
    [Tooltip("Optional override. If empty, listens to the right-hand pinch value directly.")]
    public InputActionReference pinchAction;
    [Range(0f, 1f)] public float pinchThreshold = 0.85f;
    public float cooldownSeconds = 0.35f;
    public bool requireUIMode = true;

    [Header("Result cards (any input mode)")]
    [Tooltip("Gaze-pinch also operates ResultCard content: UI buttons get a synthesized pointer click, XRGrabInteractable drag handles get a gaze-grab that then follows the HAND (pinch to grab, move the hand, release to drop), sticky notes open, anchor pins run their hold-to-delete. Works in every input mode; GestureRouter suppresses gesture starts while the gaze is over interactive content.")]
    public bool enableCardInteraction = true;
    [Tooltip("Hand anchor that drives the grabbed card's translation (1:1). Auto-resolved from GestureRouter.rightIndexTip when empty; falls back to gaze-based movement if neither exists.")]
    public Transform handTransform;

    [Header("Gaze ray")]
    [Tooltip("SphereCast radius (m) for bubble selection -- forgiveness margin around the gaze ray (eye trackers are ~1-2 deg accurate, so keep this generous).")]
    public float sphereCastRadius = 0.05f;
    public float maxDistance = 6f;
    [Tooltip("Play the router's activation clip when a gaze-pinch actually selects something. OFF by default: it is the same clip as gesture starts and made UI selections sound like gesture recognitions.")]
    public bool playFeedbackOnSelect = false;
    [Tooltip("Skip the gaze-pinch when the XR ray / UI pointer is already targeting interactive content. OFF by default: the hover check fires for ANY interactable under the always-on hand rays (the bubbles themselves are interactables), which swallowed every pinch. Double-clicks from the two paths are instead debounced inside the bubble/menu click handlers.")]
    public bool yieldToRayPointer = false;
    public bool verboseLogging = true;

    InputAction _fallbackAction;
    bool _wasPressed;
    float _lastSelectTime = -999f;
    ObjectDetectionBubble _hoveredBubble;

    // gaze-grab (drag handle) state: grabbed by gaze, MOVED by the hand.
    Transform _carryTarget;
    Vector3 _carryStartTargetPos;
    Vector3 _carryStartHandPos;
    bool _carryUsingHand;
    float _carryDistance;      // gaze-fallback path
    Vector3 _carryOffset;      // gaze-fallback path
    bool _carrying;

    AnchorPin _gazeHoldPin;

    void OnEnable()
    {
        ResolveRefs();
        pinchAction?.action?.Enable();
        EnsureFallbackAction();
        _fallbackAction?.Enable();
        _wasPressed = false;
        Debug.Log($"[GazePinchUI] armed. eyeGazeReader={(eyeGazeReader != null)} menuSpawner={(menuSpawner != null)} "
                  + $"modeManager={(modeManager != null)} pinchAction={(pinchAction != null ? "assigned" : "fallback bindings")}");
    }

    void OnDisable()
    {
        pinchAction?.action?.Disable();
        _fallbackAction?.Disable();
        ClearHoverState();
        EndCarry();
        if (_gazeHoldPin != null)
        {
            if (_gazeHoldPin.isActiveAndEnabled) _gazeHoldPin.EndGazeHold();
            _gazeHoldPin = null;
        }
    }

    void Update()
    {
        // Bubble/menu selection is UI-mode gated; card interaction is not
        // (cards appear while gesturing).
        bool uiMode = !requireUIMode || modeManager == null
            || modeManager.CurrentMode == InputMode.UIOnly;

        float value = ReadPinchValue();
        bool pressed = value >= pinchThreshold;
        bool rising = pressed && !_wasPressed;
        _wasPressed = pressed;

        // Active gaze-grab: keep following while the pinch is held.
        if (_carrying)
        {
            if (!pressed || _carryTarget == null) EndCarry();
            else { UpdateCarry(); return; }
        }

        // Active anchor-pin hold: keep it running until the pinch releases.
        if (_gazeHoldPin != null)
        {
            if (!pressed || !_gazeHoldPin.isActiveAndEnabled)
            {
                if (_gazeHoldPin != null && _gazeHoldPin.isActiveAndEnabled) _gazeHoldPin.EndGazeHold();
                _gazeHoldPin = null;
            }
            else return;
        }

        if (uiMode) UpdateHover();
        else ClearHoverState();

        if (!rising) return;
        if (verboseLogging)
            Debug.Log($"[GazePinchUI] pinch rising edge value={value:F2} uiMode={uiMode} menuOpen={(menuSpawner != null && menuSpawner.CurrentMenu != null)}");
        if (Time.unscaledTime - _lastSelectTime < cooldownSeconds) return;

        if (TrySelect(uiMode))
            _lastSelectTime = Time.unscaledTime;
        else if (verboseLogging)
            Debug.Log("[GazePinchUI] pinch consumed nothing under gaze.");
    }

    // ---- per-frame gaze hover feedback ----
    // Wedges tint via ObjectActionRadialMenu.SetGazeHoverIndex; bubbles grow
    // via ObjectDetectionBubble.SetGazeHovered. Uses the same ray + hit tests
    // as the pinch selection so what is highlighted is exactly what a pinch
    // would activate.
    void UpdateHover()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 origin = cam.transform.position;
        Vector3 dir = cam.transform.forward;
        if (eyeGazeReader != null && eyeGazeReader.LatestIsTracked
            && eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f)
        {
            dir = eyeGazeReader.LatestGazeDirection.normalized;
        }

        ObjectActionRadialMenu menu = menuSpawner != null ? menuSpawner.CurrentMenu : null;
        if (menu != null)
        {
            int index = -1;
            bool centerHole = false;
            if (TryHitMenuPlane(menu, origin, dir, out Vector2 local))
            {
                menu.TryGetIndexAtLocalPoint(local, out index);
                centerHole = index < 0 && local.magnitude < menu.innerRadius;
            }
            menu.SetGazeHoverIndex(index);

            // Center hole = the bubble that opened this menu sits right there;
            // hovering it (bubble grows) telegraphs "pinch here closes the menu".
            if (centerHole && Physics.SphereCast(origin, sphereCastRadius, dir, out RaycastHit centerHit,
                    maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                SetHoveredBubble(centerHit.collider.GetComponentInParent<ObjectDetectionBubble>());
            else
                SetHoveredBubble(null);
            return;
        }

        ObjectDetectionBubble target = null;
        if (Physics.SphereCast(origin, sphereCastRadius, dir, out RaycastHit hit, maxDistance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
        {
            target = hit.collider.GetComponentInParent<ObjectDetectionBubble>();
        }
        SetHoveredBubble(target);
    }

    void SetHoveredBubble(ObjectDetectionBubble bubble)
    {
        if (ReferenceEquals(bubble, _hoveredBubble)) return;
        if (_hoveredBubble != null) _hoveredBubble.SetGazeHovered(false);
        _hoveredBubble = bubble;
        if (_hoveredBubble != null) _hoveredBubble.SetGazeHovered(true);
    }

    void ClearHoverState()
    {
        SetHoveredBubble(null);
        ObjectActionRadialMenu menu = menuSpawner != null ? menuSpawner.CurrentMenu : null;
        if (menu != null) menu.SetGazeHoverIndex(-1);
    }

    bool TrySelect(bool uiMode)
    {
        Camera cam = Camera.main;
        if (cam == null) return false;

        // Yield to the ray/poke pipelines: if this pinch is already aimed at
        // interactive content, the EventSystem / XRI click will handle it and
        // a second gaze-routed action would fight it.
        if (yieldToRayPointer && feedbackRouter != null && feedbackRouter.IsPinchTargetingInteractive())
        {
            if (verboseLogging) Debug.Log("[GazePinchUI] pinch is targeting UI/XR via ray -- yielding.");
            return false;
        }

        Vector3 origin = cam.transform.position;
        Vector3 dir = cam.transform.forward;
        bool eyeTracked = eyeGazeReader != null && eyeGazeReader.LatestIsTracked
            && eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f;
        if (eyeTracked) dir = eyeGazeReader.LatestGazeDirection.normalized;

        if (uiMode)
        {
            // ---- 1) radial menu open: wedge ring OR the center hole (= the
            // bubble that opened the menu; clicking it toggles the menu shut).
            // Anything else while the menu is open is swallowed so a stray
            // pinch can't strike the bubbles behind the menu.
            ObjectActionRadialMenu menu = menuSpawner != null ? menuSpawner.CurrentMenu : null;
            if (menu != null)
            {
                if (TryHitMenuPlane(menu, origin, dir, out Vector2 local))
                {
                    if (menu.TryGetActionAtLocalPoint(local, out ObjectActionMenuAction action))
                    {
                        if (verboseLogging)
                            Debug.Log($"[GazePinchUI] menu wedge via gaze: {action} local=({local.x:F0},{local.y:F0}) "
                                      + $"theta={Mathf.Repeat(Mathf.Atan2(local.y, local.x) * Mathf.Rad2Deg, 360f):F0} eye={eyeTracked}");
                        if (playFeedbackOnSelect && feedbackRouter != null) feedbackRouter.PlayActivationCue();
                        menu.ClickAction(action);
                        return true;
                    }
                    if (local.magnitude < menu.innerRadius
                        && Physics.SphereCast(origin, sphereCastRadius, dir, out RaycastHit centerHit,
                            maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                    {
                        ObjectDetectionBubble centerBubble =
                            centerHit.collider.GetComponentInParent<ObjectDetectionBubble>();
                        if (centerBubble != null)
                        {
                            if (verboseLogging)
                                Debug.Log("[GazePinchUI] center bubble via gaze -> toggling menu closed.");
                            centerBubble.ClickFromGaze();
                            return true;
                        }
                    }
                }
                if (verboseLogging)
                    Debug.Log($"[GazePinchUI] menu open but gaze missed wedges/center (eye={eyeTracked}) -- ignoring pinch.");
                return false;
            }

            // ---- 2) bubbles (physics spherecast along the gaze) ----
            if (Physics.SphereCast(origin, sphereCastRadius, dir, out RaycastHit hit, maxDistance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                ObjectDetectionBubble bubble = hit.collider.GetComponentInParent<ObjectDetectionBubble>();
                if (bubble != null)
                {
                    if (verboseLogging)
                        Debug.Log($"[GazePinchUI] bubble selected via gaze at {hit.distance:F2}m eye={eyeTracked}");
                    if (playFeedbackOnSelect && feedbackRouter != null) feedbackRouter.PlayActivationCue();
                    bubble.ClickFromGaze();
                    return true;
                }
            }
        }

        if (enableCardInteraction)
        {
            // ---- 3) ResultCard UI (buttons etc.) via a synthesized pointer click.
            if (TryClickUiAtGaze(origin, dir))
                return true;

            // ---- 4) 3D interactables: sticky notes (click), anchor pins
            // (hold-to-delete), grabbable cards (gaze-grab + hand move).
            if (Physics.SphereCast(origin, sphereCastRadius, dir, out RaycastHit hit, maxDistance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                StickyNote note = hit.collider.GetComponentInParent<StickyNote>();
                if (note != null)
                {
                    if (verboseLogging) Debug.Log("[GazePinchUI] sticky note via gaze.");
                    note.PinchFromGaze();
                    return true;
                }

                AnchorPin pin = hit.collider.GetComponentInParent<AnchorPin>();
                if (pin != null)
                {
                    if (verboseLogging) Debug.Log("[GazePinchUI] anchor pin hold via gaze (hold pinch to delete).");
                    pin.BeginGazeHold();
                    _gazeHoldPin = pin;
                    return true;
                }

                if (TryBeginCarry(hit))
                    return true;
            }
        }

        return false;
    }

    // ---------- ResultCard UI click ----------
    // Geometric hit test against every enabled Selectable (buttons, toggles,
    // dropdowns...) instead of EventSystem.RaycastAll: world-space card
    // canvases often lack a GraphicRaycaster-with-worldCamera setup, which
    // made the raycaster path return nothing. Ray -> RectTransform plane ->
    // rect containment, nearest hit wins.
    bool TryClickUiAtGaze(Vector3 origin, Vector3 dir)
    {
        GameObject clickable = FindUiClickableAtGaze(origin, dir);
        if (clickable == null) return false;
        if (verboseLogging)
            Debug.Log($"[GazePinchUI] card UI click via gaze -> {clickable.name}");

        EventSystem es = EventSystem.current;
        if (es != null)
        {
            PointerEventData ped = new PointerEventData(es);
            ExecuteEvents.Execute(clickable, ped, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(clickable, ped, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(clickable, ped, ExecuteEvents.pointerClickHandler);
        }
        else
        {
            UnityEngine.UI.Button btn = clickable.GetComponent<UnityEngine.UI.Button>();
            if (btn != null) btn.onClick.Invoke();
        }
        return true;
    }

    GameObject FindUiClickableAtGaze(Vector3 origin, Vector3 dir)
    {
        UnityEngine.UI.Selectable[] selectables = UnityEngine.UI.Selectable.allSelectablesArray;
        GameObject best = null;
        float bestDist = float.PositiveInfinity;

        for (int i = 0; i < selectables.Length; i++)
        {
            UnityEngine.UI.Selectable sel = selectables[i];
            if (sel == null || !sel.isActiveAndEnabled || !sel.interactable) continue;
            // The radial menu has its own geometric path.
            if (sel.GetComponentInParent<ObjectActionRadialMenu>() != null) continue;
            RectTransform rt = sel.transform as RectTransform;
            if (rt == null) continue;

            Vector3 normal = rt.forward;
            float denom = Vector3.Dot(normal, dir);
            if (Mathf.Abs(denom) < 1e-4f) continue;
            float dist = Vector3.Dot(normal, rt.position - origin) / denom;
            if (dist <= 0f || dist >= maxDistance || dist >= bestDist) continue;

            Vector3 local = rt.InverseTransformPoint(origin + dir * dist);
            if (!rt.rect.Contains(new Vector2(local.x, local.y))) continue;

            best = sel.gameObject;
            bestDist = dist;
        }
        return best;
    }

    // ---------- gaze-grab for drag handles (XRGrabInteractable) ----------
    // The GAZE selects what to grab; the HAND moves it (1:1 translation from
    // the hand anchor, like a normal XRI grab). Gaze-follow is only the
    // fallback when no hand transform is available.
    bool TryBeginCarry(RaycastHit hit)
    {
        XRGrabInteractable grab = hit.collider.GetComponentInParent<XRGrabInteractable>();
        if (grab == null) return false;

        _carryTarget = grab.transform;
        _carryStartTargetPos = _carryTarget.position;
        _carryUsingHand = handTransform != null;
        if (_carryUsingHand)
        {
            _carryStartHandPos = handTransform.position;
        }
        else
        {
            Camera cam = Camera.main;
            Vector3 origin = cam != null ? cam.transform.position : Vector3.zero;
            _carryDistance = hit.distance;
            _carryOffset = _carryTarget.position - hit.point;
        }
        _carrying = true;
        if (verboseLogging)
            Debug.Log($"[GazePinchUI] gaze-grab START target={_carryTarget.name} handDriven={_carryUsingHand}");
        return true;
    }

    void UpdateCarry()
    {
        if (_carryTarget == null) { EndCarry(); return; }

        if (_carryUsingHand && handTransform != null)
        {
            // 1:1 hand translation from the grab moment.
            _carryTarget.position = _carryStartTargetPos + (handTransform.position - _carryStartHandPos);
            return;
        }

        // Fallback: follow the gaze ray at the original distance.
        Camera cam = Camera.main;
        if (cam == null) { EndCarry(); return; }
        Vector3 origin = cam.transform.position;
        Vector3 dir = cam.transform.forward;
        if (eyeGazeReader != null && eyeGazeReader.LatestIsTracked
            && eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f)
        {
            dir = eyeGazeReader.LatestGazeDirection.normalized;
        }
        _carryTarget.position = origin + dir * _carryDistance + _carryOffset;
    }

    void EndCarry()
    {
        if (verboseLogging && _carryTarget != null)
            Debug.Log($"[GazePinchUI] gaze-grab END target={_carryTarget.name}");
        _carrying = false;
        _carryTarget = null;
    }

    /// <summary>On-demand check for GestureRouter: is the gaze currently over
    /// something the gaze-pinch would consume (bubble, menu, card UI, drag
    /// handle)? Used to suppress gesture starts the same way ray-over-UI
    /// already does.</summary>
    public bool IsGazeOverInteractiveNow()
    {
        if (_carrying) return true;
        Camera cam = Camera.main;
        if (cam == null) return false;
        Vector3 origin = cam.transform.position;
        Vector3 dir = cam.transform.forward;
        if (eyeGazeReader != null && eyeGazeReader.LatestIsTracked
            && eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f)
        {
            dir = eyeGazeReader.LatestGazeDirection.normalized;
        }

        ObjectActionRadialMenu menu = menuSpawner != null ? menuSpawner.CurrentMenu : null;
        if (menu != null && TryHitMenuPlane(menu, origin, dir, out Vector2 local)
            && local.magnitude <= menu.radius * 1.15f)
            return true;

        if (Physics.SphereCast(origin, sphereCastRadius, dir, out RaycastHit hit, maxDistance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
        {
            if (hit.collider.GetComponentInParent<ObjectDetectionBubble>() != null) return true;
            if (hit.collider.GetComponentInParent<XRGrabInteractable>() != null) return true;
            if (hit.collider.GetComponentInParent<XRSimpleInteractable>() != null) return true;
        }

        return enableCardInteraction && FindUiClickableAtGaze(origin, dir) != null;
    }

    /// <summary>Ray/menu-canvas-plane intersection. True when the gaze ray
    /// crosses the plane in front of the user; localPoint is in canvas units
    /// (radius space). Wedge / center-hole decisions are made by the caller.</summary>
    static bool TryHitMenuPlane(ObjectActionRadialMenu menu, Vector3 origin, Vector3 dir,
                                out Vector2 localPoint)
    {
        localPoint = Vector2.zero;
        Transform t = menu.transform;
        Vector3 normal = t.forward;
        float denom = Vector3.Dot(normal, dir);
        if (Mathf.Abs(denom) < 1e-4f) return false;  // ray parallel to the menu plane

        float dist = Vector3.Dot(normal, t.position - origin) / denom;
        if (dist <= 0f) return false;                 // menu behind the gaze

        Vector3 world = origin + dir * dist;
        Vector3 local = t.InverseTransformPoint(world);  // canvas units (radius space)
        localPoint = new Vector2(local.x, local.y);
        return true;
    }

    float ReadPinchValue()
    {
        InputAction actionRef = pinchAction != null ? pinchAction.action : _fallbackAction;
        if (actionRef == null) return 0f;
        try
        {
            if (actionRef.activeControl != null && actionRef.activeControl.valueType == typeof(float))
                return actionRef.ReadValue<float>();
        }
        catch { }
        try { return actionRef.IsPressed() ? 1f : 0f; }
        catch { return 0f; }
    }

    void EnsureFallbackAction()
    {
        if (pinchAction != null || _fallbackAction != null) return;
        _fallbackAction = new InputAction("RightHandGazePinchSelect", InputActionType.Value, expectedControlType: "Axis");
        _fallbackAction.AddBinding("<MetaAimHand>{RightHand}/pinchStrengthIndex");
        _fallbackAction.AddBinding("<HandInteraction>{RightHand}/pinchValue");
        _fallbackAction.AddBinding("<HandInteraction>{RightHand}/pointerActivateValue");
        _fallbackAction.AddBinding("<XRController>{RightHand}/{Grip}");
    }

    void ResolveRefs()
    {
        if (eyeGazeReader == null) eyeGazeReader = FindObjectOfType<EyeGazeReader>();
        if (menuSpawner == null) menuSpawner = FindObjectOfType<ObjectActionRadialMenuSpawner>();
        if (modeManager == null) modeManager = FindObjectOfType<InputModeManager>();
        if (feedbackRouter == null) feedbackRouter = FindObjectOfType<GestureRouter>();
        if (handTransform == null && feedbackRouter != null) handTransform = feedbackRouter.rightIndexTip;
    }
}
