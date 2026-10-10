# History Speaks VR — Spec 7: Character Selection Hub + Multi-Character Architecture

Turn the single-character (Einstein-hardcoded) app into a multi-character platform: a hub scene where the student picks a historical figure, per-character scenes/environments, and the character's ID flowing dynamically through the proxy pipeline. Einstein becomes the first registry entry. **Diagnose-then-fix like Specs 5–6**: inspect first (Step 0), report, then implement.

**Out of scope / do not touch:** `server/` (character configs already live in Firestore; no server changes needed), `ProxyClient` request/retry internals (only where it gets its characterId), recording logic (`AudioRecorder`, 12s/4s), `WavUtility`, SALSA config, the animation template (Spec 6), safety/crisis behavior, metering.

## Step 0 — Investigate and report BEFORE changes
1. **Scene structure:** What lives in MainScene? Which GameObjects are Einstein-specific (his model, SALSA, animator, environment) vs shared infrastructure (XR rig, UI canvases, ProxyClient, controllers, SafetyFilter)?
2. **Character ID flow:** Where is `X-Character-Id: einstein` currently set? (ProxyClient? Controller?) Identify every place "einstein" is hardcoded.
3. **Scene loading readiness:** Does the project have any scene-management code? Is the XR rig (OVRCameraRigInteraction) safe to duplicate per scene, or does anything assume a single persistent instance?
4. **Session handling:** Where session IDs are created/reset (Start + ResetConversation) — selection of a new character must start a fresh session.
5. **Build settings:** Which scenes are in Build Settings currently?

Report findings, then proceed.

## Implementation

### 1. Character registry (local, data-driven)
- New ScriptableObject `CharacterDefinition`: `characterId` (must match the Firestore doc ID, e.g. "einstein"), `displayName`, `era` (short string for the card), `portrait` (Sprite), `sceneName` (the character's scene), `available` (bool — false shows a locked/"coming soon" card).
- New ScriptableObject `CharacterRegistry`: ordered list of `CharacterDefinition`s. Create the asset with Einstein as the first entry (available=true) and 9 placeholder entries (da Vinci, MLK Jr., Lincoln, Cleopatra VII, Marie Curie, Mandela, Socrates, Julius Caesar, Harriet Tubman — available=false, sceneName empty). Placeholder portraits can be a shared silhouette sprite.

### 2. Selection carrier
- Small static class or persistent singleton `CharacterSession`: holds the selected `CharacterDefinition` across scene loads, plus `NewSessionId()` (GUID) called on each selection. This is the single source the character scene reads.

### 3. Hub scene
- New scene `HubScene`: the app's entry point (first in Build Settings). Reuse the existing XR rig prefab + interaction setup (ray interactors) — same pattern as MainScene.
- World-space canvas at comfortable eye level with a grid/row of **character cards** from the registry: portrait, name, era. Available characters → a Select button; unavailable → visibly locked ("Coming soon"), non-interactable.
- Selecting a character: set `CharacterSession`, generate a new session ID, load that character's scene (single, non-additive `SceneManager.LoadScene` — simplest and most memory-safe on Quest).
- Keep the hub visually simple and clean; Daniel will art-direct later. Structure > beauty for now.

### 4. Character scene contract (make MainScene the template)
- Refactor MainScene (Einstein's auditorium) to read from `CharacterSession` instead of hardcoding: whatever component sends `X-Character-Id` now pulls `CharacterSession.Current.characterId` (fallback "einstein" if launched directly in Editor for testing — log a warning).
- Add a **"Back to Hub"** interaction in the character scene (a world-space button consistent with existing UI): ends the current conversation cleanly (no dangling request; respect isProcessing) and loads HubScene.
- Document (brief comment or README-CHARACTER-SCENE.md): the checklist for creating a new character scene from MainScene — duplicate scene, swap model + SALSA + animator + environment, set the scene name in the registry entry, add scene to Build Settings, flip available=true. This is the Unity half of the character cookbook.

### 5. Session integrity
- New character selection or re-entering a scene → fresh session ID (server history is per-session; no cross-character bleed).
- ResetConversation behavior unchanged within a scene.

## Verification
- Unity batch compile clean (Editor closed).
- Editor test: HubScene shows 10 cards (1 active, 9 locked); selecting Einstein loads his scene and a typed conversation works end-to-end (character ID flows to the proxy); Back to Hub returns and a re-entry starts a fresh session.
- Flag what needs on-device verification (card readability/reach in VR, ray interaction on cards, scene-load performance on Quest).

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC7.md in the repo root. FIRST do Step 0 — inspect the scene structure, where the character ID is hardcoded, scene-management readiness, and session handling — and report findings before changing anything. THEN implement the registry, CharacterSession, HubScene with character cards (Einstein active, 9 locked placeholders), the MainScene refactor to read the selected character dynamically, and Back to Hub. Respect the constraints (no changes to server/, ProxyClient request internals, recording logic, WavUtility, SALSA, the animation template, or safety/metering). Finish with a passing Unity batch compile, list my Inspector/scene steps, and note what needs on-device verification.
