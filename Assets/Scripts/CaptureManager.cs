using UnityEngine;
using UnityEngine.InputSystem;

/*
CaptureManager

Owns the Capture UI lifecycle on the Unity side. ResultCardSpawner forwards
"Capture" VLM_RESULT payloads here; this component:
  1) Computes the initial card world size from the YOLO bbox (pixel space
     converted to world metres at the spawn distance via the camera FOV).
  2) Instantiates the CaptureControlCard prefab at the gaze-snapshot position
     and hands it the per-hand input actions + camera reference.
  3) Subscribes to OnShutterFired / OnTimedOut and destroys the card when
     either fires. (The actual screenshot is a TODO; for now closing the card
     IS the visible effect of a successful shutter.)
*/
public class CaptureManager : MonoBehaviour
{
    [Header("Refs")]
    public Camera referenceCamera;
    [Tooltip("Assign the same GestureRouter used elsewhere. While the CaptureControlCard is open we call SetPinchSuppressed(true) on it so the shutter pinch does NOT double-fire as a Jackknife gesture trigger.")]
    public GestureRouter gestureRouter;

    [Header("Prefab")]
    public GameObject captureControlCardPrefab;

    [Header("Hand input (assign the SAME pinch select-value InputActionReferences used by GestureRouter's Compare fields). Resize positions come from XR Hands wrist joints internally, so no pinch-position actions are needed here.")]
    public InputActionReference rightPinchAction;
    public InputActionReference leftPinchAction;

    [Header("Initial card sizing")]
    [Tooltip("World distance from the camera at which the YOLO bbox->world-size conversion is evaluated. Should match the gaze-snapshot depth used by ResultCardSpawner (gazeProjectionDistance, default 1.2 m).")]
    public float cardDepth = 1.2f;
    [Tooltip("Multiplier applied to the bbox-derived world size before clamping. 1.0 = card spawns exactly at the YOLO bbox footprint; 0.5 = half that; 0.3 = thirty percent. Use this to dial down the initial card without touching code or PNG PPU.")]
    [Range(0.05f, 2f)] public float bboxSizeMultiplier = 0.5f;
    [Tooltip("Floor for the initial card size on each axis (metres). Prevents an invisibly small card when the object's bbox is tiny.")]
    public Vector2 minInitialWorldSize = new Vector2(0.08f, 0.08f);
    [Tooltip("Fallback size used when bbox/frame_size data is missing from the payload.")]
    public Vector2 fallbackInitialWorldSize = new Vector2(0.3f, 0.3f);

    [Header("Debug")]
    public bool verboseLogging = true;

    private CaptureControlCard _activeCard;

    public void BeginCapture(string objectName, string objectId, Vector3 worldPos,
                             int[] bbox, int[] frameSize)
    {
        if (captureControlCardPrefab == null)
        {
            Debug.LogWarning("[CaptureManager] captureControlCardPrefab not assigned; cannot open Capture UI.");
            return;
        }

        if (_activeCard != null)
        {
            Destroy(_activeCard.gameObject);
            _activeCard = null;
        }

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector2 initialSize = ComputeInitialWorldSize(bbox, frameSize, cam);

        var go = Instantiate(captureControlCardPrefab, worldPos, Quaternion.identity);
        EnsureConstantSize(go, cam);
        var card = go.GetComponent<CaptureControlCard>();
        if (card == null)
        {
            Debug.LogError("[CaptureManager] captureControlCardPrefab has no CaptureControlCard component.");
            Destroy(go);
            return;
        }

        card.Initialize(worldPos, initialSize, cam,
                        rightPinchAction, leftPinchAction);
        card.OnShutterFired += () => HandleClose(card, "shutter");
        card.OnTimedOut     += () => HandleClose(card, "timeout");
        _activeCard = card;

        // From this moment until HandleClose the shutter card owns the pinch
        // action -- one or both hands' pinches fire the shutter. Gate off
        // GestureRouter's Jackknife rising-edge check for the same window so a
        // single pinch never means both "take a shot" and "start a Jackknife
        // gesture".
        if (gestureRouter != null) gestureRouter.SetPinchSuppressed(true, "capture_shutter");

        if (verboseLogging)
            Debug.Log($"[CaptureManager] CaptureControlCard opened for '{objectName}' id={objectId} initSize={initialSize}");
    }

    void HandleClose(CaptureControlCard card, string reason)
    {
        if (card == null) return;
        if (verboseLogging) Debug.Log($"[CaptureManager] capture closed ({reason}).");
        if (_activeCard == card) _activeCard = null;
        Destroy(card.gameObject);
        if (gestureRouter != null) gestureRouter.SetPinchSuppressed(false, $"capture_closed_{reason}");
    }

    // bbox is in pixel coordinates of the source frame (Python side captured_frame).
    // We need the world-space size the card should be so its on-screen footprint
    // roughly matches the YOLO bbox. Conversion:
    //   visible-area-at-cardDepth = 2 * cardDepth * tan(fovY/2)  (height)
    //   world_dim = (bbox_pixels / frame_pixels) * visible-area
    Vector2 ComputeInitialWorldSize(int[] bbox, int[] frameSize, Camera cam)
    {
        if (bbox == null || bbox.Length != 4 || frameSize == null || frameSize.Length != 2 || cam == null)
            return ClampToMin(fallbackInitialWorldSize);

        float bboxW = Mathf.Abs(bbox[2] - bbox[0]);
        float bboxH = Mathf.Abs(bbox[3] - bbox[1]);
        if (bboxW <= 0 || bboxH <= 0 || frameSize[0] <= 0 || frameSize[1] <= 0)
            return ClampToMin(fallbackInitialWorldSize);

        float fovY = cam.fieldOfView * Mathf.Deg2Rad;
        float visibleH = 2f * cardDepth * Mathf.Tan(fovY * 0.5f);
        float visibleW = visibleH * cam.aspect;

        // Multiplier lets the user shrink the spawn footprint without changing
        // the bbox or any other code; clamp still enforces the min size floor.
        float worldW = (bboxW / frameSize[0]) * visibleW * bboxSizeMultiplier;
        float worldH = (bboxH / frameSize[1]) * visibleH * bboxSizeMultiplier;
        return ClampToMin(new Vector2(worldW, worldH));
    }

    Vector2 ClampToMin(Vector2 v) => new Vector2(
        Mathf.Max(v.x, minInitialWorldSize.x),
        Mathf.Max(v.y, minInitialWorldSize.y));

    void EnsureConstantSize(GameObject go, Camera cam)
    {
        if (go == null) return;
        DistanceConstantSize comp = go.GetComponent<DistanceConstantSize>();
        if (comp == null) comp = go.AddComponent<DistanceConstantSize>();
        comp.referenceCamera = cam != null ? cam : Camera.main;
    }
}
