using System;
using System.Collections.Generic;
using UnityEngine;

/*
NoteManager

Owns the full note lifecycle:
  1) BeginNote(...)            -> spawn SaveNoteCard at the supplied position
  2) Save pressed              -> create Note record + spawn StickyNote, close card
  3) Cancel/Close pressed      -> close card, no record
  4) Sticky pinched            -> spawn ViewNoteCard with that note's text
  5) Edit pressed in view      -> close view, spawn SaveNoteCard pre-filled
                                  (Save then UPDATES the same Note record)
  6) Delete pressed in view    -> remove Note + destroy StickyNote + close view
  7) Close pressed in view     -> close view, note remains

ResultCardSpawner is responsible for translating a Save VLM_RESULT payload
into a BeginNote(...) call -- this manager doesn't know about VLM payloads,
only about (objectId, objectName, worldPos).

In-memory only for now; persistence (JSON to disk, etc.) can be layered on
later without changing the public surface.
*/
public class NoteManager : MonoBehaviour
{
    [Header("Prefabs (assign in Inspector)")]
    public GameObject saveNoteCardPrefab;
    public GameObject viewNoteCardPrefab;
    public GameObject stickyNotePrefab;

    [Header("Behaviour")]
    [Tooltip("Replace the open card when a new BeginNote() arrives. False ignores new requests while a card is open.")]
    public bool replaceOpenCard = true;
    [Tooltip("Only one card on screen at a time. ViewNoteCard and SaveNoteCard cannot coexist.")]
    public bool singleOpenCard = true;
    [Tooltip("Multiplier applied to the StickyNote prefab's localScale at spawn. 1 = prefab scale unchanged; 0.5 = half size; 0.1 = one-tenth, etc.")]
    public float stickyNoteScale = 0.3f;
    public bool verboseLogging = true;

    [Serializable]
    public class Note
    {
        public string id;            // unique within this session
        public string objectId;
        public string objectName;
        public string text;
        public Vector3 worldPos;
        [NonSerialized] public StickyNote stickyInstance;
    }

    private readonly List<Note> _notes = new List<Note>();
    public IReadOnlyList<Note> Notes => _notes;

    private GameObject _openCard;          // current SaveNoteCard or ViewNoteCard instance
    private Note _editingNote;             // non-null while a SaveNoteCard is editing an existing note
    // While a ViewNoteCard (or its Edit-mode SaveNoteCard) is open for a note,
    // the note's own StickyNote is hidden -- sticky and card should never both
    // be visible at once. Restored when the card closes.
    private Note _hiddenStickyNote;

    // ---------- Public entry: called by ResultCardSpawner ----------

    /// <summary>Open a SaveNoteCard for the just-recognised object at the given
    /// world position. Used for both a brand new note and the user's first
    /// note-taking after Save RECOGNIZED comes back from Python.</summary>
    public void BeginNote(string objectId, string objectName, Vector3 worldPos, Quaternion rot)
    {
        if (saveNoteCardPrefab == null)
        {
            Debug.LogWarning("[NoteManager] saveNoteCardPrefab not assigned; can't open note UI.");
            return;
        }

        if (_openCard != null)
        {
            if (!replaceOpenCard)
            {
                if (verboseLogging) Debug.Log("[NoteManager] a card is already open; ignoring new BeginNote.");
                return;
            }
            // Switching context to a new object -- reveal whichever sticky was
            // hidden by the card we're about to replace.
            RestoreHiddenSticky();
            CloseOpenCard();
        }

        _editingNote = null;  // new-note mode
        SpawnSaveCard(objectId, objectName, "", worldPos, rot);
    }

    /// <summary>Voice Save shortcut: skip the SaveNoteCard input UI and
    /// commit a new note straight to a StickyNote. Used when Python's voice
    /// classifier already extracted the note body from the transcript
    /// (e.g. "~~라고 노트 저장해줘"). Matches the "new note" branch of
    /// HandleSaveCommitted so the resulting Note record + StickyNote are
    /// indistinguishable from ones the user typed manually.</summary>
    public void CommitNoteDirectly(string objectId, string objectName, string text,
                                   Vector3 worldPos, Quaternion rot)
    {
        if (stickyNotePrefab == null)
        {
            Debug.LogWarning("[NoteManager] stickyNotePrefab not assigned; cannot commit voice note.");
            return;
        }

        // A voice Save arriving mid-edit replaces the open card, same policy
        // as BeginNote uses -- otherwise the user could end up with a hidden
        // sticky note the surrounding UI has no handle on.
        if (_openCard != null)
        {
            RestoreHiddenSticky();
            CloseOpenCard();
        }
        _editingNote = null;

        Note note = new Note
        {
            id = Guid.NewGuid().ToString("N").Substring(0, 8),
            objectId = objectId ?? "",
            objectName = objectName ?? "",
            text = text ?? "",
            worldPos = worldPos,
        };
        note.stickyInstance = SpawnSticky(note, worldPos, rot);
        _notes.Add(note);
        if (verboseLogging)
            Debug.Log(
                $"[NoteManager] voice note {note.id} committed on '{note.objectName}' " +
                $"(len={note.text.Length}, total {_notes.Count}).");
    }

    // ---------- SaveNoteCard ----------

    void SpawnSaveCard(string objectId, string objectName, string existingText, Vector3 pos, Quaternion rot, bool overwriteExisting = false)
    {
        var go = Instantiate(saveNoteCardPrefab, pos, rot);
        EnsureConstantSize(go);
        var card = go.GetComponent<SaveNoteCard>();
        if (card == null)
        {
            Debug.LogError("[NoteManager] saveNoteCardPrefab has no SaveNoteCard component.");
            Destroy(go);
            return;
        }
        card.SetContent(objectId, objectName, existingText, overwriteExisting);
        card.OnSaveClicked   += text => HandleSaveCommitted(card, text);
        card.OnCancelClicked += () => HandleCardDismissed(go);
        card.OnCloseClicked  += () => HandleCardDismissed(go);
        _openCard = go;
        if (verboseLogging)
            Debug.Log($"[NoteManager] SaveNoteCard opened (object={objectName}, editing={_editingNote != null}, overwrite={overwriteExisting}).");
    }

    void HandleSaveCommitted(SaveNoteCard card, string text)
    {
        Vector3 pos = card.transform.position;
        Quaternion rot = card.transform.rotation;
        string objectId = card.ObjectId;
        string objectName = card.ObjectName;

        if (_editingNote != null)
        {
            // Edit flow: update text + keep sticky as-is (the sticky was hidden
            // while the edit card was open; RestoreHiddenSticky brings it back).
            _editingNote.text = text;
            if (verboseLogging)
                Debug.Log($"[NoteManager] note {_editingNote.id} updated (len={text?.Length ?? 0}).");
            _editingNote = null;
            RestoreHiddenSticky();
        }
        else
        {
            // New note flow: record + spawn sticky at the same position.
            var note = new Note
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 8),
                objectId = objectId,
                objectName = objectName,
                text = text ?? "",
                worldPos = pos,
            };
            note.stickyInstance = SpawnSticky(note, pos, rot);
            _notes.Add(note);
            if (verboseLogging)
                Debug.Log($"[NoteManager] note {note.id} saved on '{objectName}' (total {_notes.Count}).");
        }

        CloseOpenCard();
    }

    void HandleCardDismissed(GameObject card)
    {
        if (_openCard == card) CloseOpenCard();
        else if (card != null) Destroy(card);
        _editingNote = null;
        RestoreHiddenSticky();
    }

    void CloseOpenCard()
    {
        if (_openCard != null) Destroy(_openCard);
        _openCard = null;
    }

    // ---------- StickyNote visibility helpers ----------

    void HideStickyForCardSession(Note note)
    {
        if (note == null || note.stickyInstance == null) return;
        // Restore any previously hidden sticky first (paranoid safety: e.g.
        // the user pinches sticky B while a card for note A is already open).
        RestoreHiddenSticky();
        note.stickyInstance.gameObject.SetActive(false);
        _hiddenStickyNote = note;
        if (verboseLogging)
            Debug.Log($"[NoteManager] sticky for note {note.id} hidden while its card is open.");
    }

    void RestoreHiddenSticky()
    {
        if (_hiddenStickyNote == null) return;
        StickyNote sticky = _hiddenStickyNote.stickyInstance;
        if (sticky != null && sticky.gameObject != null)
            sticky.gameObject.SetActive(true);
        if (verboseLogging)
            Debug.Log($"[NoteManager] sticky for note {_hiddenStickyNote.id} restored.");
        _hiddenStickyNote = null;
    }

    // ---------- StickyNote ----------

    StickyNote SpawnSticky(Note note, Vector3 pos, Quaternion rot)
    {
        if (stickyNotePrefab == null)
        {
            Debug.LogWarning("[NoteManager] stickyNotePrefab not assigned; note is saved but has no visual marker.");
            return null;
        }
        var go = Instantiate(stickyNotePrefab, pos, rot);
        if (!Mathf.Approximately(stickyNoteScale, 1f))
            go.transform.localScale *= Mathf.Max(0.001f, stickyNoteScale);
        EnsureConstantSize(go);
        var sticky = go.GetComponent<StickyNote>();
        if (sticky == null)
        {
            Debug.LogError("[NoteManager] stickyNotePrefab has no StickyNote component.");
            Destroy(go);
            return null;
        }
        sticky.Bind(note.id, note.objectId);
        sticky.OnPinched += HandleStickyPinched;
        return sticky;
    }

    void HandleStickyPinched(StickyNote sticky)
    {
        var note = FindNote(sticky.NoteId);
        if (note == null)
        {
            Debug.LogWarning($"[NoteManager] pinch on sticky for unknown note id={sticky.NoteId}.");
            return;
        }
        if (singleOpenCard && _openCard != null)
        {
            // Any previously hidden sticky (from a card we're about to replace)
            // needs to reappear before we hide this new one.
            RestoreHiddenSticky();
            CloseOpenCard();
        }
        HideStickyForCardSession(note);
        SpawnViewCard(note);
    }

    // ---------- ViewNoteCard ----------

    void SpawnViewCard(Note note)
    {
        if (viewNoteCardPrefab == null)
        {
            Debug.LogWarning("[NoteManager] viewNoteCardPrefab not assigned; cannot open view.");
            return;
        }
        var rot = note.stickyInstance != null ? note.stickyInstance.transform.rotation : Quaternion.identity;
        var go = Instantiate(viewNoteCardPrefab, note.worldPos, rot);
        EnsureConstantSize(go);
        var view = go.GetComponent<ViewNoteCard>();
        if (view == null)
        {
            Debug.LogError("[NoteManager] viewNoteCardPrefab has no ViewNoteCard component.");
            Destroy(go);
            return;
        }
        view.SetContent(note.id, note.objectName, note.text);
        view.OnEditClicked   += () => HandleEdit(view, note);
        view.OnDeleteClicked += () => HandleDelete(view, note);
        view.OnCloseClicked  += () => HandleCardDismissed(go);
        _openCard = go;
        if (verboseLogging)
            Debug.Log($"[NoteManager] ViewNoteCard opened for note {note.id}.");
    }

    void HandleEdit(ViewNoteCard view, Note note)
    {
        // Swap the view for a SaveNoteCard prefilled with the current text.
        // Save commit will UPDATE this same Note record (because _editingNote is set).
        // overwriteExisting=true so the first STT partial replaces the shown
        // text -- there's no keyboard for surgical edits, so Edit == redo.
        Vector3 pos = view.transform.position;
        Quaternion rot = view.transform.rotation;
        CloseOpenCard();
        _editingNote = note;
        SpawnSaveCard(note.objectId, note.objectName, note.text, pos, rot, overwriteExisting: true);
    }

    void HandleDelete(ViewNoteCard view, Note note)
    {
        if (verboseLogging)
            Debug.Log($"[NoteManager] note {note.id} deleted.");
        if (note.stickyInstance != null) Destroy(note.stickyInstance.gameObject);
        _notes.Remove(note);
        // Sticky is gone -- clear the hidden reference so RestoreHiddenSticky
        // doesn't try to re-enable a destroyed GameObject on future dismiss.
        if (_hiddenStickyNote == note) _hiddenStickyNote = null;
        CloseOpenCard();
    }

    // ---------- helpers ----------

    Note FindNote(string id)
    {
        for (int i = 0; i < _notes.Count; i++)
            if (_notes[i].id == id) return _notes[i];
        return null;
    }

    void EnsureConstantSize(GameObject go)
    {
        if (go == null) return;
        DistanceConstantSize comp = go.GetComponent<DistanceConstantSize>();
        if (comp == null) comp = go.AddComponent<DistanceConstantSize>();
        // Component self-resolves referenceCamera to Camera.main and uses its
        // own default referenceDistance / floor / max. Tune via inspector if
        // a specific prefab needs a different depth band.
    }
}
