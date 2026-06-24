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

    private float _destroyAt;

    void Awake()
    {
        ARPanelStyle.ApplyTo(gameObject, ARPanelLayoutKind.AnswerCard);
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
