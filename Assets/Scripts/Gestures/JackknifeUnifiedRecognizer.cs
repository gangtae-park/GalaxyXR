using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using JKBlades = Jackknife.JackknifeBlades;
using JKSample = Jackknife.Sample;
using JKVector = Jackknife.Vector;
using JKRecognizer = Jackknife.Jackknife;

/*
JackknifeUnifiedRecognizer

Single Jackknife instance trained on every referent's pose-trajectory template.
Used by the gesture recognition scene to classify pinch-bounded buffers into
one of:
    "Search/Find Info" | "Ask" | "Compare" | "Translate" | "Anchor" | "false"
(Capture and Save/Store are detected separately via static-pose components,
not this recognizer.)

Template format on disk (gesture_templates_unified.json):
    {
      "templates": [
        { "label": "...", "frames": [{ "values": [f0, f1, ...] }, ...] },
        ...
      ]
    }

Each "values" array length must equal featureDim (= handJoints.Length * 3
from HandFeatureSource). The first non-empty template fixes featureDim; later
templates with mismatched dim are skipped during Rebuild.
*/

public class JackknifeUnifiedRecognizer : MonoBehaviour
{
    [Header("Templates")]
    public string subDirectory = "GestureTemplates";
    public string saveFileName = "gesture_templates_unified.json";

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
    [Tooltip("Templates with these labels train Jackknife normally but Recognize() returns " +
             "null when they win. Use them to teach 'this is NOT a real referent'.")]
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
    public int LoadedTemplateCount => loadedTemplateCount;

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
            Debug.LogWarning($"[JackknifeUnified] no templates at {_saveFilePath}");
            return;
        }

        featureDim = -1;
        for (int i = 0; i < file.templates.Count && featureDim < 0; i++)
        {
            foreach (var f in file.templates[i].frames)
                if (f.values != null && f.values.Count > 0) { featureDim = f.values.Count; break; }
        }
        if (featureDim <= 0)
        {
            Debug.LogWarning("[JackknifeUnified] templates exist but every frame is empty.");
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
                Debug.LogWarning($"[JackknifeUnified] template #{i} (label='{t.label}') has mismatched dim. Skipping.");
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
                $"[JackknifeUnified] only {added} templates (need >= {minTemplatesToTrain}). " +
                "Not trained -- Recognize() returns null until more samples are recorded."
            );
            return;
        }

        try
        {
            _jk.Train(gpsrN, gpsrR, beta);
            ready = true;
            Debug.Log(
                $"[JackknifeUnified] trained on {added} templates, featureDim={featureDim}. " +
                $"classes=[{string.Join(", ", knownGestures)}]"
            );
        }
        catch (Exception e)
        {
            Debug.LogError($"[JackknifeUnified] Train() failed: {e}");
        }
    }

    public string Recognize(List<float[]> trajectory)
    {
        if (!ready || _jk == null || trajectory == null) return null;
        if (trajectory.Count < minFrameCount)
        {
            Debug.Log($"[JackknifeUnified] pre-filter reject: too few frames ({trajectory.Count} < {minFrameCount})");
            return null;
        }

        var traj = new List<JKVector>(trajectory.Count);
        foreach (var f in trajectory)
        {
            if (f == null || f.Length != featureDim)
            {
                Debug.LogWarning($"[JackknifeUnified] frame dim mismatch (got {f?.Length ?? 0}, expected {featureDim}).");
                return null;
            }
            var doubles = new List<double>(featureDim);
            for (int k = 0; k < featureDim; k++) doubles.Add(f[k]);
            traj.Add(new JKVector(doubles));
        }

        int gid;
        try { gid = _jk.Classify(traj); }
        catch (Exception e) { Debug.LogError($"[JackknifeUnified] Classify threw: {e}"); return null; }

        if (gid < 0 || !_idToName.TryGetValue(gid, out string name))
        {
            Debug.Log("[JackknifeUnified] rejected (no class beat the rejection threshold)");
            return null;
        }
        if (rejectLabels != null)
            for (int i = 0; i < rejectLabels.Length; i++)
                if (!string.IsNullOrEmpty(rejectLabels[i]) && rejectLabels[i] == name)
                {
                    Debug.Log($"[JackknifeUnified] matched reject label '{name}' -> null");
                    return null;
                }

        Debug.Log($"[JackknifeUnified] recognized '{name}' (id={gid})");
        return name;
    }

    /// <summary>
    /// Append a recorded trajectory to the templates file.
    /// `retrain` controls whether Jackknife is retrained immediately (expensive --
    /// scales as templates * gpsrN * gpsrR * resampleCount * featureDim). The
    /// recording scene should pass retrain=false to keep the save snappy; the
    /// inference scene calls Rebuild() once at startup.
    /// </summary>
    public bool AppendTemplate(string label, List<float[]> frames, bool retrain = true)
    {
        if (string.IsNullOrEmpty(label) || frames == null || frames.Count < 2)
        {
            Debug.LogWarning($"[JackknifeUnified] AppendTemplate rejected: label='{label}', frames={frames?.Count ?? 0}");
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

        if (!SaveFile(file))
        {
            // Save failed -- don't pretend we appended. UI will stay at the previous count.
            Debug.LogError(
                $"[JackknifeUnified] AppendTemplate('{label}') NOT saved to disk. " +
                $"Attempted path: {_saveFilePath}"
            );
            return false;
        }

        // Update the in-memory counter so UI can refresh without a full Rebuild.
        loadedTemplateCount = file.templates.Count;

        if (retrain) Rebuild();

        Debug.Log(
            $"[JackknifeUnified] appended '{label}' frames={frames.Count} " +
            $"featureDim={(frames.Count > 0 ? frames[0]?.Length ?? 0 : 0)}. " +
            $"Total templates: {file.templates.Count} (retrain={retrain})"
        );
        return true;
    }

    [ContextMenu("Clear All Templates")]
    public void ClearAllTemplates()
    {
        if (File.Exists(_saveFilePath))
        {
            try { File.Delete(_saveFilePath); }
            catch (Exception e) { Debug.LogError($"[JackknifeUnified] clear failed: {e}"); return; }
        }
        Rebuild();
        Debug.Log("[JackknifeUnified] all templates cleared.");
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
            Debug.LogWarning($"[JackknifeUnified] load failed: {e.Message}");
            return new TemplateFile();
        }
    }

    bool SaveFile(TemplateFile file)
    {
        try
        {
            string dir = Path.GetDirectoryName(_saveFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonUtility.ToJson(file, true);

            // Delete the existing file first if it's there. Some Android scoped-
            // storage states leave stale files with permissions that block direct
            // overwrite by File.WriteAllText; deleting then recreating sidesteps
            // it. The try block around Delete keeps us moving if the file is
            // missing or already unwritable -- the subsequent write will surface
            // the real error.
            try { if (File.Exists(_saveFilePath)) File.Delete(_saveFilePath); }
            catch (Exception delEx) { Debug.LogWarning($"[JackknifeUnified] pre-write delete failed (ignored): {delEx.Message}"); }

            File.WriteAllText(_saveFilePath, json);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[JackknifeUnified] save failed at '{_saveFilePath}': {e}");
            return false;
        }
    }

    [ContextMenu("Diagnose Save Path")]
    public void DiagnoseSavePath()
    {
        ResolvePath();
        Debug.Log($"[JackknifeUnified] save path = {_saveFilePath}");
        Debug.Log($"[JackknifeUnified] file exists = {File.Exists(_saveFilePath)}");
        string dir = Path.GetDirectoryName(_saveFilePath);
        Debug.Log($"[JackknifeUnified] dir exists = {Directory.Exists(dir)} ({dir})");
        // Try a probe write next to the target to see if the dir is writable.
        string probe = Path.Combine(dir ?? "", "_probe_" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Debug.Log("[JackknifeUnified] probe write OK -- directory is writable");
        }
        catch (Exception e)
        {
            Debug.LogError($"[JackknifeUnified] probe write FAILED: {e.Message}");
        }
    }
}
