using System.Collections;
using TMPro;
using UnityEngine;

// Question-field caret (2026-10-28). On the Quest, after typing on the
// on-screen keyboard and pressing Enter without Submit, re-selecting the
// field left the caret invisible and at the start of the text. The caret
// settings (width, colour, blink) live on the TMP_InputField in each scene;
// this component only keeps the caret at the END of the text whenever the
// field is selected or the keyboard writes into it, with nothing selected.
// Added to the field by the PromptFieldSetup editor tool; no Inspector fields.
[RequireComponent(typeof(TMP_InputField))]
public class PromptCaret : MonoBehaviour
{
    private TMP_InputField field;

    private void Awake()
    {
        field = GetComponent<TMP_InputField>();
        field.onFocusSelectAll = false;
        field.onSelect.AddListener(_ => MoveToEndNextFrame());
        field.onValueChanged.AddListener(_ => { if (field.isFocused) MoveToEnd(); });
    }

    // TMP positions its own caret/selection during the select handling, so
    // the move happens one frame later to land after it.
    private void MoveToEndNextFrame()
    {
        if (isActiveAndEnabled) StartCoroutine(MoveToEndRoutine());
    }

    private IEnumerator MoveToEndRoutine()
    {
        yield return null;
        MoveToEnd();
    }

    public void MoveToEnd()
    {
        int end = field.text != null ? field.text.Length : 0;
        field.selectionAnchorPosition = end;
        field.selectionFocusPosition = end;
        field.caretPosition = end;
        field.ForceLabelUpdate();
    }
}
