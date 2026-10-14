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

    // [DIAG] temporary: holds the clip returned by RecordAudio so we can inspect its
    // amplitude after recording stops (to confirm the mic actually captured sound).
    private AudioClip capturedClip;

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
#if UNITY_ANDROID
        // [DIAG] temporary: confirm Android RECORD_AUDIO permission; request it if missing.
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            Debug.Log("[DIAG] Android RECORD_AUDIO permission: GRANTED");
        }
        else
        {
            Debug.LogWarning("[DIAG] Android RECORD_AUDIO permission: NOT granted — requesting now.");
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
        }
#endif

        audioRecorder = gameObject.AddComponent<AudioRecorder>();

        audioRecorder.OnRecordingFinished += OnRecordingAutoStop;

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
            aiController.ProcessingChanged += OnProcessingChanged;

        // [DIAG] temporary: probe each enumerated mic to find which one actually
        // captures audio on this device (Quest's devices[0] is often silent).
        StartCoroutine(ProbeMicrophones());
    }

    // [DIAG] temporary: records a short burst from EVERY microphone device and logs the
    // peak amplitude for each, so we can identify which device carries the headset mic.
    // Self-contained — does not touch AudioRecorder or the normal recording path.
    private IEnumerator ProbeMicrophones()
    {
        // Give the audio system a moment to settle after scene load.
        yield return new WaitForSeconds(0.5f);

        Debug.Log("[DIAG-PROBE] Starting per-device mic probe. " +
                  "KEEP TALKING / making noise continuously for the next ~6 seconds...");

        string[] devices = Microphone.devices;
        if (devices.Length == 0)
        {
            Debug.LogWarning("[DIAG-PROBE] No microphone devices to probe.");
            yield break;
        }

        const int probeFreq = 16000;
        const float probeSeconds = 1.5f;

        foreach (string dev in devices)
        {
            Debug.Log($"[DIAG-PROBE] Testing '{dev}' — speak NOW...");

            AudioClip probe = Microphone.Start(dev, false, 2, probeFreq);
            if (probe == null)
            {
                Debug.LogWarning($"[DIAG-PROBE] '{dev}': Microphone.Start returned null.");
                continue;
            }

            yield return new WaitForSeconds(probeSeconds);

            int pos = Microphone.GetPosition(dev);
            Microphone.End(dev);

            float peak = 0f;
            double sumAbs = 0.0;
            int counted = 0;
            if (pos > 0)
            {
                float[] s = new float[probe.samples * probe.channels];
                probe.GetData(s, 0);
                counted = Mathf.Min(pos, s.Length);
                for (int i = 0; i < counted; i++)
                {
                    float a = Mathf.Abs(s[i]);
                    if (a > peak) peak = a;
                    sumAbs += a;
                }
            }
            float avg = counted > 0 ? (float)(sumAbs / counted) : 0f;

            Debug.Log($"[DIAG-PROBE] '{dev}': position={pos}, peak abs={peak:F5}, avg abs={avg:F5} " +
                      (peak > 0.0005f ? "<-- THIS DEVICE CAPTURES AUDIO" : "(silent / dead)"));
        }

        Debug.Log("[DIAG-PROBE] Probe complete. Use the device marked 'CAPTURES AUDIO'.");
    }

    private void OnDestroy()
    {
        if (audioRecorder != null)
            audioRecorder.OnRecordingFinished -= OnRecordingAutoStop;

        if (RecordButton != null)
            RecordButton.onClick.RemoveListener(StartRecording);

        if (aiController != null)
            aiController.ProcessingChanged -= OnProcessingChanged;
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
        Debug.Log("[Voice] Started recording!");

        // [DIAG] temporary: enumerate microphones and report which device AudioRecorder
        // will hand to Microphone.Start (it uses Microphone.devices[0]).
        string[] mics = Microphone.devices;
        Debug.Log($"[DIAG] Microphone.devices ({mics.Length}): [{string.Join(", ", mics)}]");
        Debug.Log($"[DIAG] Device that will be passed to Microphone.Start: " +
                  (mics.Length > 0 ? mics[0] : "<null - no microphone found>"));

        SetButtonsInteractable(false);
        processingInProgress = false;
        recording = true;

        CountdownText.gameObject.SetActive(true);
        CountdownText.text = COUNTDOWN_PREFIX + audioRecorder.GetMaxDuration();

        AudioClip audioClip = audioRecorder.RecordAudio();
        capturedClip = audioClip; // [DIAG] keep a reference for post-stop amplitude analysis

        if (audioClip == null)
        {
            Debug.LogError("[Voice] Failed to start recording.");
            SetButtonsInteractable(true);
            recording = false;
            HideProcessing();
        }
    }

    private void Update()
    {
        if (!recording) return;

        float elapsed = audioRecorder.GetRecordingDuration();
        float remaining = audioRecorder.GetMaxDuration() - elapsed;
        remaining = Mathf.Max(0f, remaining);

        CountdownText.text = COUNTDOWN_PREFIX + Mathf.CeilToInt(remaining);
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
        ShowProcessing();

        audioRecorder.StopRecording();

        // [DIAG] temporary: inspect the captured clip's amplitude to tell silence
        // (peak near 0) from real audio. Reads the Microphone clip we kept a reference to.
        if (capturedClip != null)
        {
            float[] samples = new float[capturedClip.samples * capturedClip.channels];
            capturedClip.GetData(samples, 0);

            float peak = 0f;
            double sumAbs = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                float a = Mathf.Abs(samples[i]);
                if (a > peak) peak = a;
                sumAbs += a;
            }
            float avg = samples.Length > 0 ? (float)(sumAbs / samples.Length) : 0f;

            Debug.Log($"[DIAG] Captured clip: {capturedClip.samples} samples, " +
                      $"{capturedClip.channels}ch, {capturedClip.frequency}Hz | " +
                      $"peak abs={peak:F5}, avg abs={avg:F5}");

            if (peak < 0.0001f)
                Debug.LogWarning("[DIAG] Captured audio is effectively SILENT (peak ~0) — " +
                                 "the microphone is not capturing sound.");
        }
        else
        {
            Debug.LogWarning("[DIAG] No captured AudioClip reference to analyze.");
        }

        string audioPath = audioRecorder.SaveWavFile(null, AudioPromptCleanup.FilePath);
        Debug.Log($"[Voice] Audio saved to: {audioPath}");

        // [DIAG] temporary: report the saved WAV's byte length on disk.
        try
        {
            long wavBytes = new System.IO.FileInfo(audioPath).Length;
            Debug.Log($"[DIAG] Saved WAV byte length: {wavBytes} bytes");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DIAG] Could not read saved WAV length: " + e.Message);
        }

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

        if (RecordButton != null) RecordButton.interactable = !hasText;
        if (SubmitButton != null) SubmitButton.interactable = hasText;
    }
}