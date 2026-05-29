using UnityEngine;

/// <summary>
/// Bridges <see cref="GestureAnchorTracker"/> and <see cref="VlmResultReceiver"/>:
///   - On gesture-end anchor:  spawn a card prefab in Loading state at the anchor
///                              (offset slightly to the upper-right relative to the camera)
///   - On VLM result arrival:  populate the latest card with content (or error)
///
/// Single-card mode (default): a new gesture replaces the previous card.
/// </summary>
public class VlmInfoCardSpawner : MonoBehaviour
{
    [Header("Refs")]
    public VlmResultReceiver receiver;
    public GestureAnchorTracker anchorTracker;
    public GameObject cardPrefab;
    [Tooltip("If null, Camera.main is used for distance clamping.")]
    public Camera referenceCamera;

    [Header("Card placement (camera-relative offset from gaze anchor, in meters)")]
    [Tooltip("Offset to the user's right.")]
    public float offsetRight = 0.18f;
    [Tooltip("Offset upward (negative = below the anchor).")]
    public float offsetUp = 0.12f;
    [Tooltip("Pull card toward the camera by this amount so it doesn't clip into walls.")]
    public float pullToward = 0.05f;
    [Tooltip("Never spawn closer than this from the camera.")]
    public float minCameraDistance = 0.4f;

    [Header("Behavior")]
    [Tooltip("Spawn / populate cards only when the gesture name is in this list. " +
             "Leave empty to accept ANY gesture. " +
             "NOTE: 'Ask' is intentionally NOT here -- AskCardSpawner handles Ask flow itself.")]
    public string[] gestureNameFilters = new string[] { "Search/Find Info" };
    public bool replacePreviousCard = true;
    public bool verboseLogging = true;

    private VlmInfoCard _currentCard;

    void OnEnable()
    {
        if (anchorTracker != null) anchorTracker.OnGestureEndAnchor += HandleAnchor;
        if (receiver != null)      receiver.OnVlmResult += HandleVlmResult;
    }

    void OnDisable()
    {
        if (anchorTracker != null) anchorTracker.OnGestureEndAnchor -= HandleAnchor;
        if (receiver != null)      receiver.OnVlmResult -= HandleVlmResult;
    }

    bool GestureAccepted(string name)
    {
        if (gestureNameFilters == null || gestureNameFilters.Length == 0) return true;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var f in gestureNameFilters)
        {
            if (!string.IsNullOrEmpty(f) && f == name) return true;
        }
        return false;
    }

    void HandleAnchor(GestureAnchorTracker.AnchorPose pose)
    {
        if (!GestureAccepted(pose.gestureName))
        {
            // Anchor was for a different gesture; ignore.
            return;
        }
        if (cardPrefab == null)
        {
            Debug.LogWarning("[VlmInfoCardSpawner] cardPrefab is not assigned.");
            return;
        }

        if (replacePreviousCard && _currentCard != null)
        {
            Destroy(_currentCard.gameObject);
            _currentCard = null;
        }

        // Camera-relative offset: right + up, then pull toward the camera a bit.
        Vector3 spawnPos = pose.worldPosition
            + pose.cameraRight * offsetRight
            + pose.cameraUp    * offsetUp
            - pose.cameraForward * pullToward;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam != null)
        {
            Vector3 toSpawn = spawnPos - cam.transform.position;
            float dist = toSpawn.magnitude;
            if (dist < minCameraDistance && dist > 0.0001f)
            {
                spawnPos = cam.transform.position + toSpawn.normalized * minCameraDistance;
            }
        }

        GameObject go = Instantiate(cardPrefab, spawnPos, Quaternion.identity);
        VlmInfoCard card = go.GetComponent<VlmInfoCard>();
        if (card == null)
        {
            Debug.LogWarning("[VlmInfoCardSpawner] cardPrefab missing VlmInfoCard component.");
            return;
        }
        card.SetLoading("Searching...");
        _currentCard = card;

        if (verboseLogging)
            Debug.Log($"[VlmInfoCardSpawner] Spawned card at {spawnPos}");
    }

    void HandleVlmResult(VlmResultReceiver.VlmResultPayload payload)
    {
        // Only consume results whose gesture name is in our accepted list.
        if (payload != null && !GestureAccepted(payload.gesture))
        {
            return;
        }

        if (_currentCard == null)
        {
            if (verboseLogging)
                Debug.LogWarning("[VlmInfoCardSpawner] VLM result arrived but no active card.");
            return;
        }

        if (payload == null || payload.response == null)
        {
            _currentCard.SetError("Error", "VLM response missing.");
            return;
        }

        var r = payload.response;
        var ges = payload.gesture;

        if (!string.IsNullOrEmpty(r.error))
        {
            _currentCard.SetError("Error", r.error);
            return;
        }

        if (string.IsNullOrEmpty(r.name))
        {
            string fallback = !string.IsNullOrEmpty(r.raw) ? r.raw : "Empty VLM response";
            _currentCard.SetError("Unknown", fallback);
            return;
        }

        if (ges == "Search/Find Info")
        {
            _currentCard.SetContentSearch(r.name, r.description, r.typical_use, r.info);
        }
        else if (ges == "Ask")
        {
            _currentCard.SetContentAsk(r.name, r.answer);
        }

        if (verboseLogging)
            Debug.Log($"[VlmInfoCardSpawner] Card populated: name='{r.name}'");
    }
}
