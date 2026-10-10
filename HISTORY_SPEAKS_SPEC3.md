# History Speaks VR — Spec 3: Backend Proxy, Monitoring & Alerts

This phase moves all AI provider calls behind a server you control, adds per-school/per-headset identity, cost metering, safety monitoring, and Slack alerting, and updates the Unity client to talk to the proxy. It is a **build → deploy → test arc**: Claude Code builds everything; deployment steps (section 9) are done by Daniel by hand.

---

## 1. Context

Current state: the Quest app calls Deepgram (STT), Gemini (LLM), and Google TTS (voice) directly, with API keys in Inspector `SerializeField`s that serialize into the scene file. Safety (persona preamble, Gemini safetySettings, crisis filter, profanity-resistant persona) and an 8-turn context cap run on-device. This works, but keys ship in the build, nothing is attributable to a school or headset, there is no cost visibility, and improving the character requires rebuilding the app.

Target state: the headset makes **one authenticated call per turn** to our server. The server owns keys, persona, safety, history, cost metering, logging, and alerting.

## 2. Architecture decisions (locked)

- **Server:** Node.js 20 + Express, deployed on **Google Cloud Run** (project stays all-GCP).
- **Data:** **Firestore** (Native mode) for schools, headsets, sessions, usage, and events.
- **Identity:** school → headset. Each headset gets an opaque **device token**. No student accounts, no student PII. Headsets are anonymous devices.
- **Single conversation endpoint:** `POST /converse` accepts audio (voice path) or text (typed path), returns `{ transcript, replyText, audioBase64 }`. One round trip from the device.
- **Character config lives server-side** (persona, cutoff note, few-shots, voice params, model name, max words). Device sends only a `characterId`. Updating a character requires no app rebuild.
- **Safety is authoritative server-side.** The Unity `SafetyFilter` stays as a fast client pre-check (defense in depth), but the server re-checks everything.
- **Alerts via Slack incoming webhooks**, two channels: safety and ops.
- **No dashboard this phase.** All data lands in Firestore in dashboard-ready shape.

## 3. Repository layout

Create a new top-level folder `server/` in this repo (its own `package.json`; nothing in it referenced by Unity):

```
server/
  src/
    index.js            // Express app, routes
    auth.js             // device-token + admin-key middleware
    converse.js         // the /converse pipeline
    providers/
      deepgram.js       // STT
      gemini.js         // LLM (safetySettings, generationConfig, retry)
      googleTts.js      // TTS
    safety.js           // crisis + profanity + jailbreak checks (server copy)
    characters.js       // character config loading (Firestore w/ in-memory cache)
    sessions.js         // conversation history (Firestore, 8-turn cap, TTL)
    metering.js         // per-request cost estimation + monthly aggregation
    alerts.js           // Slack webhooks, throttling rules
    events.js           // event logging (safety/ops) with retention fields
  package.json
  Dockerfile            // or rely on Cloud Run buildpacks; keep it simple
  .env.example          // documented env vars, no real values
  README.md             // run locally, deploy, provision — generated from section 9
```

## 4. Server specification

### 4.1 Environment variables (all secrets via env, never in code)
`GEMINI_API_KEY`, `DEEPGRAM_API_KEY`, `GOOGLE_TTS_API_KEY`, `SLACK_WEBHOOK_SAFETY`, `SLACK_WEBHOOK_OPS`, `ADMIN_KEY` (long random string for admin endpoints), `PORT` (Cloud Run provides).

### 4.2 Auth
- Device requests: `Authorization: Bearer <deviceToken>`. Look up `headsets/{token}`; must exist and have `active: true`. Attach `{ schoolId, headsetId }` to the request. 401 otherwise.
- Admin requests: header `X-Admin-Key` must equal `ADMIN_KEY`. 401 otherwise.

### 4.3 Firestore collections
- `schools/{schoolId}`: `{ name, monthlyBudgetUsd, active, createdAt }`
- `headsets/{deviceToken}`: `{ schoolId, label, active, createdAt }`  // token is the doc ID; generate with crypto.randomUUID twice joined, or 48 random hex chars
- `characters/{characterId}`: `{ displayName, systemPersona, knowledgeCutoffNote, fewShotExamples: [{studentSays, characterReplies}], voice: { languageCode, voiceName, speakingRate, pitch }, modelName, maxWords, maxTurns }`
- `sessions/{sessionId}`: `{ schoolId, headsetId, characterId, turns: [{role, text}], updatedAt, expiresAt }`  // enable Firestore TTL on `expiresAt`, set 24h ahead on each write; keep only the last `maxTurns*2` messages
- `usage/{schoolId}/months/{YYYY-MM}`: `{ totalCostUsd, requestCount, sttSeconds, llmInputChars, llmOutputChars, ttsChars, perHeadset: { [headsetId]: { costUsd, requestCount } }, thresholdsFired: { warn80: bool, limit100: bool } }`  // update with Firestore increments/transaction
- `events/{autoId}`: `{ type, severity, schoolId, headsetId, sessionId, snippet?, detail?, createdAt, expiresAt }`  // TTL retention: safety events 90 days, ops events 30 days

### 4.4 `POST /converse` pipeline
Accepts either:
- `Content-Type: audio/wav` with raw WAV bytes (16kHz LINEAR16, what the device already produces), headers `X-Session-Id`, `X-Character-Id` (default `einstein`); or
- `Content-Type: application/json` `{ sessionId, characterId, text }` for the typed path.

Steps:
1. Auth (4.2). Resolve school + headset.
2. **Budget gate:** if the school's `limit100` has fired this month, still serve the request (do not cut off a classroom mid-lesson) but mark the usage doc `overBudget: true` — the Slack alert (4.7) is the enforcement mechanism for now. (Hard cutoff is a business decision for later.)
3. If audio: send bytes to Deepgram Nova-3 (`smart_format=true&punctuate=true`), get transcript. **Audio is processed in memory only — never written to disk or storage, never logged.** If transcript is empty, return 200 with `{ transcript: "", replyText: "", audioBase64: "" }` and an `emptyTranscript: true` flag (device shows its existing "didn't catch that" handling).
4. **Server safety pre-check** on the user text (`safety.js`):
   - **Crisis** (port the Unity phrase list verbatim): do NOT call the LLM. Return the crisis message (constant in `safety.js`, same wording as Unity) with TTS audio, log `events` type `crisis` severity `critical` **including the flagged text snippet**, fire the safety Slack alert immediately (4.7). Do not write the user turn into the session.
   - **Profanity/abuse** (small conservative list) and **jailbreak markers** (e.g. "ignore your instructions", "pretend you are not"): these do NOT block — the persona handles them in character — but log an `events` type `profanity` / `jailbreak` severity `info` with snippet, and feed the pattern counter (4.7).
5. Load character config (in-memory cache, 5-min TTL). Load session turns. Build the Gemini request exactly as the Unity client does today: system_instruction = persona + cutoff + SAFETY_PREAMBLE (move the preamble constant server-side), few-shots prepended as turns, then last `maxTurns` live turns, then the user text; same `safetySettings` (sexually-explicit BLOCK_LOW_AND_ABOVE, others BLOCK_MEDIUM_AND_ABOVE); same `generationConfig` (`thinkingBudget: 0`, `maxOutputTokens: 100`, `temperature: 0.7`); `modelName` from character config.
6. Retry policy (port from Unity): up to 3 attempts, 15s timeout, backoff 0.5s/1.5s, retry only on network errors/5xx. On safety-block (`finishReason: SAFETY` / `promptFeedback.blockReason` / missing text): return the in-character redirect line, log `events` type `safety_block`, do NOT store the user turn.
7. Post-process reply: word-truncate to `maxWords`, strip markdown chars (port `SanitizeResponse`).
8. TTS via Google (`en-GB-Neural2-B` etc. from character voice config, LINEAR16, explicit sampleRateHertz 24000). Server-side TTS cache: in-memory LRU (200 entries) keyed on text+voice.
9. Persist: append user+model turns to the session (cap, TTL); meter costs (4.5); write an `events` type `turn_ok` severity `debug` WITHOUT any conversation text (metadata only: durations, costs) — flagged snippets are the only student text that lands in `events`.
10. Respond `{ transcript, replyText, audioBase64 }` (WAV bytes base64-encoded; clips are ~5s, size is fine).

### 4.5 Cost metering (`metering.js`)
Estimate per request and increment the monthly usage doc transactionally:
- STT: audio seconds × ($0.0043/60) per second equivalent (Deepgram Nova-3 pre-recorded, billed per second).
- LLM: estimate tokens as chars/4. Price per `modelName` from a small config map (flash-lite input $0.10/M, output $0.40/M; flash input $0.30/M, output $2.50/M) — keep prices in one constants object with a comment to re-verify periodically.
- TTS: characters × $16/1M (cache hits cost $0 — skip metering on hit).
These are estimates for monitoring, not billing-grade. Store the per-request total on the event metadata too.

### 4.6 Admin endpoints (all behind `X-Admin-Key`)
- `POST /admin/schools` `{ name, monthlyBudgetUsd }` → creates school, returns `schoolId`.
- `POST /admin/headsets` `{ schoolId, label }` → creates headset, returns the `deviceToken`.
- `POST /admin/headsets/:token/deactivate` → sets `active: false` (lost/stolen headset kill-switch).
- `PUT /admin/characters/:characterId` body = full character doc → upsert (this is how Einstein gets seeded and updated).
- `GET /admin/usage?schoolId=&month=YYYY-MM` → returns the usage doc (the "dashboard" until there is one).
- `GET /admin/events?type=&schoolId=&limit=` → recent events.
- Include `server/scripts/seed-einstein.json` containing the full Einstein character doc (persona, cutoff, the 3 few-shots, voice `en-GB`/`en-GB-Neural2-B`/rate `0.72`/pitch `-8.0`, `modelName: gemini-2.5-flash-lite`, `maxWords: 40`, `maxTurns: 8`) and a curl example in the README to PUT it.

### 4.7 Slack alerts (`alerts.js`)
Two webhooks. Every alert includes school name, headset label, and timestamp. Throttling state kept in Firestore (`alertState` docs) so it survives instance restarts.
- **`SLACK_WEBHOOK_SAFETY`** — `🚨 CRISIS` (immediate, never throttled; include the flagged snippet and sessionId). `⚠️ PATTERN` when one headset accrues ≥5 profanity/jailbreak events within 30 minutes (then 60-min cooldown per headset) — individual teenage one-offs do NOT alert, only patterns.
- **`SLACK_WEBHOOK_OPS`** — `💰 BUDGET` at 80% and 100% of a school's `monthlyBudgetUsd` (each fires once per school per month, tracked via `thresholdsFired`). `🔥 ERRORS` when ≥3 provider failures (post-retry) occur within 5 minutes (15-min cooldown). `🟥 5xx` if the server itself throws unhandled errors (≥3 in 5 min, 15-min cooldown).
Alert sends must never break the request path: fire-and-forget with their own try/catch.

### 4.8 Data protection (COPPA posture — implement exactly)
- Student audio: in memory only, never persisted, never logged.
- Full conversation text: lives only in `sessions` (24h TTL) for context continuity.
- `events` snippets: only for crisis/profanity/jailbreak/safety_block flags; safety events expire at 90 days, ops at 30 (Firestore TTL on `expiresAt`).
- No names, no student identifiers anywhere. Tokens identify devices, not people.
- Document all of this in `server/README.md` under "Data handling" — this section is the input for legal/DPA review.

### 4.9 Misc
- `GET /healthz` → 200 `{ ok: true }` (for uptime checks).
- Structured JSON logging via console (Cloud Run captures it).
- Reasonable request size limit (e.g. 5 MB) — a 12s 16kHz WAV is ~380 KB.

## 5. Unity client changes

### 5.1 New `ProxyClient.cs`
A single MonoBehaviour with `[SerializeField] private string proxyBaseUrl;` and `[SerializeField] private string deviceToken;` exposing two coroutines:
- `ConverseWithAudio(string wavPath, string sessionId, Action<ConverseResponse> onDone, Action onFail)` — POSTs raw WAV bytes, `Content-Type: audio/wav`, `Authorization: Bearer`, `X-Session-Id`, `X-Character-Id: einstein`.
- `ConverseWithText(string text, string sessionId, ...)` — POSTs JSON.
`ConverseResponse` parsed with JsonUtility: `{ string transcript; string replyText; string audioBase64; bool emptyTranscript; }`. Same retry shape as today (3 attempts, 15s timeout, 0.5/1.5 backoff, in-character network-failure line on final failure).

### 5.2 Rewiring (and what gets removed)
- `VoiceRecognitionController`: after `SaveWavFile`, call `ProxyClient.ConverseWithAudio` instead of `SpeechToText.Recognize`. On response: put `transcript` into the input field (display), show `replyText`, play the audio.
- `AIResponseControllerV2`: becomes the conversation orchestrator only — keeps `isProcessing` locking, the client-side `SafetyFilter` pre-check (on crisis, show the local crisis message and do NOT call the proxy — identical UX, zero latency), session-ID management (`Guid.NewGuid().ToString()` at start and on `ResetConversation`), and display. **Remove**: Gemini URL/key/payload building, safetySettings, persona/few-shot assembly, JSON escaping for Gemini, truncation/sanitize (server does it), and the direct TTS call for AI replies.
- `TextToSpeechGoogle`: no longer called for AI replies (server returns audio). Keep the file but it should no longer hold a key requirement for the main flow. Playback of returned audio: decode base64 → reuse the existing `PlayAudioFromBytes` logic (move it into a small static `AudioPlaybackUtility` or onto ProxyClient — implementer's choice, but do not duplicate the code).
- `SpeechToText.cs`: no longer used in the main flow. Delete its usage; the file may be removed entirely (git history preserves it).
- `CharacterProfile` ScriptableObject: no longer drives requests (server owns character). Keep the asset/class for now (future local display needs) but remove its consumption from the request path.
- **No API keys remain anywhere in the Unity project** — Inspector fields for Gemini/Deepgram/TTS keys are deleted. The only secrets on-device: `proxyBaseUrl` (not secret) and `deviceToken` (per-headset, revocable).

### 5.3 UI fix — prompt field clearing (Daniel's request)
When a response is successfully received and displayed (both typed and voice paths), clear `PromptField.text` so the student can immediately type or ask the next question without erasing the previous one. Sequence: show `replyText`, start audio playback, then clear the field. Ensure the `PromptUIController` empty-field rule naturally re-greys the Submit button after the clear (it should, via onValueChanged — verify, don't duplicate logic). On failure paths (network fail, empty transcript), do NOT clear — the student keeps their text and can retry.

## 6. What must NOT change
`AudioRecorder`, `WavUtility`, the recording logic (12s cap / 4s silence), the request-lock pattern, the client `SafetyFilter` behavior, and the on-device crisis UX.

## 7. Build order for Claude Code (one session, in this order)
1. Server scaffold + auth + Firestore models.
2. `/converse` text path end-to-end (mock-friendly: providers behind small modules).
3. `/converse` audio path.
4. Safety, metering, events, alerts.
5. Admin endpoints + Einstein seed file + README (run-local + deploy + provision instructions per section 9).
6. Unity: ProxyClient + rewiring + key-field removal + prompt-field clear.
7. Unity batch compile must pass. For the server: `npm install` must succeed and include a tiny smoke test (`node --test` or a `scripts/smoke.js` hitting the express app in-process with mocked providers) that passes.

## 8. Out of scope (explicitly)
Web dashboard; hard budget cutoffs; Gemini context caching (next optimization pass, once the proxy is stable); school-facing crisis escalation workflow (alerts go to Daniel's Slack only for now); streaming/continuous STT.

## 9. Deployment runbook (Daniel, by hand — Claude Code writes this into server/README.md with exact commands)
1. Install gcloud CLI; `gcloud auth login`; create project (e.g. `history-speaks-prod`); link billing.
2. Enable APIs: Cloud Run, Firestore, Generative Language, Cloud Text-to-Speech.
3. Create Firestore database (Native mode) + enable TTL on the `expiresAt` field for `sessions` and `events`.
4. Create two Slack incoming webhooks (channels `#hsvr-safety`, `#hsvr-ops`); copy URLs.
5. Deploy: `gcloud run deploy hsvr-proxy --source server/ --region us-east1 --allow-unauthenticated` with env vars set (`--set-env-vars` for keys, webhooks, ADMIN_KEY). (Auth is enforced by our token middleware; Cloud Run-level auth stays open.)
6. Seed Einstein: curl `PUT /admin/characters/einstein` with `seed-einstein.json`.
7. Provision: create the first school (e.g. internal "Dev/Testing" school, then Brunswick) and one headset token per device via the admin endpoints; record tokens somewhere safe.
8. Unity: set `proxyBaseUrl` + the dev headset's `deviceToken` in the Inspector; build to Quest; run section 10 tests.
9. **Rotate ALL old API keys** (Gemini, Deepgram, Google TTS) in their consoles once the proxy is confirmed working — the old keys lived in code/scene files in git history and must die.

## 10. Test checklist (after deploy)
- Typed question through proxy → correct reply, voice plays, prompt field clears, Submit re-greys.
- Voice question → transcript appears, reply + audio play, field clears after response.
- Invalid/deactivated token → device shows the in-character network-failure line; server returns 401.
- Crisis phrase (typed) → local client filter catches it instantly (no proxy call). Crisis phrase via voice → server catches it, crisis message + audio returned, `🚨 CRISIS` lands in #hsvr-safety with school/headset/snippet.
- Say "ignore your instructions" 5+ times within 30 min → exactly one `⚠️ PATTERN` alert.
- Set the dev school's `monthlyBudgetUsd` to $0.01 → next request fires `💰 BUDGET` (80% and 100%) once each.
- Temporarily set a wrong Deepgram key in Cloud Run env → 3 voice attempts within 5 min → one `🔥 ERRORS` alert; restore key.
- `GET /admin/usage` for the dev school shows costs accumulating per headset.
- Firestore: confirm `sessions` carry `expiresAt`; confirm no audio fields anywhere; events snippets only on flagged types.
- Full 10-turn conversation: context continuity works (server-side history), latency feels same or better than before.

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC3.md in the repo root and implement it in the build order given in section 7, showing me your plan for each numbered step before implementing it. The server is a new `server/` folder (Node 20 + Express, per section 3–4). Then make the Unity client changes in section 5 exactly, respecting section 6 (do not touch AudioRecorder, WavUtility, the recording logic, the request lock, or the client SafetyFilter behavior). Finish with a passing Unity batch compile and a passing server smoke test, and generate server/README.md including the section 9 runbook with exact commands.
