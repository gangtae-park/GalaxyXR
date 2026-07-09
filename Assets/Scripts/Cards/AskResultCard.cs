using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AskResultCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text titleText;
    public TMP_Text questionText;
    public TMP_Text answerText;
    public Button closeButton;

    [Header("Lifetime")]
    public float autoDestroySeconds = 30f;
    [Tooltip("Placeholder shown in the answer field between BeginStreaming() and the first delta.")]
    public string streamingPlaceholder = "…";

    private float _destroyAt;
    private StringBuilder _streamingAnswer;

    void Awake()
    {
        _destroyAt = Time.time + autoDestroySeconds;
        if (closeButton != null) closeButton.onClick.AddListener(Close);
    }

    void OnDestroy()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(Close);
    }

    void Update()
    {
        if (Time.time >= _destroyAt) Destroy(gameObject);
    }

    public void SetContent(string objectName, string question, string answer)
    {
        if (titleText != null)
            titleText.text = string.IsNullOrEmpty(objectName) ? "Unknown" : objectName;
        if (questionText != null)
            questionText.text = question ?? "";
        if (answerText != null)
            answerText.text = string.IsNullOrEmpty(answer) ? "(no answer)" : answer;
        _streamingAnswer = null;
    }

    // Streaming lifecycle: card is spawned empty, deltas append into
    // _streamingAnswer, and EndStreaming settles the final visible state.
    public void BeginStreaming(string objectName, string question)
    {
        if (titleText != null)
            titleText.text = string.IsNullOrEmpty(objectName) ? "Unknown" : objectName;
        if (questionText != null)
            questionText.text = question ?? "";
        _streamingAnswer = new StringBuilder(256);
        if (answerText != null)
            answerText.text = streamingPlaceholder ?? "";
        _destroyAt = Time.time + autoDestroySeconds;
    }

    public void AppendAnswerDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        if (_streamingAnswer == null) _streamingAnswer = new StringBuilder(256);
        _streamingAnswer.Append(delta);
        if (answerText != null) answerText.text = _streamingAnswer.ToString();
        _destroyAt = Time.time + autoDestroySeconds;
    }

    public void EndStreaming(string finalAnswer = null)
    {
        // Prefer the assembled full text from Python's END packet when it's
        // there -- protects against a dropped delta. Fallback to whatever we
        // accumulated locally.
        string shown =
            !string.IsNullOrEmpty(finalAnswer) ? finalAnswer
            : (_streamingAnswer != null ? _streamingAnswer.ToString() : "");
        if (answerText != null)
            answerText.text = string.IsNullOrEmpty(shown) ? "(no answer)" : shown;
        _streamingAnswer = null;
        _destroyAt = Time.time + autoDestroySeconds;
    }

    public void Close()
    {
        Destroy(gameObject);
    }
}
