using UnityEngine;

public static class WavUtilityGoogle
{
    public static AudioClip ToAudioClip(byte[] wavBytes)
    {
        // El formato WAV tiene una cabecera de 44 bytes
        // Estos valores son estándar para LINEAR16 de Google TTS
        int channels = wavBytes[22];
        int sampleRate = System.BitConverter.ToInt32(wavBytes, 24);
        int pos = 44; // Saltamos la cabecera

        int samplesCount = (wavBytes.Length - pos) / 2;
        float[] floatData = new float[samplesCount];

        for (int i = 0; i < samplesCount; i++)
        {
            short bitData = System.BitConverter.ToInt16(wavBytes, pos);
            floatData[i] = bitData / 32768f;
            pos += 2;
        }

        AudioClip clip = AudioClip.Create("EinsteinVoice", samplesCount, channels, sampleRate, false);
        clip.SetData(floatData, 0);
        return clip;
    }
}