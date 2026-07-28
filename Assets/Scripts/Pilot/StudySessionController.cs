using System.Collections;
using UnityEngine;

/*
User-study session countdown for the main scene.

The researcher's Mac panel (study_control.py) starts a session; MacProgram
then sends a VLM_RESULT payload with gesture == "StudyCountdown". This
component picks it up from VlmResultReceiver.OnResult, shows a head-locked
3-2-1 countdown right in front of the participant's eyes (PilotHudText -- the
same HUD the pilot data collection used, NOT the Study Manager Card), plays a
beep, and reports the beep moment back to MacProgram as a STUDY,EVENT packet
so the session CSV records exactly when the interaction window opened.

Everything is created at runtime: drop this component on any always-active
GameObject (e.g. next to VlmResultReceiver); no scene wiring needed.
*/
public class StudySessionController : MonoBehaviour
{
    [Header("Refs (auto-resolved when empty)")]
    public VlmResultReceiver receiver;
    public PilotHudText hudText;

    [Header("Countdown")]
    [Range(1, 5)] public int countdownSeconds = 3;
    public Color countdownColor = Color.white;
    [Tooltip("Shown at beep time, then fades out.")]
    public string goText = "GO!";
    public float goVisibleSeconds = 0.6f;

    [Header("Beep")]
    public float beepFrequencyHz = 1000f;
    public float beepDurationSec = 0.35f;
    [Range(0f, 1f)] public float beepVolume = 0.8f;

    public bool verboseLogging = true;

    AudioSource _audio;
    AudioClip _beepClip;
    bool _running;

    void Awake()
    {
        ResolveReferences();
        _audio = gameObject.GetComponent<AudioSource>();
        if (_audio == null) _audio = gameObject.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.spatialBlend = 0f;  // 2D: the cue must be equally audible anywhere
        _beepClip = CreateBeepClip(beepFrequencyHz, beepDurationSec);
    }

    void OnEnable()
    {
        ResolveReferences();
        if (receiver != null) receiver.OnResult += HandleResult;
    }

    void OnDisable()
    {
        if (receiver != null) receiver.OnResult -= HandleResult;
    }

    void HandleResult(VlmResultReceiver.VlmResultPayload payload)
    {
        if (payload == null || payload.gesture != "StudyCountdown") return;
        if (_running)
        {
            Debug.LogWarning("[StudySession] countdown already running; ignoring duplicate.");
            return;
        }
        StartCoroutine(RunCountdown());
    }

    IEnumerator RunCountdown()
    {
        _running = true;
        if (verboseLogging) Debug.Log("[StudySession] countdown started.");

        for (int i = countdownSeconds; i >= 1; i--)
        {
            if (hudText != null) hudText.Show(i.ToString(), countdownColor);
            yield return new WaitForSecondsRealtime(1f);
        }

        _audio.PlayOneShot(_beepClip, beepVolume);
        if (hudText != null) hudText.Show(goText, countdownColor);
        MsgSender.Instance?.SendStudyEvent("session_start");
        if (verboseLogging) Debug.Log("[StudySession] beep -- interaction window open.");

        yield return new WaitForSecondsRealtime(goVisibleSeconds);
        if (hudText != null) hudText.Hide();
        _running = false;
    }

    void ResolveReferences()
    {
        if (receiver == null) receiver = FindObjectOfType<VlmResultReceiver>();
        if (hudText == null)
        {
            hudText = FindObjectOfType<PilotHudText>();
            if (hudText == null) hudText = gameObject.AddComponent<PilotHudText>();
        }
    }

    static AudioClip CreateBeepClip(float frequency, float duration)
    {
        int sampleRate = 44100;
        int sampleCount = Mathf.Max(1, (int)(sampleRate * duration));
        float[] samples = new float[sampleCount];
        // Short attack/release ramps kill the click at the clip edges.
        int ramp = Mathf.Min(sampleCount / 8, sampleRate / 100);
        for (int i = 0; i < sampleCount; i++)
        {
            float amp = 1f;
            if (i < ramp) amp = i / (float)ramp;
            else if (i > sampleCount - ramp) amp = (sampleCount - i) / (float)ramp;
            samples[i] = amp * Mathf.Sin(2f * Mathf.PI * frequency * i / sampleRate);
        }
        AudioClip clip = AudioClip.Create("StudyBeep", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
