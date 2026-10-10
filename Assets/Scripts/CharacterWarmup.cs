using System.Collections;
using UnityEngine;

// Spec 9, item 2 — pays the character's first-draw cost behind the loading fade
// instead of in front of the student.
//
// ----- The problem this solves -----
// The character sits in the scene with m_IsActive = 0 and is revealed by the "I'm
// facing the board!" button. Being inactive suppresses RENDERING and UPDATING, not
// LOADING — so the assets are already in memory, but every cost that is deferred
// until a renderer first draws is still owed, and the board click pays all of it in
// one frame: URP shader-variant compilation for the digital-human materials, the
// first skinned-mesh skinning pass, the first Animator evaluation, SALSA's first
// update. That is the freeze-on-activation.
//
// Those costs are one-time and cached, so they do not care WHEN they are paid. This
// component enables the character for a couple of frames during the loading
// transition — while the screen is still black — lets it actually render, then puts
// it back exactly as it was. The board click then flips a character that has already
// drawn itself, which is cheap.
//
// ----- Why it renders rather than just "existing" -----
// Shader variants compile on first DRAW, not on activation, so the warmup has to
// wait for real end-of-frame renders. Being hidden behind the fade panel does not
// prevent this: the panel is transparent geometry drawn AFTER opaque geometry, so
// the character is fully rendered underneath it and simply painted over.
//
// ----- Wiring (per character scene) -----
//   1. Put this on any always-active scene object (the AI Response Controller
//      object is the natural home — it is active from load in every scene).
//   2. Drag the character root into Warmup Targets. Leave everything else alone.
// Scene-authored on purpose: warmupTargets is an Inspector reference, and on Quest
// runtime-added components have empty SerializeFields.
[DisallowMultipleComponent]
public class CharacterWarmup : MonoBehaviour
{
    [Header("What to warm up")]
    [Tooltip("The character root(s) that the board button reveals — normally inactive " +
             "at scene load. Each is enabled for a few frames behind the fade and then " +
             "restored to exactly the active state it had, so this is invisible to the " +
             "rest of the scene. Objects already active are left active.")]
    [SerializeField] private GameObject[] warmupTargets;

    [Tooltip("Frames the targets are left rendering. Shader variants compile on the " +
             "first draw, so 1 would technically do; 2 covers the case where something " +
             "is still initialising on the frame it is enabled.")]
    [Range(1, 10)]
    [SerializeField] private int warmupFrames = 2;

    [Header("Optional")]
    [Tooltip("A ShaderVariantCollection asset to preload during the transition. Only " +
             "worth creating if activation still hitches after the render warmup above: " +
             "record one via Project Settings > Graphics > Save to asset after playing " +
             "through the scene, then drop it here. Leave empty otherwise.")]
    [SerializeField] private ShaderVariantCollection shaderVariants;

    [Tooltip("Warm up even when the scene is opened directly (pressing Play in the " +
             "Editor) rather than entered through the hub. Off would mean Editor testing " +
             "silently exercises a different code path than the device does.")]
    [SerializeField] private bool warmUpOnDirectLaunch = true;

    private bool done;

    private IEnumerator Start()
    {
        // When the hub drives the entry, SceneTransition calls WarmUpRoutine itself
        // so the warmup lands INSIDE the fade and the fade-in waits for it. Self-
        // running here as well would double it. This branch is only for a scene
        // opened directly, where there is no transition to hide behind.
        if (SceneTransition.IsTransitioning) yield break;
        if (!warmUpOnDirectLaunch) yield break;

        yield return WarmUpRoutine();
    }

    // Safe to call more than once — the second call is a no-op, since the costs it
    // exists to pay are already paid.
    public IEnumerator WarmUpRoutine()
    {
        if (done) yield break;
        done = true;

        if (shaderVariants != null)
            shaderVariants.WarmUp();

        if (warmupTargets == null || warmupTargets.Length == 0)
        {
            Debug.LogWarning("[Warmup] No warmup targets assigned — the character will " +
                             "pay its first-draw cost when the board button reveals it.");
            yield break;
        }

        // Record BEFORE enabling anything: the restore has to put each object back
        // the way it was, not blanket-disable them. An already-active target must
        // stay active or the warmup would hide a character that was meant to be
        // visible from the start.
        bool[] wasActive = new bool[warmupTargets.Length];
        for (int i = 0; i < warmupTargets.Length; i++)
        {
            if (warmupTargets[i] == null) continue;
            wasActive[i] = warmupTargets[i].activeSelf;
            warmupTargets[i].SetActive(true);
        }

        // WaitForEndOfFrame (not a bare yield) because it is the render that
        // matters: this resumes after the frame has actually been drawn, which is
        // the point at which the shader variants are compiled and cached.
        for (int frame = 0; frame < warmupFrames; frame++)
            yield return new WaitForEndOfFrame();

        for (int i = 0; i < warmupTargets.Length; i++)
        {
            if (warmupTargets[i] == null) continue;
            warmupTargets[i].SetActive(wasActive[i]);
        }
    }
}
