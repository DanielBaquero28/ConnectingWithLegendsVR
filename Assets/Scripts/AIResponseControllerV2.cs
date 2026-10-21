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

    // Spec 12 Part P: shown when the server heard nothing in the recording.
    private const string EMPTY_TRANSCRIPT = "I didn't quite catch that — please try again";

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

    // Spec 12 Part M: "Report this answer". Built in code under the response
    // panel (see ReportAnswerButton). Enabled only once a character reply has
    // finished playing; disabled on press (one report per reply) and whenever
    // a new request goes out. Null when the scene has no response panel or no
    // TMP font — everything else works without it.
    private UnityEngine.UI.Button reportButton;
    private Coroutine reportArmRoutine;

    // Spec 12 Part Q: "Back to Hub", built in code next to the report button.
    // `exiting` is one-way: once set, no new request starts and nothing that
    // is still unwinding (a late server response, a network-error line) is
    // shown. `converseRoutine` is the typed-path request coroutine, the only
    // converse coroutine that runs on this component (the voice path's runs on
    // VoiceRecognitionController, which stops it on ExitRequested).
    private const string HUB_SCENE = "HubScene";
    private UnityEngine.UI.Button backButton;
    private bool exiting = false;
    private Coroutine converseRoutine;

    // Raised once when the student presses Back to Hub, before the request
    // lock is released. VoiceRecognitionController stops recording and its
    // own coroutines on it.
    public event System.Action ExitRequested;

    private void RaiseProcessing(bool value)
    {
        if (value) DisarmReportButton();
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

        if (ResponseText != null)
        {
            reportButton = ReportAnswerButton.Build(ResponseText.transform.parent, OnReportAnswer);
            if (reportButton == null)
                Debug.LogWarning("[AI] Report button could not be built (no response panel RectTransform or TMP font).");

            backButton = BackToHubButton.Build(ResponseText.transform.parent, OnBackToHub);
            if (backButton == null)
                Debug.LogWarning("[AI] Back to Hub button could not be built (no response panel RectTransform or TMP font).");
        }

        Debug.Log($"--- CONNECTION STARTED: proxy session {sessionId} ---");
    }

    // Spec 12 Part Q: leave for the hub at once, even mid-request. Order:
    // voice side stops (recording, its coroutines, the WAV), the typed request
    // coroutine stops, the in-flight /converse is aborted (ProxyClient drops
    // its response without calling back), the character stops talking, the
    // lock is released, then the hub loads. Coroutines stopped this way skip
    // their finally blocks, which is why the lock is released here by hand.
    // Fire-and-forget metadata posts (/report, /crisis-report) are left
    // running; they die with the scene as before.
    private void OnBackToHub()
    {
        if (exiting) return;
        exiting = true;
        ReportAnswerButton.SetInteractable(backButton, false);
        Debug.Log("[AI] Back to Hub pressed" + (isProcessing ? " mid-request; cancelling it." : "."));

        ExitRequested?.Invoke();

        if (converseRoutine != null)
        {
            StopCoroutine(converseRoutine);
            converseRoutine = null;
        }
        if (proxyClient != null) proxyClient.AbortConverse();

        DisarmReportButton();
        AudioSource source = ResolveAudioSource();
        if (source != null) source.Stop();

        if (isProcessing)
        {
            isProcessing = false;
            RaiseProcessing(false);
        }

        SceneTransition.Load(HUB_SCENE);
    }

    // Spec 12 Part M: one press -> one metadata-only POST /report, outside the
    // request lock. The button disarms immediately so the same reply cannot
    // be reported twice from the device (the server ignores repeats anyway).
    private void OnReportAnswer()
    {
        DisarmReportButton();
        if (proxyClient == null) return;
        Debug.Log("[AI] Answer reported.");
        StartCoroutine(proxyClient.PostReport(sessionId));
    }

    private void DisarmReportButton()
    {
        if (reportArmRoutine != null)
        {
            StopCoroutine(reportArmRoutine);
            reportArmRoutine = null;
        }
        ReportAnswerButton.SetInteractable(reportButton, false);
    }

    // Arms the button once the reply has been heard: after the AudioSource
    // stops, or straight away when there was no audio to play.
    private IEnumerator ArmReportButtonAfterPlayback(AudioSource source, bool playing)
    {
        if (playing && source != null)
        {
            // One frame so AudioSource.isPlaying reflects the Play() call.
            yield return null;
            while (source != null && source.isPlaying) yield return null;
        }
        reportArmRoutine = null;
        ReportAnswerButton.SetInteractable(reportButton, true);
    }

    public void OnSubmit()
    {
        if (exiting) return; // Spec 12 Part Q: leaving for the hub

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
            // Crisis path: identical UX, zero latency, NO converse call.
            Debug.LogWarning("[CRISIS] flagged");
            DisarmReportButton(); // Spec 12 Part N: the crisis message is not a reply to report
            ResponseText.text = SafetyFilter.CrisisMessage;
            PlayCrisisVoice();

            // Spec 12 Part G: fire-and-forget metadata-only report so the
            // typed path is logged and alerted like a server-detected crisis.
            // Runs outside the isProcessing lock and never blocks the UI; the
            // student's text is not sent.
            if (proxyClient != null)
                StartCoroutine(proxyClient.PostCrisisReport(sessionId));
            return;
        }

        converseRoutine = StartCoroutine(ConverseTextRoutine(text));
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

            if (exiting) yield break; // Spec 12 Part Q: late result after Back to Hub

            if (failed || response == null)
            {
                // Final failure: in-character network line; field NOT cleared.
                ResponseText.text = NETWORK_ERROR;
                PlayFallbackLine(NetworkErrorClip);
                yield break;
            }

            ShowReply(response);
        }
        finally
        {
            // Release the lock no matter how we exited.
            isProcessing = false;
            RaiseProcessing(false);
            converseRoutine = null;
        }
    }

    // Voice path entry point — VoiceRecognitionController yields on this after
    // AudioRecorder has saved the WAV. Lives here so the request lock and the
    // session ID stay in one place.
    public IEnumerator ConverseFromVoiceWav(string wavPath)
    {
        if (exiting) yield break; // Spec 12 Part Q: leaving for the hub

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

            if (exiting) yield break; // Spec 12 Part Q: late result after Back to Hub

            if (failed || response == null)
            {
                ResponseText.text = NETWORK_ERROR;
                PlayFallbackLine(NetworkErrorClip);
                yield break;
            }

            if (response.emptyTranscript || string.IsNullOrEmpty(response.transcript))
            {
                // Nothing was heard — keep whatever is in the field so the
                // student can retry. Spec 12 Part P: say so (text only, no
                // further call); the finally clears "Processing..." and the
                // voice controller re-enables buttons.
                Debug.LogWarning("[AI] Empty transcript from proxy (didn't catch that).");
                ResponseText.text = EMPTY_TRANSCRIPT;
                PlayFallbackLine(DidntCatchClip);
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
        AudioSource source = ResolveAudioSource();
        bool playing = AudioPlaybackUtility.PlayBase64Wav(source, response.audioBase64);
        PromptField.text = "";

        // Spec 12 Part M: the report button arms after this reply has played.
        // Spec 12 Part N: a safety redirect or crisis message is not a
        // character reply, so the button stays disarmed for it.
        if (reportButton != null)
        {
            DisarmReportButton();
            if (!response.safetyRedirect)
                reportArmRoutine = StartCoroutine(ArmReportButtonAfterPlayback(source, playing));
        }
    }

    private void PlayCrisisVoice()
    {
        if (crisisVoiceClip == null) return; // text-only until a clip is assigned
        AudioSource source = ResolveAudioSource();
        if (source == null) return;
        source.clip = crisisVoiceClip;
        source.Play();
    }

    // Spec 12 Part Q: pre-rendered recordings of NETWORK_ERROR and
    // EMPTY_TRANSCRIPT in the character's voice, from the hub selection's
    // CharacterDefinition. A direct Editor launch has no selection, so those
    // lines stay text-only there, as does a character with no clip assigned.
    private static AudioClip NetworkErrorClip
    {
        get { return CharacterSession.Current != null ? CharacterSession.Current.networkErrorClip : null; }
    }

    private static AudioClip DidntCatchClip
    {
        get { return CharacterSession.Current != null ? CharacterSession.Current.didntCatchClip : null; }
    }

    // Plays through the reply AudioSource, so SALSA lip-syncs it like a reply.
    private void PlayFallbackLine(AudioClip clip)
    {
        if (clip == null) return;
        AudioSource source = ResolveAudioSource();
        if (source == null) return;
        source.Stop();
        source.clip = clip;
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
        DisarmReportButton(); // the old session's reply is no longer on screen
        ResponseText.text = "Conversation restarted.";
        Debug.Log("Conversation restarted with new session " + sessionId);
    }
}
