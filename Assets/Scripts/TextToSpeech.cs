using UnityEngine;
using Microsoft.CognitiveServices.Speech;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

public class TextToSpeech : MonoBehaviour
{
    private string speech_key;
    private string speech_region;

    public AudioSource audioSource;

    private void Start()
    {
        speech_key = System.Environment.GetEnvironmentVariable("SPEECH_KEY");
        speech_region = System.Environment.GetEnvironmentVariable("SPEECH_REGION");

        if (string.IsNullOrEmpty(speech_key))
        {
            Debug.LogError("Speech API key is not found in the list of environment variables");
        }

        if (string.IsNullOrEmpty(speech_region))
        {
            Debug.LogError("Speech API Region is not found in the list of environment variables");
        }

        //SynthesizeSpeech("Hello World");
    }

    

    public async Task SynthesizeSpeech(string text)
    {
        Debug.Log("Flag 1");
        var config = SpeechConfig.FromSubscription(speech_key, speech_region);

        config.SpeechSynthesisVoiceName = "en-US-GuyNeural";

        using var synthesizer = new SpeechSynthesizer(config, null);

        using var result = await synthesizer.SpeakTextAsync(text);

        if (result.Reason == ResultReason.SynthesizingAudioCompleted)

        {
            Debug.Log("Speech synthesis succeeded.");
            PlayAudio(result.AudioData);
        } else
        {
            Debug.LogError($"Speech synthesis failed. Reason: {result.Reason}");
        }
    }

    private void PlayAudio(byte[] audioData)
    {
        Debug.Log("Flag 2");
        float[] samples = ConvertByteArrayToFloatArray(audioData);

        // Create AudioClip from samples
        AudioClip clip = AudioClip.Create("TTS_Audio", samples.Length, 1, 16000, false);
        clip.SetData(samples, 0);

        audioSource.clip = clip;
        audioSource.Play();
    }

    private float[] ConvertByteArrayToFloatArray(byte[] byteArray)
    {
        Debug.Log("Flag 3");
        int length = byteArray.Length / 2;
        float[] floatArray = new float[length];

        for (int i = 0; i < length; i++)
        {
            short value = BitConverter.ToInt16(byteArray, i * 2);
            floatArray[i] = value / 32768.0f;
        }

        return floatArray;
    }
}
