using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
Drives the template recording UI in TemplateRecordScene:
    - Reads the selected referent label.
    - On "Start Recording" click, runs a coroutine that:
        1) shows a 3-2-1 countdown on statusText
        2) captures hand-pose feature frames for `recordingSeconds`
        3) hands the captured frames to JackknifeUnifiedRecognizer.AppendTemplate
        4) updates statusText / countText with the result
    - Disables the Start button while busy so double-presses are ignored.
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
    [Range(0, 10)] public int countdownSeconds = 3;
    public float recordingSeconds = 2.0f;
    public float minFrameInterval = 0.03f;
    public float postSaveSeconds = 1.2f;
    public float goHoldSeconds = 0.2f;

    [Header("Status colors")]
    public Color readyColor = new Color(0.78f, 0.78f, 0.85f);
    public Color countdownColor = new Color(1.0f, 0.85f, 0.20f);
    public Color recordingColor = new Color(0.40f, 1.00f, 0.40f);
    public Color savedColor = Color.white;
    public Color errorColor = new Color(1.0f, 0.5f, 0.4f);

    [Header("Status font sizes")]
    public float bigFontSize = 96f;
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
            Debug.LogError("[JackknifeRecorder] featureSource or recognizer not assigned.");
            return;
        }
        StartCoroutine(RecordRoutine());
    }

    IEnumerator RecordRoutine()
    {
        _busy = true;
        if (startButton != null) startButton.interactable = false;

        string label = ReadSelectedLabel();
        Debug.Log($"[JackknifeRecorder] START RECORDING: label='{label}'");

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

        Debug.Log($"[JackknifeRecorder] END RECORDING: Captured {_frames.Count} frames for label='{label}'");

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
            Debug.LogWarning("[JackknifeRecorder] dropdown empty; defaulting to 'False'.");
            return "False";
        }
        int idx = referentDropdown.value;
        if (idx < 0 || idx >= referentDropdown.options.Count) return "False";
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
