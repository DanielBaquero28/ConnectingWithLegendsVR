using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;

public class AudioRecorder : MonoBehaviour
{
    public AudioClip RecordAudio(int duration = 10, int frequency = 44100)
    {
        AudioClip audioClip = Microphone.Start(null,false, duration, frequency);

        return audioClip;
    }

    public string SaveWavFile(AudioClip audioClip, string filePath)
    {
        var samples = new float[audioClip.samples];
        audioClip.GetData(samples, 0);
        byte[] wavFile = WavUtility.FromAudioClip(audioClip);
        File.WriteAllBytes(filePath, wavFile);
        return filePath;
    }
}
