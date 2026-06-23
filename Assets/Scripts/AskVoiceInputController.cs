using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/*
AskVoiceInputController

Legacy/fallback controller.

Captures the user's spoken question while an AskQuestionCard is active and
POSTs the raw WAV audio to MacProgram. STT + GPT happen on the Python side.
The default voice input flow now uses Android SpeechRecognizer and transcript
text, so allowLegacyRecording stays false unless this fallback is explicitly
re-enabled for comparison testing.

Lifecycle:

  1) Watches SearchResultCardSpawner.PendingAskQuestion. When it transitions
     from null -> non-null (i.e. an AskQuestionCard was just spawned), starts
     a microphone recording.

  2) Monitors the recording's RMS level each frame:
       - Until the first voice frame, just waits (user may take a moment).
       - Once voice has been detected, watches for `silenceHoldSeconds` of
         silence (RMS below `silenceThreshold`). On that mark, stops the mic.
       - Hard cap at `maxRecordingSeconds` either way.

  3) On stop:
       - encodes the recorded samples to a PCM-16 WAV byte buffer
       - calls AskQuestionCard.NotifyThinking()  (status -> "Thinking...")
       - POSTs the WAV bytes to `voiceServerUrl`

  4) If the AskQuestionCard is destroyed before the recording finishes (user
     closed it, or AskResultCard replaced it), the recording is cancelled.

Android requires the RECORD_AUDIO permission. Make sure the Player Settings or
your AndroidManifest declares it.
*/

public class AskVoiceInputController : MonoBehaviour
{
    [Header("Legacy / fallback")]
    [Tooltip("Keep false for the Android STT transcript flow. Turn on only when intentionally testing the old WAV upload path.")]
    public bool allowLegacyRecording = false;

    [Header("Refs")]
    public ResultCardSpawner spawner;

    [Header("Network")]
    [Tooltip("URL of the MacProgram voice endpoint. The body is raw audio/wav bytes.")]
    public string voiceServerUrl = "http://192.168.0.8:5007/ask_voice";

    [Header("Microphone")]
    public int sampleRate = 16000;
    [Tooltip("Hard cap on a single recording's duration.")]
    public int maxRecordingSeconds = 15;

    [Header("Silence detection")]
    [Tooltip("RMS amplitude below this is treated as silence.")]
    public float silenceThreshold = 0.012f;
    [Tooltip("After voice has been detected, stop recording when silence persists this long.")]
    public float silenceHoldSeconds = 1.5f;

    [Header("Status (read-only)")]
    [SerializeField] private bool isRecording;
    [SerializeField] private float currentRms;
    [SerializeField] private bool hasDetectedVoice;
    [SerializeField] private float recordedSeconds;

    private AskQuestionCard _activeCard;
    private AudioClip _recordingClip;
    private string _micDevice;
    private float _recordStartTime;
    private float _lastVoiceTime;
    private bool _loggedLegacyDisabled;

    void Start()
    {
        if (!allowLegacyRecording)
            Debug.Log("[AskVoice] Legacy WAV recorder is disabled. Android STT voice input should provide transcripts instead.");
    }

    void Update()
    {
        if (!allowLegacyRecording)
        {
            if (!_loggedLegacyDisabled)
            {
                _loggedLegacyDisabled = true;
                Debug.Log("[AskVoice] Legacy recording skipped because allowLegacyRecording=false.");
            }
            return;
        }

        AskQuestionCard current = spawner != null ? spawner.PendingAskQuestion : null;

        if (current != _activeCard)
        {
            if (isRecording) CancelRecording("active card changed");
            _activeCard = current;
            if (current != null) StartRecording();
        }

        if (isRecording && _activeCard != null) MonitorRecording();
    }

    void OnDisable()
    {
        if (isRecording) CancelRecording("controller disabled");
    }

    void StartRecording()
    {
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            Debug.LogError("[AskVoice] no microphone device available.");
            return;
        }
        _micDevice = Microphone.devices[0];
        _recordingClip = Microphone.Start(_micDevice, false, maxRecordingSeconds, sampleRate);
        if (_recordingClip == null)
        {
            Debug.LogError($"[AskVoice] Microphone.Start failed on '{_micDevice}'.");
            return;
        }
        _recordStartTime = Time.time;
        _lastVoiceTime = Time.time;
        hasDetectedVoice = false;
        isRecording = true;
        recordedSeconds = 0f;
        Debug.Log($"[AskVoice] recording started on '{_micDevice}' @ {sampleRate}Hz");
    }

    void MonitorRecording()
    {
        recordedSeconds = Time.time - _recordStartTime;
        currentRms = ComputeCurrentRMS();

        if (currentRms > silenceThreshold)
        {
            if (!hasDetectedVoice) hasDetectedVoice = true;
            _lastVoiceTime = Time.time;
        }

        bool maxReached = recordedSeconds >= maxRecordingSeconds;

        if (!hasDetectedVoice)
        {
            if (maxReached)
            {
                Debug.LogWarning("[AskVoice] no voice within max window; discarding.");
                CancelRecording("no voice");
            }
            return;
        }

        float silentDuration = Time.time - _lastVoiceTime;
        if (silentDuration >= silenceHoldSeconds || maxReached)
            StopAndSend(maxReached ? "max duration" : "silence");
    }

    float ComputeCurrentRMS()
    {
        if (_recordingClip == null || string.IsNullOrEmpty(_micDevice)) return 0f;
        int pos = Microphone.GetPosition(_micDevice);
        const int windowSize = 512;
        if (pos < windowSize) return 0f;

        float[] window = new float[windowSize];
        _recordingClip.GetData(window, pos - windowSize);

        float sum = 0f;
        for (int i = 0; i < windowSize; i++) sum += window[i] * window[i];
        return Mathf.Sqrt(sum / windowSize);
    }

    void StopAndSend(string reason)
    {
        if (!isRecording) return;
        int finalPos = Microphone.GetPosition(_micDevice);
        Microphone.End(_micDevice);
        isRecording = false;
        Debug.Log($"[AskVoice] recording STOPPED ({reason}) @ {finalPos} samples ({recordedSeconds:F2}s)");

        if (finalPos <= 0 || _recordingClip == null)
        {
            Debug.LogWarning("[AskVoice] no captured samples; nothing to send.");
            return;
        }

        int channels = _recordingClip.channels;
        float[] samples = new float[finalPos * channels];
        _recordingClip.GetData(samples, 0);

        byte[] wav = EncodeWav(samples, sampleRate, channels);
        Debug.Log($"[AskVoice] encoded {wav.Length} WAV bytes");

        if (_activeCard != null) _activeCard.NotifyThinking();

        StartCoroutine(PostWav(wav));
    }

    void CancelRecording(string reason)
    {
        if (!isRecording) return;
        if (!string.IsNullOrEmpty(_micDevice)) Microphone.End(_micDevice);
        isRecording = false;
        _recordingClip = null;
        Debug.Log($"[AskVoice] recording CANCELLED ({reason})");
    }

    IEnumerator PostWav(byte[] wavBytes)
    {
        var request = new UnityWebRequest(voiceServerUrl, "POST");
        request.uploadHandler = new UploadHandlerRaw(wavBytes);
        request.uploadHandler.contentType = "audio/wav";
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "audio/wav");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
            Debug.Log($"[AskVoice] POST OK code={request.responseCode} body={request.downloadHandler.text}");
        else
            Debug.LogError($"[AskVoice] POST failed: {request.error} ({request.result})");

        request.Dispose();
    }

    // ---- PCM-16 WAV encoder ----

    static byte[] EncodeWav(float[] samples, int sampleRate, int channels)
    {
        const int headerSize = 44;
        int dataSize = samples.Length * 2;
        int fileSize = headerSize + dataSize - 8;
        byte[] bytes = new byte[headerSize + dataSize];

        WriteAscii(bytes, 0,  "RIFF");
        WriteInt32(bytes, 4,  fileSize);
        WriteAscii(bytes, 8,  "WAVE");
        WriteAscii(bytes, 12, "fmt ");
        WriteInt32(bytes, 16, 16);                          // chunk size
        WriteInt16(bytes, 20, 1);                           // PCM
        WriteInt16(bytes, 22, (short)channels);
        WriteInt32(bytes, 24, sampleRate);
        WriteInt32(bytes, 28, sampleRate * channels * 2);   // byte rate
        WriteInt16(bytes, 32, (short)(channels * 2));       // block align
        WriteInt16(bytes, 34, 16);                          // bits per sample
        WriteAscii(bytes, 36, "data");
        WriteInt32(bytes, 40, dataSize);

        int idx = headerSize;
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
            bytes[idx++] = (byte)(s & 0xff);
            bytes[idx++] = (byte)((s >> 8) & 0xff);
        }
        return bytes;
    }

    static void WriteAscii(byte[] dst, int offset, string s)
    {
        for (int i = 0; i < s.Length; i++) dst[offset + i] = (byte)s[i];
    }

    static void WriteInt32(byte[] dst, int offset, int v)
    {
        dst[offset    ] = (byte)( v        & 0xff);
        dst[offset + 1] = (byte)((v >>  8) & 0xff);
        dst[offset + 2] = (byte)((v >> 16) & 0xff);
        dst[offset + 3] = (byte)((v >> 24) & 0xff);
    }

    static void WriteInt16(byte[] dst, int offset, short v)
    {
        dst[offset    ] = (byte)(v & 0xff);
        dst[offset + 1] = (byte)((v >> 8) & 0xff);
    }
}
