using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

// Question-field caret. On the Quest, after typing on the keyboard and
// pressing Enter without Submit, re-selecting the field left the caret
// invisible and at the start of the text. Two causes, both device-only:
//
//  1. Invisible: on Android TMP_InputField opens a TouchScreenKeyboard on
//     select (TouchScreenKeyboard.isSupported is true there, false in the
//     Editor). With that keyboard open TMP stops drawing its own caret
//     ("handled by the device's keyboard", TMP_InputField.UpdateGeometry) and
//     takes caret positions from the soft keyboard's selection. This app does
//     not use that keyboard: the Meta system keyboard is not enabled
//     (requiresSystemKeyboard 0) and students type on the MRTK
//     NonNativeKeyboard that ShowKeyboard presents. shouldHideSoftKeyboard =
//     true stops TMP opening it, so TMP draws and owns the caret again.
//  2. At the start: ShowKeyboard -> NonNativeKeyboard.PresentKeyboard(text)
//     calls Clear(), which moves the caret to 0 and empties the field, then
//     puts the text back without moving the caret.
//
// So on every select, keyboard open and tap on the field this moves the caret
// to the end of the text with nothing selected, once TMP has activated the
// field (it activates in its LateUpdate). The caret's look (width, colour,
// blink) is set on the field in each scene by PromptFieldSetup. [CaretDiag]
// logcat lines carry numbers and states only, never the text.
[RequireComponent(typeof(TMP_InputField))]
public class PromptCaret : MonoBehaviour, IPointerClickHandler
{
    private const int MAX_WAIT_FRAMES = 10;

    private TMP_InputField field;
    private ShowKeyboard keyboard;
    private Coroutine pending;

    // TMP 3.0.9 has no public accessor for the soft keyboard it opens; read
    // only for the diagnostics and the belt-and-braces selection below.
    private static readonly FieldInfo SoftKeyboardField =
        typeof(TMP_InputField).GetField("m_SoftKeyboard", BindingFlags.Instance | BindingFlags.NonPublic);

    private void Awake()
    {
        field = GetComponent<TMP_InputField>();
        keyboard = GetComponent<ShowKeyboard>();
        field.onFocusSelectAll = false;
        field.shouldHideSoftKeyboard = true;
        field.onSelect.AddListener(_ => MoveToEndWhenFocused("select"));
        field.onTouchScreenKeyboardStatusChanged.AddListener(_ => Log("softKeyboard"));
    }

    // A second tap on an already-selected field does not raise onSelect, so
    // the keyboard would not come back after Enter closed it. Re-present it,
    // then put the caret at the end (TMP has just placed it at the tap point).
    public void OnPointerClick(PointerEventData eventData)
    {
        if (keyboard != null && !keyboard.IsKeyboardOpen) keyboard.OpenKeyboard();
        MoveToEndWhenFocused("click");
    }

    public void MoveToEndWhenFocused(string reason)
    {
        if (!isActiveAndEnabled) return;
        if (pending != null) StopCoroutine(pending);
        pending = StartCoroutine(MoveToEndRoutine(reason));
    }

    private IEnumerator MoveToEndRoutine(string reason)
    {
        // TMP activates a selected field in its LateUpdate, which can reset the
        // caret; wait for that, then one more frame for the keyboard's Clear().
        for (int i = 0; i < MAX_WAIT_FRAMES && !field.isFocused; i++) yield return null;
        yield return null;
        MoveToEnd();
        Log(reason);
        pending = null;
    }

    public void MoveToEnd()
    {
        int end = field.text != null ? field.text.Length : 0;
        field.selectionAnchorPosition = end;
        field.selectionFocusPosition = end;
        field.caretPosition = end;
        // If a soft keyboard is open anyway, keep its selection in step.
        var soft = SoftKeyboard();
        if (soft != null) soft.selection = new RangeInt(end, 0);
        field.ForceLabelUpdate();
    }

    private TouchScreenKeyboard SoftKeyboard()
    {
        return SoftKeyboardField != null ? SoftKeyboardField.GetValue(field) as TouchScreenKeyboard : null;
    }

    // One numbers/states-only line: caret and selection indices, text length,
    // focus, keyboard states. Never the text itself.
    public void Log(string evt)
    {
        var soft = SoftKeyboard();
        string softState = soft == null ? "none" : soft.status.ToString();
        string softSel = soft == null ? "-" : $"{soft.selection.start}+{soft.selection.length}";
        Debug.Log($"[CaretDiag] event={evt} caret={field.caretPosition} anchor={field.selectionAnchorPosition} " +
                  $"focus={field.selectionFocusPosition} len={(field.text != null ? field.text.Length : 0)} " +
                  $"focused={field.isFocused.ToString().ToLowerInvariant()} " +
                  $"hideSoftKb={field.shouldHideSoftKeyboard.ToString().ToLowerInvariant()} " +
                  $"tsSupported={TouchScreenKeyboard.isSupported.ToString().ToLowerInvariant()} " +
                  $"softKb={softState} softSel={softSel} " +
                  $"mrtkKb={(keyboard != null && keyboard.IsKeyboardOpen ? "open" : "closed")} " +
                  $"caretWidth={field.caretWidth} caretAlpha={field.caretColor.a:F2}");
    }
}
