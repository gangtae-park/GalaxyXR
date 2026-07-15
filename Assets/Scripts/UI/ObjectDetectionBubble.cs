using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

/*
A small 3D marker placed at each YOLO-detected object's world position. The
prefab is a plain sphere (MeshFilter + MeshRenderer + SphereCollider). No
Canvas / Button / Text -- just a round handle the user can pinch / ray-select.

Two click paths feed HandleClick():

  - IPointerClickHandler  -- works whenever the camera has a PhysicsRaycaster
    (or XRI's TrackedDevicePhysicsRaycaster). XR Ray Interactor + an XRUI
    Input Module bridges pinch into pointer click here.

  - XRSimpleInteractable.selectEntered -- when the prefab carries that XRI
    component, hand/controller select via the XR Ray Interactor directly
    fires this UnityEvent. We attach by reflection so this script keeps
    compiling even in projects that don't have XR Interaction Toolkit
    installed.

Either path calls HandleClick(), which invokes the onClicked callback the
spawner registered during Initialize().
*/

public class ObjectDetectionBubble : MonoBehaviour, IPointerClickHandler
{
    [Header("Refs")]
    [SerializeField] Camera referenceCamera;
    [SerializeField] bool verboseLogging = true;

    DetectionResult detection;
    VlmResultReceiver.VlmResultPayload payload;
    string requestId;
    // Callback signature: (detection, payload, requestId, bubbleWorldPosition).
    // The world position is the bubble's transform.position at the moment of
    // the click, so downstream UI (radial menu) can anchor there directly
    // instead of re-projecting via the anchor resolver.
    Action<DetectionResult, VlmResultReceiver.VlmResultPayload, string, Vector3> onClicked;

    // Hook bookkeeping for the optional XRSimpleInteractable.selectEntered.
    Component _xrInteractable;
    object _xrUnityEvent;        // the SelectEnterEvent (UnityEvent<T>) instance
    MethodInfo _xrRemoveMethod;  // SelectEnterEvent.RemoveListener
    Delegate _xrDelegate;        // the UnityAction<T> we added

    public Camera ReferenceCamera
    {
        get => referenceCamera;
        set => referenceCamera = value;
    }

    /// <summary>Detection this bubble represents (used by the gaze-pinch selector).</summary>
    public DetectionResult Detection => detection;

    // -------- Hover feedback (gaze + XR ray) --------
    // The bubble grows while ANY hover source is active. Scale rides through
    // DistanceConstantSize.externalMultiplier when present so the two systems
    // don't fight over localScale; plain transforms are scaled directly.
    [Tooltip("Scale multiplier while hovered by gaze or the hand ray.")]
    public float hoverScaleMultiplier = 1.35f;

    bool _gazeHover;
    bool _rayHover;
    bool _hoverVisualOn;
    Vector3 _hoverBaseScale;
    bool _hoverBaseCaptured;

    public void SetGazeHovered(bool on)
    {
        if (_gazeHover == on) return;
        _gazeHover = on;
        ApplyHoverVisual();
    }

    void SetRayHovered(bool on)
    {
        if (_rayHover == on) return;
        _rayHover = on;
        ApplyHoverVisual();
    }

    void ApplyHoverVisual()
    {
        bool on = _gazeHover || _rayHover;
        if (on == _hoverVisualOn) return;
        _hoverVisualOn = on;

        DistanceConstantSize sizer = GetComponent<DistanceConstantSize>();
        if (sizer != null)
        {
            sizer.externalMultiplier = on ? hoverScaleMultiplier : 1f;
            return;
        }
        if (!_hoverBaseCaptured)
        {
            _hoverBaseScale = transform.localScale;
            _hoverBaseCaptured = true;
        }
        transform.localScale = _hoverBaseScale * (on ? hoverScaleMultiplier : 1f);
    }

    /// <summary>Entry point for the gaze-pinch selector -- same path as a
    /// pointer/XR click.</summary>
    public void ClickFromGaze()
    {
        if (verboseLogging)
            Debug.Log($"[OBJECT_BUBBLE] gaze-pinch click label={(detection != null ? detection.label : "?")}");
        HandleClick();
    }

    public void Initialize(
        DetectionResult detection,
        VlmResultReceiver.VlmResultPayload payload,
        string requestId,
        Action<DetectionResult, VlmResultReceiver.VlmResultPayload, string, Vector3> onClicked)
    {
        this.detection = detection;
        this.payload = payload;
        this.requestId = requestId ?? "";
        this.onClicked = onClicked;
        if (referenceCamera == null) referenceCamera = Camera.main;
        TryHookXrInteractable();
        LogClickPathDiagnostics();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (verboseLogging)
            Debug.Log($"[OBJECT_BUBBLE] OnPointerClick fired label={(detection != null ? detection.label : "?")}");
        HandleClick();
    }

    void OnDestroy()
    {
        if (_xrUnityEvent != null && _xrRemoveMethod != null && _xrDelegate != null)
        {
            try { _xrRemoveMethod.Invoke(_xrUnityEvent, new object[] { _xrDelegate }); }
            catch { /* toolkit may have torn down already; ignore */ }
        }
        for (int i = 0; i < _auxHooks.Count; i++)
        {
            var (evt, remove, del) = _auxHooks[i];
            if (evt == null || remove == null || del == null) continue;
            try { remove.Invoke(evt, new object[] { del }); } catch { }
        }
        _auxHooks.Clear();
    }

    // One physical pinch can arrive through several click paths at once
    // (gaze-pinch selector, XRI selectEntered, EventSystem pointer click).
    // Without this gate the duplicates toggle the menu open->closed within a
    // frame, which looked like "clicks don't work".
    float _lastClickTime = -999f;
    const float ClickDebounceSeconds = 0.4f;

    void HandleClick()
    {
        if (Time.unscaledTime - _lastClickTime < ClickDebounceSeconds)
        {
            if (verboseLogging)
                Debug.Log("[OBJECT_BUBBLE] duplicate click within debounce window ignored.");
            return;
        }
        _lastClickTime = Time.unscaledTime;

        string label = detection != null ? detection.label : "";
        Vector3 worldPos = transform.position;
        Debug.Log($"[OBJECT_BUBBLE] clicked label={label} request_id={requestId} world=({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3})");
        onClicked?.Invoke(detection, payload, requestId, worldPos);
    }

    // Reflection-based subscription so this script compiles without a hard
    // dependency on Unity.XR.Interaction.Toolkit. When the toolkit IS present
    // and the prefab has an XRSimpleInteractable, we bind our handler to the
    // selectEntered UnityEvent.
    //
    // NOTE: `selectEntered` is a PROPERTY returning SelectEnterEvent (a
    // UnityEvent<SelectEnterEventArgs>) -- it is NOT a C# event. The previous
    // implementation used GetEvent() which always returned null, so this hook
    // never actually fired. We now use GetProperty/GetField, fetch the
    // UnityEvent<T> instance, and call its AddListener with a delegate built
    // via MakeGenericMethod so the parameter type matches exactly.
    void TryHookXrInteractable()
    {
        if (_xrUnityEvent != null) return;

        Type type = Type.GetType(
            "UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable, Unity.XR.Interaction.Toolkit")
            // Fallback to older 2.x namespace.
            ?? Type.GetType("UnityEngine.XR.Interaction.Toolkit.XRSimpleInteractable, Unity.XR.Interaction.Toolkit");
        if (type == null)
        {
            if (verboseLogging) Debug.Log("[OBJECT_BUBBLE] XR Interaction Toolkit not installed; XRSimpleInteractable hook skipped.");
            return;
        }
        Component comp = GetComponent(type);
        if (comp == null)
        {
            if (verboseLogging) Debug.Log("[OBJECT_BUBBLE] no XRSimpleInteractable on this bubble; relying on IPointerClickHandler only.");
            return;
        }

        // Resolve the SelectEnterEvent instance (property in newer XRI, field in older).
        object unityEvent = null;
        PropertyInfo prop = type.GetProperty("selectEntered", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null) unityEvent = prop.GetValue(comp);
        if (unityEvent == null)
        {
            FieldInfo fld = type.GetField("selectEntered", BindingFlags.Public | BindingFlags.Instance);
            if (fld != null) unityEvent = fld.GetValue(comp);
        }
        if (unityEvent == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE] XRSimpleInteractable.selectEntered not found via reflection; click won't fire from XR select.");
            return;
        }

        // Find AddListener / RemoveListener on the UnityEvent<T>.
        Type eventType = unityEvent.GetType();
        MethodInfo addMethod = eventType.GetMethod("AddListener");
        MethodInfo removeMethod = eventType.GetMethod("RemoveListener");
        if (addMethod == null)
        {
            Debug.LogWarning("[OBJECT_BUBBLE] selectEntered UnityEvent has no AddListener; cannot subscribe.");
            return;
        }

        // The listener type is UnityAction<SelectEnterEventArgs>. Build a
        // matching delegate by making our generic stub closed over T.
        Type listenerType = addMethod.GetParameters()[0].ParameterType;
        if (!listenerType.IsGenericType)
        {
            Debug.LogWarning("[OBJECT_BUBBLE] unexpected AddListener parameter type; cannot subscribe.");
            return;
        }
        Type argType = listenerType.GetGenericArguments()[0];

        MethodInfo genericStub = typeof(ObjectDetectionBubble).GetMethod(
            nameof(XrSelectStub),
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (genericStub == null) return;

        MethodInfo closedStub = genericStub.MakeGenericMethod(argType);
        Delegate typedDelegate;
        try
        {
            typedDelegate = Delegate.CreateDelegate(listenerType, this, closedStub);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[OBJECT_BUBBLE] CreateDelegate failed for selectEntered: {e.Message}");
            return;
        }

        addMethod.Invoke(unityEvent, new object[] { typedDelegate });
        _xrInteractable = comp;
        _xrUnityEvent = unityEvent;
        _xrRemoveMethod = removeMethod;
        _xrDelegate = typedDelegate;

        Debug.Log("[OBJECT_BUBBLE] hooked XRSimpleInteractable.selectEntered (pinch / XR select will fire click).");

        // Hover feedback: same reflection dance for hoverEntered / hoverExited
        // so the hand-ray hover grows the bubble like gaze hover does.
        HookAuxEvent(comp, type, "hoverEntered", nameof(XrHoverEnterStub));
        HookAuxEvent(comp, type, "hoverExited", nameof(XrHoverExitStub));
    }

    // Bookkeeping for the extra hover subscriptions.
    readonly System.Collections.Generic.List<(object evt, MethodInfo remove, Delegate del)> _auxHooks
        = new System.Collections.Generic.List<(object, MethodInfo, Delegate)>();

    void HookAuxEvent(Component comp, Type type, string memberName, string stubName)
    {
        object unityEvent = null;
        PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null) unityEvent = prop.GetValue(comp);
        if (unityEvent == null)
        {
            FieldInfo fld = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (fld != null) unityEvent = fld.GetValue(comp);
        }
        if (unityEvent == null) return;

        Type eventType = unityEvent.GetType();
        MethodInfo addMethod = eventType.GetMethod("AddListener");
        MethodInfo removeMethod = eventType.GetMethod("RemoveListener");
        if (addMethod == null) return;
        Type listenerType = addMethod.GetParameters()[0].ParameterType;
        if (!listenerType.IsGenericType) return;
        Type argType = listenerType.GetGenericArguments()[0];

        MethodInfo genericStub = typeof(ObjectDetectionBubble).GetMethod(
            stubName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (genericStub == null) return;
        Delegate del;
        try { del = Delegate.CreateDelegate(listenerType, this, genericStub.MakeGenericMethod(argType)); }
        catch { return; }
        addMethod.Invoke(unityEvent, new object[] { del });
        _auxHooks.Add((unityEvent, removeMethod, del));
    }

    void XrHoverEnterStub<T>(T args) { SetRayHovered(true); }
    void XrHoverExitStub<T>(T args) { SetRayHovered(false); }

    // Generic stub invoked by reflection. The exact T is bound at runtime to
    // SelectEnterEventArgs via MakeGenericMethod. We don't need the args
    // value; the click already carries everything we need via captured state.
    void XrSelectStub<T>(T args)
    {
        if (verboseLogging) Debug.Log("[OBJECT_BUBBLE] XR select event received -> HandleClick");
        HandleClick();
    }

    // Print a one-line summary so it's easy to see at runtime whether the
    // click should actually be wired up. If both ladders are empty, the user
    // sees clearly why pinch doesn't open the menu.
    void LogClickPathDiagnostics()
    {
        Collider col = GetComponent<Collider>();
        bool hasInteractable = _xrInteractable != null;
        bool hasCollider = col != null;
        bool cameraHasPhysicsRaycaster = false;
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam != null)
        {
            // PhysicsRaycaster from UnityEngine + XRI's TrackedDevicePhysicsRaycaster both extend BaseRaycaster.
            BaseRaycaster[] raycasters = cam.GetComponents<BaseRaycaster>();
            cameraHasPhysicsRaycaster = raycasters != null && raycasters.Length > 0;
        }
        Debug.Log(
            $"[OBJECT_BUBBLE][SETUP] collider={hasCollider} xr_interactable={hasInteractable} " +
            $"camera_pointer_raycaster={cameraHasPhysicsRaycaster} " +
            $"({(hasInteractable || cameraHasPhysicsRaycaster ? "click path OK" : "no click path -- add XRSimpleInteractable to the prefab OR a (Tracked Device) PhysicsRaycaster to the camera")})");
    }
}
