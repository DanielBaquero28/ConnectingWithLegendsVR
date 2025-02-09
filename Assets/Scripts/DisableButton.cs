using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class DisableButton : MonoBehaviour
{
    private Button recordButton;

    // Start is called before the first frame update
    void Start()
    {
        recordButton = this.GetComponent<Button>();

        recordButton.onClick.AddListener(ButtonDisable);
    }

    public void ButtonDisable()
    {
        recordButton.interactable = false;
    }
}
