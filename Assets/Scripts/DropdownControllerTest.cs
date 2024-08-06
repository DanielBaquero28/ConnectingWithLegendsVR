using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class DropdownControllerTest : MonoBehaviour
{
    private int optionSelected;

    public void DropdownSample(int index)
    {
        switch (index)
        {
            case 0:
                optionSelected = 1;
                Debug.Log("Albert Einstein");
                break;
            case 1:
                optionSelected = 2;
                Debug.Log("Alexander the Great");
                break;
            case 2:
                optionSelected = 3;
                Debug.Log("Leonardo da Vinci");
                break;
            default:
                optionSelected = 0;
                return;
        }

    }
}
