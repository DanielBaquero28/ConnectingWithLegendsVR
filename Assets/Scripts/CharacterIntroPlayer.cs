using System.Collections;
using TMPro;
using UnityEngine;

// Spec 8, item 4 — plays a character's pre-rendered self-introduction once, when
// the student clicks "I'm facing the board!".
//
// The intro is a PRE-RENDERED WAV, not runtime TTS: it costs nothing per session,
// has zero latency, and its wording is fixed and reviewable. Render it with
//   node server/scripts/render-speech-wav.js server/scripts/seed-<char>.json intro-<char>.wav @server/scripts/intro-<char>.txt
// so the intro uses the exact same voice block the proxy uses for that character's
// replies — no audible seam between the introduction and the conversation.
//
// It deliberately plays through the EXISTING TextToSpeechController AudioSource
// (the one reply audio uses). That single choice is what makes the character come
// alive during the intro for free: SALSA lip-syncs from that AudioSource, and
// CharacterAnimationDriver's whole "is speaking" signal is that same source's
// isPlaying. Assigning a different AudioSource would leave him talking with a
// still face — always point speechSource at the TTS one.
//
// ----- Wiring (per character scene) -----
//   1. Put this component on a scene object (the AI controller object is fine).
//   2. Assign introClip, speechSource (TextToSpeechController's AudioSource),
//      voiceController, and optionally captionText + introCaption. Tune
//      introDelaySeconds (Spec 9, item 1) to that scene's entrance.
//   3. On the "I'm facing the board!" Button, add a 5th On Click () entry:
//      target = this component, function = CharacterIntroPlayer.PlayIntro().
//
// ----- COOKBOOK NOTE for future character scenes -----
// New character scenes are made by duplicating an existing one, and a duplicated
// scene keeps the ORIGINAL character's object references. The board button's
// On Click () list is Inspector-wired UnityEvents (no script owns it), so nothing
// in code catches this: in MLKScene the third On Click () entry still called
// SetActive(true) on the leftover Albert_Einstein instance, which is why Einstein
// appeared in MLK's scene. Any "new character scene" checklist MUST include
// "reassign every character reference on the board button's On Click () list, and
// delete the previous character's instance from the scene" — see Spec 8, item 6.
[DisallowMultipleComponent]
public class CharacterIntroPlayer : MonoBehaviour
{
    [Header("Intro audio")]
    [Tooltip("Pre-rendered introduction WAV for THIS character. If left empty the " +
             "intro is skipped silently and the student can talk immediately.")]
    [SerializeField] private AudioClip introClip;

    [Tooltip("The TextToSpeechController AudioSource the replies play through — the " +
             "SAME one SALSA analyses and CharacterAnimationDriver watches, so lip-sync " +
             "and gestures react to the intro with no extra wiring.")]
    [SerializeField] private AudioSource speechSource;

    [Header("Caption (optional)")]
    [Tooltip("The existing ResponseText / caption field. Leave empty for audio only.")]
    [SerializeField] private TMP_Text captionText;

    [Tooltip("Text shown while the intro plays. Should match the rendered WAV's wording.")]
    [TextArea(2, 5)]
    [SerializeField] private string introCaption;

    [Header("Input lock")]
    [Tooltip("Used to grey Record/Submit while the character introduces himself, so the " +
             "student doesn't talk over him. Re-enabled when the clip ends.")]
    [SerializeField] private VoiceRecognitionController voiceController;

    [Header("Timing")]
    [Tooltip("Seconds between the board click and the first word of the introduction. " +
             "The character is activated by the same click, so without this beat he " +
             "starts talking while he is still popping in — the voice arrives before " +
             "there is anyone visibly there to own it. Input stays locked for the whole " +
             "wait, so the student can't talk over the entrance either. Tune per scene: " +
             "a character that fades in slowly wants a longer beat than one already " +
             "settled. 0 restores the original talk-immediately behaviour.")]
    [SerializeField] private float introDelaySeconds = 5f;

    // Play once per scene entry. The board button can be clicked again after the
    // prompt UI is up, and re-introducing himself mid-conversation would be absurd.
    private bool hasPlayed;

    // Hooked to the board button's On Click () in the Inspector — the same event
    // that reveals the character and the prompt canvas.
    public void PlayIntro()
    {
        if (hasPlayed) return;
        hasPlayed = true;

        StartCoroutine(PlayIntroRoutine());
    }

    // Every path — delay, audio, and both failsafes — runs through this one
    // coroutine so there is exactly ONE lock and ONE unlock. That is what
    // guarantees the spec's "no stuck-disabled state": the re-enable at the
    // bottom is unconditional and unreachable-past only by the object being
    // destroyed, which takes the buttons with it anyway.
    private IEnumerator PlayIntroRoutine()
    {
        SetInputEnabled(false);

        // Resolved BEFORE the wait so a wiring mistake shows up in the log at
        // click time rather than N seconds later, when it reads as a different bug.
        bool canSpeak = introClip != null && speechSource != null;
        if (!canSpeak)
        {
            if (introClip == null)
                Debug.Log("[Intro] No intro clip assigned; holding the entrance beat, then skipping the introduction.");
            else
                Debug.LogWarning("[Intro] No speech AudioSource assigned; holding the entrance beat, then skipping the introduction.");
        }

        // The delay is the character's ENTRANCE beat, not a lead-in to the audio,
        // so a missing clip still waits it out. Two reasons: the entrance needs
        // settling time whether or not anyone speaks over it, and keeping the beat
        // clip-independent means dropping the WAV in later changes nothing about
        // the pacing — the intro simply becomes audible.
        if (introDelaySeconds > 0f)
            yield return new WaitForSeconds(introDelaySeconds);

        if (canSpeak)
        {
            if (captionText != null && !string.IsNullOrEmpty(introCaption))
                captionText.text = introCaption;

            speechSource.Stop();
            speechSource.clip = introClip;
            speechSource.Play();

            // One frame for isPlaying to latch before we start watching it, otherwise
            // the loop below can exit immediately on the frame Play() was called.
            yield return null;

            // Watch the clip too: if anything else takes the AudioSource over we stop
            // waiting rather than holding the buttons hostage.
            while (speechSource.isPlaying && speechSource.clip == introClip)
                yield return null;
        }

        SetInputEnabled(true);
    }

    // Reuses the existing lock so the intro obeys the same rule as recording and
    // in-flight requests: SetButtonsInteractable(true) hands Record/Submit back to
    // the one-mode-at-a-time rule rather than blindly enabling both.
    private void SetInputEnabled(bool enabled)
    {
        if (voiceController == null) return;
        voiceController.SetButtonsInteractable(enabled);
    }
}
