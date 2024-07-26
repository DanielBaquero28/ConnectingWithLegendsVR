using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class TestUIController : MonoBehaviour
{
    public Button SubmitButton;
    public VoiceRecognitionController VoiceRecognitionController;

    private void Start()
    {
        //Debug.Log(Application.persistentDataPath);
        SubmitButton.onClick.AddListener(VoiceRecognitionController.StartRecording);
    }
}
