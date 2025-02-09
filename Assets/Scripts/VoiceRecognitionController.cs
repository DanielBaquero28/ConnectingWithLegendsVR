using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class VoiceRecognitionController : MonoBehaviour
{
    public TMP_InputField OutputField;
    private AudioRecorder audioRecorder;
    private SpeechToText speechToText;

    public TMP_Text CountdownText;
    private float countdown = 10f;
    private bool recording = false;

    [SerializeField]
    public Button RecordButton;

    private void Start()
    {
        audioRecorder = gameObject.AddComponent<AudioRecorder>();
        speechToText = gameObject.AddComponent<SpeechToText>();
    }

    public void StartRecording()
    {
        Debug.Log("Started recording!");
        recording = true;
        countdown = 10f;
        AudioClip audioClip = audioRecorder.RecordAudio();
        StartCoroutine(ProcessRecording(audioClip));
    }

    private void Update()
    {
        if (recording == true && countdown >= 0)
        {
            countdown -= Time.deltaTime;
            //Debug.Log(countdown);
            CountdownText.text = countdown.ToString("F0");
        } else if (countdown < 0)
        {
            recording = false;
            CountdownText.text = "0";
            CountdownText.gameObject.SetActive(false);
            RecordButton.interactable = true;
        }

    }

    private IEnumerator ProcessRecording(AudioClip audioClip)
    {
        yield return new WaitForSeconds(countdown);

        string audioPath = audioRecorder.SaveWavFile(audioClip, Application.persistentDataPath + "/audio_prompt.wav");
        //Debug.Log(audioPath);
        yield return StartCoroutine(speechToText.Recognize(audioPath, OutputField));
    }
}
