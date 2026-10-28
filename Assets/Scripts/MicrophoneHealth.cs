using System.Collections;
using UnityEngine;

// Microphone reliability (2026-10-28). While the Quest is cabled to a PC
// running the Link software, the Link service owns the headset microphone and
// a standalone app records exact zeros for the whole clip. That is a DEAD
// microphone, which is different from a quiet student (small but non-zero
// levels): a dead clip must never be uploaded.
//
// This component (added at runtime next to AudioRecorder; it has no
// Inspector fields) does three things:
//   - classifies a finished clip (Classify) from AudioRecorder's whole-clip
//     measurements;
//   - probes the microphone: Microphone.End, then a 0.5 s Microphone.Start,
//     peak over what arrived, End. Used once to recover after a dead clip, and
//     again whenever the app regains focus / resumes or the permission is
//     granted, never while a recording is running;
//   - reads what Android exposes about the mic (AudioManager.isMicrophoneMute)
//     and the cable (BatteryManager plugged == USB, i.e. a PC rather than a
//     charger; Link itself is not visible to apps) and writes ONE logcat line
//     per event with numbers and states only — no audio, no text.
public class MicrophoneHealth : MonoBehaviour
{
    public enum ClipOutcome { Ok, Quiet, Dead }

    public const float PROBE_SECONDS = 0.5f;
    private const int PROBE_FREQUENCY = 16000;

    // Student-facing lines (shown as text in the response panel).
    public const string DEAD_MESSAGE =
        "I can’t hear the microphone. If the headset is connected to a computer, unplug it and close Quest Link. " +
        "Then check that the microphone isn’t muted in Quick Settings (tap the clock), and try again. " +
        "If it continues, restart the headset.";
    public const string RECOVERED_MESSAGE =
        "The microphone just woke up — please press Record and ask again.";

    private int recoveryAttempts = 0;
    private bool probing = false;
    public bool IsProbing => probing;

    // True after the most recent clip or probe found exact zeros; false once
    // any signal is seen. Lets the focus/permission probe clear the message.
    public bool LastKnownDead { get; private set; }

    // A dead clip: no samples at all, or a non-zero sample count whose peak is
    // exactly 0. Quiet: signal below the recorder's silence threshold.
    public static ClipOutcome Classify(int sampleCount, float peak, float silenceThreshold)
    {
        if (sampleCount <= 0 || peak <= 0f) return ClipOutcome.Dead;
        return peak < silenceThreshold ? ClipOutcome.Quiet : ClipOutcome.Ok;
    }

    public void LogClip(AudioRecorder recorder, ClipOutcome outcome)
    {
        if (outcome == ClipOutcome.Dead) LastKnownDead = true;
        else LastKnownDead = false;
        Log("clip", recorder.CurrentDevice, recorder.LastClipSampleCount, recorder.LastClipPeak,
            recorder.LastClipAverage, outcome.ToString().ToLowerInvariant());
    }

    // Restarts the microphone and listens for PROBE_SECONDS. `onDone(true)`
    // = signal seen. `reason` is the event name in the log line.
    public void Probe(string device, string reason, System.Action<bool> onDone)
    {
        if (probing) { onDone?.Invoke(!LastKnownDead); return; }
        StartCoroutine(ProbeRoutine(device, reason, onDone));
    }

    private IEnumerator ProbeRoutine(string device, string reason, System.Action<bool> onDone)
    {
        probing = true;
        if (reason == "recover") recoveryAttempts++;
        bool alive = false;
        int samples = 0;
        float peak = 0f, avg = 0f;
        try
        {
            if (string.IsNullOrEmpty(device))
                device = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
            if (device != null)
            {
                if (Microphone.IsRecording(device)) Microphone.End(device);
                AudioClip clip = Microphone.Start(device, false, 1, PROBE_FREQUENCY);
                yield return new WaitForSeconds(PROBE_SECONDS);
                int position = Microphone.GetPosition(device);
                Microphone.End(device);
                if (clip != null && position > 0)
                {
                    var data = new float[position * clip.channels];
                    clip.GetData(data, 0);
                    samples = data.Length;
                    float sum = 0f;
                    for (int i = 0; i < data.Length; i++)
                    {
                        float a = Mathf.Abs(data[i]);
                        sum += a;
                        if (a > peak) peak = a;
                    }
                    avg = sum / data.Length;
                }
                if (clip != null) Destroy(clip);
            }
            alive = samples > 0 && peak > 0f;
            LastKnownDead = !alive;
            Log(reason, device, samples, peak, avg, alive ? "ok" : "dead");
        }
        finally
        {
            probing = false;
        }
        onDone?.Invoke(alive);
    }

    // One line, numbers and states only.
    private void Log(string evt, string device, int samples, float peak, float avg, string outcome)
    {
        Debug.Log($"[MicDiag] event={evt} device=\"{device}\" samples={samples} peak={peak:F5} avg={avg:F5} " +
                  $"zeros={(samples > 0 && peak <= 0f).ToString().ToLowerInvariant()} " +
                  $"permission={(MicrophonePermission.IsGranted ? "granted" : "denied")} " +
                  $"mute={SystemMicMuteState()} usb={UsbToPcState()} recovery={recoveryAttempts} outcome={outcome}");
    }

    // ----- Android system state (strings so "unknown" is a value too) -----

    // AudioManager.isMicrophoneMute(): no permission needed. Whether the Quest
    // Quick Settings mute toggle is reflected here is only verifiable on the
    // device — hence it is logged, not acted on.
    public static string SystemMicMuteState()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var audio = activity.Call<AndroidJavaObject>("getSystemService", "audio"))
                return audio.Call<bool>("isMicrophoneMute") ? "true" : "false";
        }
        catch (System.Exception) { return "unknown"; }
#else
        return "n/a";
#endif
    }

    // BatteryManager sticky intent: EXTRA_PLUGGED 2 = USB (a computer), 1 = AC
    // charger, 4 = wireless, 0 = unplugged. No permission needed.
    public static string UsbToPcState()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED"))
            using (var intent = activity.Call<AndroidJavaObject>("registerReceiver", null, filter))
            {
                if (intent == null) return "unknown";
                int plugged = intent.Call<int>("getIntExtra", "plugged", -1);
                return plugged == 2 ? "true" : plugged < 0 ? "unknown" : "false";
            }
        }
        catch (System.Exception) { return "unknown"; }
#else
        return "n/a";
#endif
    }
}
