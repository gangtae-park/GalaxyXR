using TMPro;
using UnityEngine;

/*
AnchorPin

Per-instance behavior for the anchor_pin prefab. Mirrors the pattern of
SearchResultCard / TranslateResultCard / etc. -- attached to the prefab root,
populated by ResultCardSpawner via SetContent() right after Instantiate.

Anchors persist (no auto-destroy) and keep their world position and orientation
once spawned. If you want them billboarded toward the user later, add a
billboard component separately.
*/

public class AnchorPin : MonoBehaviour
{
    [Header("Optional UI")]
    [Tooltip("If assigned, the response.name is written here when SetContent() runs.")]
    public TMP_Text labelText;

    public string ObjectName { get; private set; }

    public void SetContent(string objectName)
    {
        ObjectName = objectName ?? "";
        if (labelText != null) labelText.text = ObjectName;
    }
}
