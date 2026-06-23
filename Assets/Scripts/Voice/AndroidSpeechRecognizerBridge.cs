using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public class AndroidSpeechRecognizerBridge : MonoBehaviour
{
    [Header("Recognition")]
    public string language = "ko-KR";
    public string languagePreference = "ko-KR";
    public string prompt = "한국어로 말씀하세요";
    public int maxResults = 1;
    public bool enablePartialResults = true;
    public bool preferOfflineRecognition = false;

    [Header("Editor/mock")]
    public bool enableEditorMockTranscript = true;
    public string editorMockTranscript = "이거 뭐야";
    public float editorMockDelaySeconds = 0.25f;

    [Header("Logging")]
    public bool verboseLogging = true;

    [Header("Status")]
    [SerializeField] private bool recognitionAvailable;
    [SerializeField] private bool onDeviceRecognitionAvailable;
    [SerializeField] private bool isListening;

    public bool IsRecognitionAvailable => recognitionAvailable;
    public bool IsOnDeviceRecognitionAvailable => onDeviceRecognitionAvailable;
    public bool IsListening => isListening;

    public event Action<string> OnFinalTranscript;
    public event Action<string> OnPartialTranscript;
    public event Action<int, string> OnError;

    private readonly object _eventLock = new object();
    private readonly List<string> _finalTranscripts = new List<string>();
    private readonly List<string> _partialTranscripts = new List<string>();
    private readonly List<SpeechErrorEvent> _errors = new List<SpeechErrorEvent>();
    private Coroutine _editorMockRoutine;

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _activity;
    private AndroidJavaObject _speechRecognizer;
    private RecognitionListenerProxy _listenerProxy;
#endif

    void Start()
    {
        ProbeAvailability();
    }

    void Update()
    {
        DrainQueuedEvents();
    }

    public void ProbeAvailability()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            AndroidJavaObject activity = GetActivity();
            using (AndroidJavaClass recognizerClass = new AndroidJavaClass("android.speech.SpeechRecognizer"))
            {
                recognitionAvailable = recognizerClass.CallStatic<bool>("isRecognitionAvailable", activity);
                try
                {
                    onDeviceRecognitionAvailable = recognizerClass.CallStatic<bool>("isOnDeviceRecognitionAvailable", activity);
                }
                catch (Exception e)
                {
                    onDeviceRecognitionAvailable = false;
                    if (verboseLogging) Debug.Log($"[AndroidSTT] isOnDeviceRecognitionAvailable unavailable: {e.Message}");
                }
            }
            Debug.Log($"[AndroidSTT] isRecognitionAvailable={recognitionAvailable} isOnDeviceRecognitionAvailable={onDeviceRecognitionAvailable}");
        }
        catch (Exception e)
        {
            recognitionAvailable = false;
            onDeviceRecognitionAvailable = false;
            Debug.LogWarning($"[AndroidSTT] availability probe failed: {e.Message}");
        }
#else
        recognitionAvailable = false;
        onDeviceRecognitionAvailable = false;
        if (verboseLogging) Debug.Log("[AndroidSTT] Android SpeechRecognizer is available only on Android device builds. Editor mock can still be used.");
#endif
    }

    public void StartListening()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            QueueError(-100, "RECORD_AUDIO permission is not granted.");
            return;
        }

        try
        {
            AndroidJavaObject activity = GetActivity();
            isListening = true;
            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try
                {
                    EnsureRecognizerOnAndroidThread(activity);
                    AndroidJavaObject intent = BuildRecognizerIntent();
                    _speechRecognizer.Call("startListening", intent);
                    if (verboseLogging) Debug.Log($"[AndroidSTT] startListening language={language}");
                }
                catch (Exception e)
                {
                    isListening = false;
                    QueueError(-101, "startListening failed: " + e.Message);
                }
            }));
        }
        catch (Exception e)
        {
            isListening = false;
            QueueError(-102, "StartListening setup failed: " + e.Message);
        }
#else
        if (!enableEditorMockTranscript)
        {
            QueueError(-200, "Android STT is not available in the Unity Editor and editor mock is disabled.");
            return;
        }
        if (_editorMockRoutine != null) StopCoroutine(_editorMockRoutine);
        isListening = true;
        _editorMockRoutine = StartCoroutine(EditorMockRoutine());
#endif
    }

    public void StopListening()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject activity = GetActivity();
        activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
        {
            try { _speechRecognizer?.Call("stopListening"); }
            catch (Exception e) { QueueError(-103, "stopListening failed: " + e.Message); }
        }));
#else
        if (_editorMockRoutine != null)
        {
            StopCoroutine(_editorMockRoutine);
            _editorMockRoutine = null;
        }
#endif
        isListening = false;
    }

    public void CancelListening()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject activity = GetActivity();
        activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
        {
            try { _speechRecognizer?.Call("cancel"); }
            catch (Exception e) { QueueError(-104, "cancel failed: " + e.Message); }
        }));
#else
        if (_editorMockRoutine != null)
        {
            StopCoroutine(_editorMockRoutine);
            _editorMockRoutine = null;
        }
#endif
        isListening = false;
    }

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            AndroidJavaObject activity = GetActivity();
            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try
                {
                    _speechRecognizer?.Call("destroy");
                    _speechRecognizer?.Dispose();
                    _speechRecognizer = null;
                }
                catch (Exception e)
                {
                    QueueError(-105, "destroy failed: " + e.Message);
                }
            }));
        }
        catch { }
#endif
    }

    IEnumerator EditorMockRoutine()
    {
        if (verboseLogging) Debug.Log($"[AndroidSTT] Editor mock listening, will return '{editorMockTranscript}'.");
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, editorMockDelaySeconds));
        isListening = false;
        QueueFinal(editorMockTranscript);
        _editorMockRoutine = null;
    }

    void QueueFinal(string transcript)
    {
        lock (_eventLock) _finalTranscripts.Add(transcript ?? "");
    }

    void QueuePartial(string transcript)
    {
        lock (_eventLock) _partialTranscripts.Add(transcript ?? "");
    }

    void QueueError(int code, string message)
    {
        lock (_eventLock) _errors.Add(new SpeechErrorEvent { code = code, message = message ?? "" });
    }

    void DrainQueuedEvents()
    {
        List<string> finals = null;
        List<string> partials = null;
        List<SpeechErrorEvent> errors = null;

        lock (_eventLock)
        {
            if (_finalTranscripts.Count > 0)
            {
                finals = new List<string>(_finalTranscripts);
                _finalTranscripts.Clear();
            }
            if (_partialTranscripts.Count > 0)
            {
                partials = new List<string>(_partialTranscripts);
                _partialTranscripts.Clear();
            }
            if (_errors.Count > 0)
            {
                errors = new List<SpeechErrorEvent>(_errors);
                _errors.Clear();
            }
        }

        if (partials != null)
        {
            for (int i = 0; i < partials.Count; i++)
            {
                if (verboseLogging) Debug.Log($"[AndroidSTT] partial='{partials[i]}'");
                OnPartialTranscript?.Invoke(partials[i]);
            }
        }

        if (finals != null)
        {
            for (int i = 0; i < finals.Count; i++)
            {
                if (verboseLogging) Debug.Log($"[AndroidSTT] final='{finals[i]}'");
                LogUnicodeCodePoints("[AndroidSTT] final", finals[i]);
                OnFinalTranscript?.Invoke(finals[i]);
            }
        }

        if (errors != null)
        {
            for (int i = 0; i < errors.Count; i++)
            {
                Debug.LogWarning($"[AndroidSTT] error code={errors[i].code} {errors[i].message}");
                OnError?.Invoke(errors[i].code, errors[i].message);
            }
        }
    }

    static void LogUnicodeCodePoints(string prefix, string transcript)
    {
        string text = transcript ?? "";
        StringBuilder builder = new StringBuilder();
        builder.Append(prefix)
            .Append(" transcript length=")
            .Append(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            builder.Append('\n')
                .Append("char[")
                .Append(i)
                .Append("]='")
                .Append(PrintableChar(c))
                .Append("' code=U+")
                .Append(((int)c).ToString("X4"));
        }

        Debug.Log(builder.ToString());
    }

    static string PrintableChar(char c)
    {
        switch (c)
        {
            case '\r': return "\\r";
            case '\n': return "\\n";
            case '\t': return "\\t";
            case '\'': return "\\'";
            case '\\': return "\\\\";
            default: return c.ToString();
        }
    }

    struct SpeechErrorEvent
    {
        public int code;
        public string message;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject GetActivity()
    {
        if (_activity != null) return _activity;
        using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        }
        return _activity;
    }

    void EnsureRecognizerOnAndroidThread(AndroidJavaObject activity)
    {
        if (_speechRecognizer != null) return;
        using (AndroidJavaClass recognizerClass = new AndroidJavaClass("android.speech.SpeechRecognizer"))
        {
            _listenerProxy = new RecognitionListenerProxy(this);
            _speechRecognizer = recognizerClass.CallStatic<AndroidJavaObject>("createSpeechRecognizer", activity);
            _speechRecognizer.Call("setRecognitionListener", _listenerProxy);
        }
    }

    AndroidJavaObject BuildRecognizerIntent()
    {
        AndroidJavaObject intent = new AndroidJavaObject("android.content.Intent", "android.speech.action.RECOGNIZE_SPEECH");
        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE_MODEL", "free_form");
        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE", language);
        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE_PREFERENCE", languagePreference);
        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.PROMPT", prompt);
        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.MAX_RESULTS", Mathf.Max(1, maxResults));
        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.PARTIAL_RESULTS", enablePartialResults);
        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.PREFER_OFFLINE", preferOfflineRecognition);
        return intent;
    }

    class RecognitionListenerProxy : AndroidJavaProxy
    {
        private readonly AndroidSpeechRecognizerBridge _owner;

        public RecognitionListenerProxy(AndroidSpeechRecognizerBridge owner)
            : base("android.speech.RecognitionListener")
        {
            _owner = owner;
        }

        public void onReadyForSpeech(AndroidJavaObject bundle)
        {
            if (_owner.verboseLogging) Debug.Log("[AndroidSTT] ready for speech.");
        }

        public void onBeginningOfSpeech()
        {
            if (_owner.verboseLogging) Debug.Log("[AndroidSTT] beginning of speech.");
        }

        public void onRmsChanged(float rmsdB) { }

        public void onBufferReceived(byte[] buffer) { }

        public void onEndOfSpeech()
        {
            if (_owner.verboseLogging) Debug.Log("[AndroidSTT] end of speech.");
        }

        public void onError(int error)
        {
            _owner.isListening = false;
            _owner.QueueError(error, ErrorCodeToString(error));
        }

        public void onResults(AndroidJavaObject results)
        {
            _owner.isListening = false;
            string transcript = ReadTopTranscript(results);
            _owner.QueueFinal(transcript);
        }

        public void onPartialResults(AndroidJavaObject partialResults)
        {
            string transcript = ReadTopTranscript(partialResults);
            if (!string.IsNullOrEmpty(transcript)) _owner.QueuePartial(transcript);
        }

        public void onEvent(int eventType, AndroidJavaObject bundle) { }

        static string ReadTopTranscript(AndroidJavaObject bundle)
        {
            if (bundle == null) return "";
            AndroidJavaObject matches = null;
            try { matches = bundle.Call<AndroidJavaObject>("getStringArrayList", "results_recognition"); }
            catch { return ""; }
            if (matches == null) return "";

            int size = 0;
            try { size = matches.Call<int>("size"); }
            catch { return ""; }
            if (size <= 0) return "";

            try { return matches.Call<string>("get", 0); }
            catch
            {
                try
                {
                    AndroidJavaObject value = matches.Call<AndroidJavaObject>("get", 0);
                    return value != null ? value.Call<string>("toString") : "";
                }
                catch { return ""; }
            }
        }

        static string ErrorCodeToString(int error)
        {
            switch (error)
            {
                case 1: return "ERROR_NETWORK_TIMEOUT";
                case 2: return "ERROR_NETWORK";
                case 3: return "ERROR_AUDIO";
                case 4: return "ERROR_SERVER";
                case 5: return "ERROR_CLIENT";
                case 6: return "ERROR_SPEECH_TIMEOUT";
                case 7: return "ERROR_NO_MATCH";
                case 8: return "ERROR_RECOGNIZER_BUSY";
                case 9: return "ERROR_INSUFFICIENT_PERMISSIONS";
                case 10: return "ERROR_TOO_MANY_REQUESTS";
                case 11: return "ERROR_SERVER_DISCONNECTED";
                case 12: return "ERROR_LANGUAGE_NOT_SUPPORTED";
                case 13: return "ERROR_LANGUAGE_UNAVAILABLE";
                default: return "ERROR_UNKNOWN";
            }
        }
    }
#endif
}
