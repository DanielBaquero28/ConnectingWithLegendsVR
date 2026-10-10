using UnityEngine;

// Spec 3: AI reply audio now comes back from the proxy server, so this
// component no longer calls Google TTS and holds NO API key (the old
// Inspector key field is deleted on purpose — no provider keys exist
// on-device). The class is kept as the scene's holder of the character's
// AudioSource and a thin playback wrapper; the WAV-decoding logic moved to
// AudioPlaybackUtility so it exists in exactly one place.
public class TextToSpeechGoogle : MonoBehaviour
{
    public AudioSource audioSource;

    public void PlayWavBytes(byte[] audioData)
    {
        AudioPlaybackUtility.PlayWavBytes(audioSource, audioData);
    }
}
