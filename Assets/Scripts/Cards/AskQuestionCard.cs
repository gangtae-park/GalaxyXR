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
            titleText.text = string.IsNullOrEmpty(name) ? "Unknown" : name;
    }

    public void NotifyThinking()
    {
        if (statusText != null) statusText.text = thinkingMessage;
    }

    public void Close()
    {
        Destroy(gameObject);
    }
}
