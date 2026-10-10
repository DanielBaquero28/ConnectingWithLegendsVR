# History Speaks VR — Spec 9: Intro Timing + Scene Load Smoothing

Two items from MLKScene testing. **Do not touch:** server/, ProxyClient request internals, recording logic, WavUtility, SALSA config, safety/metering, the character models/materials themselves (texture optimization is handled manually by Daniel in import settings).

## 1. Intro delay (character visible before it speaks)
The self-introduction (Spec 8 item 4) currently starts before the character is visibly present/settled. Add a pre-intro delay:
- In `CharacterIntroPlayer`, add `[SerializeField] float introDelaySeconds = 5f`.
- When the board-facing flow triggers the intro: keep Record/Submit disabled, wait `introDelaySeconds` (character is visible and settled during this beat), THEN play the intro clip, THEN re-enable buttons on clip end.
- If introClip is null, still respect the disable, skip the audio, and re-enable after the delay (or immediately — implementer's call, but no stuck-disabled state).
- Expose the delay so Daniel can tune it per scene in the Inspector.

## 2. Scene-load smoothing (reduce freeze/hitch on character scenes)
MLKScene hitches/freezes on load and when the character activates. Texture compression is being handled separately by Daniel; this item covers what the code/scene setup can do:

Investigate and report first: how are the character scenes loaded from the hub (single `SceneManager.LoadScene`, sync)? Is the heavy character active immediately on scene load, or instantiated/enabled at a point that causes a spike?

Then implement whichever apply:
- **Async scene loading with a loading screen:** change hub→character scene transitions to `LoadSceneAsync`, showing a simple loading indicator (fade or "Entering…" canvas) so the main-thread hitch isn't a frozen frame in the headset. This is the biggest perceived-smoothness win in VR (a frozen frame in a headset is nauseating; a loading fade is comfortable).
- **Warm up the character before reveal:** if the freeze happens when the character first becomes visible/animates (shader compilation, first SALSA/skinned-mesh update), enable the character slightly before it's revealed (behind a fade or off-camera for a frame or two) so the first-frame cost is paid during the transition, not in front of the user. Consider a brief fade-from-black on scene entry to mask any residual hitch.
- **Shader warmup:** if the hitch is shader compilation on first appearance, add a shader warmup (preload the URP/character shader variants) during the loading transition.
- Do NOT change the models, materials, or textures.

Report which causes you find and which fixes you applied. Note reusable pieces (loading screen, fade) so all future character scenes inherit them — this is cookbook infrastructure.

## Verification
- Unity batch compile clean.
- List Inspector steps (intro delay values, any loading-canvas/fade setup, Build Settings if a shared loading scene is added).

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC9.md. Implement item 1 (intro delay). For item 2, FIRST investigate and report how character scenes load and where the hitch originates, then apply the async-load/loading-screen/warmup fixes that fit, without touching models/materials/textures. Show each change; finish with a clean compile and my Inspector steps.
