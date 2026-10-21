using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// Spec 12 Part P: Android microphone permission. The hub asks for it once per
// hub load; conversation scenes only read the OS state (IsGranted), so there
// is no shared flag to go stale. In the Editor and on non-Android platforms
// the microphone always counts as available.
public static class MicrophonePermission
{
    public static bool IsGranted
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
            return true;
#endif
        }
    }

    // No-op when already granted. Once the student picks "don't ask again",
    // Android answers denied straight away without showing a dialog.
    public static void RequestIfNeeded()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (IsGranted) return;

        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => Debug.Log("[Mic] Microphone permission granted.");
        callbacks.PermissionDenied += _ => Debug.LogWarning("[Mic] Microphone permission denied; typing still works.");
        callbacks.PermissionDeniedAndDontAskAgain += _ =>
            Debug.LogWarning("[Mic] Microphone permission denied (don't ask again); typing still works.");
        Permission.RequestUserPermission(Permission.Microphone, callbacks);
#endif
    }
}
