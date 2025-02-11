using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class DisableButton : MonoBehaviour
{
    private Button recordButton;

    [SerializeField]
    private Button submitButton;

    [SerializeField]
    private Button newQuestionButton;

    // Start is called before the first frame update
    void Start()
    {
        recordButton = this.GetComponent<Button>();

        recordButton.onClick.AddListener(ButtonDisable);
    }

    public void ButtonDisable()
    {
        recordButton.interactable = false;
        submitButton.interactable = false;
        newQuestionButton.interactable = false;
    }
}
