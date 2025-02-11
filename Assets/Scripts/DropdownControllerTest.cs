using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class DropdownControllerTest : MonoBehaviour
{
    private int optionSelected;

    [SerializeField]
    public GameObject AlbertEinstein;

    [SerializeField]
    public GameObject AlexanderTheGreat;

    [SerializeField]
    public GameObject LeonardoDaVinci;

    public void DropdownSample(int index)
    {
        switch (index)
        {
            case 0:
                optionSelected = 1;
                Debug.Log("Albert Einstein");
                AlbertEinstein.SetActive(true);
                AlexanderTheGreat.SetActive(false);
                LeonardoDaVinci.SetActive(false);
                break;
            case 1:
                optionSelected = 2;
                Debug.Log("Alexander the Great");
                AlbertEinstein.SetActive(false);
                AlexanderTheGreat.SetActive(true);
                LeonardoDaVinci.SetActive(false);
                break;
            case 2:
                optionSelected = 3;
                Debug.Log("Leonardo da Vinci");
                AlbertEinstein.SetActive(false);
                AlexanderTheGreat.SetActive(false);
                LeonardoDaVinci.SetActive(true);
                break;
            default:
                optionSelected = 0;
                return;
        }

    }
}
