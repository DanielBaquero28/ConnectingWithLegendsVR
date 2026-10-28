using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Microsoft.MixedReality.Toolkit.Experimental.UI;

public class ShowKeyboard : MonoBehaviour
{
    private TMP_InputField inputField;
    private float Distance = 0.5f;
    private float VerticalOffset = -0.14f;

    public Transform PositionSource;
    
    // Start is called before the first frame update
    void Start()
    {
        inputField = GetComponent<TMP_InputField>();
        inputField.onSelect.AddListener(x => OpenKeyboard());
    }



    public void OpenKeyboard()
    {
        NonNativeKeyboard.Instance.InputField = inputField;
        NonNativeKeyboard.Instance.PresentKeyboard(inputField.text);

        Vector3 direction = PositionSource.forward;
        direction.y = 0;
        direction.Normalize();

        Vector3 targetPosition = PositionSource.position + direction * Distance + Vector3.up * VerticalOffset;

        NonNativeKeyboard.Instance.RepositionKeyboard(targetPosition);

        // 2026-10-28: this used to set the caret colour's alpha to 0 when the
        // keyboard closed (and back to 1 only here), which is why the caret was
        // invisible after pressing Enter. The caret now keeps the colour, width
        // and blink set on the field in the scene; PromptCaret keeps it at the
        // end of the text.
    }
}
