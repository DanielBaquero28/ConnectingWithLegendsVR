using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class ClearPromptText : MonoBehaviour
{
    [SerializeField]
    public TMP_InputField PromptInputField;

    [SerializeField]
    public TMP_Text ResponseText;

    public void Start()
    {
        Button eraseButton = this.GetComponent<Button>();

        eraseButton.onClick.AddListener(ClearText);
    }

    public void ClearText()
    {
        PromptInputField.text = string.Empty;
        ResponseText.text = string.Empty;
    }
}
