using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Microsoft.MixedReality.Toolkit.Experimental.UI;

public class ShowKeyboard : MonoBehaviour
{
    private TMP_InputField inputField;
    private PromptCaret caret;
    private float Distance = 0.5f;
    private float VerticalOffset = -0.14f;

    public Transform PositionSource;

    // True while the MRTK keyboard is presented for this field.
    public bool IsKeyboardOpen =>
        NonNativeKeyboard.Instance != null && NonNativeKeyboard.Instance.gameObject.activeInHierarchy &&
        NonNativeKeyboard.Instance.InputField == inputField;

    // Start is called before the first frame update
    void Start()
    {
        inputField = GetComponent<TMP_InputField>();
        caret = GetComponent<PromptCaret>();
        inputField.onSelect.AddListener(x => OpenKeyboard());
    }



    public void OpenKeyboard()
    {
        if (IsKeyboardOpen) return; // a tap that also raised onSelect

        NonNativeKeyboard.Instance.InputField = inputField;
        NonNativeKeyboard.Instance.PresentKeyboard(inputField.text);

        Vector3 direction = PositionSource.forward;
        direction.y = 0;
        direction.Normalize();

        Vector3 targetPosition = PositionSource.position + direction * Distance + Vector3.up * VerticalOffset;

        NonNativeKeyboard.Instance.RepositionKeyboard(targetPosition);

        // 2026-10-28: this used to set the caret colour's alpha to 0 when the
        // keyboard closed (and back to 1 only here). The caret now keeps the
        // colour, width and blink set on the field in the scene.
        // PresentKeyboard(text) calls the keyboard's Clear(), which leaves the
        // caret at 0; PromptCaret moves it to the end once the field is active.
        if (caret != null) caret.MoveToEndWhenFocused("keyboardOpen");

        NonNativeKeyboard.Instance.OnClosed += Instance_OnClosed;
    }

    private void Instance_OnClosed(object sender, System.EventArgs e)
    {
        NonNativeKeyboard.Instance.OnClosed -= Instance_OnClosed;
        if (caret != null) caret.Log("keyboardClose");
    }
}
