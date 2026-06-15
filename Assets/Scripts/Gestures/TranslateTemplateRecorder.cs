using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/*
TranslateTemplateRecorder

Records Translate-gesture templates for JackknifeTranslateRecognizer.

Usage:
  1) Assign `detector` (for feature builder + pause coordination) and
     `recognizer` (where templates get saved).
  2) Assign `recordingAction` -- typically the off-hand pinch action.
  3) Set `recordingLabel` to "Translate" for positive examples or "false"
     for negative ones.
  4) (Optional) Toggle `resetOnNextRecord` to wipe all existing templates
     before saving the next one (useful when joint config changed or you
     want to start a fresh corpus). It auto-resets after firing once.

While `recordingAction` is held the detector is paused (its inference buffer
is cleared), and this recorder captures feature frames at `minFrameInterval`
into a list. On release the list is handed to
`recognizer.AppendTemplate(recordingLabel, frames)` and Jackknife retrains.
*/

public class TranslateTemplateRecorder : MonoBehaviour
{
    [Header("References")]
    public TranslateGestureDetector detector;
    public JackknifeTranslateRecognizer recognizer;

    [Header("Recording trigger")]
    [Tooltip("InputAction (e.g. left-hand pinch). Recording runs while it's pressed.")]
    public InputActionReference recordingAction;
    [Tooltip("If the action is analog (pinch_value), this is the press threshold.")]
    [Range(0f, 1f)] public float recordingActionThreshold = 0.7f;

    [Header("Recording config")]
    [Tooltip("Label to save under. 'Translate' for positive samples, 'false' for negatives.")]
    public string recordingLabel = "Translate";
    [Tooltip("If true, wipe ALL existing templates before saving the next recorded one. " +
             "Auto-clears back to false after firing. Use the context-menu shortcut to set it.")]
    public bool resetOnNextRecord = false;
    [Tooltip("Same as the detector's: minimum interval between feature samples.")]
    public float minFrameInterval = 0.03f;
    [Tooltip("Hard cap on a single recording's duration.")]
    public float maxRecordingSeconds = 6.0f;

    [Header("Status (read-only)")]
    [SerializeField] private bool isRecording;
    [SerializeField] private int recordedFrameCount;

    public bool IsRecording => isRecording;
    public int RecordedFrameCount => recordedFrameCount;

    public event Action OnRecordingStarted;
    public event Action<bool> OnRecordingFinished;  // arg = saved successfully

    private bool _wasPressed;
    private float _recordingStartTime;
    private float _lastSampleTime = -1f;
    private readonly List<float[]> _frames = new List<float[]>(256);

    void OnEnable()
    {
        recordingAction?.action.Enable();
    }

    void OnDisable()
    {
        recordingAction?.action.Disable();
        if (isRecording) FinishRecording();
    }

    void Update()
    {
        bool pressed = ReadPressed();

        if (pressed && !_wasPressed) BeginRecording();
        else if (!pressed && _wasPressed) FinishRecording();
        _wasPressed = pressed;

        if (isRecording)
        {
            SampleFrameIfDue();
            if (Time.time - _recordingStartTime > maxRecordingSeconds)
            {
                Debug.LogWarning("[TranslateTemplateRecorder] hit maxRecordingSeconds; auto-finishing");
                FinishRecording();
            }
        }
    }

    void BeginRecording()
    {
        if (detector == null || recognizer == null)
        {
            Debug.LogError("[TranslateTemplateRecorder] detector or recognizer not assigned.");
            return;
        }
        _frames.Clear();
        recordedFrameCount = 0;
        _lastSampleTime = -1f;
        _recordingStartTime = Time.time;
        isRecording = true;
        detector.externalPause = true;

        Debug.Log(
            $"[TranslateTemplateRecorder] RECORDING started " +
            $"(label='{recordingLabel}', reset={resetOnNextRecord})"
        );
        try { OnRecordingStarted?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
    }

    void SampleFrameIfDue()
    {
        if (_lastSampleTime > 0f && (Time.time - _lastSampleTime) < minFrameInterval) return;
        _lastSampleTime = Time.time;

        float[] f = detector.BuildFeatureFrame();
        if (f == null) return;
        _frames.Add(f);
        recordedFrameCount = _frames.Count;
    }

    void FinishRecording()
    {
        if (!isRecording) return;
        isRecording = false;
        if (detector != null) detector.externalPause = false;

        Debug.Log($"[TranslateTemplateRecorder] RECORDING ended ({_frames.Count} frames). label='{recordingLabel}'");

        bool saved = false;
        if (_frames.Count < 2)
        {
            Debug.LogWarning("[TranslateTemplateRecorder] too few frames; skipping save.");
        }
        else if (recognizer == null)
        {
            Debug.LogError("[TranslateTemplateRecorder] recognizer not assigned; can't save.");
        }
        else
        {
            if (resetOnNextRecord)
            {
                Debug.Log("[TranslateTemplateRecorder] resetOnNextRecord=true -> clearing all templates first");
                recognizer.ClearAllTemplates();
                resetOnNextRecord = false;  // one-shot
            }
            saved = recognizer.AppendTemplate(recordingLabel, _frames);
        }

        try { OnRecordingFinished?.Invoke(saved); } catch (Exception e) { Debug.LogError(e); }
    }

    bool ReadPressed()
    {
        if (recordingAction == null || recordingAction.action == null) return false;
        var act = recordingAction.action;
        try
        {
            if (act.activeControl != null && act.activeControl.valueType == typeof(float))
                return act.ReadValue<float>() >= recordingActionThreshold;
        }
        catch { /* fall through */ }
        try { return act.IsPressed(); }
        catch { return false; }
    }

    [ContextMenu("Arm Reset On Next Record")]
    public void ArmResetOnNextRecord()
    {
        resetOnNextRecord = true;
        Debug.Log("[TranslateTemplateRecorder] resetOnNextRecord ARMED. Next recording will clear existing templates first.");
    }
}
