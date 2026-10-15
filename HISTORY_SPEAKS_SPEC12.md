# History Speaks VR — Spec 12: LLM Provider Migration + Privacy & Crisis Fixes

## Why this spec exists

1. **Google's terms.** The Google Cloud Service Specific Terms and the Gemini API terms say customers will not use Generative AI Services in a service directed to, or likely to be accessed by, people under 18. Our users are students aged 13–19. This covers **every model served through Vertex**, including partner models (Claude on Vertex). So the LLM has to leave Google entirely.
2. **Model retirement.** `gemini-2.5-flash-lite` retires on Oct 20. We would have to migrate anyway.
3. **Privacy claims must be true.** Student text currently reaches logs and Slack. A recorded WAV stays on the headset. Deepgram's model-improvement opt-out is not set.
4. **Typed crisis gap.** A crisis phrase that is typed shows the local crisis message, but it creates no server event and no Slack alert.

**Decision:** use Anthropic Claude through the **Claude API directly** (`api.anthropic.com`), not through Vertex. The model is **`claude-haiku-5-5`**, with effort `low`. Sonnet 5.5 stays available as a per-character fallback if the eval in Part B shows Haiku falls short on a character.

## Do NOT touch

- `AudioRecorder.cs`, `WavUtility*.cs`
- SALSA setup, the animation controllers and avatar masks, `CharacterAnimationDriver`
- The structure of the ConversationRig prefab (wiring only where a part below says so)
- The Firestore schema, except where a part below says otherwise
- The crisis message wording and the crisis WAVs
- CC eye and hair materials

---

## Part 0 — Safety net and project review (before any edit)

1. Confirm you are on `develop`, the working tree is clean, and the tag `pre-spec12` exists. Daniel has already created all three; if any is missing, stop and tell him. Create the branch `spec12-claude-migration` from `develop` and do all work there. **Never commit to, merge into or push to `main`.** Daniel merges `spec12-claude-migration` into `develop` after the Quest tests pass, and `develop` into `main` himself.
2. Read `CLAUDE.md`, all of `server/src`, `server/scripts`, and every Unity script involved in: `ProxyClient`, `AIResponseControllerV2`, `VoiceRecognitionController`, `SpeechToText`, `SafetyFilter`, `PromptUIController`, `CharacterSession`/`CharacterRegistry`, plus the hub bootstrap.
3. Review as a senior full-stack Unity/Node engineer. **Make no edits.** Produce:
   - An impact map: every file each part (A–H) will touch and why.
   - Every call path that currently depends on Gemini/Vertex response shapes.
   - The risks you see (behavior that could break on the Quest), each with how you'll avoid it.
   - Anything in this spec that conflicts with the actual code. Flag it; don't silently adapt.
4. Wait for my approval before starting Part A.
5. Commit after each part, with a message naming the part, so any part can be reverted on its own. Run the offline smoke test before each commit.

---

## Part A — Claude provider (server)

Create **`server/src/providers/claude.js`** with the **same exported interface** as `server/src/providers/gemini.js`, so `converse.js` changes as little as possible.

**Request**
- `POST https://api.anthropic.com/v1/messages`
- Headers: `x-api-key: process.env.ANTHROPIC_API_KEY`, `anthropic-version` set to the value in the current Claude API docs (check the docs; don't guess), `content-type: application/json`.
- `model`: the character's `modelName`, defaulting to `claude-haiku-5-5`.
- `max_tokens: 600`. Thinking tokens count toward this limit. Our replies are about 40 words.
- `thinking: { type: "adaptive" }` and `output_config: { effort: character.effort || "low" }`.
- **Omit** `temperature`, `top_p` and `top_k`: Haiku 5.5 returns 400 for non-default values.
- Remove Gemini `generationConfig` and `safetySettings` entirely.
- `system`: persona + knowledge-cutoff instruction + the existing `SAFETY_PREAMBLE`, unchanged in content.
- `messages`: the few-shots as alternating `user`/`assistant` text turns, then the windowed **text-only** history, then the current user turn.
  - The array **must end with a user turn**. Assistant prefill returns 400.
  - Enforce strict alternation: merge consecutive same-role turns and drop a leading assistant turn.
- Data residency: if the current docs support a US-only inference parameter, add it behind env `CLAUDE_INFERENCE_GEO` (default `us`). If the docs don't support it, skip it and say so in the report.

**Response**
- The reply is the concatenated `text` of all content blocks with `type === "text"`.
- **Never store or resend thinking blocks.** History stays text-only.
- `stop_reason === "refusal"` → route to the **existing safety-block path** (same student-facing message, same `safety` event).
- `stop_reason === "max_tokens"` with no text → the existing failure fallback line.
- After the reply, the existing output safety filter still runs, unchanged.

**Retries and logging**
- Up to 3 attempts, 15 s timeout each, backoff of 0.5 s and then 1.5 s.
- Retry on network errors, 429, 5xx and 529. Honor `retry-after` if it is present.
- Log `claude.attempt_failed` with `{status, attempt, request_id}` **only**. Never log the request body, the reply text or the key.

**Metering**
- Use `usage.input_tokens` and `usage.output_tokens`.
- Price map (USD per million tokens): `claude-haiku-5-5` $0.10 in / $0.50 out; `claude-sonnet-5-5` $2.00 in / $10.00 out.
- Keep the existing per-school/month metering document shape.

**Seeds and admin**
- Every `server/scripts/seed-*.json` gets `"modelName": "claude-haiku-5-5"` and `"effort": "low"`.
- `PUT /admin/characters/:id` rejects any `modelName` that is not in the price map (400).

**Cleanup**
- Delete the Vertex/Gemini provider code, its env vars (`VERTEX_PROJECT_ID`, `VERTEX_LOCATION`, any `GEMINI_*`), its tests and its dependencies (`google-auth-library` only if nothing else uses it — Google TTS may).
- Update `.env.example` and `README.md`.
- Update `scripts/smoke.js` to mock the Anthropic endpoint. Cover these cases: a normal reply, `refusal`, `max_tokens` with no text, 529 then success, and a check that the messages array always ends with a user turn.

## Part B — Persona eval script

Create **`server/scripts/eval-personas.js`**. It runs locally with `ANTHROPIC_API_KEY` set and reads the seed files directly; it does not call the deployed proxy.

- There are 6 prompts per character:
  1. A curriculum question
  2. A follow-up to that question
  3. A break-character attempt ("you're just an AI, right?")
  4. Profanity bait
  5. A question about something after the character's era
  6. The hardest sensitive question for that figure (for example slavery for Lincoln, Caesar's war deaths in Gaul, Cleopatra's relationships kept age-appropriate)
- Configs:
  - `haiku-5-5@low`
  - `haiku-5-5@medium`
  - `sonnet-5-5@low`
  - If the docs allow turning thinking off for Haiku 5.5, add `haiku-5-5@no-thinking`.
- The output is `server/eval/report-<date>.md`. Each prompt × config row shows the reply, word count, latency in ms, `stop_reason`, input/output tokens and cost.
- Add a summary table per config with mean and p95 latency, mean words, the count of replies over 60 words, the refusal count and total cost.
- Run it on all seeded characters and commit the report.

## Part C — TTS: generative-voice guard

Google lists Chirp 3 HD as a generative feature, and that is a gray zone under the same under-18 clause. Until counsel confirms:

- In `providers/googleTts.js` and the admin PUT validation, reject any `voiceName` containing `Chirp` unless `ALLOW_GENERATIVE_TTS=true`. The default is `false`.
- Keep the Chirp handling code, which omits pitch/rate, so it can be switched back on.
- If a character's seed references a Chirp voice while the flag is off, fail loudly at seed time with a clear message.
- Daniel will recast **MLK, Lincoln and Caesar** to Neural2, Studio or WaveNet voices and update their seeds. Don't choose voices yourself; list the candidates the API returns for `en-US`, `en-GB` and `it-IT` in the report.

## Part D — Deepgram opt-out

- Add `mip_opt_out=true` to **every** Deepgram request's query string.
- Add a smoke-test assertion that the outgoing Deepgram URL contains it.

## Part E — No audio left on the headset

- In the **caller** (`VoiceRecognitionController` / `SpeechToText` / `ProxyClient` — wherever the WAV is read for upload), delete `audio_prompt.wav` in a `finally` block right after the bytes are read, whether the upload succeeds or fails.
- On app start (the hub scene's bootstrap), delete any leftover `audio_prompt.wav` in `Application.persistentDataPath`.
- Do **not** change `AudioRecorder` or `WavUtility`.

## Part F — No student text in logs

**Unity**
- Remove every `Debug.Log` / `LogWarning` / `LogError` that prints a transcript, typed text, the reply or the prompt. Wrapping it in `#if UNITY_EDITOR` is acceptable for dev-only diagnostics.
- Lengths and counts are fine, for example `[STT] transcript length=42`.

**Server**
- Add a logger guard that drops the keys `text`, `transcript`, `reply`, `snippet`, `prompt` and `message` (when the value is a string longer than 40 characters) from any structured log payload before it is written.
- Add a smoke test that logs an object with these keys and asserts they are absent from the output.

## Part G — Typed crisis reports reach us

**Unity**
- When `SafetyFilter`'s client pre-check catches a crisis phrase in **typed** input, keep the current local behavior (crisis message and crisis audio) unchanged.
- Also fire and forget `POST /crisis-report` with `{ sessionId, characterId, source: "typed" }`. Use `ProxyClient` auth (device token), with 3 retries and backoff. Never block the UI on it.
- **Don't send the student's text.** The alert only needs to say that a crisis was detected, where and when.

**Server**
- `POST /crisis-report` uses the existing device-token auth and writes a `crisis` event (90-day TTL, same as the other safety events). It sends the existing #hsvr-safety Slack alert.

**Slack payloads**
- Audit **every** Slack payload (safety, crisis, ops). Each payload contains only: the event type, school, headset label, character, timestamp and event ID.
- Remove any student text or reply snippet from Slack. Staff look up details in Firestore by event ID if needed.

## Part H — Report back (no changes, just facts)

Report these at the end:

1. Firestore database location.
2. Whether the `expiresAt` field on `sessions` and `events` is written as a **Firestore Timestamp**; TTL ignores strings and numbers. Show the code line.
3. The project's IAM bindings for the Cloud Run service account.
4. The `_Default` log bucket retention.
5. A grep confirming **no remaining calls** to `aiplatform.googleapis.com` or `generativelanguage.googleapis.com` anywhere in `server/` or `Assets/`.
6. Whether Part A's inference-geo parameter was supported.

## Part I — Deploy runbook (write it into README; Daniel runs it)

1. Create the secret: `ANTHROPIC_API_KEY` in Secret Manager, mounted as an env var on `hsvr-proxy`. A plain env var is acceptable if Secret Manager is not yet set up. Never put it in the repo, never in chat.
2. Deploy **without traffic** first:
   - `gcloud run deploy hsvr-proxy --source server/ --region us-east1 --no-traffic --tag spec12` (keep min-instances 1, max 10, concurrency 80).
   - Run the smoke checks against the tagged URL. Only then send traffic to it with `gcloud run services update-traffic hsvr-proxy --region us-east1 --to-latest`.
   - Rollback is one command: `gcloud run services update-traffic hsvr-proxy --region us-east1 --to-revisions <previous-revision>=100`. Write the previous revision name in the README before deploying.
3. Remove the `VERTEX_PROJECT_ID` and `VERTEX_LOCATION` env vars from the service.
4. Re-seed all characters (`PUT /admin/characters/:id` with each seed file, `--data "@file.json"`, `curl.exe` on Windows).
5. Check `/status`, then test Einstein, MLK, Cleopatra, Caesar and Lincoln on the Quest. For each one, run a normal question, typed crisis phrase → Slack alert with no text, and voice question → confirm no WAV left in persistentDataPath.
6. After 24 h of clean traffic:
   - Remove `roles/aiplatform.user` from the service account.
   - Disable the `aiplatform.googleapis.com` API.
   - Delete the "HS - Gemini API Key".

---

## Part J — AI disclosure for minors

- **Unity:** before each conversation starts, show a disclosure panel: "You're about to talk with an AI recreation of [Character]. It's a computer program, not the real person, and it can make mistakes." Show a Continue button; the conversation starts only after it's pressed. Show where it fits in the hub → character flow before building it.
- **SAFETY_PREAMBLE:** the character stays in character, with one exception. If the student sincerely asks whether they're talking to an AI or a real person, it answers honestly in one short sentence (it's an AI recreation made for learning), then continues in character.
- **safety.js:** a plain question like "are you an AI?" or "are you real?" is never logged or blocked as a jailbreak. Real manipulation attempts still are.
- Add the disclosure question to the Part B eval prompts.

## Part K — Data minimization

- Store `snippet` only on server-detected crisis events. Remove it from profanity, jailbreak and safety_block events.
- Change the retention of the four safety event types from 90 to 30 days. Ops events stay at 30.
- **SAFETY_PREAMBLE:** the character never asks for or encourages personal information (full name, school, address, contact details, social media). If the student volunteers it, the character doesn't repeat it and steers back to the topic.
- **README runbook:** add a step with the exact gcloud command for a log exclusion on the `_Default` sink that drops `run.googleapis.com/requests` logs for `hsvr-proxy`, so client IP addresses are not retained. Take the syntax from the current docs. Don't run it.

## Part L — Timeouts and metering

- Give `/converse` an overall server deadline of 20 s covering speech-to-text, the AI and the voice. If it's exceeded, stop the remaining work and return 504 with no safety event and no history write.
- Raise ProxyClient's request timeout to 25 s, and make sure the headset never automatically resends a `/converse` request after a timeout. It shows the existing in-character network line instead.
- Meter input tokens on refusal and `llm_empty` turns.

Finish with the green smoke test, a clean Unity batch compile, and a short summary. Don't run Part B and don't deploy.

---

## Part M — Anthropic minors safeguards

- **Disclosure panel:** add a line under the existing text: "By continuing, you confirm you're 13 or older."
- **"Report this answer" button** in the conversation UI, enabled only after a character reply has played. It calls a new `POST /report` with `{ sessionId, characterId }`, using device-token auth, fire-and-forget with retries, never blocking the UI. The device sends no text.
- **Server:** reads the last character reply from that session's document and writes a `user_report` event that includes that reply only, never student text. TTL 30 days. Send a Slack alert to #hsvr-safety in the same text-free format as other alerts. One report per character reply; ignore repeats.
- **Smoke test:** extend it to cover `/report`, including that no student text is ever stored on the event.

Then run Part B (all ten characters on every config), commit `server/eval/report-<date>.md`, and summarise per character and config: mean and p95 latency; mean word count and the count of replies over 60 words; refusals and empty replies; total cost; how each config handled the break-character, disclosure, post-era and sensitive-question prompts, with the single worst reply quoted per config. Then recommend a config per character.

---

## Kickoff (paste into Claude Code)

> Read `HISTORY_SPEAKS_SPEC12.md` in the repo root. Start with Part 0: branch, tag, full review and impact map, no edits, then stop for my approval. After that, implement Parts A–H in order, committing after each part. Show me your plan for each part before implementing it, and show each file change before moving on. Use the current Claude API docs for exact header values, the thinking/effort parameters and any data-residency parameter rather than guessing. Respect the do-not-touch list. Finish with a green offline smoke test, a clean Unity batch compile, the Part H report, and the README runbook for Part I. Do not run Part B against the live API until I confirm the key is set.
