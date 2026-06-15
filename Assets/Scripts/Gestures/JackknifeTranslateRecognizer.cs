using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using JKBlades = Jackknife.JackknifeBlades;
using JKSample = Jackknife.Sample;
using JKVector = Jackknife.Vector;
using JKRecognizer = Jackknife.Jackknife;

/*
JackknifeTranslateRecognizer

Separate Jackknife instance dedicated to the Translate gesture. Trajectories
are multi-dimensional per-frame feature vectors -- typically the concatenated
positions of every hand joint (21+ joints x 3 coords). This mirrors the way
the public Jackknife datasets (e.g. jk2017 Leap Motion) are organised: one
frame per line, many floats per line.

Templates live in their own file (gesture_templates_translate.json), so
training/tuning is independent from JackknifeGestureRecognizer.

API:
  Recognize(List<float[]>)          -> class name or null
  AppendTemplate(string, List<float[]>) -> save + retrain

Templates and inference must share:
  - the same number of joints (feature_dim per frame)
  - the same normalisation convention (joint origin + camera-frame rotation)
TranslateGestureDetector enforces this.
*/

public class JackknifeTranslateRecognizer : MonoBehaviour
{
    [Header("Templates")]
    public string subDirectory = "GestureTemplates";
    public string saveFileName = "gesture_templates_translate.json";

    [Header("Jackknife params")]
    public int resampleCount = 32;
    public int radius = 1;
    public bool useEuclidean = false;
    public double beta = 0.1;

    public int gpsrN = 6;
    public int gpsrR = 2;
    public int minTemplatesToTrain = 4;

    [Header("Pre-filter")]
    public int minFrameCount = 8;

    [Header("Reject labels")]
    public string[] rejectLabels = new string[] { "false" };

    [Header("Status")]
    [SerializeField] private int loadedTemplateCount;
    [SerializeField] private int featureDim = -1;
    [SerializeField] private string[] knownGestures = new string[0];
    [SerializeField] private bool ready;

    private JKRecognizer _jk;
    private readonly Dictionary<int, string> _idToName = new Dictionary<int, string>();
    private string _saveFilePath;

    public bool IsReady => ready;
    public string SaveFilePath => _saveFilePath;
    public string[] KnownGestures => knownGestures;
    public int FeatureDim => featureDim;

    [Serializable]
    private class FrameData
    {
        public List<float> values = new List<float>();
    }

    [Serializable]
    private class TemplateEntry
    {
        public string label;
        public List<FrameData> frames = new List<FrameData>();
    }

    [Serializable]
    private class TemplateFile
    {
        public List<TemplateEntry> templates = new List<TemplateEntry>();
    }

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

    [ContextMenu("Rebuild From Disk")]
    public void Rebuild()
    {
        ready = false;
        _idToName.Clear();

        TemplateFile file = LoadFile();
        loadedTemplateCount = file.templates.Count;
        if (file.templates.Count == 0)
        {
            featureDim = -1;
            Debug.LogWarning($"[JackknifeTranslate] no templates at {_saveFilePath}");
            return;
        }

        // The first non-empty template defines the feature dimension; everything
        // else has to match.
        featureDim = -1;
        for (int i = 0; i < file.templates.Count && featureDim < 0; i++)
        {
            foreach (var f in file.templates[i].frames)
                if (f.values != null && f.values.Count > 0) { featureDim = f.values.Count; break; }
        }
        if (featureDim <= 0)
        {
            Debug.LogWarning("[JackknifeTranslate] templates exist but every frame is empty.");
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
            if (t.frames == null || t.frames.Count < 2) continue;

            string gname = string.IsNullOrEmpty(t.label) ? "Unknown" : t.label;
            if (!nameToId.TryGetValue(gname, out int gid))
            {
                gid = nameToId.Count;
                nameToId[gname] = gid;
                _idToName[gid] = gname;
            }

            var sample = new JKSample(0, gid, i);
            var traj = new List<JKVector>(t.frames.Count);
            bool dimMismatch = false;
            foreach (var fr in t.frames)
            {
                if (fr.values == null || fr.values.Count != featureDim) { dimMismatch = true; break; }
                var doubles = new List<double>(featureDim);
                for (int k = 0; k < featureDim; k++) doubles.Add(fr.values[k]);
                traj.Add(new JKVector(doubles));
            }
            if (dimMismatch)
            {
                Debug.LogWarning($"[JackknifeTranslate] template #{i} has mismatched feature dim. Skipping.");
                continue;
            }
            sample.AddTrajectory(traj);
            _jk.AddTemplate(sample);
            added++;
        }

        knownGestures = new List<string>(nameToId.Keys).ToArray();

        if (added < minTemplatesToTrain)
        {
            Debug.LogWarning(
                $"[JackknifeTranslate] only {added} templates (need >= {minTemplatesToTrain}). " +
                "Not trained -- Recognize() returns null until more samples are recorded."
            );
            return;
        }

        try
        {
            _jk.Train(gpsrN, gpsrR, beta);
            ready = true;
            Debug.Log(
                $"[JackknifeTranslate] trained on {added} templates, featureDim={featureDim}. " +
                $"classes=[{string.Join(", ", knownGestures)}]"
            );
        }
        catch (Exception e)
        {
            Debug.LogError($"[JackknifeTranslate] Train() failed: {e}");
        }
    }

    /// <summary>Classify a per-frame feature trajectory. Each frame is a float[]
    /// whose length must match the trained featureDim. Returns the class name
    /// (e.g. "Translate") or null on rejection / not ready.</summary>
    public string Recognize(List<float[]> trajectory)
    {
        if (!ready || _jk == null || trajectory == null) return null;
        if (trajectory.Count < minFrameCount)
        {
            Debug.Log($"[JackknifeTranslate] pre-filter reject: too few frames ({trajectory.Count} < {minFrameCount})");
            return null;
        }

        var traj = new List<JKVector>(trajectory.Count);
        foreach (var f in trajectory)
        {
            if (f == null || f.Length != featureDim)
            {
                Debug.LogWarning($"[JackknifeTranslate] frame dim mismatch (got {f?.Length ?? 0}, expected {featureDim}).");
                return null;
            }
            var doubles = new List<double>(featureDim);
            for (int k = 0; k < featureDim; k++) doubles.Add(f[k]);
            traj.Add(new JKVector(doubles));
        }

        int gid;
        try { gid = _jk.Classify(traj); }
        catch (Exception e) { Debug.LogError($"[JackknifeTranslate] Classify threw: {e}"); return null; }

        if (gid < 0 || !_idToName.TryGetValue(gid, out string name))
        {
            Debug.Log("[JackknifeTranslate] rejected (no class beat the rejection threshold)");
            return null;
        }
        if (rejectLabels != null)
        {
            for (int i = 0; i < rejectLabels.Length; i++)
                if (!string.IsNullOrEmpty(rejectLabels[i]) && rejectLabels[i] == name)
                {
                    Debug.Log($"[JackknifeTranslate] matched reject label '{name}' -> null");
                    return null;
                }
        }

        Debug.Log($"[JackknifeTranslate] recognized '{name}' (id={gid})");
        return name;
    }

    /// <summary>Append a recorded trajectory to disk as a template and retrain.</summary>
    public bool AppendTemplate(string label, List<float[]> frames)
    {
        if (string.IsNullOrEmpty(label) || frames == null || frames.Count < 2)
        {
            Debug.LogWarning($"[JackknifeTranslate] AppendTemplate rejected: label='{label}', frames={frames?.Count ?? 0}");
            return false;
        }
        TemplateFile file = LoadFile();

        var entry = new TemplateEntry { label = label };
        foreach (var f in frames)
        {
            var fd = new FrameData();
            if (f != null) for (int k = 0; k < f.Length; k++) fd.values.Add(f[k]);
            entry.frames.Add(fd);
        }
        file.templates.Add(entry);
        SaveFile(file);
        Rebuild();
        Debug.Log($"[JackknifeTranslate] appended template label='{label}' frames={frames.Count} featureDim={(frames.Count > 0 ? frames[0]?.Length ?? 0 : 0)}. Total templates: {file.templates.Count}");
        return true;
    }

    [ContextMenu("Clear All Templates")]
    public void ClearAllTemplates()
    {
        if (File.Exists(_saveFilePath))
        {
            try { File.Delete(_saveFilePath); }
            catch (Exception e) { Debug.LogError($"[JackknifeTranslate] clear failed: {e}"); return; }
        }
        Rebuild();
        Debug.Log("[JackknifeTranslate] all templates cleared.");
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
            Debug.LogWarning($"[JackknifeTranslate] load failed: {e.Message}");
            return new TemplateFile();
        }
    }

    void SaveFile(TemplateFile file)
    {
        try
        {
            string dir = Path.GetDirectoryName(_saveFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonUtility.ToJson(file, true);
            File.WriteAllText(_saveFilePath, json);
        }
        catch (Exception e) { Debug.LogError($"[JackknifeTranslate] save failed: {e}"); }
    }
}
