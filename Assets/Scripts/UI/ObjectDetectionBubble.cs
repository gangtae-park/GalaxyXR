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
    }

    void HandleClick()
    {
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
    }

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
