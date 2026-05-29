using System;
using System.Collections;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/// <summary>
/// World-space card that appears for the "Ask" gesture.
/// Three visual states:
///   1. INPUT     - input field + Submit button + Close button. User types or speaks (via the
///                  system virtual keyboard's mic icon) a question about the target.
///   2. LOADING   - waiting for the VLM round-trip from Python.
///   3. ANSWER    - shows the VLM response. Close button remains.
///
/// The spawner wires this up; the card just emits OnSubmit when the user clicks Submit and
/// receives the answer via ShowAnswer / ShowError.
/// </summary>
public class AskQuestionCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text titleText;
    public TMP_InputField questionInput;
    public Button submitButton;
    public Button closeButton;
    public GameObject inputPanel;       // visible in INPUT state
    public GameObject loadingIndicator; // visible in LOADING state
    public GameObject answerPanel;      // visible in ANSWER state
    public TMP_Text answerText;
    [Tooltip("Optional child Transform to spin while loading.")]
    public Transform spinningTransform;

    [Header("Behavior")]
    public bool billboard = true;
    [Tooltip("If null, Camera.main is used.")]
    public Camera billboardCamera;
    public float spinSpeedDegPerSec = 240f;
    [Tooltip("Auto-destroy this many seconds after an answer is shown. <= 0 disables.")]
    public float autoDestroyAfterAnswer = 60f;
    [Tooltip("Auto-destroy if no question is submitted within this many seconds. <= 0 disables.")]
    public float idleTimeoutSec = 120f;
    [Tooltip("Show 'Timed out' error if VLM doesn't respond within this many seconds.")]
    public float loadingTimeoutSec = 60f;

    [Header("Keyboard (XR cannot show OS keyboard reliably; left for fallback)")]
    public bool forceOpenSystemKeyboard = false;
    public string keyboardPrompt = "Ask a question about this object...";

    private TouchScreenKeyboard _keyboard;

    [Header("Voice Input (recommended for XR)")]
    [Tooltip("If true, mic recording auto-starts when the card spawns.")]
    public bool autoStartVoice = true;
    [Tooltip("Delay before mic starts so the card finishes its appearance animation.")]
    public float voiceStartDelaySec = 0.5f;
    [Tooltip("Mic sample rate. 16kHz is plenty for speech and keeps WAV small.")]
    public int micSampleRate = 16000;
    [Tooltip("Hard cap on recording length (sec). Recording stops automatically at this limit.")]
    public float maxRecordSec = 10f;
    [Tooltip("Minimum recording length (sec). VAD silence detection won't fire before this.")]
    public float minRecordSec = 0.8f;
    [Tooltip("RMS energy below this is considered silence. Tune for ambient noise.")]
    public float silenceThreshold = 0.005f;
    [Tooltip("Stop recording after this many seconds of continuous silence (after minRecordSec).")]
    public float silenceDurationToStop = 1.5f;

    [Header("Voice -> Python")]
    [Tooltip("Python host running gesture_vlm.py (same Mac as MsgSender.serverIP).")]
    public string pythonHost = "192.168.0.0";
    [Tooltip("Port of the Python voice_server (default 5007).")]
    public int voiceServerPort = 5007;

    private AudioClip _micClip;
    private string _micDevice;
    private bool _isRecording;
    private float _lastVoiceTime;
    private float _recordingStartTime;

    public enum State { Input, Loading, Answer, Error }
    public State CurrentState { get; private set; } = State.Input;

    /// <summary>Raised when the user clicks Submit with a non-empty question.</summary>
    public event Action<string> OnSubmit;

    private float _spawnedAt;
    private float _loadingStartedAt = -1f;
    private float _destroyAt = float.PositiveInfinity;

    void Start()
    {
        if (autoStartVoice)
        {
            StartCoroutine(VoiceFlowCoroutine());
        }
        else
        {
            StartCoroutine(AutoActivateInputField());
        }
    }

    IEnumerator AutoActivateInputField()
    {
        yield return null;
        yield return null;
        if (questionInput != null && CurrentState == State.Input)
        {
            questionInput.Select();
            questionInput.ActivateInputField();
            Debug.Log("[AskQuestionCard] auto-activated input field");
        }
    }

    // =================== VOICE INPUT ===================

    IEnumerator VoiceFlowCoroutine()
    {
        yield return new WaitForSeconds(voiceStartDelaySec);

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Debug.Log("[AskQuestionCard] requesting microphone permission...");
            Permission.RequestUserPermission(Permission.Microphone);
            float waited = 0f;
            while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) && waited < 5f)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
            }
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                ShowError("Permission", "Microphone permission denied.");
                yield break;
            }
        }
#endif

        if (Microphone.devices.Length == 0)
        {
            ShowError("Voice", "No microphone available.");
            yield break;
        }

        StartRecording();
    }

    void StartRecording()
    {
        _micDevice = Microphone.devices[0];
        int lengthSec = Mathf.CeilToInt(maxRecordSec) + 1;
        _micClip = Microphone.Start(_micDevice, false, lengthSec, micSampleRate);

        if (_micClip == null)
        {
            ShowError("Voice", "Microphone.Start failed.");
            return;
        }

        _isRecording = true;
        _recordingStartTime = Time.time;
        _lastVoiceTime = Time.time;

        Debug.Log($"[AskQuestionCard] Recording started on '{_micDevice}' @ {micSampleRate}Hz");
        if (titleText != null) titleText.text = "Listening...";
        if (questionInput != null) questionInput.text = "";
    }

    void Update()
    {
        if (!_isRecording) return;

        float rms = ComputeRecentRMS();
        if (rms > silenceThreshold) _lastVoiceTime = Time.time;

        float silentFor = Time.time - _lastVoiceTime;
        float recordedFor = Time.time - _recordingStartTime;

        bool hardStop = recordedFor >= maxRecordSec;
        bool vadStop = recordedFor >= minRecordSec && silentFor >= silenceDurationToStop;

        if (hardStop || vadStop)
        {
            StopRecordingAndSend(vadStop ? "silence" : "max-length");
        }
    }

    float ComputeRecentRMS()
    {
        if (_micClip == null || string.IsNullOrEmpty(_micDevice)) return 0f;
        int micPos = Microphone.GetPosition(_micDevice);
        int win = micSampleRate / 10; // 100ms window
        if (micPos < win) return 0f;

        float[] samples = new float[win];
        _micClip.GetData(samples, micPos - win);

        float sum = 0f;
        for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
        return Mathf.Sqrt(sum / samples.Length);
    }

    void StopRecordingAndSend(string reason)
    {
        if (!_isRecording) return;
        _isRecording = false;

        int micPos = Microphone.GetPosition(_micDevice);
        Microphone.End(_micDevice);

        Debug.Log($"[AskQuestionCard] Recording stopped ({reason}). pos={micPos}");

        if (_micClip == null || micPos < (int)(micSampleRate * 0.3f))
        {
            ShowError("Voice", "Recording too short. Try again.");
            return;
        }

        float[] samples = new float[micPos];
        _micClip.GetData(samples, 0);

        byte[] wav = EncodeWav(samples, micSampleRate, 1);
        Debug.Log($"[AskQuestionCard] WAV ready: {wav.Length} bytes");

        SetState(State.Loading);
        if (titleText != null) titleText.text = "Transcribing & asking...";

        StartCoroutine(PostAudioToPython(wav));
    }

    IEnumerator PostAudioToPython(byte[] wav)
    {
        string url = $"http://{pythonHost}:{voiceServerPort}/ask_voice";

        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(wav);
        req.uploadHandler.contentType = "audio/wav";
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout = 30;

        Debug.Log($"[AskQuestionCard] POST {url} ({wav.Length} bytes)");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[AskQuestionCard] voice POST failed: {req.error}");
            ShowError("Network", req.error);
            yield break;
        }

        Debug.Log($"[AskQuestionCard] voice POST OK. Waiting for VLM_RESULT...");
        // Actual answer arrives via VLM_RESULT UDP -> AskCardSpawner.HandleVlmResult -> ShowAnswer.
    }

    // PCM16 mono WAV encoder.
    static byte[] EncodeWav(float[] samples, int sampleRate, int channels)
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            int byteRate = sampleRate * channels * 2;
            int subchunk2Size = samples.Length * 2;

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + subchunk2Size);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                       // Subchunk1Size for PCM
            bw.Write((short)1);                 // AudioFormat PCM
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)(channels * 2));    // BlockAlign
            bw.Write((short)16);                // BitsPerSample
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(subchunk2Size);

            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
                bw.Write(s);
            }
            return ms.ToArray();
        }
    }
    
    void Awake()
    {
        _spawnedAt = Time.time;
        if (submitButton != null) submitButton.onClick.AddListener(OnSubmitClicked);
        if (closeButton != null)  closeButton.onClick.AddListener(Close);
        // if (questionInput != null)
        // {
        //     // When user pinches/clicks the input field, force-open the system keyboard.
        //     questionInput.onSelect.AddListener(_ => OpenSystemKeyboard());
        // }
        SetState(State.Input);

        Debug.Log(
            $"[AskQuestionCard] TouchScreenKeyboard.isSupported = {TouchScreenKeyboard.isSupported}"
        );
    }

    public void OpenSystemKeyboard()
    {
        if (!forceOpenSystemKeyboard) return;
        if (!TouchScreenKeyboard.isSupported)
        {
            Debug.LogWarning(
                "[AskQuestionCard] TouchScreenKeyboard.isSupported=false. " +
                "This device's OS doesn't expose a virtual keyboard to Unity. " +
                "Use voice input, predefined questions, or an in-world keyboard prefab instead."
            );
            return;
        }

        // Avoid opening a second instance if already visible.
        if (_keyboard != null && _keyboard.active &&
            _keyboard.status == TouchScreenKeyboard.Status.Visible)
        {
            return;
        }

        string initial = questionInput != null ? questionInput.text : "";
        _keyboard = TouchScreenKeyboard.Open(
            initial,
            TouchScreenKeyboardType.Default,
            autocorrection: true,
            multiline: false,
            secure: false,
            alert: false,
            textPlaceholder: keyboardPrompt
        );
        Debug.Log($"[AskQuestionCard] TouchScreenKeyboard.Open called. status={_keyboard?.status}");
    }

    void OnSubmitClicked()
    {
        string q = questionInput != null ? questionInput.text : "";
        if (string.IsNullOrWhiteSpace(q))
        {
            Debug.Log("[AskQuestionCard] empty question; ignoring submit.");
            return;
        }
        Debug.Log($"[AskQuestionCard] submit: {q}");
        try { OnSubmit?.Invoke(q); }
        catch (Exception e) { Debug.LogError($"[AskQuestionCard] OnSubmit subscriber threw: {e}"); }
        SetState(State.Loading);
    }

    /// <summary>Called externally by the spawner when the VLM answer arrives.
    /// transcribedQuestion (optional) is shown back in the QuestionInput field so the
    /// user can see what the speech-to-text understood.</summary>
    public void ShowAnswer(string title, string body, string transcribedQuestion = null)
    {
        if (titleText != null && !string.IsNullOrEmpty(title)) titleText.text = title;
        if (answerText != null) answerText.text = body ?? "";
        if (questionInput != null && !string.IsNullOrEmpty(transcribedQuestion))
            questionInput.text = transcribedQuestion;
        SetState(State.Answer);
        if (autoDestroyAfterAnswer > 0f) _destroyAt = Time.time + autoDestroyAfterAnswer;
    }

    public void ShowError(string title, string message, string transcribedQuestion = null)
    {
        if (titleText != null) titleText.text = string.IsNullOrEmpty(title) ? "Error" : title;
        if (answerText != null) answerText.text = message ?? "";
        if (questionInput != null && !string.IsNullOrEmpty(transcribedQuestion))
            questionInput.text = transcribedQuestion;
        SetState(State.Error);
        if (autoDestroyAfterAnswer > 0f) _destroyAt = Time.time + autoDestroyAfterAnswer;
    }

    public void Close()
    {
        if (_isRecording)
        {
            _isRecording = false;
            try { Microphone.End(_micDevice); } catch { }
        }
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (_isRecording)
        {
            _isRecording = false;
            try { Microphone.End(_micDevice); } catch { }
        }
    }

    void SetState(State s)
    {
        CurrentState = s;
        // inputPanel: keep visible in Answer/Error too so the user can see what they asked.
        if (inputPanel != null)
            inputPanel.SetActive(s == State.Input || s == State.Answer || s == State.Error);
        if (loadingIndicator != null) loadingIndicator.SetActive(s == State.Loading);
        if (answerPanel != null)      answerPanel.SetActive(s == State.Answer || s == State.Error);

        // The question field becomes read-only outside Input state so the user can re-read it
        // but not accidentally retype.
        if (questionInput != null) questionInput.readOnly = (s != State.Input);

        // Submit button only makes sense while collecting the question.
        if (submitButton != null) submitButton.gameObject.SetActive(s == State.Input);

        if (s == State.Loading) _loadingStartedAt = Time.time;
        if (s == State.Input)   _loadingStartedAt = -1f;
    }

    void LateUpdate()
    {
        // Continuous billboard so the card always faces the user, even while they walk around.
        if (billboard)
        {
            Camera cam = billboardCamera != null ? billboardCamera : Camera.main;
            if (cam != null)
            {
                Vector3 toCam = transform.position - cam.transform.position;
                if (toCam.sqrMagnitude > 0.000001f)
                {
                    transform.rotation = Quaternion.LookRotation(toCam, cam.transform.up);
                }
            }
        }

        // Sync OS system keyboard text back into the visible TMP_InputField every frame
        // while typing, and also when keyboard closes via Done.
        if (_keyboard != null && questionInput != null)
        {
            switch (_keyboard.status)
            {
                case TouchScreenKeyboard.Status.Visible:
                    if (questionInput.text != _keyboard.text)
                        questionInput.text = _keyboard.text;
                    break;
                case TouchScreenKeyboard.Status.Done:
                    questionInput.text = _keyboard.text;
                    _keyboard = null;
                    break;
                case TouchScreenKeyboard.Status.Canceled:
                case TouchScreenKeyboard.Status.LostFocus:
                    _keyboard = null;
                    break;
            }
        }

        if (spinningTransform != null && loadingIndicator != null && loadingIndicator.activeInHierarchy)
        {
            spinningTransform.Rotate(0f, 0f, -spinSpeedDegPerSec * Time.deltaTime, Space.Self);
        }

        // Loading timeout
        if (CurrentState == State.Loading && loadingTimeoutSec > 0f &&
            _loadingStartedAt > 0f &&
            Time.time - _loadingStartedAt > loadingTimeoutSec)
        {
            ShowError("Timed out", $"VLM did not respond within {loadingTimeoutSec:F0}s.");
        }

        // Idle timeout (no submit ever happened)
        if (CurrentState == State.Input && idleTimeoutSec > 0f &&
            Time.time - _spawnedAt > idleTimeoutSec)
        {
            Debug.Log("[AskQuestionCard] idle timeout; closing.");
            Close();
        }

        // Auto-destroy after answer
        if (Time.time >= _destroyAt)
        {
            Destroy(gameObject);
        }
    }

    // ---- Buttons for XRGrabInteractable wiring (optional) ----
    public void DisableBillboard() { billboard = false; }
    public void EnableBillboard()  { billboard = true; }
    public void SetBillboardEnabled(bool v) { billboard = v; }
}
