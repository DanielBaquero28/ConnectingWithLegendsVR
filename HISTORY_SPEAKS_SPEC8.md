# History Speaks VR — Spec 8: Conversation UX Polish + Character Intros

Six adjustments from on-device testing of the two character scenes (MainScene/Einstein, MLKScene/MLK). Items 1, 2, 4, 5 are shared code — they must work identically in both scenes with no per-scene forks. **Do not touch:** `server/` runtime code (the render script is the one exception), ProxyClient request internals, recording logic (`AudioRecorder` 12s/4s), `WavUtility`, SALSA config, safety/crisis behavior, metering.

## 1. Countdown text: "Ask your question…"
In `VoiceRecognitionController`, the recording countdown currently shows only the number. Change the display to `"Ask your question... {N}"` (same CountdownText object, single string). Keep "Processing..." and other states as-is.

## 2. "Processing..." feedback on the typed path
Today only the voice path shows "Processing..." (via CountdownText). When the user clicks Submit (or Enter) on a typed question, nothing indicates work is happening until the reply arrives. Show the same "Processing..." indicator (reuse CountdownText: activate + set text) from the moment a typed request is dispatched until the reply is displayed or a failure path resolves — then hide it. Must respect the existing isProcessing lock and never get stuck visible on failure paths (network fail, safety redirect, crisis). Keep one source of truth for showing/hiding it (don't duplicate logic between the typed and voice paths — extract a small shared show/hide if needed).

## 3. Crisis clip rendering for MLK (script update only)
Extend `server/scripts/render-crisis-wav.js` to be character-parameterized: accept a path to a character seed JSON (e.g. `node render-crisis-wav.js server/scripts/seed-mlk.json crisis-mlk.wav`) and use THAT file's voice block (languageCode, voiceName, speakingRate, pitch) with the same crisis message text from safety.js. Default behavior with no args stays Einstein-compatible. Daniel then runs it for MLK (en-US-Neural2-D / 0.88 / −3.0), imports `crisis-mlk.wav`, and assigns it to MLKScene's crisisVoiceClip field.

## 4. Character self-introductions (after "I'm facing the board!")
**Design (locked):** each character has a pre-rendered intro WAV (NOT runtime TTS). When the board-facing button is clicked, the intro plays **through the existing TextToSpeechController AudioSource** — so SALSA lip-sync and the CharacterAnimationDriver gestures react automatically with zero extra wiring.

Implementation:
- New small component `CharacterIntroPlayer` (scene-level, reusable): `[SerializeField] AudioClip introClip`, `[SerializeField] AudioSource speechSource` (the TextToSpeechController AudioSource), optional `[SerializeField] TMP_Text captionText` + `[TextArea] string introCaption` to also display the intro line in the existing ResponseText/caption UI.
- Hook: invoked by the same event/flow as the board button (identify where the "I'm facing the board!" click is handled — see item 6's investigation — and call the intro from there). Play once per scene entry; do not replay on subsequent interactions.
- While the intro plays, disable Record/Submit (reuse SetButtonsInteractable) and re-enable when the clip ends — the student shouldn't talk over the introduction. Failsafe: if introClip is null, skip silently and enable buttons immediately.
- Also extend `render-crisis-wav.js` (or add a sibling `render-intro-wav.js`, implementer's choice — prefer one generalized script `render-speech-wav.js` that takes seed JSON + text + output path) so intros are rendered with each character's voice.

Intro scripts (Daniel may edit wording before rendering):
- **Einstein:** "Ah, welcome! I am Albert Einstein. I once imagined riding alongside a beam of light — and that daydream became the theory of relativity. Ask me anything, and let us wonder together."
- **MLK:** "Good day, my friend. I am Martin Luther King Jr. I led a movement that changed unjust laws without raising a fist — through nonviolence and love. What shall we talk about today?"

## 5. Calmer, more natural gestures (pauses between movement)
Current behavior chains gesture clips back-to-back for the whole reply — reads as over-animated. Add **calm interludes** to `CharacterAnimationDriver`:
- With configurable probability (`[SerializeField] float calmChance`, default 0.4), the NEXT speaking segment is a "pause": fade the gesture layer weight to 0 over ~0.4s, hold for a configurable duration (`[SerializeField] Vector2 calmDurationRange`, default 2–4s) while speech continues (idle base layer shows through — hands settle naturally), then fade back up and resume gestures.
- Never two calm interludes in a row; never start a reply with one (first segment is always a gesture, so he visibly "comes alive" when starting to speak).
- Keep all existing behavior (fill-the-duration, crossfades, layer fade in/out at speech start/end) intact. Expose the new fields for Inspector tuning.
- Note for Daniel: also lower `gestureLayerWeight` to ~0.45–0.5 on both characters (manual Inspector tune).

## 6. MLKScene: Einstein appears on board click — find and fix the activation logic
In MLKScene a disabled Einstein instance still gets activated when "I'm facing the board!" is clicked. Investigate: find the component that activates/reveals the character on the board-click flow (likely PromptEnabler or a serialized GameObject reference in the button's handler). Report exactly which component + field references the character. Fix: make the reference point at the MLK instance in MLKScene, and flag to Daniel that the disabled Einstein instance should be deleted from MLKScene entirely (list it as an Inspector step; don't delete scene objects yourself if risky — reassigning the reference is the code-safe part). Also note the finding in a comment so future character scenes (the cookbook) include "reassign character reference" as a step.

## Verification
- Unity batch compile clean.
- Both scenes get 1/2/4/5 behavior identically (shared scripts — confirm no scene-specific code paths).
- List all Inspector steps for Daniel (intro clip assignments, caption wiring, calm-tune fields, MLKScene reference fix + Einstein deletion, crisis clip assignment).

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC8.md in the repo root and implement all six items, showing me each change before moving on. For item 6, investigate and report the activation logic before fixing. Respect the do-not-touch list. Finish with a passing Unity batch compile and a consolidated list of my Inspector steps.
