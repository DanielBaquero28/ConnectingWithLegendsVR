# History Speaks VR — Spec 2: Key Hygiene + Voice Restoration

Follow-up to the hardening session. Two changes only. Same constraints as before: no backend proxy; do not touch `AudioRecorder`, `WavUtility`, the TTS cache, the recording logic, or the Spec 1 safety/persona logic.

---

## Change 1 — All API keys as `[SerializeField]`

Goal: **no API key hardcoded in any `.cs` source file.** Audit all three integration scripts so each key is an Inspector-set `[SerializeField] private string` field, matching the existing pattern.

- **`SpeechToText.cs` (Deepgram)** — the key is currently a hardcoded literal in source (`"YOUR_DEEPGRAM_KEY"`). Replace it with `[SerializeField] private string deepgramApiKey = "";` and use that field in the `Authorization: Token {deepgramApiKey}` header. Remove the hardcoded literal entirely.
- **`AIResponseControllerV2.cs` (Gemini)** — verify the key is already `[SerializeField] private string apiKey = "";`. If it is hardcoded anywhere, convert it.
- **`TextToSpeechGoogle.cs` (Google TTS)** — verify the key is already `[SerializeField] private string apiKey = "";`. If it is hardcoded anywhere, convert it.

Do not change any other logic. Never log a key value (no `Debug.Log` of the key).

---

## Change 2 — Restore Einstein's original voice

The original voice stacked two layers that the refactor dropped: an SSML `<prosody rate='0.85' pitch='-6st'>` on top of audioConfig `pitch=-2.0, speakingRate=0.85`. Net effect ≈ **pitch −8 semitones, rate 0.85 × 0.85 ≈ 0.72**. Reproduce this purely through the CharacterProfile audioConfig fields — do **not** re-introduce SSML prosody wrapping.

- In **`CharacterProfile.cs`**, change the default field values to `pitch = -8.0f` and `speakingRate = 0.72f` (so newly created profiles start correct).
- If an **`Einstein` CharacterProfile asset already exists** in the project, update its serialized `pitch` to `-8.0` and `speakingRate` to `0.72` directly in the `.asset` file.
- In **`TextToSpeechGoogle.cs`**, confirm the synthesized request uses only the audioConfig values passed from the active CharacterProfile (no `<speak>`/`<prosody>` wrapping). Leave the existing JSON escaping, sample-rate-from-header read, and audio cache untouched.

---

## After Claude Code finishes (Inspector / manual)

1. Re-enter the **Deepgram key** in the SpeechToText component's new `Deepgram Api Key` field (it is no longer in code).
2. Confirm the **Gemini** and **Google-TTS** keys are still populated in their components.
3. Confirm the **Einstein** asset shows `pitch -8.0`, `speakingRate 0.72`. If the asset did not exist yet, create it (it will now default correctly) and fill persona/cutoff/few-shots/voiceName per Spec 1 §7.

---

## Kickoff instruction (paste into Claude Code)

> Read `HISTORY_SPEAKS_SPEC2.md` in the project root and implement both changes exactly as written, showing me each file edit before moving on. Then run the Unity build and fix any compile errors. Do NOT implement the backend proxy, do NOT re-add SSML prosody to the TTS request, and do NOT touch AudioRecorder, WavUtility, the TTS cache, or the recording logic.
