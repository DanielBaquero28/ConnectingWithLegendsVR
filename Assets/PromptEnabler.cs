using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PromptEnabler : MonoBehaviour
{
    [SerializeField]
    public Transform OVRCamera;

    [SerializeField]
    public Canvas MainBoardTitleCanvas;

    [SerializeField]
    public Button EnablerButton;

    private float activationThreshold = 0.5f;

    private TMP_Text buttonText;

    private void Start()
    {
        buttonText = EnablerButton.GetComponentInChildren<TMP_Text>();
    }

    // Update is called once per frame
    void Update()
    {
        if (IsCanvasInView())
        {
            EnablerButton.interactable = true;
            EnablerButton.image.color = Color.green;
            buttonText.color = Color.black;
        }
        else
        {
            EnablerButton.interactable = false;
            EnablerButton.image.color = Color.red;
            buttonText.color = Color.white;
        }
    }

    private bool IsCanvasInView()
    {
        // Get direction
        Vector3 direction = (MainBoardTitleCanvas.transform.position - OVRCamera.position).normalized;
        //Debug.Log($"Direction: {direction}");

        Vector3 cameraForward = OVRCamera.forward;
        Debug.Log($"Camera Forward: {cameraForward}");

        float dotProduct = Vector3.Dot(cameraForward, direction);
        Debug.Log($"Dot Product: {dotProduct}");

        return dotProduct > activationThreshold;


    }
}
