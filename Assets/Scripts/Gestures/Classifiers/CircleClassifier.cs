using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rule-based circle classifier. Same heuristic that the original
/// CircleGestureRecognizer.IsCircle() used. Operates directly on world-space
/// 3D points -- no projection needed because the checks (radius variance,
/// start-end distance, bbox aspect) are scale/orientation tolerant.
/// </summary>
public class CircleClassifier : GestureClassifierComponent
{
    [Header("Identity")]
    [SerializeField] private string gestureName = "Search/Find Info";
    public override string GestureName => gestureName;

    [Header("Sampling")]
    [Tooltip("Minimum number of points required to even consider the stroke.")]
    public int minPointCount = 12;

    [Header("Circle Validation Thresholds")]
    [Tooltip("Reject if std(radii)/mean(radius) exceeds this -- larger means more elliptical/jagged.")]
    public float maxRadiusStdRatio = 0.35f;
    [Tooltip("Reject if start point and end point are farther apart than this (meters).")]
    public float maxStartEndDistance = 0.1f;
    [Tooltip("Reject if bbox aspect ratio min/max < this -- i.e. shape is too elongated.")]
    public float minWidthHeightRatio = 0.4f;
    [Tooltip("Reject if mean radius is smaller than this (meters).")]
    public float minMeanRadius = 0.025f;

    public override bool TryClassify(Stroke stroke, out float confidence)
    {
        confidence = 0f;
        if (stroke == null || stroke.WorldPoints.Count < minPointCount)
        {
            return false;
        }

        var pts = stroke.WorldPoints;

        // 1) centroid
        Vector3 center = Vector3.zero;
        foreach (var p in pts) center += p;
        center /= pts.Count;

        // 2) radii statistics
        List<float> radii = new List<float>(pts.Count);
        float radiusSum = 0f;
        foreach (var p in pts)
        {
            float r = Vector3.Distance(p, center);
            radii.Add(r);
            radiusSum += r;
        }
        float meanRadius = radiusSum / radii.Count;
        if (meanRadius < minMeanRadius)
        {
            return false;
        }

        float variance = 0f;
        foreach (var r in radii)
        {
            float diff = r - meanRadius;
            variance += diff * diff;
        }
        variance /= radii.Count;
        float std = Mathf.Sqrt(variance);
        float radiusStdRatio = std / meanRadius;
        if (radiusStdRatio > maxRadiusStdRatio)
        {
            return false;
        }

        // 3) start-end closure
        float startEndDistance = Vector3.Distance(pts[0], pts[pts.Count - 1]);
        if (startEndDistance > maxStartEndDistance)
        {
            return false;
        }

        // 4) bounding box aspect (use the two largest extents)
        Vector3 min = pts[0], max = pts[0];
        foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        Vector3 size = max - min;

        float[] dims = new float[] { size.x, size.y, size.z };
        System.Array.Sort(dims);
        float width = dims[2];
        float height = dims[1];
        if (height < 0.0001f) return false;
        float WHRatio = Mathf.Min(width, height) / Mathf.Max(width, height);
        if (WHRatio < minWidthHeightRatio)
        {
            return false;
        }

        // All checks passed. Confidence: how well centered the radii are relative to allowed std.
        // Higher when the stroke is a tighter circle.
        float radiusScore = 1f - Mathf.Clamp01(radiusStdRatio / maxRadiusStdRatio);
        float closureScore = 1f - Mathf.Clamp01(startEndDistance / maxStartEndDistance);
        float aspectScore = Mathf.Clamp01(WHRatio);
        confidence = (radiusScore + closureScore + aspectScore) / 3f;

        Debug.Log(
            $"[CircleClassifier] OK: meanR={meanRadius:F3} stdRatio={radiusStdRatio:F3} " +
            $"startEnd={startEndDistance:F3} WH={WHRatio:F2} conf={confidence:F2}"
        );
        return true;
    }
}
