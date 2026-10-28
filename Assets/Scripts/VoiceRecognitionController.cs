using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class VoiceRecognitionController : MonoBehaviour
{
    public TMP_InputField OutputField;
    private AudioRecorder audioRecorder;

    public TMP_Text CountdownText;

    private bool recording = false;
    private bool processingInProgress = false;

    // True while recording or a request is processing. The idle input-mode rule
    // (ApplyInputModeButtons) must not re-enable Record/Submit during this window,
    // so the existing lock always wins. Set from SetButtonsInteractable.
    private bool buttonsLocked = false;

    // Spec 12 Part Q: set once on Back to Hub; Record does nothing after it.
    private bool exiting = false;

    // Spec 12 Part P: false while Android's microphone permission is denied.
    // Re-read at Start and whenever the app regains focus (the student may have
    // just enabled it in Settings). Always true in the Editor.
    private bool micAvailable = true;
    private TMP_Text recordLabel;
    private string recordLabelDefault;
    private const string MIC_OFF_TEXT = "Microphone is off — enable it in Settings > Privacy";

    // Mic reliability (2026-10-28): dead-mic detection, recovery probe and the
    // live level meter under Record. See MicrophoneHealth.
    private MicrophoneHealth micHealth;
    private RectTransform levelFill;
    private GameObject levelBar;
    private const float LEVEL_FULL_SCALE_PEAK = 0.1f; // a normal speaking peak fills the bar

    [Header("Microphone (Editor only)")]
    [Tooltip("Editor only: exact name from Microphone.devices. Empty, or not connected, " +
             "uses the default device. Ignored in device builds.")]
    [SerializeField] private string preferredMicDeviceEditor = "";

    [Header("Buttons Information")]
    [SerializeField] public Button RecordButton;
    [SerializeField] public Button SubmitButton;
    [SerializeField] public Button NewQuestionButton;

    public AIResponseControllerV2 aiController;

    // Spec 8, item 1: the recording countdown reads as a prompt, not a bare
    // number. Single string, same CountdownText object.
    private const string COUNTDOWN_PREFIX = "Ask your question... ";

    // Spec 8, item 2: shown while a request is in flight, on BOTH the voice and
    // the typed path.
    private const string PROCESSING_TEXT = "Processing...";

    private void Start()
    {
        audioRecorder = gameObject.AddComponent<AudioRecorder>();
#if UNITY_EDITOR
        // Spec 12 Part P: AudioRecorder falls back to Microphone.devices[0] when
        // this is empty or not connected. Device builds never set it.
        audioRecorder.PreferredDevice = preferredMicDeviceEditor;
#endif

        audioRecorder.OnRecordingFinished += OnRecordingAutoStop;
        micHealth = gameObject.AddComponent<MicrophoneHealth>();
        MicrophonePermission.Granted += OnMicPermissionGranted;
        BuildLevelBar();

        // Record button -> StartRecording is wired here, off the RecordButton this
        // controller already owns. It used to live in a separate TestUIController
        // with its own copy of the button reference, which silently went stale when
        // the UI was rebuilt. Don't also add StartRecording to the button's
        // On Click () list — the microphone would start twice per click.
        if (RecordButton != null)
            RecordButton.onClick.AddListener(StartRecording);
        else
            Debug.LogWarning("[Voice] RecordButton not assigned; the Record button can't start recording.");

        // Spec 8, item 2: the typed path (Submit button / Enter) needs the same
        // "Processing..." feedback the voice path already had. Rather than let a
        // second component write to CountdownText, this controller stays the ONE
        // owner of that object and just listens to the AI controller's
        // in-flight signal — which is raised around the request lock on BOTH
        // converse coroutines, so every exit path (success, network failure,
        // empty transcript) hides it exactly once. Crisis and safety redirects
        // never start a request, so they never show it in the first place.
        if (aiController != null)
        {
            aiController.ProcessingChanged += OnProcessingChanged;
            aiController.ExitRequested += OnExitRequested;
        }

        // Spec 12 Part P: the hub asked for the permission; here we only read it.
        if (RecordButton != null)
        {
            recordLabel = RecordButton.GetComponentInChildren<TMP_Text>(true);
            if (recordLabel != null) recordLabelDefault = recordLabel.text;
            else Debug.LogWarning("[Voice] RecordButton has no TMP label; the microphone-off notice can't be shown.");
        }
        RefreshMicAvailability();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // Returning from the permission dialog or from Settings.
        if (hasFocus && audioRecorder != null)
        {
            RefreshMicAvailability();
            ProbeMicIfIdle("focus"); // Link may have taken or released the mic meanwhile
        }
    }

    private void OnApplicationPause(bool paused)
    {
        // Headset taken off and put back on: resume comes through here on Quest.
        if (!paused && audioRecorder != null) ProbeMicIfIdle("resume");
    }

    private void OnMicPermissionGranted()
    {
        RefreshMicAvailability();
        ProbeMicIfIdle("permission");
    }

    // 0.5 s microphone probe, only when nothing is recording or in flight. A
    // dead probe shows the dead-mic message; a live one clears that message
    // if it is the one on the panel.
    private void ProbeMicIfIdle(string reason)
    {
        if (exiting || recording || processingInProgress || !micAvailable || micHealth == null) return;
        micHealth.Probe(audioRecorder.CurrentDevice, reason, alive =>
        {
            if (!alive) ShowPanelMessage(MicrophoneHealth.DEAD_MESSAGE);
            else if (PanelShows(MicrophoneHealth.DEAD_MESSAGE)) ShowPanelMessage("");
        });
    }

    private void ShowPanelMessage(string text)
    {
        if (aiController != null && aiController.ResponseText != null) aiController.ResponseText.text = text;
    }

    private bool PanelShows(string text)
    {
        return aiController != null && aiController.ResponseText != null && aiController.ResponseText.text == text;
    }

    // Thin bar along the bottom of the Record button, filled from the live
    // peak while recording, hidden otherwise. Built in code (plain values, no
    // Inspector references) so every scene gets it.
    private void BuildLevelBar()
    {
        if (RecordButton == null) return;
        levelBar = new GameObject("LevelBar", typeof(RectTransform), typeof(Image));
        var bar = (RectTransform)levelBar.transform;
        bar.SetParent(RecordButton.transform, false);
        bar.anchorMin = new Vector2(0.08f, 0f);
        bar.anchorMax = new Vector2(0.92f, 0f);
        bar.pivot = new Vector2(0.5f, 0f);
        bar.anchoredPosition = new Vector2(0f, 3f);
        bar.sizeDelta = new Vector2(0f, 4f);
        var back = levelBar.GetComponent<Image>();
        back.color = new Color(0f, 0f, 0f, 0.35f);
        back.raycastTarget = false;

        var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        levelFill = (RectTransform)fillGo.transform;
        levelFill.SetParent(bar, false);
        levelFill.anchorMin = Vector2.zero;
        levelFill.anchorMax = new Vector2(0f, 1f);
        levelFill.offsetMin = levelFill.offsetMax = Vector2.zero;
        var fill = fillGo.GetComponent<Image>();
        fill.color = new Color(0.35f, 0.85f, 0.45f, 1f);
        fill.raycastTarget = false;
        levelBar.SetActive(false);
    }

    private void SetLevel(float peak)
    {
        if (levelFill == null) return;
        float level = Mathf.Clamp01(peak / LEVEL_FULL_SCALE_PEAK);
        levelFill.anchorMax = new Vector2(level, 1f);
    }

    // Spec 12 Part P: denied -> Record shows the notice and stays disabled;
    // typing and Submit are unaffected. Granted -> original label back.
    private void RefreshMicAvailability()
    {
        micAvailable = MicrophonePermission.IsGranted;
        if (recordLabel != null)
            recordLabel.text = micAvailable ? recordLabelDefault : MIC_OFF_TEXT;
        if (!micAvailable && RecordButton != null)
            RecordButton.interactable = false;
        ApplyInputModeButtons(); // no-op while locked; the lock re-applies on release
    }

    private void OnDestroy()
    {
        if (audioRecorder != null)
            audioRecorder.OnRecordingFinished -= OnRecordingAutoStop;
        MicrophonePermission.Granted -= OnMicPermissionGranted;

        if (RecordButton != null)
            RecordButton.onClick.RemoveListener(StartRecording);

        if (aiController != null)
        {
            aiController.ProcessingChanged -= OnProcessingChanged;
            aiController.ExitRequested -= OnExitRequested;
        }
    }

    // Spec 12 Part Q: Back to Hub. Stops the voice side for good: the
    // microphone, ProcessRecordingNow and the voice-path converse coroutine it
    // waits on (both run on this component). A stopped coroutine skips its
    // finally, so the recorded WAV is deleted here instead. The AI controller
    // releases the request lock right after this returns.
    private void OnExitRequested()
    {
        exiting = true;
        StopAllCoroutines();
        if (recording && audioRecorder != null) audioRecorder.StopRecording();
        recording = false;
        processingInProgress = false;
        AudioPromptCleanup.DeleteQuietly(AudioPromptCleanup.FilePath, "left for hub");
        SetButtonsInteractable(false);
        if (CountdownText != null) CountdownText.gameObject.SetActive(false);
    }

    // ---- Spec 8, item 2: the single source of truth for the processing indicator ----
    // Both the voice path (below) and the typed path (via ProcessingChanged) go
    // through these two methods; nothing else shows or hides CountdownText for
    // processing. HideProcessing is safe to call when nothing is showing.

    private void OnProcessingChanged(bool processing)
    {
        if (processing) ShowProcessing();
        else HideProcessing();
    }

    public void ShowProcessing()
    {
        if (CountdownText == null) return;
        CountdownText.gameObject.SetActive(true);
        CountdownText.text = PROCESSING_TEXT;
    }

    public void HideProcessing()
    {
        // Don't yank the label out from under a live countdown: StartRecording
        // owns it while recording is true.
        if (CountdownText == null || recording) return;
        CountdownText.gameObject.SetActive(false);
    }

    public void StartRecording()
    {
        if (exiting) return; // Spec 12 Part Q: leaving for the hub

        if (!micAvailable)
        {
            Debug.LogWarning("[Voice] Record ignored: microphone permission is off.");
            return;
        }

        Debug.Log("[Voice] Started recording!");

        SetButtonsInteractable(false);
        processingInProgress = false;
        recording = true;

        CountdownText.gameObject.SetActive(true);
        CountdownText.text = COUNTDOWN_PREFIX + audioRecorder.GetMaxDuration();

        AudioClip audioClip = audioRecorder.RecordAudio();

        if (audioClip == null)
        {
            Debug.LogError("[Voice] Failed to start recording.");
            SetButtonsInteractable(true);
            recording = false;
            HideProcessing();
            return;
        }
        if (levelBar != null) { SetLevel(0f); levelBar.SetActive(true); }
    }

    private void Update()
    {
        if (!recording) return;

        float elapsed = audioRecorder.GetRecordingDuration();
        float remaining = audioRecorder.GetMaxDuration() - elapsed;
        remaining = Mathf.Max(0f, remaining);

        CountdownText.text = COUNTDOWN_PREFIX + Mathf.CeilToInt(remaining);
        SetLevel(audioRecorder.LastPeak); // measured by the recorder's own silence check
    }

    private void OnRecordingAutoStop()
    {
        if (!recording || processingInProgress) return;
        Debug.Log("[Voice] Auto-stop triggered. Starting processing.");
        StartCoroutine(ProcessRecordingNow());
    }

    private IEnumerator ProcessRecordingNow()
    {
        if (processingInProgress) yield break;
        processingInProgress = true;

        recording = false;
        if (levelBar != null) levelBar.SetActive(false);
        ShowProcessing();

        audioRecorder.StopRecording();

        // Dead microphone (exact zeros, e.g. Link owns it): never upload.
        // Restart + probe once; tell the student what happened either way.
        var outcome = MicrophoneHealth.Classify(audioRecorder.LastClipSampleCount,
            audioRecorder.LastClipPeak, audioRecorder.SilenceThreshold);
        micHealth.LogClip(audioRecorder, outcome);
        if (outcome == MicrophoneHealth.ClipOutcome.Dead)
        {
            bool done = false, alive = false;
            micHealth.Probe(audioRecorder.CurrentDevice, "recover", a => { alive = a; done = true; });
            yield return new WaitUntil(() => done);
            ShowPanelMessage(alive ? MicrophoneHealth.RECOVERED_MESSAGE : MicrophoneHealth.DEAD_MESSAGE);
            processingInProgress = false;
            HideProcessing();
            SetButtonsInteractable(true);
            yield break;
        }

        string audioPath = audioRecorder.SaveWavFile(null, AudioPromptCleanup.FilePath);
        Debug.Log($"[Voice] Audio saved to: {audioPath}");

        // One round trip to the proxy: STT + reply + voice come back together.
        // The AI controller owns the request lock, the session ID, and all
        // display (transcript into the field, reply text, audio, field clear).
        try
        {
            if (aiController != null)
            {
                yield return StartCoroutine(aiController.ConverseFromVoiceWav(audioPath));
            }
            else
            {
                Debug.LogWarning("[Voice] aiController not assigned; cannot send recording.");
            }
        }
        finally
        {
            // Spec 12 Part E, belt and braces: ProxyClient already deletes the
            // WAV right after reading it, but a turn dropped by the request lock
            // (or a missing controller) never reaches ProxyClient at all.
            AudioPromptCleanup.DeleteQuietly(audioPath, "after turn");
        }

        HideProcessing();
        SetButtonsInteractable(true);
    }

    public void SetButtonsInteractable(bool state)
    {
        // Track the lock so the idle input-mode rule below can't re-enable
        // Record/Submit while recording or processing.
        buttonsLocked = !state;

        NewQuestionButton.interactable = state;

        if (state)
        {
            // Idle: hand Record/Submit to the single source of truth so the
            // one-mode-at-a-time rule is applied on the voice re-enable path too.
            ApplyInputModeButtons();
        }
        else
        {
            RecordButton.interactable = false;
            SubmitButton.interactable = false;
        }
    }

    // Single source of truth for the idle input-mode rule (Spec 5, Fix 3):
    //   field empty    -> not typing: enable Record, disable Submit
    //   field has text -> typing:     disable Record, enable Submit
    //   mic permission off (Spec 12 Part P) -> Record stays disabled either way
    // No-ops while locked (recording / processing) so the existing lock always
    // wins. Called from SetButtonsInteractable(true) AND from PromptUIController's
    // onValueChanged handler, so the voice re-enable path and live typing share
    // one rule (no duplicated empty/non-empty logic). Reads the same field
    // OnSubmit does (AiResponseController.PromptField) so Submit is interactable
    // exactly when a submit would actually fire.
    public void ApplyInputModeButtons()
    {
        if (buttonsLocked) return;

        TMP_InputField promptField = aiController != null ? aiController.PromptField : OutputField;
        bool hasText = promptField != null && !string.IsNullOrEmpty(promptField.text);

        if (RecordButton != null) RecordButton.interactable = micAvailable && !hasText;
        if (SubmitButton != null) SubmitButton.interactable = hasText;
    }
}