using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AskQuestionCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text titleText;
    public TMP_Text statusText;
    public Button closeButton;

    [Header("Status messages")]
    public string listeningMessage = "Listening...";
    public string thinkingMessage = "Thinking...";

    public string ObjectName { get; private set; }
    public string SubmittedQuestion { get; private set; }

    public event Action<string> OnQuestionSubmitted;

    void Awake()
    {
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (statusText != null) statusText.text = listeningMessage;
    }

    void OnDestroy()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(Close);
    }

    public void SetObjectName(string name)
    {
        ObjectName = name;
        if (titleText != null)
        {
            string safeName = string.IsNullOrEmpty(name) ? "Unknown" : name;
            titleText.text = $"{safeName}";
        }
        if (statusText != null && string.IsNullOrEmpty(SubmittedQuestion))
            statusText.text = listeningMessage;
    }

    public void NotifyThinking()
    {
        if (statusText != null) statusText.text = thinkingMessage;
    }

    public void Submit(string question)
    {
        SetSubmittedQuestion(question);
        OnQuestionSubmitted?.Invoke(SubmittedQuestion);
        NotifyThinking();
    }

    public void SubmitLocal(string question)
    {
        SetSubmittedQuestion(question);
        NotifyThinking();
    }

    void SetSubmittedQuestion(string question)
    {
        SubmittedQuestion = question ?? "";
        if (statusText != null)
            statusText.text = string.IsNullOrWhiteSpace(SubmittedQuestion)
                ? thinkingMessage
                : SubmittedQuestion;
    }

    public void Close()
    {
        Destroy(gameObject);
    }
}
