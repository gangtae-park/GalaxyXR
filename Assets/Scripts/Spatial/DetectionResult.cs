using System;
using UnityEngine;

public enum YoloBboxFormat
{
    PixelXYXY,
    NormalizedXYXY,
    PixelXYWH,
    NormalizedXYWH
}

[Serializable]
public class DetectionResult
{
    public string requestId;
    public string label;
    public float confidence;
    public float x1;
    public float y1;
    public float x2;
    public float y2;
    public int imageWidth;
    public int imageHeight;
    public bool requireExactRequestContext;

    public Vector2 Center => new Vector2((x1 + x2) * 0.5f, (y1 + y2) * 0.5f);
    public Vector2 Size => new Vector2(Mathf.Abs(x2 - x1), Mathf.Abs(y2 - y1));

    public bool HasValidBbox()
    {
        return !float.IsNaN(x1) && !float.IsNaN(y1) && !float.IsNaN(x2) && !float.IsNaN(y2)
            && !Mathf.Approximately(x1, x2)
            && !Mathf.Approximately(y1, y2);
    }

    public static DetectionResult FromXYXY(
        string requestId,
        string label,
        float confidence,
        float[] bbox,
        int imageWidth,
        int imageHeight)
    {
        if (bbox == null || bbox.Length < 4) return null;
        return new DetectionResult
        {
            requestId = requestId ?? "",
            label = string.IsNullOrEmpty(label) ? "detected_object" : label,
            confidence = confidence,
            x1 = bbox[0],
            y1 = bbox[1],
            x2 = bbox[2],
            y2 = bbox[3],
            imageWidth = imageWidth,
            imageHeight = imageHeight
        };
    }
}

[Serializable]
public class DetectedObjectAnchor
{
    public DetectionResult detection;
    public CaptureContextRegistry.CaptureContext context;
    public Vector2 viewportPoint;
    public Vector3 worldPosition;
    public Vector3 rayOrigin;
    public Vector3 rayDirection;
    public string resolveMethod;
}
