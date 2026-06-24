using UnityEngine;

public class YoloScreenToWorldMapper : MonoBehaviour
{
    [Header("BBox")]
    public YoloBboxFormat bboxFormat = YoloBboxFormat.PixelXYXY;
    public bool flipY = true;
    public int fallbackImageWidth = 0;
    public int fallbackImageHeight = 0;

    [Header("Letterbox / resize")]
    public bool compensateLetterbox = false;
    public Vector2 letterboxScale = Vector2.one;
    public Vector2 letterboxOffsetPixels = Vector2.zero;

    public Vector2 GetViewportPoint(DetectionResult detection, CaptureContextRegistry.CaptureContext context)
    {
        if (detection == null)
        {
            Debug.LogWarning("[YoloScreenToWorld] detection is null; using viewport center.");
            return new Vector2(0.5f, 0.5f);
        }

        float imageWidth = detection.imageWidth > 0 ? detection.imageWidth :
            (context != null && context.imageWidth > 0 ? context.imageWidth : fallbackImageWidth);
        float imageHeight = detection.imageHeight > 0 ? detection.imageHeight :
            (context != null && context.imageHeight > 0 ? context.imageHeight : fallbackImageHeight);

        bool normalized = bboxFormat == YoloBboxFormat.NormalizedXYXY || bboxFormat == YoloBboxFormat.NormalizedXYWH;
        if (!normalized && (imageWidth <= 0f || imageHeight <= 0f))
        {
            imageWidth = context != null && context.screenWidth > 0 ? context.screenWidth : Mathf.Max(1, Screen.width);
            imageHeight = context != null && context.screenHeight > 0 ? context.screenHeight : Mathf.Max(1, Screen.height);
            Debug.LogWarning($"[YoloScreenToWorld] image size missing; using screen fallback {imageWidth}x{imageHeight}. If server image was resized/letterboxed, configure mapper compensation.");
        }

        float cx;
        float cy;
        switch (bboxFormat)
        {
            case YoloBboxFormat.PixelXYWH:
            case YoloBboxFormat.NormalizedXYWH:
                cx = detection.x1 + detection.x2 * 0.5f;
                cy = detection.y1 + detection.y2 * 0.5f;
                break;
            default:
                cx = (detection.x1 + detection.x2) * 0.5f;
                cy = (detection.y1 + detection.y2) * 0.5f;
                break;
        }

        if (compensateLetterbox)
        {
            Vector2 scale = new Vector2(
                Mathf.Approximately(letterboxScale.x, 0f) ? 1f : letterboxScale.x,
                Mathf.Approximately(letterboxScale.y, 0f) ? 1f : letterboxScale.y);
            cx = (cx - letterboxOffsetPixels.x) / scale.x;
            cy = (cy - letterboxOffsetPixels.y) / scale.y;
            Debug.Log("[YoloScreenToWorld] applied letterbox compensation. Verify scale/offset against server preprocessing.");
        }

        float vx = normalized ? cx : cx / Mathf.Max(1f, imageWidth);
        float vy = normalized ? cy : cy / Mathf.Max(1f, imageHeight);
        if (flipY) vy = 1f - vy;

        Vector2 viewport = new Vector2(Mathf.Clamp01(vx), Mathf.Clamp01(vy));
        Debug.Log($"[YoloScreenToWorld] bbox center=({cx:F2},{cy:F2}) viewport={viewport} format={bboxFormat} flipY={flipY}");
        return viewport;
    }
}
