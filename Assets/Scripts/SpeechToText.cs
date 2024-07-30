using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Google.Cloud.Speech.V1;
using TMPro;

public class SpeechToText : MonoBehaviour
{

    private SpeechClient speechClient;

    private void Start()
    {
        speechClient = SpeechClient.Create();
    }

    public IEnumerator Recognize(string audioPath, TMP_InputField outputField)
    {
        //Debug.Log("Entered Recognize.");
        var response = speechClient.Recognize(new RecognitionConfig()
        {
            Encoding = RecognitionConfig.Types.AudioEncoding.Linear16,
            SampleRateHertz = 44100,
            LanguageCode = LanguageCodes.Spanish.Colombia,
        }, RecognitionAudio.FromFile(audioPath));

        //Debug.Log(response);

        foreach (var result in response.Results)
        {
            foreach (var alternative in result.Alternatives)
            {
                outputField.text += " " + alternative.Transcript;
            }
        }
        yield return null;
    }
}
