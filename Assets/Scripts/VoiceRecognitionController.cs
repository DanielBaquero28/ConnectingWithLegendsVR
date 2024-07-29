using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class VoiceRecognitionController : MonoBehaviour
{
    public TMP_InputField OutputField;
    private AudioRecorder audioRecorder;
    private SpeechToText speechToText;

    public TMP_Text CountdownText;
    public float countdown;
    private bool recording = false;

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
            Debug.Log(countdown);
            CountdownText.text = countdown.ToString("F0");
        } else if (countdown < 0)
        {
            recording = false;
            CountdownText.text = "0";
        }

    }

    private IEnumerator ProcessRecording(AudioClip audioClip)
    {
        yield return new WaitForSeconds(10);

        string audioPath = audioRecorder.SaveWavFile(audioClip, Application.persistentDataPath + "/audio_prompt.wav");
        Debug.Log(audioPath);
        yield return StartCoroutine(speechToText.Recognize(audioPath, OutputField));
    }
}
