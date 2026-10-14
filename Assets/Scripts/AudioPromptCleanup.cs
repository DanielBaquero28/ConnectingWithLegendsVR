using System;
using System.IO;
using UnityEngine;

// Spec 12 Part E — the recorded prompt WAV must never outlive its upload.
//
// AudioRecorder (protected, unchanged) writes audio_prompt.wav to
// persistentDataPath; ProxyClient deletes it the moment the bytes are in
// memory, and VoiceRecognitionController deletes it again after the turn in
// case the request was dropped before ProxyClient ever read it. This class
// covers the remaining gap: a crash or kill between write and delete leaves a
// file behind, so it is removed once at app start.
//
// Static + [RuntimeInitializeOnLoadMethod]: no scene object, no Inspector
// wiring, runs on Quest and on a direct Editor launch of any scene.
public static class AudioPromptCleanup
{
    public const string FileName = "audio_prompt.wav";

    public static string FilePath
    {
        get { return Path.Combine(Application.persistentDataPath, FileName); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void DeleteLeftoverOnStartup()
    {
        DeleteQuietly(FilePath, "startup");
    }

    // Never throws: a failed delete must not break the turn. Logs the reason
    // only (never the path contents — there is no student text here anyway).
    public static void DeleteQuietly(string path, string reason)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Debug.Log("[Audio] Deleted recorded prompt WAV (" + reason + ").");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Audio] Could not delete recorded prompt WAV (" + reason + "): " + e.GetType().Name);
        }
    }
}
