using System.Collections.Generic;
using TMPro;
using UnityEngine;

/*
AnchorSpawner

Subscribes to VlmResultReceiver and instantiates a 3D anchor-pin (the
`anchor_pin.obj` model wrapped in a prefab) at the user's gaze position
when an "Anchor" result comes back from Python.

Pattern mirrors ResultCardSpawner:
  - GestureRouter.OnCaptureRecognized -> snapshot the gaze world position
    at the moment the gesture was classified.
  - VlmResultReceiver.OnResult -> if payload.gesture == "Anchor", instantiate
    anchorPrefab at the stored snapshot.

Behavior differences from a card:
  - Anchors persist (no auto-destroy timer).
  - Anchors accumulate. `maxAnchors > 0` caps the count and prunes the
    oldest first; set to 0 for unlimited.
  - Anchors face camera-forward at spawn but DO NOT billboard afterwards --
    they mark a fixed world location.
  - Optional label (response.name) gets written into a TMP_Text child of the
    prefab if you wire one up; leave the labelTextPath empty to skip.
*/

public class AnchorSpawner : MonoBehaviour
{
    [Header("Refs")]
    public VlmResultReceiver receiver;
    public GestureRouter gestureRouter;
    public EyeGazeReader eyeGazeReader;
    public Camera referenceCamera;

    [Header("Anchor Prefab")]
    [Tooltip("Prefab wrapping anchor_pin.obj. Should be a 3D GameObject (Mesh + " +
             "MeshRenderer), NOT a Canvas.")]
    public GameObject anchorPrefab;

    [Header("Spawn position (relative to gaze)")]
    [Tooltip("Distance from the camera along the gaze direction where the anchor lands.")]
    public float gazeProjectionDistance = 1.2f;
    [Tooltip("Optional offset to the right (m, camera-relative at spawn time).")]
    public float horizontalOffset = 0f;
    [Tooltip("Optional vertical offset (m, camera-relative at spawn time).")]
    public float verticalOffset = 0f;

    [Header("Anchor management")]
    [Tooltip("Maximum simultaneous anchors. 0 = unlimited. When exceeded the oldest is removed.")]
    public int maxAnchors = 0;
    [Tooltip("If true, anchor's local scale is multiplied by this factor at spawn time.")]
    public float scaleMultiplier = 1f;

    [Header("Optional label")]
    [Tooltip("Child Transform path to a TMP_Text inside the prefab (e.g. 'Label/Text'). " +
             "Leave empty to skip. The text gets set from payload.response.name.")]
    public string labelTextPath = "";

    [Header("Filter / logging")]
    public string gestureName = "Anchor";
    public bool verboseLogging = true;

    private readonly Queue<GameObject> _spawned = new Queue<GameObject>();
    private Vector3 _lastGazeWorldPos;
    private bool _haveGazeSnapshot;

    void OnEnable()
    {
        if (receiver != null) receiver.OnResult += HandleResult;
        if (gestureRouter != null) gestureRouter.OnCaptureRecognized += HandleGestureRecognized;
    }

    void OnDisable()
    {
        if (receiver != null) receiver.OnResult -= HandleResult;
        if (gestureRouter != null) gestureRouter.OnCaptureRecognized -= HandleGestureRecognized;
    }

    // ---------- gaze snapshot ----------

    void HandleGestureRecognized(string recognizedName)
    {
        if (recognizedName != gestureName) return;
        _lastGazeWorldPos = ComputeGazeWorldPosition();
        _haveGazeSnapshot = true;
        if (verboseLogging)
            Debug.Log($"[AnchorSpawner] '{recognizedName}' END -> gaze snapshot {_lastGazeWorldPos}");
    }

    Vector3 ComputeGazeWorldPosition()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return transform.position;

        Vector3 gazeDir = cam.transform.forward;
        if (eyeGazeReader != null && eyeGazeReader.LatestIsTracked
            && eyeGazeReader.LatestGazeDirection.sqrMagnitude > 0.0001f)
        {
            gazeDir = eyeGazeReader.LatestGazeDirection.normalized;
        }
        return cam.transform.position + gazeDir * gazeProjectionDistance;
    }

    Vector3 ComputeSpawnPosition()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector3 basePos = _haveGazeSnapshot
            ? _lastGazeWorldPos
            : (cam != null
                ? cam.transform.position + cam.transform.forward * gazeProjectionDistance
                : transform.position);
        if (cam == null) return basePos;
        return basePos
            + cam.transform.right * horizontalOffset
            + cam.transform.up    * verticalOffset;
    }

    Quaternion ComputeSpawnRotation()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null) return Quaternion.identity;
        // Face the camera once at spawn time (no per-frame billboard).
        Vector3 toCam = transform.position - cam.transform.position;
        if (toCam.sqrMagnitude < 0.000001f) return Quaternion.identity;
        return Quaternion.LookRotation(cam.transform.forward, Vector3.up);
    }

    // ---------- result dispatch ----------

    void HandleResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (payload == null) return;
        if (payload.gesture != gestureName) return;

        // Python signals a fail by send_gesture_fail_to_unity():
        //   payload.status = "fail", payload.stage = "ack" (early), reason = <text>
        // Also covers the post-VLM fail (stage = "answer") and any legacy path
        // that only sets response.error.
        bool failed =
            (payload.status != null && payload.status.Equals("fail", System.StringComparison.OrdinalIgnoreCase))
            || (payload.response != null && !string.IsNullOrEmpty(payload.response.error));

        if (failed)
        {
            if (verboseLogging)
            {
                string why = !string.IsNullOrEmpty(payload.reason)
                    ? payload.reason
                    : (payload.response != null ? payload.response.error : "(no reason)");
                Debug.Log($"[AnchorSpawner] anchor REJECTED by Python: status={payload.status} stage={payload.stage} reason='{why}' -- no pin spawned.");
            }
            return;
        }

        if (payload.response == null) return;
        if (anchorPrefab == null)
        {
            Debug.LogWarning("[AnchorSpawner] anchorPrefab not assigned.");
            return;
        }

        Vector3 pos = ComputeSpawnPosition();
        Quaternion rot = ComputeSpawnRotation();

        GameObject go = Instantiate(anchorPrefab, pos, rot);
        if (!Mathf.Approximately(scaleMultiplier, 1f))
            go.transform.localScale *= scaleMultiplier;

        // Optional label
        if (!string.IsNullOrEmpty(labelTextPath))
        {
            Transform t = go.transform.Find(labelTextPath);
            if (t != null)
            {
                TMP_Text tmp = t.GetComponent<TMP_Text>();
                if (tmp != null) tmp.text = payload.response.name ?? "";
            }
            else if (verboseLogging)
            {
                Debug.LogWarning($"[AnchorSpawner] labelTextPath '{labelTextPath}' not found in prefab.");
            }
        }

        _spawned.Enqueue(go);
        EnforceMaxAnchors();

        if (verboseLogging)
            Debug.Log($"[AnchorSpawner] spawned anchor at {pos}, name='{payload.response.name}' (total {_spawned.Count})");
    }

    void EnforceMaxAnchors()
    {
        if (maxAnchors <= 0) return;
        while (_spawned.Count > maxAnchors)
        {
            GameObject oldest = _spawned.Dequeue();
            if (oldest != null) Destroy(oldest);
        }
    }

    // ---------- public API ----------

    /// <summary>Destroy every spawned anchor.</summary>
    [ContextMenu("Clear All Anchors")]
    public void ClearAllAnchors()
    {
        while (_spawned.Count > 0)
        {
            GameObject go = _spawned.Dequeue();
            if (go != null) Destroy(go);
        }
    }
}
