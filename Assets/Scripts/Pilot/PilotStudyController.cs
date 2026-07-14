using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
Drives the pilot data-collection UI in DataCollectionScene:
    - Participant number is set in the Inspector before the session.
    - The experimenter picks a referent in the dropdown and hits Start.
    - Coroutine: 3-2-1 countdown -> PilotSender.BeginTrial -> records for
      `recordingSeconds` (participant performs the gesture) -> EndTrial.
    - Tracks how many of the `trialsPerReferent` repetitions are done for each
      referent (in-session only) and shows "Trial n / 20" for the selected one.
    - Start button is disabled while a trial is running.

Recording itself (gaze + hand joints streaming, CSV/video saving) lives in
PilotSender + MacProgram/pilot_receiver.py; this script only sequences trials.
*/

public class PilotStudyController : MonoBehaviour
{
    [System.Serializable]
    public struct ReferentTrialOverride
    {
        [Tooltip("Must match the dropdown option text exactly.")]
        public string referent;
        public int trials;
    }

    [Header("Session")]
    [Tooltip("Set per participant before starting the session. Used in packet metadata and in the Mac-side output folder name (P01, P02, ...).")]
    public int participantId = 1;
    [Tooltip("Repetitions for referents without an override below.")]
    public int trialsPerReferent = 10;
    [Tooltip("Per-referent repetition overrides (e.g. Search = 20).")]
    public ReferentTrialOverride[] trialOverrides =
    {
        new ReferentTrialOverride { referent = "Search", trials = 20 },
    };

    [Header("UI references")]
    public TMP_Dropdown referentDropdown;
    public TMP_Text statusText;
    [Tooltip("Shows 'Trial n / 20' for the currently selected referent.")]
    public TMP_Text trialCountText;
    public Button startButton;

    [Header("Refs")]
    public PilotSender pilotSender;

    [Header("Head-up display")]
    [Tooltip("Head-locked floating text for the 3-2-1 / GO! / Saved! messages so they appear right in front of the participant's eyes. When unassigned, everything falls back onto the big statusText like before.")]
    public PilotHudText hudText;
    [Tooltip("How long GO! stays visible into the recording window before fading out of the participant's view.")]
    public float goVisibleSeconds = 0.6f;

    [Header("Timing")]
    [Range(0, 10)] public int countdownSeconds = 3;
    public float recordingSeconds = 3.0f;
    public float postTrialSeconds = 1.2f;

    [Header("Status colors")]
    public Color readyColor = new Color(0.78f, 0.78f, 0.85f);
    public Color countdownColor = new Color(1.0f, 0.85f, 0.20f);
    public Color recordingColor = new Color(0.40f, 1.00f, 0.40f);
    public Color savedColor = Color.white;
    public Color errorColor = new Color(1.0f, 0.5f, 0.4f);
    public Color doneColor = new Color(0.55f, 0.85f, 1.0f);

    [Header("Status font sizes")]
    public float bigFontSize = 96f;
    public float smallFontSize = 40f;

    private bool _busy;
    // completed trial count per referent label, this session only
    private readonly Dictionary<string, int> _completedByReferent = new Dictionary<string, int>();

    void OnEnable()
    {
        if (startButton != null) startButton.onClick.AddListener(OnStartButtonClicked);
        if (referentDropdown != null) referentDropdown.onValueChanged.AddListener(OnReferentChanged);
        SetIdle();
        UpdateTrialText();
    }

    void OnDisable()
    {
        if (startButton != null) startButton.onClick.RemoveListener(OnStartButtonClicked);
        if (referentDropdown != null) referentDropdown.onValueChanged.RemoveListener(OnReferentChanged);
    }

    void OnReferentChanged(int _)
    {
        if (!_busy) SetIdle();
        UpdateTrialText();
    }

    void OnStartButtonClicked()
    {
        if (_busy) return;
        if (pilotSender == null)
        {
            SetStatus("SENDER MISSING", errorColor, smallFontSize);
            Debug.LogError("[PilotStudy] pilotSender not assigned.");
            return;
        }
        StartCoroutine(TrialRoutine());
    }

    IEnumerator TrialRoutine()
    {
        _busy = true;
        if (startButton != null) startButton.interactable = false;

        string referent = ReadSelectedReferent();
        int trialsForReferent = GetTrialsFor(referent);
        int trialIndex = GetCompletedCount(referent) + 1;
        Debug.Log($"[PilotStudy] START P{participantId} referent='{referent}' trial={trialIndex}/{trialsForReferent}");

        if (hudText != null) SetStatus("Get ready...", countdownColor, smallFontSize);
        for (int i = countdownSeconds; i > 0; i--)
        {
            ShowBig(i.ToString(), countdownColor);
            yield return new WaitForSeconds(1f);
        }

        pilotSender.BeginTrial(participantId, referent, trialIndex);
        ShowBig("GO!", recordingColor);
        if (hudText != null) SetStatus("Recording...", recordingColor, smallFontSize);

        float startTime = Time.unscaledTime;
        bool goHidden = false;
        while (Time.unscaledTime - startTime < recordingSeconds)
        {
            // GO! only lingers briefly, then fades so nothing blocks the
            // participant's view (or the gaze data) for the rest of the window.
            if (!goHidden && Time.unscaledTime - startTime >= goVisibleSeconds)
            {
                HideBig();
                goHidden = true;
            }
            yield return null;
        }

        pilotSender.EndTrial();

        _completedByReferent[referent] = trialIndex;
        UpdateTrialText();
        Debug.Log($"[PilotStudy] END P{participantId} referent='{referent}' trial={trialIndex}/{trialsForReferent}");

        bool referentDone = trialIndex >= trialsForReferent;
        string doneMsg = referentDone ? $"{referent} DONE!" : "Saved!";
        Color doneMsgColor = referentDone ? doneColor : savedColor;
        ShowBig(doneMsg, doneMsgColor);
        if (hudText != null) SetStatus(doneMsg, doneMsgColor, smallFontSize);

        yield return new WaitForSeconds(postTrialSeconds);
        HideBig();
        SetIdle();
        if (startButton != null) startButton.interactable = true;
        _busy = false;
    }

    // Countdown / GO! / Saved! go onto the head-locked HUD when one is
    // assigned; otherwise fall back to the old big-text-on-canvas behaviour.
    void ShowBig(string message, Color color)
    {
        if (hudText != null) hudText.Show(message, color);
        else SetStatus(message, color, bigFontSize);
    }

    void HideBig()
    {
        if (hudText != null) hudText.Hide();
    }

    string ReadSelectedReferent()
    {
        if (referentDropdown == null || referentDropdown.options == null
            || referentDropdown.options.Count == 0)
        {
            Debug.LogWarning("[PilotStudy] dropdown empty; defaulting to 'Unknown'.");
            return "Unknown";
        }
        int idx = referentDropdown.value;
        if (idx < 0 || idx >= referentDropdown.options.Count) return "Unknown";
        return referentDropdown.options[idx].text;
    }

    int GetCompletedCount(string referent)
    {
        return _completedByReferent.TryGetValue(referent, out int n) ? n : 0;
    }

    int GetTrialsFor(string referent)
    {
        if (trialOverrides != null)
        {
            foreach (var o in trialOverrides)
            {
                if (!string.IsNullOrEmpty(o.referent)
                    && string.Equals(o.referent, referent, System.StringComparison.OrdinalIgnoreCase)
                    && o.trials > 0)
                    return o.trials;
            }
        }
        return trialsPerReferent;
    }

    void SetIdle() => SetStatus("Ready", readyColor, smallFontSize);

    void SetStatus(string text, Color color, float fontSize)
    {
        if (statusText == null) return;
        statusText.text = text;
        statusText.color = color;
        statusText.fontSize = fontSize;
    }

    void UpdateTrialText()
    {
        if (trialCountText == null) return;
        string referent = ReadSelectedReferent();
        int trials = GetTrialsFor(referent);
        int done = GetCompletedCount(referent);
        int next = Mathf.Min(done + 1, trials);
        trialCountText.text = done >= trials
            ? $"P{participantId}  |  {referent}  {done} / {trials}  (done)"
            : $"P{participantId}  |  {referent}  Trial {next} / {trials}";
        trialCountText.color = done >= trials ? doneColor : readyColor;
    }

    // Redo support: wipe this session's count for the selected referent so its
    // trials can be recorded again (Mac side overwrites files with the same
    // trial index and warns in its console).
    [ContextMenu("Reset count for selected referent")]
    void ResetSelectedReferentCount()
    {
        string referent = ReadSelectedReferent();
        _completedByReferent.Remove(referent);
        UpdateTrialText();
        Debug.Log($"[PilotStudy] Reset trial count for referent='{referent}'.");
    }
}
