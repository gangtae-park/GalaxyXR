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

    [Header("Bundled template bootstrap")]
    [Tooltip("Copies a bundled Resources template file into persistentDataPath when the runtime template file is missing or empty.")]
    public bool bootstrapFromResourcesIfMissing = true;
    [Tooltip("Resources path without extension, for example GestureTemplates/gesture_templates_unified.")]
    public string resourcesTemplatePath = "GestureTemplates/gesture_templates_unified";
    [Tooltip("Force-copy the bundled file over the persistent template file on startup. Leave off during recording sessions.")]
    public bool overwritePersistentWithBundledTemplates = false;

    [Header("Jackknife params")]
    public int resampleCount = 32;
    public int radius = 1;
    public bool useEuclidean = false;
    public double beta = 0.1;

    public int gpsrN = 6;
    public int gpsrR = 2;
    public int minTemplatesToTrain = 4;

    [Header("Training cache")]
    [Tooltip("Cache the per-template rejection thresholds (the only thing Train() computes) next to the template file. When the template file and Jackknife params are unchanged, Rebuild skips the Monte-Carlo training entirely -- from minutes to milliseconds at 160+ templates.")]
    public bool useThresholdCache = true;
    [Tooltip("When a valid threshold cache exists, run Rebuild automatically at scene start (it is cheap then).")]
    public bool autoRebuildOnStartWhenCached = true;
    [Tooltip("On a cache miss (fresh install / templates changed) automatically train on a BACKGROUND thread at scene start instead of waiting for the manual Rebuild button. The scene stays responsive; recognition turns on when training finishes and the cache makes every later launch instant. No manual step is ever needed.")]
    public bool autoTrainInBackgroundOnCacheMiss = true;
    [Tooltip("Monte-Carlo iterations for threshold learning (Jackknife paper uses 1000). Lower = faster cold Rebuild with slightly noisier rejection thresholds; 250 is a reasonable floor.")]
    public int trainIterations = 1000;

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
        // Only path resolution + Resources bootstrap happen automatically.
        // Rebuild() is now triggered manually from the StudyManagerCanvas
        // Rebuild button (wire the Button's onClick to Rebuild() in the
        // Inspector). This lets the study operator load templates on demand
        // instead of paying the disk read + Jackknife training cost at scene
        // start. Until Rebuild runs, `ready` stays false and Recognize()
        // returns null -- gestures are effectively disabled.
        ResolvePath();
        BootstrapTemplatesFromResourcesIfNeeded();

        // Fast path: if the threshold cache matches the current template file,
        // Rebuild costs only the disk read + feature extraction, so it is safe
        // to run at scene start. A stale/missing cache keeps the old manual
        // flow (Rebuild button) so the minutes-long training never blocks here.
        if (useThresholdCache && autoRebuildOnStartWhenCached && ThresholdCacheMatches())
        {
            Debug.Log("[StudyLog][JackknifeRecognizer] valid threshold cache found -- auto-rebuilding at start.");
            Rebuild();
        }
        else if (autoTrainInBackgroundOnCacheMiss)
        {
            Debug.Log("[StudyLog][JackknifeRecognizer] no valid threshold cache -- training in background.");
            RebuildInBackground();
        }
    }

    void ResolvePath()
    {
        string dir = Application.persistentDataPath;
        if (!string.IsNullOrEmpty(subDirectory)) dir = Path.Combine(dir, subDirectory);
        _saveFilePath = Path.Combine(dir, saveFileName);
    }

    void BootstrapTemplatesFromResourcesIfNeeded()
    {
        if (!bootstrapFromResourcesIfMissing) return;

        bool shouldCopy = overwritePersistentWithBundledTemplates || !File.Exists(_saveFilePath);
        if (!shouldCopy)
        {
            TemplateFile existing = LoadFile();
            shouldCopy = existing.templates == null || existing.templates.Count == 0;
        }
        if (!shouldCopy) return;

        TextAsset bundled = Resources.Load<TextAsset>(resourcesTemplatePath);
        if (bundled == null)
        {
            Debug.LogWarning($"[JackknifeUnified] bundled template not found at Resources/{resourcesTemplatePath}.json");
            return;
        }

        try
        {
            string dir = Path.GetDirectoryName(_saveFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_saveFilePath, bundled.text);
            Debug.Log($"[JackknifeUnified] bootstrapped templates from Resources/{resourcesTemplatePath}.json to {_saveFilePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[JackknifeUnified] template bootstrap failed at '{_saveFilePath}': {e}");
        }
    }

    private Coroutine _rebuildRoutine;

    [ContextMenu("Rebuild From Disk")]
    public void Rebuild()
    {
        if (_rebuildRoutine != null)
        {
            Debug.LogWarning("[StudyLog][JackknifeRecognizer] background rebuild already running; ignoring manual Rebuild.");
            return;
        }
        int added = BuildRecognizer(out string configHash);
        if (added < 0) return;
        if (TryApplyCachedThresholds(configHash, added)) return;
        TrainSync(added, configHash);
    }

    /// <summary>Non-blocking Rebuild: the build/cache phase runs on the main
    /// thread (fast), and on a cache miss Train() runs on a worker thread so
    /// scene load / interaction never stalls. `ready` flips on completion.</summary>
    public void RebuildInBackground()
    {
        if (_rebuildRoutine != null) return;
        _rebuildRoutine = StartCoroutine(RebuildRoutine());
    }

    System.Collections.IEnumerator RebuildRoutine()
    {
        int added = BuildRecognizer(out string configHash);
        if (added < 0 || TryApplyCachedThresholds(configHash, added))
        {
            _rebuildRoutine = null;
            yield break;
        }

        JKRecognizer jk = _jk;
        int iters = Mathf.Max(50, trainIterations);
        int n = gpsrN, r = gpsrR;
        double b = beta;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = System.Threading.Tasks.Task.Run(() => jk.Train(n, r, b, iters));
        while (!task.IsCompleted) yield return null;

        if (task.IsFaulted)
        {
            Debug.LogError($"[StudyLog][JackknifeRecognizer] background Train() failed: {task.Exception}");
        }
        else if (jk == _jk) // recognizer not replaced by another Rebuild meanwhile
        {
            ready = true;
            SaveThresholdCache(configHash);
            Debug.Log(
                $"[StudyLog][JackknifeRecognizer] background training done: {added} templates in " +
                $"{sw.Elapsed.TotalSeconds:F1}s. classes=[{string.Join(", ", knownGestures)}]"
            );
        }
        _rebuildRoutine = null;
    }

    /// <summary>Load + feature-extract all templates (fast). Returns the added
    /// template count, or -1 when there is nothing usable to train on.</summary>
    int BuildRecognizer(out string configHash)
    {
        configHash = "";
        ready = false;
        _idToName.Clear();

        TemplateFile file = LoadFile();
        loadedTemplateCount = file.templates.Count;
        if (file.templates.Count == 0)
        {
            featureDim = -1;
            Debug.LogWarning($"[StudyLog][JackknifeRecognizer] no templates at {_saveFilePath}");
            return -1;
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
            return -1;
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
            return -1;
        }

        configHash = ComputeConfigHash();
        return added;
    }

    bool TryApplyCachedThresholds(string configHash, int added)
    {
        if (!useThresholdCache) return false;
        if (!TryLoadThresholdCache(configHash, out double[] cachedThresholds)) return false;
        if (!_jk.SetRejectionThresholds(cachedThresholds)) return false;
        ready = true;
        Debug.Log(
            $"[StudyLog][JackknifeRecognizer] loaded {added} templates, thresholds from cache " +
            $"(training skipped). featureDim={featureDim}, classes=[{string.Join(", ", knownGestures)}]"
        );
        return true;
    }

    void TrainSync(int added, string configHash)
    {
        try
        {
            float t0 = Time.realtimeSinceStartup;
            _jk.Train(gpsrN, gpsrR, beta, Mathf.Max(50, trainIterations));
            ready = true;
            Debug.Log(
                $"[StudyLog][JackknifeRecognizer] trained on {added} templates in " +
                $"{Time.realtimeSinceStartup - t0:F1}s, featureDim={featureDim}. " +
                $"classes=[{string.Join(", ", knownGestures)}]"
            );
            if (useThresholdCache) SaveThresholdCache(configHash);
        }
        catch (Exception e)
        {
            Debug.LogError($"[StudyLog][JackknifeRecognizer] Train() failed: {e}");
        }
    }

    // ---------- Threshold cache ----------
    // Train() only learns one rejection threshold per template; everything
    // else Rebuild does (feature extraction) is fast. So we persist the
    // thresholds keyed by a hash of the template file + all params that
    // influence training, and skip Train when nothing changed.

    [Serializable]
    private class ThresholdCache
    {
        public string hash;
        public double[] thresholds;
    }

    string ThresholdCachePath => _saveFilePath + ".thresholds.json";

    string ComputeConfigHash()
    {
        try
        {
            string raw = File.Exists(_saveFilePath) ? File.ReadAllText(_saveFilePath) : "";
            string config = $"|rs={resampleCount}|r={radius}|eu={useEuclidean}|b={beta}" +
                            $"|n={gpsrN}|g={gpsrR}|it={Mathf.Max(50, trainIterations)}|dim={featureDim}";
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw + config));
                return BitConverter.ToString(h).Replace("-", "");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StudyLog][JackknifeRecognizer] cache hash failed: {e.Message}");
            return "";
        }
    }

    bool TryLoadThresholdCache(string configHash, out double[] thresholds)
    {
        thresholds = null;
        if (string.IsNullOrEmpty(configHash) || !File.Exists(ThresholdCachePath)) return false;
        try
        {
            var cache = JsonUtility.FromJson<ThresholdCache>(File.ReadAllText(ThresholdCachePath));
            if (cache == null || cache.hash != configHash || cache.thresholds == null) return false;
            thresholds = cache.thresholds;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StudyLog][JackknifeRecognizer] cache load failed: {e.Message}");
            return false;
        }
    }

    void SaveThresholdCache(string configHash)
    {
        if (string.IsNullOrEmpty(configHash) || _jk == null) return;
        try
        {
            var cache = new ThresholdCache { hash = configHash, thresholds = _jk.GetRejectionThresholds() };
            File.WriteAllText(ThresholdCachePath, JsonUtility.ToJson(cache));
            Debug.Log($"[StudyLog][JackknifeRecognizer] threshold cache saved ({cache.thresholds.Length} entries).");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StudyLog][JackknifeRecognizer] cache save failed: {e.Message}");
        }
    }

    // Cheap pre-check for Awake: does a cache exist that matches the current
    // template file? featureDim is not known before Rebuild, so it is probed
    // the same way Rebuild does (first non-empty frame).
    bool ThresholdCacheMatches()
    {
        if (!File.Exists(ThresholdCachePath) || !File.Exists(_saveFilePath)) return false;
        TemplateFile file = LoadFile();
        featureDim = -1;
        for (int i = 0; i < file.templates.Count && featureDim < 0; i++)
        {
            foreach (var f in file.templates[i].frames)
                if (f.values != null && f.values.Count > 0) { featureDim = f.values.Count; break; }
        }
        if (featureDim <= 0) return false;
        return TryLoadThresholdCache(ComputeConfigHash(), out _);
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
    The inference scene loads templates on demand -- the StudyManagerCanvas
    Rebuild button is wired to Rebuild() and must be pressed once before
    Recognize() will return anything.
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
