using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ViewNoteCard : MonoBehaviour
{
    [Header("Refs")]
    public TMP_Text objectNameLabel;
    public TMP_Text noteTextDisplay;
    public Button editButton;
    public Button deleteButton;
    public Button closeButton;

    public string NoteId { get; private set; }

    public event Action OnEditClicked;
    public event Action OnDeleteClicked;
    public event Action OnCloseClicked;

    void Awake()
    {
        if (editButton != null)   editButton.onClick.AddListener(HandleEdit);
        if (deleteButton != null) deleteButton.onClick.AddListener(HandleDelete);
        if (closeButton != null)  closeButton.onClick.AddListener(HandleClose);
    }

    void OnDestroy()
    {
        if (editButton != null)   editButton.onClick.RemoveListener(HandleEdit);
        if (deleteButton != null) deleteButton.onClick.RemoveListener(HandleDelete);
        if (closeButton != null)  closeButton.onClick.RemoveListener(HandleClose);
    }

    public void SetContent(string noteId, string objectName, string noteText)
    {
        NoteId = noteId ?? "";
        if (objectNameLabel != null) objectNameLabel.text = objectName ?? "";
        if (noteTextDisplay != null) noteTextDisplay.text = noteText ?? "";
    }

    void HandleEdit()   { try { OnEditClicked?.Invoke(); }   catch (Exception e) { Debug.LogError(e); } }
    void HandleDelete() { try { OnDeleteClicked?.Invoke(); } catch (Exception e) { Debug.LogError(e); } }
    void HandleClose()  { try { OnCloseClicked?.Invoke(); }  catch (Exception e) { Debug.LogError(e); } }
}
