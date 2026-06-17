using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
AskResultCard

Spawned when the Ask gesture's second VLM result (the actual answer) arrives.
Mirrors SearchResultCard's lifetime semantics: 30-second auto-destroy timer
that keeps ticking regardless of grab / move, and a Close button that destroys
immediately.

Three text fields:
  TitleText    = recognized object name
  QuestionText = user's question (typically from payload.target_meta.user_question)
  AnswerText   = VLM's answer
*/

public class AskResultCard : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text titleText;
    public TMP_Text questionText;
    public TMP_Text answerText;
    public Button closeButton;

    [Header("Lifetime")]
    public float autoDestroySeconds = 30f;

    private float _destroyAt;

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
    }

    public void Close()
    {
        Destroy(gameObject);
    }
}
