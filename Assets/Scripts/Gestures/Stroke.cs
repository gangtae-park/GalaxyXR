using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A single pinch trajectory captured in world space, plus the camera pose at the
/// moment the stroke started so 3D points can be projected to 2D for $Q.
/// </summary>
public class Stroke
{
    public List<Vector3> WorldPoints = new List<Vector3>();
    public Vector3 CameraPositionAtStart;
    public Quaternion CameraRotationAtStart;
    public Vector3 CameraRightAtStart;
    public Vector3 CameraUpAtStart;
    public Vector3 CameraForwardAtStart;
    public float StartTime;
    public float EndTime;

    public int PointCount => WorldPoints.Count;

    /// <summary>
    /// Project the 3D world-space stroke onto a 2D plane orthogonal to the camera-forward
    /// direction at stroke start. Returns (u, v) coordinates in meters along the camera's
    /// right and up axes, with origin at the first stroke point.
    /// </summary>
    public List<Vector2> ProjectTo2DCameraPlane()
    {
        List<Vector2> result = new List<Vector2>(WorldPoints.Count);
        if (WorldPoints.Count == 0) return result;

        Vector3 origin = WorldPoints[0];
        Vector3 right = CameraRightAtStart.sqrMagnitude > 0.0001f
            ? CameraRightAtStart.normalized : Vector3.right;
        Vector3 up = CameraUpAtStart.sqrMagnitude > 0.0001f
            ? CameraUpAtStart.normalized : Vector3.up;

        foreach (var p in WorldPoints)
        {
            Vector3 d = p - origin;
            float u = Vector3.Dot(d, right);
            float v = Vector3.Dot(d, up);
            result.Add(new Vector2(u, v));
        }
        return result;
    }
}
