using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


public class YoloSegLogger : MonoBehaviour
{
    [Header("YOLO Model")]
    public Unity.InferenceEngine.ModelAsset modelAsset;
    public TextAsset classNamesAsset;

    [Header("YOLO Settings")]
    public int inputSize = 640;
    public float confThreshold = 0.35f;
    public float iouThreshold = 0.45f;
    public int maskCoeffCount = 32;
    public int maxDetections = 20;

    [Header("Sentis")]
    public Unity.InferenceEngine.BackendType backendType = Unity.InferenceEngine.BackendType.GPUCompute;
    public string detectionOutputName = "output0";

    private Unity.InferenceEngine.Worker worker;
    private Unity.InferenceEngine.Model runtimeModel;
    private string[] classNames;
    private bool initialized = false;
    private Unity.InferenceEngine.TextureTransform inputTransform;

    private void Start()
    {
        InitModel();
    }

    private void InitModel()
    {
        if (modelAsset == null)
        {
            Debug.LogError("[YOLO] ModelAsset is missing. Drag your yolov8n-seg.onnx or yolo11n-seg.onnx into the Model Asset field.");
            return;
        }

        runtimeModel = Unity.InferenceEngine.ModelLoader.Load(modelAsset);
        worker = new Unity.InferenceEngine.Worker(runtimeModel, backendType);

        classNames = LoadClassNames();

        inputTransform = new Unity.InferenceEngine.TextureTransform()
            .SetDimensions(width: inputSize, height: inputSize, channels: 3);

        initialized = true;
        Debug.Log($"[YOLO] Model initialized. Classes={classNames.Length}, Backend={backendType}");
    }

    public void RunAndLog(Texture2D frame, Action onComplete)
    {
        Debug.Log("[YOLO] RunAndLog called");

        if (!initialized)
        {
            onComplete?.Invoke();
            return;
        }

        if (frame == null)
        {
            Debug.LogWarning("[YOLO] Frame is null");
            onComplete?.Invoke();
            return;
        }

        Unity.InferenceEngine.Tensor<float> inputTensor = null;
        Unity.InferenceEngine.Tensor<float> cpuOutput = null;

        try
        {
            inputTensor = Unity.InferenceEngine.TextureConverter.ToTensor(frame, inputTransform);

            worker.Schedule(inputTensor);

            Unity.InferenceEngine.Tensor<float> outputTensor = string.IsNullOrEmpty(detectionOutputName)
                ? worker.PeekOutput() as Unity.InferenceEngine.Tensor<float>
                : worker.PeekOutput(detectionOutputName) as Unity.InferenceEngine.Tensor<float>;

            if (outputTensor == null)
            {
                Debug.LogError("[YOLO] Detection output tensor is null. Check detectionOutputName in the Inspector.");
                return;
            }

            cpuOutput = outputTensor.ReadbackAndClone();

            List<SegResult> results = DecodeYoloDetectionOutput(cpuOutput, frame.width, frame.height);

            Debug.Log($"[YOLO] Segments detected: {results.Count}");

            foreach (var r in results)
            {
                Debug.Log(
                    $"[YOLO] class={r.className}, conf={r.confidence:F2}, " +
                    $"bbox=({r.x:F0},{r.y:F0},{r.w:F0},{r.h:F0})"
                );
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[YOLO] Inference failed: {e}");
        }
        finally
        {
            inputTensor?.Dispose();
            cpuOutput?.Dispose();
            onComplete?.Invoke();
        }
    }

    private List<SegResult> DecodeYoloDetectionOutput(Unity.InferenceEngine.Tensor<float> output, int originalWidth, int originalHeight)
    {
        Unity.InferenceEngine.TensorShape shape = output.shape;
        Debug.Log($"[YOLO] Output shape: {ShapeToString(shape)}");

        if (shape.rank != 3)
        {
            Debug.LogError("[YOLO] Expected detection output shape like [1, 116, 8400] or [1, 8400, 116].");
            return new List<SegResult>();
        }

        int dim1 = shape[1];
        int dim2 = shape[2];

        // YOLOv8/YOLO11-seg exported by Ultralytics commonly uses [1, 4 + classes + 32, anchors].
        // Some exports transpose it to [1, anchors, 4 + classes + 32].
        bool channelsFirst = dim1 < dim2;
        int channels = channelsFirst ? dim1 : dim2;
        int anchors = channelsFirst ? dim2 : dim1;

        int classCount = channels - 4 - maskCoeffCount;
        if (classCount <= 0)
        {
            Debug.LogError($"[YOLO] Invalid classCount={classCount}. channels={channels}, maskCoeffCount={maskCoeffCount}. If your model is not seg, set maskCoeffCount=0.");
            return new List<SegResult>();
        }

        float scaleX = originalWidth / (float)inputSize;
        float scaleY = originalHeight / (float)inputSize;

        List<SegResult> candidates = new List<SegResult>();

        for (int a = 0; a < anchors; a++)
        {
            float cx = GetOutputValue(output, channelsFirst, 0, a);
            float cy = GetOutputValue(output, channelsFirst, 1, a);
            float w = GetOutputValue(output, channelsFirst, 2, a);
            float h = GetOutputValue(output, channelsFirst, 3, a);

            int bestClass = -1;
            float bestScore = 0f;

            for (int c = 0; c < classCount; c++)
            {
                float score = GetOutputValue(output, channelsFirst, 4 + c, a);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestScore < confThreshold)
                continue;

            float x1 = (cx - w * 0.5f) * scaleX;
            float y1 = (cy - h * 0.5f) * scaleY;
            float bw = w * scaleX;
            float bh = h * scaleY;

            candidates.Add(new SegResult
            {
                classId = bestClass,
                className = GetClassName(bestClass),
                confidence = bestScore,
                x = x1,
                y = y1,
                w = bw,
                h = bh
            });
        }

        return ApplyNms(candidates)
            .OrderByDescending(r => r.confidence)
            .Take(maxDetections)
            .ToList();
    }

    private float GetOutputValue(Unity.InferenceEngine.Tensor<float> output, bool channelsFirst, int channel, int anchor)
    {
        if (channelsFirst)
            return output[0, channel, anchor];

        return output[0, anchor, channel];
    }

    private List<SegResult> ApplyNms(List<SegResult> candidates)
    {
        List<SegResult> sorted = candidates
            .OrderByDescending(r => r.confidence)
            .ToList();

        List<SegResult> kept = new List<SegResult>();

        while (sorted.Count > 0)
        {
            SegResult current = sorted[0];
            kept.Add(current);
            sorted.RemoveAt(0);

            sorted = sorted
                .Where(r => r.classId != current.classId || CalculateIoU(current, r) < iouThreshold)
                .ToList();
        }

        return kept;
    }

    private float CalculateIoU(SegResult a, SegResult b)
    {
        float ax1 = a.x;
        float ay1 = a.y;
        float ax2 = a.x + a.w;
        float ay2 = a.y + a.h;

        float bx1 = b.x;
        float by1 = b.y;
        float bx2 = b.x + b.w;
        float by2 = b.y + b.h;

        float ix1 = Mathf.Max(ax1, bx1);
        float iy1 = Mathf.Max(ay1, by1);
        float ix2 = Mathf.Min(ax2, bx2);
        float iy2 = Mathf.Min(ay2, by2);

        float iw = Mathf.Max(0, ix2 - ix1);
        float ih = Mathf.Max(0, iy2 - iy1);
        float intersection = iw * ih;

        float union = a.w * a.h + b.w * b.h - intersection;
        if (union <= 0f) return 0f;

        return intersection / union;
    }

    private string[] LoadClassNames()
    {
        if (classNamesAsset == null || string.IsNullOrWhiteSpace(classNamesAsset.text))
        {
            Debug.LogWarning("[YOLO] ClassNamesAsset is missing. Logs will use class_0, class_1, ...");
            return Array.Empty<string>();
        }

        return classNamesAsset.text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
    }

    private string GetClassName(int classId)
    {
        if (classId >= 0 && classId < classNames.Length)
            return classNames[classId];

        return $"class_{classId}";
    }

    private string ShapeToString(Unity.InferenceEngine.TensorShape shape)
    {
        List<string> dims = new List<string>();
        for (int i = 0; i < shape.rank; i++)
            dims.Add(shape[i].ToString());
        return "[" + string.Join(", ", dims) + "]";
    }

    private void OnDestroy()
    {
        worker?.Dispose();
    }

    public class SegResult
    {
        public int classId;
        public string className;
        public float confidence;
        public float x;
        public float y;
        public float w;
        public float h;
    }
}