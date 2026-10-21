using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

// Spec 9, item 2 — the one way scenes change in this app.
//
// Replaces the synchronous SceneManager.LoadScene calls that used to sit in
// HubController and BackToHubButton. The order below is the whole point:
//
//   1. fade to black          — the student stops seeing the world BEFORE anything
//                               expensive starts, so no hitch is ever on screen
//   2. LoadSceneAsync         — spread across frames instead of one blocking call,
//                               so the compositor keeps getting frames and the
//                               headset never freezes
//   3. settle frames          — let the new scene's Awake/Start spikes pass
//   4. CharacterWarmup        — pay the character's first-draw cost here (see that
//                               file), while there is still nothing to see
//   5. fade back in           — onto a scene that has already done its expensive
//                               work
//
// Static, with no scene presence and no Inspector wiring: it borrows SceneFader's
// coroutine runner, which is DontDestroyOnLoad and therefore survives the load it
// is driving. Every future character scene gets this transition automatically —
// nothing to add to Build Settings, nothing to remember when a character is added.
public static class SceneTransition
{
    // Out is quicker than in: leaving should feel responsive to the click, while
    // arriving benefits from a gentler reveal (and buys the new scene a few more
    // frames to settle before it is actually looked at).
    private const float FADE_OUT_SECONDS = 0.35f;
    private const float FADE_IN_SECONDS = 0.5f;

    private const string STATUS_TEXT = "Entering...";

    // Frames between "scene is loaded" and "warm it up". Awake/Start across the
    // whole scene land in the first of these; warming up during that spike would
    // just pile onto it.
    private const int SETTLE_FRAMES = 2;

    // Read by CharacterWarmup to tell "entered through the hub" (warmup is driven
    // from here, inside the fade) from "opened directly in the Editor" (no fade to
    // hide behind, so it warms itself up).
    public static bool IsTransitioning { get; private set; }

    public static void Load(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("[Transition] Load called with no scene name.");
            return;
        }

        // A scene load is irreversible, so a second request mid-transition can only
        // do harm. Callers latch too (HubController.loadingScene,
        // AIResponseControllerV2.exiting); this is the backstop that makes the guarantee
        // hold no matter who calls.
        if (IsTransitioning)
        {
            Debug.LogWarning($"[Transition] Already loading — ignoring request for '{sceneName}'.");
            return;
        }

        IsTransitioning = true;
        SceneFader.Instance.StartCoroutine(LoadRoutine(sceneName));
    }

    private static IEnumerator LoadRoutine(string sceneName)
    {
        SceneFader fader = SceneFader.Instance;

        // try/finally so a fault anywhere below cannot leave IsTransitioning stuck
        // on — which would silently block every future scene change for the rest of
        // the session, including the way back to the hub.
        try
        {
            yield return fader.FadeTo(1f, FADE_OUT_SECONDS);
            fader.SetStatus(STATUS_TEXT);

            AsyncOperation load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (load == null)
            {
                // Almost always a scene missing from Build Settings. Bail while still
                // black, then fade back onto the scene we never left.
                Debug.LogError($"[Transition] Could not start loading '{sceneName}' — " +
                               "is it added to Build Settings?");
            }
            else
            {
                while (!load.isDone)
                    yield return null;

                for (int i = 0; i < SETTLE_FRAMES; i++)
                    yield return null;

                // Only ever finds the new scene's warmup: the old scene is gone by
                // now, single-mode. Absent (hub, or a scene with no heavy character)
                // is a normal case, not an error.
                CharacterWarmup warmup = Object.FindObjectOfType<CharacterWarmup>();
                if (warmup != null)
                    yield return warmup.WarmUpRoutine();
            }

            fader.SetStatus(null);
            yield return fader.FadeTo(0f, FADE_IN_SECONDS);
        }
        finally
        {
            IsTransitioning = false;
        }
    }
}
