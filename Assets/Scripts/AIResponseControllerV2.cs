using System.Collections;
using TMPro;
using UnityEngine;

// Conversation orchestrator (Spec 3, section 5.2). All AI provider work —
// persona, safety re-checks, Gemini, TTS — now lives on the proxy server.
// This component keeps only: the isProcessing request lock, the client-side
// SafetyFilter crisis pre-check (defense in depth, zero latency), session-ID
// management, and display.
public class AIResponseControllerV2 : MonoBehaviour
{
    public TMP_InputField PromptField;
    public TMP_Text ResponseText;

    // Kept as the holder of the scene-assigned AudioSource the character
    // speaks through. Its Google TTS networking and key are gone — reply
    // audio comes back from the proxy.
    public TextToSpeechGoogle textToSpeech;

    [Header("Proxy")]
    [SerializeField] private ProxyClient proxyClient;

    [Header("Safety")]
    [SerializeField] private SafetyFilter safetyFilter;

    // Optional: a pre-rendered recording of SafetyFilter.CrisisMessage so the
    // crisis line is still SPOKEN with zero latency and zero network — there
    // is no on-device TTS key anymore. Generate once (e.g. via the server's
    // TTS) and assign in the Inspector. If empty, the crisis path shows text only.
    [SerializeField] private AudioClip crisisVoiceClip;

    // In-character message after all network retries fail (never "Connection error!").
    private const string NETWORK_ERROR =
        "Forgive me — my thoughts seem to wander just now. Would you ask me once more?";

    // Server-side history is keyed on this; new GUID at start and on reset.
    private string sessionId;

    // Request lock: prevents overlapping or double-fired requests (e.g. tapping Ask
    // twice, or a typed + voice submission landing together). Set true at the start of
    // the converse coroutines and released in their finally on EVERY exit path.
    private bool isProcessing = false;

    // Read-only view of the request lock for scene-transition code (Back to
    // Hub waits for idle before unloading). The lock pattern itself — set in
    // the converse coroutines, released in their finally — is unchanged.
    public bool IsProcessing => isProcessing;

    // Spec 8, item 2: raised true when a request goes out and false when it
    // resolves, on BOTH converse coroutines. Purely a notification riding
    // alongside the request lock — it does not gate anything, so the lock's
    // behaviour is untouched. VoiceRecognitionController listens and owns the
    // actual "Processing..." UI, keeping one source of truth for that indicator
    // across the typed and voice paths. Raised from the finally blocks too, so
    // network failure / empty transcript / any unhandled error all clear it.
    public event System.Action<bool> ProcessingChanged;

    private void RaiseProcessing(bool value)
    {
        ProcessingChanged?.Invoke(value);
    }

    private void Start()
    {
        // Spec 7: adopt the session ID the hub minted at selection time, so
        // every selection (including re-entering the same character) starts a
        // fresh server-side conversation. Direct scene launches (Editor
        // testing) have no selection — mint a local one and warn.
        sessionId = CharacterSession.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = System.Guid.NewGuid().ToString();
            Debug.LogWarning("[AI] No hub session found (scene launched directly?) — using a locally generated session ID.");
        }

        if (proxyClient == null)
            proxyClient = GetComponent<ProxyClient>();
        if (proxyClient == null)
            Debug.LogError("[AI] ProxyClient not assigned on AIResponseControllerV2.");

        if (safetyFilter == null)
            Debug.LogWarning("[AI] SafetyFilter not assigned — crisis routing is disabled.");

        Debug.Log($"--- CONNECTION STARTED: proxy session {sessionId} ---");
    }

    public void OnSubmit()
    {
        // Drop duplicates while a request is in flight.
        if (isProcessing)
        {
            Debug.Log("[AI] Request already in progress, ignoring duplicate.");
            return;
        }

        string prompt = PromptField.text;
        if (string.IsNullOrEmpty(prompt)) return;

        // NOTE: the field is NOT cleared here. Per spec 5.3 it clears only
        // after a successful response, so a failed request keeps the text
        // and the student can retry.
        HandleUserInput(prompt);
    }

    // Runs the client-side crisis check BEFORE anything is sent to the proxy.
    private void HandleUserInput(string text)
    {
        if (safetyFilter != null && safetyFilter.IsCrisis(text))
        {
            // Crisis path: identical UX, zero latency, NO proxy call.
            Debug.LogWarning("[CRISIS] flagged");
            ResponseText.text = SafetyFilter.CrisisMessage;
            PlayCrisisVoice();
            return;
        }

        StartCoroutine(ConverseTextRoutine(text));
    }

    private IEnumerator ConverseTextRoutine(string prompt)
    {
        // Acquire the lock. The finally below guarantees release on EVERY exit
        // path (success, network failure, or any unhandled error), so the
        // controller can never get stuck permanently locked. C# allows yield
        // inside a try that has only a finally (no catch).
        isProcessing = true;
        RaiseProcessing(true);
        try
        {
            ProxyClient.ConverseResponse response = null;
            bool failed = false;

            yield return StartCoroutine(proxyClient.ConverseWithText(
                prompt, sessionId, r => response = r, () => failed = true));

            if (failed || response == null)
            {
                // Final failure: in-character network line; field NOT cleared.
                ResponseText.text = NETWORK_ERROR;
                yield break;
            }

            ShowReply(response);
        }
        finally
        {
            // Release the lock no matter how we exited.
            isProcessing = false;
            RaiseProcessing(false);
        }
    }

    // Voice path entry point — VoiceRecognitionController yields on this after
    // AudioRecorder has saved the WAV. Lives here so the request lock and the
    // session ID stay in one place.
    public IEnumerator ConverseFromVoiceWav(string wavPath)
    {
        // Drop duplicates while a request is in flight (e.g. a voice result
        // arriving mid-request).
        if (isProcessing)
        {
            Debug.Log("[AI] Request already in progress, ignoring duplicate.");
            yield break;
        }

        isProcessing = true;
        RaiseProcessing(true);
        try
        {
            ProxyClient.ConverseResponse response = null;
            bool failed = false;

            yield return StartCoroutine(proxyClient.ConverseWithAudio(
                wavPath, sessionId, r => response = r, () => failed = true));

            if (failed || response == null)
            {
                ResponseText.text = NETWORK_ERROR;
                yield break;
            }

            if (response.emptyTranscript || string.IsNullOrEmpty(response.transcript))
            {
                // Nothing was heard — keep whatever is in the field so the
                // student can retry; the voice controller re-enables buttons.
                Debug.LogWarning("[AI] Empty transcript from proxy (didn't catch that).");
                yield break;
            }

            // Show what was heard, then the reply (server re-checks safety;
            // a crisis spoken aloud comes back as the crisis message + audio).
            PromptField.text = response.transcript;
            ShowReply(response);
        }
        finally
        {
            isProcessing = false;
            RaiseProcessing(false);
        }
    }

    // Spec 5.3 sequence: show replyText, start audio playback, then clear the
    // prompt field. PromptUIController's onValueChanged listener re-greys the
    // Submit button when the field empties — no extra logic here.
    private void ShowReply(ProxyClient.ConverseResponse response)
    {
        ResponseText.text = response.replyText;
        AudioPlaybackUtility.PlayBase64Wav(ResolveAudioSource(), response.audioBase64);
        PromptField.text = "";
    }

    private void PlayCrisisVoice()
    {
        if (crisisVoiceClip == null) return; // text-only until a clip is assigned
        AudioSource source = ResolveAudioSource();
        if (source == null) return;
        source.clip = crisisVoiceClip;
        source.Play();
    }

    private AudioSource ResolveAudioSource()
    {
        return textToSpeech != null ? textToSpeech.audioSource : null;
    }

    public void ResetConversation()
    {
        // Server history lives under the session ID; a fresh GUID starts a
        // clean conversation (the old session expires via its 24h TTL).
        sessionId = System.Guid.NewGuid().ToString();
        ResponseText.text = "Conversation restarted.";
        Debug.Log("Conversation restarted with new session " + sessionId);
    }
}
