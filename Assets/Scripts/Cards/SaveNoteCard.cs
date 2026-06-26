using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
SaveNoteCard

Per-instance behaviour for the SaveNoteCard prefab. Owns the input field and
three buttons; raises C# events when the user commits / cancels / closes so
the NoteManager can drive the rest of the flow without the prefab needing any
inspector wiring beyond the field/button refs below.

The card is reused for two purposes:
  - new note    : SetContent(objectName, "") and the user types from scratch
  - edit note   : SetContent(objectName, existingText) prefills the field
*/
public class SaveNoteCard : MonoBehaviour
{
    [Header("Refs (assign in prefab)")]
    [Tooltip("Optional. Shows which object the note is being attached to.")]
    public TMP_Text objectNameLabel;
    [Tooltip("Required. The input field where the user types the note text.")]
    public TMP_InputField noteField;
    public Button saveButton;
    public Button cancelButton;
    public Button closeButton;

    public string ObjectId { get; private set; }
    public string ObjectName { get; private set; }
    public string CurrentText => noteField != null ? noteField.text : "";

    /// <summary>Fired with the current note text when Save is pressed.</summary>
    public event Action<string> OnSaveClicked;
    /// <summary>Fired when Cancel is pressed.</summary>
    public event Action OnCancelClicked;
    /// <summary>Fired when Close (X) is pressed.</summary>
    public event Action OnCloseClicked;

    void Awake()
    {
        if (saveButton != null)   saveButton.onClick.AddListener(HandleSave);
        if (cancelButton != null) cancelButton.onClick.AddListener(HandleCancel);
        if (closeButton != null)  closeButton.onClick.AddListener(HandleClose);
    }

    void OnDestroy()
    {
        if (saveButton != null)   saveButton.onClick.RemoveListener(HandleSave);
        if (cancelButton != null) cancelButton.onClick.RemoveListener(HandleCancel);
        if (closeButton != null)  closeButton.onClick.RemoveListener(HandleClose);
    }

    public void SetContent(string objectId, string objectName, string existingText = "")
    {
        ObjectId = objectId ?? "";
        ObjectName = objectName ?? "";
        if (objectNameLabel != null) objectNameLabel.text = ObjectName;
        if (noteField != null)
        {
            noteField.text = existingText ?? "";
            noteField.ActivateInputField();
        }
    }

    void HandleSave()   { try { OnSaveClicked?.Invoke(CurrentText); }  catch (Exception e) { Debug.LogError(e); } }
    void HandleCancel() { try { OnCancelClicked?.Invoke(); }            catch (Exception e) { Debug.LogError(e); } }
    void HandleClose()  { try { OnCloseClicked?.Invoke(); }             catch (Exception e) { Debug.LogError(e); } }
}
