using System.Collections;
using UnityEngine;
using System.IO;

public class AudioRecorder : MonoBehaviour
{
    private AudioClip recordedClip;
    private string microphoneDevice;

    // Optional device name. Empty (default) or not in Microphone.devices:
    // RecordAudio() uses Microphone.devices[0] exactly as before.
    public string PreferredDevice { get; set; } = "";

    private int frequency = 16000;
    private int maxDuration = 12;

    // ===== FIX: Lowered threshold significantly =====
    // Quest 3 microphone at 16kHz produces very low RMS values.
    // 0.01 was too high — speech was being treated as silence.
    // 0.002 catches actual speech while still detecting real silence.
    // If it still triggers too early, lower this further (0.001).
    // If it never triggers, raise it slightly (0.003).
    // Watch the [RMS] debug logs to calibrate.
    private float silenceThreshold = 0.002f;
    private float silenceDuration = 4.0f;
    private float minRecordingTime = 1.0f;

    private bool isRecording = false;
    private float silenceTimer = 0f;
    private float recordingTimer = 0f;

    // ===== FIX: Prevents OnRecordingFinished from firing multiple times =====
    private bool eventFired = false;

    public System.Action OnRecordingFinished;

    // Mic reliability (2026-10-28), read-only additions for MicrophoneHealth
    // and the live level meter. Nothing below changes how recording behaves.
    // Last ~100ms window measured by GetCurrentRMS (updated while recording).
    public float LastRms { get; private set; }
    public float LastPeak { get; private set; }
    // Whole clip, measured in StopRecording from the samples it already copies.
    public float LastClipPeak { get; private set; }
    public float LastClipAverage { get; private set; }
    public int LastClipSampleCount { get; private set; }
    public string CurrentDevice => microphoneDevice;
    // Read-only view of the (unchanged) silence threshold, so MicrophoneHealth
    // can tell a quiet student (below it, non-zero) from a dead mic (zeros).
    public float SilenceThreshold => silenceThreshold;

    public AudioClip RecordAudio()
    {
        microphoneDevice = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
        if (!string.IsNullOrEmpty(PreferredDevice) && System.Array.IndexOf(Microphone.devices, PreferredDevice) >= 0)
            microphoneDevice = PreferredDevice;

        if (microphoneDevice == null)
        {
            Debug.LogError("No microphone found on device.");
            return null;
        }

        recordedClip = Microphone.Start(microphoneDevice, false, maxDuration, frequency);
        isRecording = true;
        eventFired = false;
        silenceTimer = 0f;
        recordingTimer = 0f;

        return recordedClip;
    }

    private void Update()
    {
        if (!isRecording || eventFired || recordedClip == null) return;

        recordingTimer += Time.deltaTime;

        if (recordingTimer < minRecordingTime) return;

        // Hard cap: stop at 12 seconds
        if (recordingTimer >= maxDuration)
        {
            Debug.Log("[AudioRecorder] Max duration (12s) reached. Stopping.");
            FireFinishedEvent();
            return;
        }

        // Silence detection
        float currentRMS = GetCurrentRMS();

        // ===== Debug log: shows actual RMS values for calibration =====
        // Watch these in the console. Speech should show values above the threshold,
        // silence should show values below. If speech shows below threshold,
        // lower silenceThreshold further.
        if (recordingTimer % 0.5f < Time.deltaTime) // Log every ~0.5 seconds
        {
            Debug.Log($"[RMS] {currentRMS:F5} | Threshold: {silenceThreshold} | Silence timer: {silenceTimer:F1}s");
        }

        if (currentRMS < silenceThreshold)
        {
            silenceTimer += Time.deltaTime;

            if (silenceTimer >= silenceDuration)
            {
                Debug.Log($"[AudioRecorder] 4s silence after {recordingTimer:F1}s. Stopping.");
                FireFinishedEvent();
            }
        }
        else
        {
            // Speech detected — reset silence timer
            silenceTimer = 0f;
        }
    }

    // ===== FIX: Centralized event firing with guard =====
    // Prevents Update() from firing the event on multiple frames
    // before StopRecording() gets called by the coroutine.
    private void FireFinishedEvent()
    {
        if (eventFired) return;
        eventFired = true;
        isRecording = false;
        OnRecordingFinished?.Invoke();
    }

    private float GetCurrentRMS()
    {
        if (!Microphone.IsRecording(microphoneDevice)) return 0f;

        int position = Microphone.GetPosition(microphoneDevice);
        if (position <= 0) return 0f;

        // Sample the last ~100ms of audio
        int sampleWindow = Mathf.Min(frequency / 10, position);
        float[] samples = new float[sampleWindow];

        int startSample = Mathf.Max(0, position - sampleWindow);
        recordedClip.GetData(samples, startSample);

        float sum = 0f;
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
            peak = Mathf.Max(peak, Mathf.Abs(samples[i])); // for the level meter / dead-mic check
        }

        LastRms = Mathf.Sqrt(sum / samples.Length);
        LastPeak = peak;
        return LastRms;
    }

    public void StopRecording()
    {
        isRecording = false;

        if (Microphone.IsRecording(microphoneDevice))
        {
            int position = Microphone.GetPosition(microphoneDevice);
            Microphone.End(microphoneDevice);

            if (position > 0)
            {
                float[] samples = new float[position * recordedClip.channels];
                recordedClip.GetData(samples, 0);
                MeasureClip(samples);

                AudioClip trimmedClip = AudioClip.Create(
                    "trimmed_recording",
                    position,
                    recordedClip.channels,
                    frequency,
                    false
                );

                trimmedClip.SetData(samples, 0);
                recordedClip = trimmedClip;

                float durationSeconds = (float)position / frequency;
                Debug.Log($"[AudioRecorder] Trimmed to {durationSeconds:F1}s ({position} samples at {frequency}Hz)");
            }
            else
            {
                MeasureClip(null);
                Debug.LogWarning("[AudioRecorder] Microphone position was 0 — recording may be empty.");
            }
        }
        else
        {
            Debug.Log("[AudioRecorder] Microphone already stopped (hit maxDuration naturally).");
        }
    }

    // Whole-clip peak / mean absolute level over the samples StopRecording
    // already copied. Exact zeros across the clip = a dead microphone (Link
    // owns it), as opposed to a quiet student (small but non-zero).
    private void MeasureClip(float[] samples)
    {
        float peak = 0f, sum = 0f;
        int n = samples != null ? samples.Length : 0;
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Abs(samples[i]);
            sum += a;
            if (a > peak) peak = a;
        }
        LastClipPeak = peak;
        LastClipAverage = n > 0 ? sum / n : 0f;
        LastClipSampleCount = n;
    }

    public string SaveWavFile(AudioClip audioClip, string filePath)
    {
        byte[] wavFile = WavUtility.FromAudioClip(recordedClip);
        File.WriteAllBytes(filePath, wavFile);

        float fileSizeKB = wavFile.Length / 1024f;
        Debug.Log($"[AudioRecorder] WAV saved: {fileSizeKB:F0}KB at {filePath}");

        return filePath;
    }

    public bool IsCurrentlyRecording()
    {
        return isRecording;
    }

    public float GetRecordingDuration()
    {
        return recordingTimer;
    }

    public int GetMaxDuration()
    {
        return maxDuration;
    }
}