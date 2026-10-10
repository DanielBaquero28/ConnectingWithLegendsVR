# History Speaks VR — Spec 6: Character Body Animation (Idle + Speaking Gestures)

Give Einstein natural body motion: subtle idle when listening, varied hand/arm gestures while speaking, blended smoothly and driven by whether TTS audio is playing. **Diagnose-then-fix, like Spec 5:** the character rig and how SALSA/audio are wired are not fully known to the spec author — Claude Code must investigate and report (Step 0) before building. Design this to be **reusable across all future characters**, since we're about to replicate.

**Out of scope / do not touch:** the `server/` backend, `ProxyClient` request logic, `AudioRecorder`/recording logic, `WavUtility`, crisis/safety behavior, metering, and the XR rig/recenter/UI work from Spec 5. This is animation setup only.

## Prerequisite (Daniel provides)
Mixamo FBX clips imported into the project: one refined idle, and **five** talking/gesture clips of varying length (~3–4s each, one longer), downloaded "FBX for Unity," "Without Skin"/animation-only, 30 FPS, no keyframe reduction, "In Place," retargeted to Einstein's Humanoid rig. Einstein's model is confirmed Humanoid.

## Step 0 — Investigate and report BEFORE changes
1. **Character rig:** Is Einstein's model an **Animation Type = Humanoid** rig (required for Mixamo retargeting) or Generic? Name the model asset and its current rig type. If Generic, flag it — Humanoid is needed for the Mixamo clips to retarget; recommend the switch but do not change import settings without confirming.
2. **Existing Animator:** Does Einstein already have an Animator component + Animator Controller? What states/parameters exist? Where does the current Idle animation live and how is it played?
3. **The "is speaking" signal:** How does the project currently know Einstein is talking? Identify the exact source — almost certainly the `AudioSource` playing the TTS reply (the same signal SALSA lip-sync uses). Name the component/field. This bool is what will drive the animation blend.
4. **SALSA coexistence:** Confirm SALSA drives only face/lips/eyes (blendshapes/bones on the head) and does NOT control body/arm bones — so body animation and SALSA won't fight. Report which bones/transforms SALSA touches.
5. **Imported clips:** List the imported Mixamo clips (expected: one idle + five talking clips of varying length, ~3–4s each with one longer) and whether their import settings are correct (Humanoid; Loop Time enabled on the idle; "In Place"). Note each talking clip's length, since the Speaking state must cycle them to cover longer replies.

Report findings, then proceed.

## Implementation

### 1. Animator Controller — idle ↔ speaking blend
- Create (or extend) an Animator Controller for Einstein with two core states:
  - **Idle** (default): the refined idle clip, looping.
  - **Speaking**: the talking/gesture clips.
- Add a **bool parameter `isSpeaking`**.
- Transitions: Idle → Speaking when `isSpeaking` becomes true; Speaking → Idle when false. Use smooth transition durations (~0.25–0.4s, with exit time disabled on the to-Speaking transition so it responds promptly) so he eases between poses rather than snapping.

### 2. Fill the FULL speaking duration — loop/cycle gestures until audio stops (CRITICAL)
**Problem to solve:** Einstein's reply audio can run ~10+ seconds, but each talking clip is only ~3–4 seconds (one is longer). A single clip that plays once and stops would leave him frozen mid-speech. The gesture animation MUST fill the entire speaking duration, whatever its length.

**Required behavior:** While `isSpeaking` is true, continuously play gesture clips back-to-back for as long as he is talking. A ~3s reply plays one clip; a ~10s reply plays ~3 clips in sequence; a 20s reply plays more. The animation length must be fully decoupled from the audio length — never freeze while audio is still playing.

**Implementation:**
- The **Speaking** state cycles through the 5 talking clips (which have varying lengths ~3–4s, one longer) for the full duration of speech. Preferred approach: a sub-state machine (or a small controller script) that, each time a gesture clip finishes while `isSpeaking` is still true, **transitions to a different gesture clip** — randomized so it doesn't cycle in a fixed, predictable order and doesn't immediately repeat the same clip twice.
- **Blend between consecutive gesture clips smoothly** (crossfade ~0.2–0.35s), so back-to-back clips flow rather than hard-cutting between arm poses. This is what makes a long reply look like continuous natural gesturing instead of stitched loops.
- This design gives gesture variety within a single long reply for free (multiple different clips play during one answer), satisfying the "avoid robotic looping" goal.

### 2b. Return to idle cleanly when speech ends
- When `isSpeaking` becomes false (audio stopped), do NOT snap to idle. Let the current gesture ease out and blend back to Idle over ~0.3–0.4s so his arms settle naturally to a neutral/idle pose rather than jumping.
- Ensure gesture clips resolve toward a neutral arm position on exit so there's no jarring pose pop when returning to Idle.

### 3. Drive `isSpeaking` from the TTS audio (single source of truth)
- Create a small script (e.g. `CharacterAnimationDriver.cs`) that sets the Animator's `isSpeaking` bool based on whether the TTS `AudioSource` is currently playing the reply.
- Use the exact audio signal identified in Step 0 (the same one SALSA uses), so lip-sync and body gestures start/stop together. When audio starts → `isSpeaking = true`; when it stops → `false`.
- Do not create a second, independent "is speaking" detection — reference the existing signal so body, lips, and eyes stay in sync.

### 4. Reusability (important — we replicate to more characters)
- Structure this so a new character reuses the same pattern with minimal work: the Animator Controller and `CharacterAnimationDriver` should be a template a new character can adopt by assigning its own rig + clips. Keep character-specific data (which clips) separate from the logic. Note in a brief comment how to apply this to a new character.

## Constraints (unchanged)
No changes to `server/`, `ProxyClient` request logic, `AudioRecorder`/recording logic, `WavUtility`, safety/crisis, metering, or Spec 5's XR/UI work. Animation only. Do not alter SALSA's face/eye configuration.

## Verification
- Unity batch compile passes (Editor closed): zero `error CS`.
- Final validation is visual/on-device: Claude Code should note that gesture naturalness, blend timing, and clip variety must be tuned by Daniel by watching (in the Editor and on Quest). Provide sensible starting transition/blend values and flag them for tuning.

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC6.md in the repo root. FIRST do Step 0 — investigate Einstein's rig type, existing Animator, the "is speaking" audio signal, SALSA's bone coverage, and any imported Mixamo clips — and report findings before changing anything. THEN build the idle↔speaking Animator blend driven by the TTS audio signal, with varied speaking gestures, structured for reuse across future characters. Respect the constraints (do not touch server/, ProxyClient request logic, AudioRecorder/recording logic, WavUtility, safety/metering, SALSA's face config, or Spec 5's XR/UI work). Finish with a passing Unity batch compile and note which parts I need to tune visually.
