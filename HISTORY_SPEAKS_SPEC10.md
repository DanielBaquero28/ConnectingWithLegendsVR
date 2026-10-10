# History Speaks VR — Spec 10: Chirp 3 HD Voice Support (MLK → Charon)

Switch MLK to `en-US-Chirp3-HD-Charon`. Chirp 3: HD voices **do not support SSML, `pitch`, or `speakingRate` audio parameters** — sending them can fail the request. Other characters (Einstein on `en-GB-Neural2-D` etc.) still need those params, so this must be a **general voice-capability rule**, not an MLK special case.

**Do not touch:** ProxyClient request internals, recording logic, WavUtility, SALSA, safety/crisis logic, metering, Unity animation.

## 1. Server: conditional audioConfig by voice family (`server/src/providers/googleTts.js`)
- Add a helper, e.g. `supportsProsodyParams(voiceName)` → returns **false** when the voice name contains `Chirp` (case-insensitive), **true** otherwise.
- When building the TTS request:
  - If **false**: send `audioConfig` with `audioEncoding: LINEAR16` and `sampleRateHertz: 24000` **only** — omit `pitch` and `speakingRate` entirely (omit the keys; do not send nulls or zeros).
  - If **true**: current behavior unchanged (pitch + speakingRate from the character's voice block).
- Never send SSML for Chirp voices — plain text input only (the current implementation already sends plain text; confirm and keep it that way).
- Log which mode was used at debug level (voice name + whether prosody params were sent) so this is diagnosable later.

## 2. Render script: same rule
Apply the identical capability check in the WAV render script (`render-crisis-wav.js` / `render-speech-wav.js`, whichever exists after Spec 8). Rendering MLK's clips with Charon must omit pitch/speakingRate the same way, so pre-rendered audio matches live output exactly.

## 3. MLK seed file (`server/scripts/seed-mlk.json`)
Update the voice block to:
```json
"voice": {
  "languageCode": "en-US",
  "voiceName": "en-US-Chirp3-HD-Charon",
  "speakingRate": 1.0,
  "pitch": 0.0
}
```
Keep the fields present (schema consistency) — the server ignores them for Chirp voices. Add a brief comment in the guide/README noting Chirp voices ignore rate/pitch.

## 4. Verification
- `npm install` clean, smoke test green.
- Confirm no code path sends `pitch` or `speakingRate` when the voice name contains "Chirp".
- Report the exact commands for Daniel to: re-seed MLK, re-render his crisis clip, re-render his intro clip.

## Notes / troubleshooting for Daniel
- If TTS returns an error on the voice name, try the lowercase-locale variant (`en-us-Chirp3-HD-Charon`) — Google's docs show both spellings.
- Chirp 3: HD is served from global/us/eu endpoints; if an availability error appears, that's the thing to check (not the code).

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC10.md in the repo root and implement it. The key requirement is a general voice-capability rule (Chirp voices → omit pitch/speakingRate/SSML; all other voices unchanged), applied in BOTH the server TTS provider and the WAV render script, plus the MLK seed update. Do not touch ProxyClient, recording logic, WavUtility, SALSA, safety, metering, or Unity animation. Finish with a clean install + green smoke test, and give me the exact commands to re-seed MLK and re-render his crisis and intro clips.
