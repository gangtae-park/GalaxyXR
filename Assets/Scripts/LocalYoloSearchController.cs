using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Resolves a Search gesture against the latest local YOLO detections and
/// displays the selected detection in the existing world-space info card.
/// </summary>
public class LocalYoloSearchController : MonoBehaviour
{
    [Header("References")]
    public GestureAnchorTracker anchorTracker;
    public YoloSegLogger yoloLogger;
    public Camera2FrameReceiver frameReceiver;
    public GameObject cardPrefab;
    public Camera referenceCamera;

    [Header("Selection")]
    public string gestureNameFilter = "Search/Find Info";
    [Tooltip("Camera2/YOLO image coordinates normally have their origin at the top-left.")]
    public bool flipImageY = true;
    [Tooltip("Maximum normalized image distance from gaze to a detection center.")]
    [Range(0.05f, 1f)] public float maxCenterDistance = 0.35f;
    [Tooltip("Reject detections older than this many seconds. Set to 0 to disable.")]
    public float maxResultAgeSeconds = 2.5f;

    [Header("Card Placement")]
    public float offsetRight = 0.18f;
    public float offsetUp = 0.12f;
    public float pullToward = 0.05f;
    public float minCameraDistance = 0.4f;
    public bool replacePreviousCard = true;
    public bool verboseLogging = true;

    private VlmInfoCard currentCard;

    private void OnEnable()
    {
        if (anchorTracker != null)
            anchorTracker.OnGestureEndAnchor += HandleAnchor;
    }

    private void OnDisable()
    {
        if (anchorTracker != null)
            anchorTracker.OnGestureEndAnchor -= HandleAnchor;
    }

    private void HandleAnchor(GestureAnchorTracker.AnchorPose pose)
    {
        if (pose == null || pose.gestureName != gestureNameFilter)
            return;

        VlmInfoCard card = SpawnCard(pose);
        if (card == null)
            return;

        if (yoloLogger == null)
        {
            card.SetError("Local search unavailable", "YoloSegLogger is not assigned.");
            return;
        }

        if (yoloLogger.LastResultsTime < 0f)
        {
            card.SetError("No camera result", "YOLO has not analyzed a camera frame yet.");
            return;
        }

        float resultAge = Time.realtimeSinceStartup - yoloLogger.LastResultsTime;
        if (maxResultAgeSeconds > 0f && resultAge > maxResultAgeSeconds)
        {
            card.SetError(
                "Camera result is stale",
                $"The latest YOLO result is {resultAge:F1}s old. Wait for a new frame and try again."
            );
            return;
        }

        IReadOnlyList<YoloSegLogger.SegResult> results = yoloLogger.LatestResults;
        if (results == null || results.Count == 0)
        {
            card.SetError("Nothing detected", "YOLO found no object in the latest camera frame.");
            return;
        }

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        Vector2 gazePoint = GetGazeImagePoint(pose, cam);
        YoloSegLogger.SegResult selected = SelectResult(results, gazePoint);

        if (selected == null)
        {
            card.SetError(
                "No object at gaze",
                "No YOLO detection was close enough to the point you were looking at."
            );
            return;
        }

        string objectName = string.IsNullOrEmpty(selected.className)
            ? $"class_{selected.classId}"
            : selected.className;
        string details =
            $"Local YOLO detection\n" +
            $"Confidence: {selected.confidence:P0}\n" +
            $"Box: ({selected.x:F0}, {selected.y:F0}) {selected.w:F0} x {selected.h:F0}";

        card.SetContentSearch(objectName, details, null, null);

        if (verboseLogging)
        {
            Debug.Log(
                $"[LocalYoloSearch] Selected '{objectName}' ({selected.confidence:F2}) " +
                $"at gaze pixel ({gazePoint.x:F0}, {gazePoint.y:F0})."
            );
        }
    }

    private Vector2 GetGazeImagePoint(GestureAnchorTracker.AnchorPose pose, Camera cam)
    {
        int frameWidth = frameReceiver != null ? frameReceiver.FrameWidth : 1280;
        int frameHeight = frameReceiver != null ? frameReceiver.FrameHeight : 720;
        Vector2 viewportPoint = new Vector2(0.5f, 0.5f);

        if (cam != null)
        {
            Vector3 direction = pose.gazeDirection.sqrMagnitude > 0.0001f
                ? pose.gazeDirection.normalized
                : pose.cameraForward.normalized;
            Vector3 projected = cam.WorldToViewportPoint(cam.transform.position + direction * 10f);
            if (projected.z > 0f)
                viewportPoint = new Vector2(projected.x, projected.y);
        }

        viewportPoint.x = Mathf.Clamp01(viewportPoint.x);
        viewportPoint.y = Mathf.Clamp01(viewportPoint.y);
        float imageY = flipImageY ? 1f - viewportPoint.y : viewportPoint.y;
        return new Vector2(viewportPoint.x * frameWidth, imageY * frameHeight);
    }

    private YoloSegLogger.SegResult SelectResult(
        IReadOnlyList<YoloSegLogger.SegResult> results,
        Vector2 gazePoint)
    {
        int frameWidth = frameReceiver != null ? frameReceiver.FrameWidth : 1280;
        int frameHeight = frameReceiver != null ? frameReceiver.FrameHeight : 720;
        float diagonal = Mathf.Sqrt(frameWidth * frameWidth + frameHeight * frameHeight);

        YoloSegLogger.SegResult bestInside = null;
        float bestInsideScore = float.NegativeInfinity;
        YoloSegLogger.SegResult nearest = null;
        float nearestDistance = float.PositiveInfinity;

        foreach (YoloSegLogger.SegResult result in results)
        {
            if (result == null || result.w <= 0f || result.h <= 0f)
                continue;

            Rect box = new Rect(result.x, result.y, result.w, result.h);
            Vector2 center = box.center;
            float normalizedDistance = Vector2.Distance(gazePoint, center) / Mathf.Max(1f, diagonal);

            if (box.Contains(gazePoint))
            {
                float insideScore = result.confidence - normalizedDistance;
                if (insideScore > bestInsideScore)
                {
                    bestInside = result;
                    bestInsideScore = insideScore;
                }
            }

            if (normalizedDistance < nearestDistance)
            {
                nearest = result;
                nearestDistance = normalizedDistance;
            }
        }

        if (bestInside != null)
            return bestInside;

        return nearestDistance <= maxCenterDistance ? nearest : null;
    }

    private VlmInfoCard SpawnCard(GestureAnchorTracker.AnchorPose pose)
    {
        if (cardPrefab == null)
        {
            Debug.LogWarning("[LocalYoloSearch] cardPrefab is not assigned.");
            return null;
        }

        if (replacePreviousCard && currentCard != null)
        {
            Destroy(currentCard.gameObject);
            currentCard = null;
        }

        Vector3 spawnPosition = pose.worldPosition
            + pose.cameraRight * offsetRight
            + pose.cameraUp * offsetUp
            - pose.cameraForward * pullToward;

        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam != null)
        {
            Vector3 fromCamera = spawnPosition - cam.transform.position;
            if (fromCamera.magnitude < minCameraDistance && fromCamera.sqrMagnitude > 0.0001f)
                spawnPosition = cam.transform.position + fromCamera.normalized * minCameraDistance;
        }

        GameObject instance = Instantiate(cardPrefab, spawnPosition, Quaternion.identity);
        currentCard = instance.GetComponent<VlmInfoCard>();
        if (currentCard == null)
        {
            Debug.LogWarning("[LocalYoloSearch] cardPrefab is missing VlmInfoCard.");
            Destroy(instance);
            return null;
        }

        currentCard.SetLoading("Local search...");
        return currentCard;
    }
}
