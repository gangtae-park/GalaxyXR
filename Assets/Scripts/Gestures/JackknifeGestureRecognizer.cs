using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using JKBlades = Jackknife.JackknifeBlades;
using JKSample = Jackknife.Sample;
using JKVector = Jackknife.Vector;
using JKRecognizer = Jackknife.Jackknife;

/*
Single multi-class gesture recognizer backed by Jackknife (DTW + synthetic
negative bootstrapping). Replaces the per-gesture rule-based / $Q classifiers.

Templates come from the same JSON the recorder writes. Each TemplateEntry.label
is treated as the GESTURE CLASS NAME (e.g. "Circle", "Ask"). All examples of a
class share the same label.

Jackknife.Train() auto-derives a rejection threshold from synthetic negatives,
so unrecognized / garbage strokes return null (no false-positive tuning hell).
*/

public class JackknifeGestureRecognizer : MonoBehaviour
{
    [Header("Templates")]
    public string subDirectory = "GestureTemplates";
    public string saveFileName = "gesture_templates.json";

    [Header("Jackknife params")]
    public int resampleCount = 32;
    public int radius = 1;
    public bool useEuclidean = false;
    public double beta = 0.1;

    public int gpsrN = 6;
    public int gpsrR = 2;
    public int minTemplatesToTrain = 4;

    [Header("Stroke pre-filter (runs BEFORE Jackknife to reject obvious non-gestures)")]
    [Tooltip("Reject strokes whose 2D bounding box width/height ratio is below this. " +
             "Catches purely-vertical motions (just moving hand down/up) before they reach " +
             "Jackknife. 0.1 = width must be at least 10% of height. Set 0 to disable.")]
    public float minWidthHeightRatio = 0.1f;
    [Tooltip("Strokes shorter than this many points are also rejected. Stops jitter / noise.")]
    public int minPointCount = 8;

    [Header("Reject labels (templates with these labels = explicit 'NOT a gesture')")]
    [Tooltip("Any template whose label is in this list trains Jackknife as a normal class " +
             "but Recognize() will return null when that class wins. Use it to teach the " +
             "recognizer 'this motion = nothing'. Record samples of unintended motions " +
             "(just down, just up, scribbles) under any of these labels.")]
    public string[] rejectLabels = new string[] { "false" };

    [Header("Status")]
    [SerializeField] private int loadedTemplateCount;
    [SerializeField] private string[] knownGestures = new string[0];
    [SerializeField] private bool ready;

    private JKRecognizer _jk;
    private readonly Dictionary<int, string> _idToName = new Dictionary<int, string>();
    private string _saveFilePath;

    public bool IsReady => ready;
    public string[] KnownGestures => knownGestures;

    [Serializable]
    private class TemplateFile { public List<TemplateEntry> templates = new List<TemplateEntry>(); }

    [Serializable]
    private class TemplateEntry { public string label; public List<Vector2> points = new List<Vector2>(); }

    void Awake()
    {
        ResolvePath();
        Rebuild();
    }

    void ResolvePath()
    {
        string dir = Application.persistentDataPath;
        if (!string.IsNullOrEmpty(subDirectory)) dir = Path.Combine(dir, subDirectory);
        _saveFilePath = Path.Combine(dir, saveFileName);
    }

    /// <summary>(Re)load the template JSON and retrain Jackknife. Call after recording
    /// new templates at runtime, or just rely on the Awake() call.</summary>
    [ContextMenu("Rebuild From Disk")]
    public void Rebuild()
    {
        ready = false;
        _idToName.Clear();

        TemplateFile file = LoadFile();
        loadedTemplateCount = file.templates.Count;
        if (file.templates.Count == 0)
        {
            Debug.LogWarning($"[Jackknife] no templates at {_saveFilePath}");
            return;
        }

        var blades = new JKBlades();
        if (useEuclidean) blades.SetEDDefaults(); else blades.SetIPDefaults();
        blades.ResampleCnt = resampleCount;
        blades.Radius = radius;

        _jk = new JKRecognizer(blades);

        var nameToId = new Dictionary<string, int>();
        int added = 0;

        for (int i = 0; i < file.templates.Count; i++)
        {
            TemplateEntry t = file.templates[i];
            if (t.points == null || t.points.Count < 2) continue;

            string gname = string.IsNullOrEmpty(t.label) ? "Unknown" : t.label;
            if (!nameToId.TryGetValue(gname, out int gid))
            {
                gid = nameToId.Count;
                nameToId[gname] = gid;
                _idToName[gid] = gname;
            }

            var sample = new JKSample(0, gid, i);
            var traj = new List<JKVector>(t.points.Count);
            foreach (Vector2 p in t.points)
                traj.Add(new JKVector(new List<double> { p.x, p.y }));
            sample.AddTrajectory(traj);

            _jk.AddTemplate(sample);
            added++;
        }

        knownGestures = new List<string>(nameToId.Keys).ToArray();

        if (added < minTemplatesToTrain)
        {
            Debug.LogWarning(
                $"[Jackknife] only {added} templates (need >= {minTemplatesToTrain}). " +
                "Not trained -- Recognize() will return null. Record more samples.");
            return;
        }

        try
        {
            _jk.Train(gpsrN, gpsrR, beta);
            ready = true;
            Debug.Log(
                $"[Jackknife] trained on {added} templates. " +
                $"classes=[{string.Join(", ", knownGestures)}] " +
                $"(resample={resampleCount}, beta={beta})");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Jackknife] Train() failed: {e}");
        }
    }

    /// <summary>Classify a finished stroke. Returns the gesture class name, or null
    /// if Jackknife rejected it (below the auto rejection threshold) / not ready.</summary>
    public string Recognize(Stroke stroke)
    {
        if (!ready || _jk == null || stroke == null) return null;

        List<Vector2> pts2d = stroke.ProjectTo2DCameraPlane();
        if (pts2d == null || pts2d.Count < minPointCount)
        {
            Debug.Log($"[Jackknife] pre-filter reject: too few points ({pts2d?.Count ?? 0} < {minPointCount})");
            return null;
        }

        // ---- 2D bounding box pre-filter ----
        // Reject "purely vertical" strokes (just moving hand up/down). A real "?" or
        // circle has both horizontal AND vertical extent; a straight up-down motion
        // has width / height close to 0.
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 p in pts2d)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }
        float bboxW = maxX - minX;
        float bboxH = maxY - minY;
        if (minWidthHeightRatio > 0f && bboxH > 0.0001f)
        {
            float wh = bboxW / bboxH;
            if (wh < minWidthHeightRatio)
            {
                Debug.Log(
                    $"[Jackknife] pre-filter reject: bbox W/H={wh:F3} < {minWidthHeightRatio:F2} " +
                    $"(W={bboxW:F3}m, H={bboxH:F3}m -- looks like a straight vertical motion)"
                );
                return null;
            }
        }

        var traj = new List<JKVector>(pts2d.Count);
        foreach (Vector2 p in pts2d)
            traj.Add(new JKVector(new List<double> { p.x, p.y }));

        int gid;
        try
        {
            gid = _jk.Classify(traj);   // -1 == rejected
        }
        catch (Exception e)
        {
            Debug.LogError($"[Jackknife] Classify() threw: {e}");
            return null;
        }

        if (gid < 0 || !_idToName.TryGetValue(gid, out string name))
        {
            Debug.Log("[Jackknife] rejected (no class beat the rejection threshold)");
            return null;
        }

        // Reject labels: classes that were trained explicitly as "not a gesture".
        if (rejectLabels != null)
        {
            for (int i = 0; i < rejectLabels.Length; i++)
            {
                if (!string.IsNullOrEmpty(rejectLabels[i]) && rejectLabels[i] == name)
                {
                    Debug.Log($"[Jackknife] matched reject label '{name}' -> treating as null");
                    return null;
                }
            }
        }

        Debug.Log($"[Jackknife] recognized '{name}' (id={gid})");
        return name;
    }

    TemplateFile LoadFile()
    {
        if (!File.Exists(_saveFilePath)) return new TemplateFile();
        try
        {
            string json = File.ReadAllText(_saveFilePath);
            var f = JsonUtility.FromJson<TemplateFile>(json);
            return f ?? new TemplateFile();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Jackknife] load failed: {e.Message}");
            return new TemplateFile();
        }
    }
}
