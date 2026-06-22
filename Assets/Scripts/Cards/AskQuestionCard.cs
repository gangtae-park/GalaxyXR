using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
AskQuestionCard

Spawned when the Ask gesture's first VLM result arrives (object identified).
Shows the object name and waits for the user's voice question to be captured.
The card stays alive until the spawner destroys it on the second Ask result arrival.
*/

public class AskQuestionCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text titleText;
    public TMP_Text questionText;
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

    /// <summary>Use when the question text is known on the Unity side (text-only flow).
    /// Puts the text in questionText, flips status to "Thinking...", and fires the
    /// OnQuestionSubmitted event.</summary>
    public void Submit(string question)
    {
        SubmittedQuestion = question ?? "";
        if (questionText != null) questionText.text = SubmittedQuestion;
        if (statusText != null) statusText.text = thinkingMessage;
        try { OnQuestionSubmitted?.Invoke(SubmittedQuestion); } catch (Exception e) { Debug.LogError(e); }
    }

    /// <summary>Use when the question text is NOT known on the Unity side (audio is sent
    /// to Python which does STT). Just switches the status to "Thinking..." -- the
    /// recognized question text will be filled in on the next card (AskResultCard) when
    /// Python echoes it back in target_meta.user_question.</summary>
    public void NotifyThinking()
    {
        if (statusText != null) statusText.text = thinkingMessage;
    }

    public void Close()
    {
        Destroy(gameObject);
    }
}
