using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
TemplateRecordController

Drives the template recording UI in TemplateRecordScene:
  - Reads the selected referent label from a TMP_Dropdown.
  - On "Start Recording" click, runs a coroutine that:
      1) shows a 3-2-1 countdown on statusText
      2) captures hand-pose feature frames for `recordingSeconds`
      3) hands the captured frames to JackknifeUnifiedRecognizer.AppendTemplate
      4) updates statusText / countText with the result
  - Disables the Start button while busy so double-presses are ignored.

The countdown values, recording duration, and frame interval are inspector-tunable
so the same prefab can be reused for quick/slow/long-recording variants.
*/

public class TemplateRecordController : MonoBehaviour
{
    [Header("UI references")]
    public TMP_Dropdown referentDropdown;
    public TMP_Text statusText;
    public TMP_Text countText;
    public Button startButton;

    [Header("Capture")]
    public HandFeatureSource featureSource;
    public JackknifeUnifiedRecognizer recognizer;

    [Header("Timing")]
    [Tooltip("Number of one-second countdown beats before recording starts.")]
    [Range(0, 10)] public int countdownSeconds = 3;
    [Tooltip("How long the actual capture window lasts.")]
    public float recordingSeconds = 2.0f;
    [Tooltip("Minimum interval between feature samples during recording.")]
    public float minFrameInterval = 0.03f;
    [Tooltip("How long the 'Saved!' message stays before resetting to Ready.")]
    public float postSaveSeconds = 1.2f;
    [Tooltip("Short pause between 'GO!' and the actual recording start.")]
    public float goHoldSeconds = 0.3f;

    [Header("Status colors")]
    public Color readyColor = new Color(0.78f, 0.78f, 0.85f);
    public Color countdownColor = new Color(1.0f, 0.85f, 0.20f);
    public Color recordingColor = new Color(0.40f, 1.00f, 0.40f);
    public Color savedColor = Color.white;
    public Color errorColor = new Color(1.0f, 0.5f, 0.4f);

    [Header("Status font sizes")]
    [Tooltip("Font size used for short numeric / single-word states (countdown digits, GO!, Saved!).")]
    public float bigFontSize = 96f;
    [Tooltip("Font size for longer messages (Ready, Recording..., errors).")]
    public float smallFontSize = 40f;

    private bool _busy;
    private readonly List<float[]> _frames = new List<float[]>(256);

    void OnEnable()
    {
        if (startButton != null) startButton.onClick.AddListener(OnStartButtonClicked);
        SetIdle();
        UpdateCount();
    }

    void OnDisable()
    {
        if (startButton != null) startButton.onClick.RemoveListener(OnStartButtonClicked);
    }

    void OnStartButtonClicked()
    {
        if (_busy) return;
        if (featureSource == null || recognizer == null)
        {
            SetStatus("REFS MISSING", errorColor, smallFontSize);
            Debug.LogError("[TemplateRecord] featureSource or recognizer not assigned.");
            return;
        }
        StartCoroutine(RecordRoutine());
    }

    IEnumerator RecordRoutine()
    {
        _busy = true;
        if (startButton != null) startButton.interactable = false;

        string label = ReadSelectedLabel();
        Debug.Log($"[TemplateRecord] Begin: label='{label}', countdown={countdownSeconds}s, record={recordingSeconds}s");

        for (int i = countdownSeconds; i > 0; i--)
        {
            SetStatus(i.ToString(), countdownColor, bigFontSize);
            yield return new WaitForSeconds(1f);
        }
        SetStatus("GO!", countdownColor, bigFontSize);
        yield return new WaitForSeconds(goHoldSeconds);

        SetStatus("Recording...", recordingColor, smallFontSize);
        _frames.Clear();
        float startTime = Time.time;
        float lastSample = -1f;
        while (Time.time - startTime < recordingSeconds)
        {
            float now = Time.time;
            if (lastSample < 0f || (now - lastSample) >= minFrameInterval)
            {
                float[] f = featureSource.BuildFeatureFrame();
                if (f != null) _frames.Add(f);
                lastSample = now;
            }
            yield return null;
        }

        Debug.Log($"[TemplateRecord] Captured {_frames.Count} frames for label='{label}'");

        if (_frames.Count < 2)
        {
            SetStatus("TOO FEW FRAMES", errorColor, smallFontSize);
        }
        else
        {
            bool saved = recognizer.AppendTemplate(label, _frames, false);
            SetStatus(saved ? "Saved!" : "SAVE FAILED", saved ? savedColor : errorColor, bigFontSize);
        }
        UpdateCount();

        yield return new WaitForSeconds(postSaveSeconds);
        SetIdle();
        if (startButton != null) startButton.interactable = true;
        _busy = false;
    }

    string ReadSelectedLabel()
    {
        if (referentDropdown == null || referentDropdown.options == null
            || referentDropdown.options.Count == 0)
        {
            Debug.LogWarning("[TemplateRecord] dropdown empty; defaulting to 'Translate'.");
            return "Translate";
        }
        int idx = referentDropdown.value;
        if (idx < 0 || idx >= referentDropdown.options.Count) return "Translate";
        return referentDropdown.options[idx].text;
    }

    void SetIdle() => SetStatus("Ready", readyColor, smallFontSize);

    void SetStatus(string text, Color color, float fontSize)
    {
        if (statusText == null) return;
        statusText.text = text;
        statusText.color = color;
        statusText.fontSize = fontSize;
    }

    void UpdateCount()
    {
        if (countText == null || recognizer == null) return;
        countText.text = $"Total: {recognizer.LoadedTemplateCount} templates";
    }
}
