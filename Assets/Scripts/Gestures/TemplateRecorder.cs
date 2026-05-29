using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

/*
Records the next pinch stroke as a "?" template and persists it to a JSON file
in Application.persistentDataPath. Loads any saved templates back into the
connected QuestionMarkClassifier on Awake.

Triggering record mode:
    - Set `recordOnNextStroke = true` from the Inspector at runtime, OR 
    - Press the keyboard key (Editor / Standalone), OR
    - Trigger the optional InputActionReference (e.g. controller button on headset).

After triggering, the NEXT pinch you do will be saved as a template (not classified).
You'll see "[TemplateRecorder] saved template ..." in the log.
*/

public class TemplateRecorder : MonoBehaviour
{
    [Header("References")]
    public PinchStrokeCapture strokeCapture;
    public JackknifeGestureRecognizer jackknifeRecognizer;

    [Header("Gesture being recorded")]
    [Tooltip("Search, Ask")]
    public string referentLabel = "Ask";

    [Header("Recording trigger")]
    public bool recordOnNextStroke = false;
    public InputActionReference recordingAction;

    [Header("Storage")]
    public string subDirectory = "GestureTemplates";
    public string saveFileName = "gesture_templates.json";
    public bool resetOnNextRecord = false;

    [Header("Status")]
    [SerializeField] private int loadedTemplateCount = 0;
    [SerializeField] private string saveFilePath;
    [SerializeField] private string adbPullHint;

    [Serializable]
    private class TemplateFile
    {
        public List<TemplateEntry> templates = new List<TemplateEntry>();
    }

    [Serializable]
    private class TemplateEntry
    {
        public string label;
        public List<Vector2> points = new List<Vector2>();
    }

    void Awake()
    {
        ResolvePathAndEnsureDir();
        LogPathInfo();
        LoadFromDisk();
    }

    /*
    Build the full save path from persistentDataPath + subDirectory + saveFileName,
    creating the directory if necessary.
    */

    void ResolvePathAndEnsureDir()
    {
        string dir = Application.persistentDataPath;
        if (!string.IsNullOrEmpty(subDirectory))
        {
            dir = Path.Combine(dir, subDirectory);
        }
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TemplateRecorder] could not create directory '{dir}': {e.Message}");
        }
        saveFilePath = Path.Combine(dir, saveFileName);

// ADB
#if UNITY_ANDROID && !UNITY_EDITOR
    string pkg = Application.identifier; 
    adbPullHint =
        $"adb pull \"/storage/emulated/0/Android/data/{pkg}/files/" +
        (string.IsNullOrEmpty(subDirectory) ? "" : subDirectory + "/") +
        $"{saveFileName}\" ./";
#else
    adbPullHint = "(not Android build)";
#endif
    }

    [ContextMenu("Log Path Info")]
    public void LogPathInfo()
    {
        Debug.Log(
            "[TemplateRecorder] Save path resolution\n" +
            $"  full saveFilePath = {saveFilePath}\n" +
            $"  Application.identifier = {Application.identifier}\n" +
            $"  ADB pull hint       = {adbPullHint}"
        );
    }

    void OnEnable()
    {
        if (strokeCapture != null)
        {
            strokeCapture.OnStrokeCompleted += HandleStrokeCompleted;
        }
        recordingAction?.action.Enable();
    }

    void OnDisable()
    {
        if (strokeCapture != null)
        {
            strokeCapture.OnStrokeCompleted -= HandleStrokeCompleted;
        }
        recordingAction?.action.Disable();
    }

    void Update()
    {
        if (recordingAction != null && recordingAction.action != null &&
            recordingAction.action.WasPressedThisFrame())
        {
            ArmRecording();
        }
    }

    public void ArmRecording()
    {
        recordOnNextStroke = true;
        Debug.Log("[TemplateRecorder] ARMED - next pinch will be saved");
    }

    void HandleStrokeCompleted(Stroke stroke)
    {
        if (!recordOnNextStroke) return;
        recordOnNextStroke = false;

        if (stroke == null) return;

        var pts = stroke.ProjectTo2DCameraPlane();
        if (pts.Count < 5)
        {
            Debug.LogWarning("[TemplateRecorder] projected stroke too short, not saving.");
            return;
        }

        TemplateFile file = LoadFile();
        if (resetOnNextRecord)
        {
            file.templates.Clear();
            resetOnNextRecord = false;
            Debug.Log("[TemplateRecorder] cleared previous templates");
        }

        // label = gesture CLASS NAME
        var entry = new TemplateEntry
        {
            label = string.IsNullOrEmpty(referentLabel) ? "Unknown" : referentLabel,
            points = new List<Vector2>(pts),
        };
        file.templates.Add(entry);
        SaveFile(file);

        int sameClass = 0;
        foreach (var t in file.templates) if (t.label == entry.label) sameClass++;

        Debug.Log($"[TemplateRecorder] saved '{entry.label}' sample #{sameClass} " +
                  $"({pts.Count} pts). total file entries: {file.templates.Count}");

        // Live rebuild so the new sample takes effect without a restart.
        if (jackknifeRecognizer != null)
        {
            jackknifeRecognizer.Rebuild();
            Debug.Log("[TemplateRecorder] rebuilt Jackknife recognizer");
        }

        loadedTemplateCount = file.templates.Count;
    }

    void LoadFromDisk()
    {
        TemplateFile file = LoadFile();
        loadedTemplateCount = file.templates.Count;
        Debug.Log($"[TemplateRecorder] loaded {loadedTemplateCount} templates from {saveFilePath}");
    }

    TemplateFile LoadFile()
    {
        if (!File.Exists(saveFilePath)) return new TemplateFile();
        try
        {
            string json = File.ReadAllText(saveFilePath);
            var f = JsonUtility.FromJson<TemplateFile>(json);
            return f ?? new TemplateFile();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TemplateRecorder] load failed: {e.Message}");
            return new TemplateFile();
        }
    }

    void SaveFile(TemplateFile file)
    {
        try
        {
            string json = JsonUtility.ToJson(file, prettyPrint: true);
            File.WriteAllText(saveFilePath, json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[TemplateRecorder] save failed: {e.Message}");
        }
    }

}
