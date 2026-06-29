using UnityEngine;
using UnityEngine.UI;

public class RadialMenuSegmentGraphic : MaskableGraphic, ICanvasRaycastFilter
{
    public float startAngle;
    public float sweepAngle = 45f;
    public float innerRadius = 54f;
    public float outerRadius = 180f;
    public int steps = 14;

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        int segmentCount = Mathf.Max(2, steps);
        float step = sweepAngle / segmentCount;
        int vertexIndex = 0;

        for (int i = 0; i < segmentCount; i++)
        {
            float a0 = (startAngle - step * i) * Mathf.Deg2Rad;
            float a1 = (startAngle - step * (i + 1)) * Mathf.Deg2Rad;

            Vector2 inner0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * innerRadius;
            Vector2 outer0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * outerRadius;
            Vector2 outer1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * outerRadius;
            Vector2 inner1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * innerRadius;

            vh.AddVert(inner0, color, Vector2.zero);
            vh.AddVert(outer0, color, Vector2.zero);
            vh.AddVert(outer1, color, Vector2.zero);
            vh.AddVert(inner1, color, Vector2.zero);
            vh.AddTriangle(vertexIndex, vertexIndex + 1, vertexIndex + 2);
            vh.AddTriangle(vertexIndex, vertexIndex + 2, vertexIndex + 3);
            vertexIndex += 4;
        }
    }

    public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
    {
        RectTransform rect = transform as RectTransform;
        if (rect == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, eventCamera, out Vector2 local))
            return false;

        float distance = local.magnitude;
        if (distance < innerRadius || distance > outerRadius) return false;

        float angle = Mathf.Atan2(local.y, local.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;

        float clockwiseFromStart = Mathf.Repeat(startAngle - angle, 360f);
        return clockwiseFromStart <= sweepAngle;
    }
}
