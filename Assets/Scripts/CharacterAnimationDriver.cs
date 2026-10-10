using UnityEngine;

// Spec 6 — Character body-animation driver (REUSABLE across every character).
//
// Drives an idle <-> speaking body-gesture blend from a SINGLE source of truth:
// whether the character's TTS AudioSource is currently playing a reply. Step 0
// confirmed that same AudioSource (on "TextToSpeechController") is the one SALSA
// analyses for lip-sync, so lips, eyes and body gestures all start and stop
// together — there is deliberately no second "is speaking" detector here.
//
// ----- Layered architecture (why gestures no longer swap the whole body) -----
// The Animator Controller has TWO layers:
//   * Base Layer  — plays ONLY the looping Idle clip, full body, always. It is
//     never touched by this script, so the legs, hips and overall stance stay
//     rock-steady while he talks.
//   * Gestures     — a SECOND layer masked to the UPPER BODY (arms, shoulders,
//     chest/spine; head is OFF because SALSA owns it). It holds an Empty default
//     state plus the Speaking_* gesture clips. Blending mode is Override.
// This script fades the Gestures layer's WEIGHT up when speech starts and back to
// zero when it ends, so gestures are layered ON TOP of the persistent idle rather
// than replacing it — no more full-body pose swaps between clips. The peak weight
// is < 1 on purpose (default 0.65) so the talking motion reads as a natural
// overlay on his idle posture instead of a hard takeover.
//
// While speaking it cross-fades through the gesture states in a RANDOM,
// NON-REPEATING order for the FULL duration of speech: a short reply plays ~1
// clip, a long reply cycles several. Animation length is fully decoupled from clip
// length, so he never freezes mid-sentence. When audio stops, the layer weight
// eases back to 0 and the layer returns to Empty rather than snapping.
//
// Why script-driven CrossFade instead of Animator condition-transitions:
// plain Animator transitions are deterministic, so they cannot do "pick a random
// gesture that is never the same one twice in a row." CrossFadeInFixedTime lets
// us choose the next state in code while still blending smoothly. The isSpeaking
// bool is still published on the Animator as the shared source of truth.
//
// ----- To reuse on a NEW character -----
//   1. Duplicate the body Animator Controller AND the UpperBodyMask (retarget the
//      mask if the new rig differs). Drop THAT character's idle clip into the base
//      Idle state and its gesture clips into the Gestures-layer Speaking_* states.
//      Keep the state NAMES + the masked second-layer structure identical.
//   2. Add this component to the new character; in the Inspector assign its
//      Animator and its TTS AudioSource. No code changes per character — only data.
//
// All fields are Inspector-set on purpose: on Quest 3 (Android) there are no
// runtime env vars and runtime-added components have empty SerializeFields, so
// this must live on a scene object with its references wired by hand.
[DisallowMultipleComponent]
public class CharacterAnimationDriver : MonoBehaviour
{
    [Header("References (assign in Inspector)")]
    [Tooltip("The character's Humanoid Animator that uses the body Animator Controller.")]
    [SerializeField] private Animator animator;

    [Tooltip("The TTS AudioSource the reply plays through — the SAME one SALSA uses " +
             "(TextToSpeechController's AudioSource). This is the single 'is speaking' signal.")]
    [SerializeField] private AudioSource speechAudioSource;

    [Header("Animator wiring (names/index must match the Controller)")]
    [Tooltip("Bool parameter published on the Animator while speaking (shared source of truth).")]
    [SerializeField] private string isSpeakingParam = "isSpeaking";

    [Tooltip("Index of the masked upper-body Gestures layer. Base Layer (idle) is 0, so this " +
             "is 1 in EinsteinBodyAnimator. The driver only ever touches THIS layer.")]
    [SerializeField] private int gestureLayerIndex = 1;

    [Tooltip("No-motion default state on the Gestures layer, returned to when speech ends.")]
    [SerializeField] private string emptyStateName = "Empty";

    [Tooltip("Gesture state names on the Gestures layer, cycled while speaking. Order is " +
             "irrelevant — selection is randomised. Each holds one talking clip (Loop Time OFF). " +
             "MUST match states that exist on the Gestures layer — a name that is not there " +
             "makes CrossFade silently do nothing.")]
    [SerializeField] private string[] gestureStateNames = { "Speaking_0", "Speaking_1" };

    [Header("Gesture layer weight — tune for naturalness")]
    [Range(0f, 1f)]
    [Tooltip("Peak weight of the upper-body gesture overlay while speaking. 1 = full takeover " +
             "of the masked bones; lower keeps more of his idle posture showing through. 0.65 " +
             "reads as natural gesticulation over a steady stance.")]
    [SerializeField] private float gestureLayerWeight = 0.65f;

    [Tooltip("Seconds to raise the gesture layer from 0 to peak when speech starts.")]
    [SerializeField] private float layerFadeInTime = 0.4f;

    [Tooltip("Seconds to lower the gesture layer back to 0 when speech ends, so the arms " +
             "settle into idle instead of snapping.")]
    [SerializeField] private float layerFadeOutTime = 0.5f;

    [Header("Gesture crossfade timing (seconds) — tune visually")]
    [Tooltip("Empty -> first gesture crossfade on the Gestures layer when speech starts.")]
    [SerializeField] private float startSpeakingBlend = 0.3f;

    [Tooltip("Gesture -> next gesture crossfade — keeps a long reply flowing.")]
    [SerializeField] private float gestureToGestureBlend = 0.3f;

    [Tooltip("Gesture -> Empty crossfade on the Gestures layer when speech ends.")]
    [SerializeField] private float returnToEmptyBlend = 0.35f;

    [Range(0.5f, 0.99f)]
    [Tooltip("Fraction of the current gesture played before crossfading to the next one. " +
             "Lower = more overlap/flow; higher = each gesture reads more fully.")]
    [SerializeField] private float gestureAdvanceAt = 0.8f;

    [Header("Calm interludes — pauses between gestures (Spec 8, item 5)")]
    [Range(0f, 1f)]
    [Tooltip("Chance that the NEXT speaking segment is a calm pause instead of another " +
             "gesture. Chaining gesture clips back-to-back for a whole reply reads as " +
             "over-animated; letting him settle every so often reads as a person thinking. " +
             "0 = original always-gesturing behaviour, 1 = a pause after every gesture.")]
    [SerializeField] private float calmChance = 0.4f;

    [Tooltip("Min/max seconds a calm pause lasts (randomised per pause). Speech continues " +
             "throughout — only the gesture overlay rests, so the idle base layer shows " +
             "through and his hands settle naturally.")]
    [SerializeField] private Vector2 calmDurationRange = new Vector2(2f, 4f);

    [Tooltip("Seconds to fade the gesture layer down into a calm pause and back up out of " +
             "it. Short enough to feel deliberate, long enough not to snap.")]
    [SerializeField] private float calmFadeTime = 0.4f;

    [Header("Diagnostics (TEMPORARY — turn off / remove once wiring is confirmed)")]
    [Tooltip("Logs ~2x/sec the held AudioSource's name, instance ID and isPlaying, so you can " +
             "confirm at runtime this is the SAME AudioSource that actually plays the reply " +
             "(compare the instance ID with the one SALSA/TextToSpeechController uses). If audio " +
             "is audible but this logs isPlaying=False, speechAudioSource points at the wrong " +
             "instance.")]
    [SerializeField] private bool logDiagnostics = true;

    // --- runtime state ---
    private int isSpeakingHash;
    private bool wasSpeaking;
    private int currentGestureIndex = -1;
    private int pendingGestureHash;    // hash of the gesture we last cross-faded to
    private bool layerReady;           // gestureLayerIndex is valid on this Animator
    private float currentLayerWeight;  // cached weight we apply each frame
    private float targetLayerWeight;   // 0 when idle, gestureLayerWeight while speaking
    private float weightFadeRate;      // units/sec toward the target (set per fade)
    private float diagTimer;           // [DIAG] throttle for the temporary log below

    // --- calm-interlude state (Spec 8, item 5) ---
    // A calm pause is a SEGMENT, exactly like a gesture is: it is chosen at the
    // same decision point (when the current gesture is finishing), so it can never
    // be the first segment of a reply — he always comes visibly alive when he
    // starts speaking — and, because a pause is always followed by a gesture, two
    // pauses can never run back to back. lastSegmentWasCalm makes that invariant
    // explicit rather than merely emergent.
    private enum CalmPhase { None, FadingOut, Holding }
    private CalmPhase calmPhase;
    private bool lastSegmentWasCalm;
    private float calmHoldUntil;       // Time.time at which the hold ends

    private void Awake()
    {
        isSpeakingHash = Animator.StringToHash(isSpeakingParam);
        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null)
            Debug.LogWarning("[CharacterAnimationDriver] No Animator assigned; body animation disabled.");
        if (speechAudioSource == null)
            Debug.LogWarning("[CharacterAnimationDriver] No speech AudioSource assigned; " +
                             "cannot detect speaking — body will stay idle.");
    }

    private void Start()
    {
        // The gesture layer must exist before we can drive its weight/states.
        layerReady = animator != null && gestureLayerIndex > 0 && gestureLayerIndex < animator.layerCount;
        if (animator != null && !layerReady)
            Debug.LogWarning($"[CharacterAnimationDriver] Gesture layer index {gestureLayerIndex} " +
                             $"is out of range (Animator has {animator.layerCount} layer(s)); " +
                             "gestures disabled — check the controller has the masked Gestures layer.");

        // Start hidden: gesture overlay contributes nothing until he speaks.
        currentLayerWeight = 0f;
        targetLayerWeight = 0f;
        if (layerReady) animator.SetLayerWeight(gestureLayerIndex, 0f);
    }

    private void Update()
    {
        if (animator == null || speechAudioSource == null) return;

        bool speaking = speechAudioSource.isPlaying;

        // [DIAG] TEMPORARY: prove the driver is watching the SAME AudioSource instance that
        // plays the reply. If you hear audio but this prints isPlaying=False, this field is
        // wired to a different AudioSource than the one being Play()'d — fix the reference,
        // then set logDiagnostics = false. Remove this block once confirmed.
        if (logDiagnostics)
        {
            diagTimer -= Time.unscaledDeltaTime;
            if (diagTimer <= 0f)
            {
                diagTimer = 0.5f;
                Debug.Log($"[DIAG][CharacterAnimationDriver] AudioSource '{speechAudioSource.name}' " +
                          $"instanceID={speechAudioSource.GetInstanceID()} isPlaying={speaking} " +
                          $"clip={(speechAudioSource.clip != null ? speechAudioSource.clip.name : "<none>")}");
            }
        }

        if (speaking && !wasSpeaking)
        {
            // Speech just started: publish the flag, fade the overlay in, first gesture.
            animator.SetBool(isSpeakingHash, true);
            ResetCalmState();
            BeginLayerFade(gestureLayerWeight, layerFadeInTime);
            StartGesture(startSpeakingBlend, forceDifferent: false);
        }
        else if (!speaking && wasSpeaking)
        {
            // Speech just ended: fade the overlay out and settle the layer to Empty.
            animator.SetBool(isSpeakingHash, false);
            currentGestureIndex = -1;
            pendingGestureHash = 0;
            // Drop any pause in progress so its hold timer can't leak into the
            // next reply and suppress that reply's opening gesture.
            ResetCalmState();
            BeginLayerFade(0f, layerFadeOutTime);
            if (layerReady && !string.IsNullOrEmpty(emptyStateName))
                animator.CrossFadeInFixedTime(emptyStateName, returnToEmptyBlend, gestureLayerIndex);
        }
        else if (speaking)
        {
            // Still speaking: either we're resting mid-reply, or the current
            // gesture is finishing and it's time to choose the next segment.
            if (calmPhase != CalmPhase.None)
                UpdateCalmInterlude();
            else
                AdvanceGestureIfFinishing();
        }

        ApplyLayerWeight();
        wasSpeaking = speaking;
    }

    // Sets where the gesture layer weight is heading and how fast (units/sec) so the
    // fade takes ~duration seconds regardless of the current weight.
    private void BeginLayerFade(float target, float duration)
    {
        targetLayerWeight = Mathf.Clamp01(target);
        float span = Mathf.Max(gestureLayerWeight, 0.0001f);
        weightFadeRate = span / Mathf.Max(duration, 0.0001f);
    }

    // Eases the actual layer weight toward the target every frame.
    private void ApplyLayerWeight()
    {
        if (!layerReady) return;
        if (Mathf.Approximately(currentLayerWeight, targetLayerWeight)) return;

        currentLayerWeight = Mathf.MoveTowards(currentLayerWeight, targetLayerWeight,
                                               weightFadeRate * Time.deltaTime);
        animator.SetLayerWeight(gestureLayerIndex, currentLayerWeight);
    }

    // Picks a gesture (optionally guaranteed different from the current one) and
    // cross-fades into it ON THE GESTURE LAYER, remembering it so we can tell when
    // it is finishing.
    private void StartGesture(float blend, bool forceDifferent)
    {
        if (!layerReady) return;
        if (gestureStateNames == null || gestureStateNames.Length == 0) return;

        int index = PickGestureIndex(forceDifferent);
        currentGestureIndex = index;
        string stateName = gestureStateNames[index];
        pendingGestureHash = Animator.StringToHash(stateName);
        animator.CrossFadeInFixedTime(stateName, blend, gestureLayerIndex);
    }

    private void AdvanceGestureIfFinishing()
    {
        if (!layerReady || pendingGestureHash == 0) return;
        if (animator.IsInTransition(gestureLayerIndex)) return; // let the current crossfade finish

        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(gestureLayerIndex);
        if (state.shortNameHash != pendingGestureHash) return; // not on our gesture yet

        // normalizedTime runs 0..1 across one play (it only grows past 1 if the
        // clip is set to loop). Crossfading a little before the end fills the gap
        // so back-to-back gestures flow instead of hard-cutting.
        if (state.normalizedTime < gestureAdvanceAt) return;

        // The one decision point for what the next speaking segment is: another
        // gesture, or a calm pause. Never two pauses in a row.
        if (!lastSegmentWasCalm && Random.value < calmChance)
            BeginCalmInterlude();
        else
            StartGesture(gestureToGestureBlend, forceDifferent: true);
    }

    // Starts a calm pause: the gesture layer eases out to nothing and settles on
    // Empty, leaving the base idle layer to carry the body while speech continues.
    private void BeginCalmInterlude()
    {
        if (!layerReady) return;

        calmPhase = CalmPhase.FadingOut;
        lastSegmentWasCalm = true;

        // Nothing to advance while resting — clearing this also stops
        // AdvanceGestureIfFinishing from firing if it ever runs during the pause.
        pendingGestureHash = 0;

        BeginLayerFade(0f, calmFadeTime);
        if (!string.IsNullOrEmpty(emptyStateName))
            animator.CrossFadeInFixedTime(emptyStateName, calmFadeTime, gestureLayerIndex);
    }

    private void UpdateCalmInterlude()
    {
        switch (calmPhase)
        {
            case CalmPhase.FadingOut:
                // Start counting only once the overlay is actually down, so the
                // configured hold is the time he genuinely rests.
                if (currentLayerWeight > 0.0001f) break;

                calmPhase = CalmPhase.Holding;
                calmHoldUntil = Time.time + Random.Range(calmDurationRange.x, calmDurationRange.y);
                break;

            case CalmPhase.Holding:
                if (Time.time < calmHoldUntil) break;

                // Rest over: fade the overlay back in and come out of it on a
                // fresh gesture — same treatment as the start of a reply, so he
                // re-enters the motion rather than snapping into a half-played clip.
                calmPhase = CalmPhase.None;
                lastSegmentWasCalm = false;
                BeginLayerFade(gestureLayerWeight, calmFadeTime);
                StartGesture(startSpeakingBlend, forceDifferent: true);
                break;
        }
    }

    private void ResetCalmState()
    {
        calmPhase = CalmPhase.None;
        lastSegmentWasCalm = false;
    }

    private int PickGestureIndex(bool forceDifferent)
    {
        int count = gestureStateNames.Length;
        if (count <= 1) return 0;
        if (!forceDifferent || currentGestureIndex < 0)
            return Random.Range(0, count);

        // Random, but never the same index twice in a row.
        int next = Random.Range(0, count - 1);
        if (next >= currentGestureIndex) next++;
        return next;
    }
}
