using System;
using UnityEngine;

// Single home of the LINEAR16-WAV -> AudioClip playback logic (moved from
// TextToSpeechGoogle so the proxy path and any future caller share one copy).
public static class AudioPlaybackUtility
{
    // Decodes a base64-encoded WAV (as returned by the proxy) and plays it.
    public static bool PlayBase64Wav(AudioSource audioSource, string base64Wav)
    {
        if (string.IsNullOrEmpty(base64Wav)) return false;

        byte[] audioData;
        try
        {
            audioData = Convert.FromBase64String(base64Wav);
        }
        catch (FormatException e)
        {
            Debug.LogError("[Audio] Invalid base64 audio: " + e.Message);
            return false;
        }
        return PlayWavBytes(audioSource, audioData);
    }

    // Plays raw LINEAR16 WAV bytes (mono, 44-byte header).
    public static bool PlayWavBytes(AudioSource audioSource, byte[] audioData)
    {
        if (audioSource == null)
        {
            Debug.LogWarning("[Audio] No AudioSource assigned; skipping playback.");
            return false;
        }

        // LINEAR16 WAV header is 44 bytes
        const int headerOffset = 44;
        if (audioData == null || audioData.Length <= headerOffset)
        {
            Debug.LogWarning("[Audio] Audio data too short to play.");
            return false;
        }

        int samplesCount = (audioData.Length - headerOffset) / 2;
        float[] floatData = new float[samplesCount];

        for (int i = 0; i < samplesCount; i++)
        {
            short bitData = BitConverter.ToInt16(audioData, headerOffset + (i * 2));
            floatData[i] = bitData / 32768f;
        }

        // Read the sample rate from the WAV header (bytes 24-27) instead of
        // hardcoding 24000 — prevents distortion if the server output changes.
        int sampleRate = BitConverter.ToInt32(audioData, 24);

        AudioClip clip = AudioClip.Create("AI_Voice", samplesCount, 1, sampleRate, false);
        clip.SetData(floatData, 0);

        audioSource.clip = clip;
        audioSource.Play();

        float durationSec = (float)samplesCount / sampleRate;
        Debug.Log($"[Audio] Playing {durationSec:F1}s of audio at {sampleRate}Hz");
        return true;
    }
}
