using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using JKBlades = Jackknife.JackknifeBlades;
using JKSample = Jackknife.Sample;
using JKVector = Jackknife.Vector;
using JKRecognizer = Jackknife.Jackknife;

/*
Single Jackknife instance trained on every referent's pose-trajectory template.
Used by the gesture recognition scene to classify pinch-bounded buffers into one of the refrents and "false".

Template format on disk (gesture_templates_unified.json):
    {
        "templates": [
            { "label": "...", "frames": [{ "values": [f0, f1, ...] }, ...] },
            ...
        ]
    }

Each "values" array length must equal featureDim (= handJoints.Length * 3 from HandFeatureSource).
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
    public string[] rejectLabels = new string[] { "False" };

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
            Debug.LogWarning($"[StudyLog][JackknifeRecognizer] no templates at {_saveFilePath}");
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
            Debug.LogWarning("[StudyLog][JackknifeRecognizer] templates exist but every frame is empty.");
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
                Debug.LogWarning($"[StudyLog][JackknifeRecognizer] template #{i} (label='{t.label}') has mismatched dim. Skipping.");
                continue;
            }
            sample.AddTrajectory(traj);
            _jk.AddTemplate(sample);
            added++;
        }

        knownGestures = new List<string>(nameToId.Keys).ToArray();

        if (added < minTemplatesToTrain)
        {
            Debug.LogWarning($"[StudyLog][JackknifeRecognizer] only {added} templates (need >= {minTemplatesToTrain}). ");
            return;
        }

        try
        {
            _jk.Train(gpsrN, gpsrR, beta);
            ready = true;
            Debug.Log(
                $"[StudyLog][JackknifeRecognizer] trained on {added} templates, featureDim={featureDim}. " +
                $"classes=[{string.Join(", ", knownGestures)}]"
            );
        }
        catch (Exception e)
        {
            Debug.LogError($"[StudyLog][JackknifeRecognizer] Train() failed: {e}");
        }
    }

    public string Recognize(List<float[]> trajectory)
    {
        if (!ready || _jk == null || trajectory == null) return null;
        if (trajectory.Count < minFrameCount)
        {
            Debug.Log($"[StudyLog][JackknifeRecognizer] REJECT: too few frames ({trajectory.Count} < {minFrameCount})");
            return null;
        }

        var traj = new List<JKVector>(trajectory.Count);
        foreach (var f in trajectory)
        {
            if (f == null || f.Length != featureDim)
            {
                Debug.LogWarning($"[StudyLog][JackknifeRecognizer] REJECT: frame dim mismatch (got {f?.Length ?? 0}, expected {featureDim}).");
                return null;
            }
            var doubles = new List<double>(featureDim);
            for (int k = 0; k < featureDim; k++) doubles.Add(f[k]);
            traj.Add(new JKVector(doubles));
        }

        int gid;
        try { gid = _jk.Classify(traj); }
        catch (Exception e) { Debug.LogError($"[StudyLog][JackknifeRecognizer] Classify threw: {e}"); return null; }

        if (gid < 0 || !_idToName.TryGetValue(gid, out string name))
        {
            Debug.Log("[StudyLog][JackknifeRecognizer] REJECT: no class beat the rejection threshold");
            return null;
        }
        if (rejectLabels != null)
            for (int i = 0; i < rejectLabels.Length; i++)
                if (!string.IsNullOrEmpty(rejectLabels[i]) && rejectLabels[i] == name)
                {
                    Debug.Log($"[StudyLog][JackknifeRecognizer] REJECT: matched reject label '{name}'");
                    return null;
                }

        Debug.Log($"[StudyLog][JackknifeRecognizer] RECOGNIZED: '{name}'");
        return name;
    }

    /*
    Append a recorded trajectory to the templates file.
    `retrain` controls whether Jackknife is retrained immediately.
    The inference scene calls Rebuild() once at startup.
    */

    public bool AppendTemplate(string label, List<float[]> frames, bool retrain = true)
    {
        if (string.IsNullOrEmpty(label) || frames == null || frames.Count < 2)
        {
            Debug.LogWarning($"[JackknifeRecorder] AppendTemplate REJECT: label='{label}', frames={frames?.Count ?? 0}");
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
            // Save failed. UI will stay at the previous count.
            Debug.LogError(
                $"[JackknifeRecorder] AppendTemplate('{label}') NOT saved to disk. " +
                $"Attempted path: {_saveFilePath}"
            );
            return false;
        }

        // Update the in-memory counter so UI can refresh without a full Rebuild.
        loadedTemplateCount = file.templates.Count;

        if (retrain) Rebuild();

        Debug.Log(
            $"[JackknifeRecorder] AppendTemplate '{label}' frames={frames.Count} | " +
            $"Total templates: {file.templates.Count}"
        );
        return true;
    }

    [ContextMenu("Clear All Templates")]
    public void ClearAllTemplates()
    {
        if (File.Exists(_saveFilePath))
        {
            try { File.Delete(_saveFilePath); }
            catch (Exception e) { Debug.LogError($"[JackknifeRecorder] clear failed: {e}"); return; }
        }
        Rebuild();
        Debug.Log("[JackknifeRecorder] all templates cleared.");
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
            Debug.LogWarning($"[StudyLog][JackknifeRecorder] load failed: {e.Message}");
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

            try { if (File.Exists(_saveFilePath)) File.Delete(_saveFilePath); }
            catch (Exception delEx) { Debug.LogWarning($"[JackknifeRecorder] pre-write delete failed (ignored): {delEx.Message}"); }

            File.WriteAllText(_saveFilePath, json);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[JackknifeRecorder] save failed at '{_saveFilePath}': {e}");
            return false;
        }
    }

    [ContextMenu("Diagnose Save Path")]
    public void DiagnoseSavePath()
    {
        ResolvePath();
        Debug.Log($"[JackknifeRecorder] save path = {_saveFilePath}");
        Debug.Log($"[JackknifeRecorder] file exists = {File.Exists(_saveFilePath)}");
        string dir = Path.GetDirectoryName(_saveFilePath);
        Debug.Log($"[JackknifeRecorder] dir exists = {Directory.Exists(dir)} ({dir})");
        // Try a probe write next to the target to see if the dir is writable.
        string probe = Path.Combine(dir ?? "", "_probe_" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Debug.Log("[JackknifeRecorder] probe write OK -- directory is writable");
        }
        catch (Exception e)
        {
            Debug.LogError($"[JackknifeRecorder] probe write FAILED: {e.Message}");
        }
    }
}
