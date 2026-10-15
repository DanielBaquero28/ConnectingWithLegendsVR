# History Speaks VR — Backend Proxy

Node 20 + Express proxy on Google Cloud Run. The Quest headset makes **one
authenticated call per turn** (`POST /converse`, audio or text); this server
owns the provider credentials (Anthropic, Deepgram STT and Google TTS keys),
the character persona, safety checks, conversation history, cost metering, and
Slack alerting. Built from `HISTORY_SPEAKS_SPEC3.md`; the LLM moved from
Gemini/Vertex AI to the **Anthropic Claude API** (Claude Haiku 5.5, Sonnet 5.5
as a per-character fallback) per `HISTORY_SPEAKS_SPEC12.md`.

## Layout

```
server/
  src/
    index.js            Express app, routes, error handler
    auth.js             device-token + admin-key middleware
    converse.js         the /converse pipeline
    admin.js            /admin/* provisioning endpoints
    providers/
      deepgram.js       STT (Nova-3 pre-recorded)
      claude.js         LLM — Anthropic Messages API (adaptive thinking, effort, US inference_geo, retry + 12s deadline)
      googleTts.js      TTS (LINEAR16 24kHz, 200-entry LRU cache)
    safety.js           crisis + profanity + jailbreak checks (server copy)
    characters.js       character config (Firestore, 5-min in-memory cache)
    voice.js            voice capability rule (Chirp = no pitch/rate/SSML)
    sessions.js         conversation history (8-turn cap, 24h TTL)
    metering.js         per-request cost estimates + monthly aggregation
    alerts.js           Slack webhooks + throttling (state in Firestore)
    events.js           event logging with TTL retention fields
    db.js               Firestore singleton (swappable for tests)
    log.js              structured JSON logging
  scripts/
    seed-*.json         one full character doc per character (modelName + effort included)
    smoke.js            in-process smoke test (mocked STT/TTS, stubbed Anthropic endpoint, fake Firestore)
  Dockerfile
  .env.example
```

## API summary

| Route | Auth | Purpose |
|---|---|---|
| `GET /status` | none | uptime check |
| `POST /converse` | `Authorization: Bearer <deviceToken>` | one conversation turn (audio/wav or JSON text) |
| `POST /crisis-report` | `Authorization: Bearer <deviceToken>` | device-detected typed crisis: `{ sessionId, characterId, source: "typed" }`, no text (spec 12) |
| `POST /report` | `Authorization: Bearer <deviceToken>` | "Report this answer": `{ sessionId, characterId }`, no text; server attaches the last character reply (spec 12 Part M) |
| `POST /admin/schools` | `X-Admin-Key` | create school |
| `POST /admin/headsets` | `X-Admin-Key` | create headset, returns deviceToken |
| `POST /admin/headsets/:token/deactivate` | `X-Admin-Key` | kill-switch |
| `PUT /admin/characters/:id` | `X-Admin-Key` | upsert character (no app rebuild) |
| `GET /admin/usage?schoolId=&month=YYYY-MM` | `X-Admin-Key` | monthly cost/usage doc |
| `GET /admin/events?type=&schoolId=&limit=` | `X-Admin-Key` | recent safety/ops events |

`POST /converse` accepts either
`Content-Type: audio/wav` (raw 16kHz LINEAR16 WAV bytes, headers
`X-Session-Id`, `X-Character-Id`) or `Content-Type: application/json`
(`{ "sessionId": "...", "characterId": "einstein", "text": "..." }`) and
returns `{ transcript, replyText, audioBase64, emptyTranscript }`.

## Run locally

```bash
cd server
npm install
cp .env.example .env          # fill in keys (never commit .env)
gcloud auth application-default login   # Firestore credentials for local runs
node --env-file=.env src/index.js
curl http://localhost:8080/status      # -> { "ok": true }
```

## Smoke test (no GCP, no keys needed)

Runs the Express app in-process with mocked providers and an in-memory fake
Firestore:

```bash
cd server
npm run smoke
```

The Anthropic endpoint is a local fetch stub, so the smoke test needs no key or
network — but the **real** `claude.js` runs against it, so request headers,
body shape (strict user/assistant alternation, ending with the user turn),
refusal handling, the `max_tokens`-with-no-text path and 529 retries are all
exercised. The live round-trip is verified by `scripts/eval-personas.js`
(Part B of spec 12) and on-device.

## LLM (spec 12)

`src/providers/claude.js` calls `POST https://api.anthropic.com/v1/messages`
with `x-api-key: $ANTHROPIC_API_KEY` and `anthropic-version: 2023-06-01`.

| Field | Value |
|---|---|
| `model` | the character's `modelName` (default `claude-haiku-5-5`; `claude-sonnet-5-5` allowed) |
| `max_tokens` | 600 (thinking tokens count against it) |
| `thinking` | `{ "type": <character's thinking> }` — `"disabled"` (default, the production decision from the Part B eval) or `"adaptive"` |
| `output_config.effort` | the character's `effort` (default `low`; `low`/`medium`/`high`/`xhigh`/`max`). Still sent with thinking disabled — per the docs effort "works whether or not thinking is enabled". `xhigh`/`max` need `"adaptive"` (Haiku 5.5 returns 400 otherwise); the PUT rejects that combination. |
| `inference_geo` | `CLAUDE_INFERENCE_GEO` env, default `us` (data residency; billed 1.1x) |
| `system` | persona + knowledge-cutoff note + `SAFETY_PREAMBLE` |
| `messages` | few-shots as user/assistant pairs, windowed text-only history, current user turn **plus the length suffix** (spec 12 Part O: "(Reply in at most 40 words, in two or three sentences, ending with a short question when natural.)", appended in the request only — sessions store the student's text as sent) |

No `temperature`/`top_p`/`top_k` are sent (Haiku 5.5 rejects non-defaults).
Thinking blocks are never stored or resent: only text blocks become the reply,
and sessions store plain text.

Response handling: `stop_reason: "refusal"` → the existing in-character
`SAFETY_REDIRECT` + `safety_block` event; no text at all (e.g. `max_tokens`
spent on thinking) → an ops-only `llm_empty` event (no text) and a 502, so the
device shows its in-character network line.

Retries: 3 attempts, 15 s per attempt, 0.5 s / 1.5 s backoff, on network
errors, 429, 5xx and 529, honouring `retry-after` — under an **overall 12 s
deadline** across all attempts.

### Turn deadline (spec 12 Part L)

Every `/converse` request has one **20 s deadline covering STT, the LLM and
TTS**. Each provider call's timeout is capped to the time left; once the
deadline passes, remaining work is skipped and the device gets **504
`{ error: "Request timed out" }`** — no event is written, nothing is stored in
the session, nothing is metered and no alert fires (only a
`converse.deadline_exceeded` log line with the stage). The headset's request
timeout is 25 s, above the server's deadline, and the headset **never resends**
a turn after a timeout or a 504: it shows the in-character network line.

Turns that end in a model refusal or in `llm_empty` are metered (tokens and
STT seconds) even though no reply shipped — Claude bills them.

`PUT /admin/characters/:id` rejects a `modelName` that is not in the price map
in `src/metering.js`, an `effort` outside the documented values, a `thinking`
value other than `disabled`/`adaptive`, and `thinking: "disabled"` combined
with `xhigh`/`max` effort, so a bad seed fails at seed time.

### Length rule and reasoning-leak guard (spec 12 Part N)

- **Length.** `SAFETY_PREAMBLE` asks for "two or three short sentences, never
  more than 40 words". A reply at or under the character's `maxWords` (40)
  ships as is. A longer reply is cut at the **last complete sentence that
  keeps it at or under 50 words** (`maxWords + 10`). Only when there is no
  sentence boundary inside that allowance is it cut at exactly 50 words,
  with no ellipsis. Replies are never cut mid-sentence otherwise.
- **Few-shots and myths (spec 12 Part O).** Every few-shot reply in the
  seeds is 35 words or fewer (the smoke test checks), and every
  `systemPersona` ends with a "Common myths to avoid" line. The preamble
  adds: speak in the first person, never about yourself by name in the third
  person; repeat only well-documented history and flag disputed stories.
- **Era discipline.** The preamble also states that the character knows
  nothing after their death or knowledge cutoff, including honors, memorials,
  films, currency or anything named after them, and steers back to their own
  time when asked.
- **Reasoning-leak guard.** A reply that talks *about* the conversation or
  its instructions ("the student", "I should", "in character", "the user",
  "my instructions", "system prompt", …; `safety.hasMetaLeak`) is never
  spoken. The server retries the model once; if the retry leaks too, the
  device gets the existing `SAFETY_REDIRECT` line with `safetyRedirect: true`.
  One ops event `reply_meta_leak` is written per affected turn — metadata
  only (`characterId`, `modelName`, `leaks`, `outcome`), no text. Leaked
  calls are still metered.
- **`safetyRedirect`** is `true` on the crisis message and the redirect line,
  so the headset's "Report this answer" button stays disarmed for them.
  Older APKs ignore the field.

### Persona eval (spec 12 Part B)

`scripts/eval-personas.js` runs every seeded character through six prompts
(curriculum question, follow-up, break-character, profanity bait, post-era
question, hardest sensitive question) under four configs — `haiku-5-5@low`,
`haiku-5-5@medium`, `sonnet-5-5@low`, `haiku-5-5@no-thinking` — against the
live API, using the production request builder. It writes
`eval/report-<date>.md` with every reply, word count, latency, `stop_reason`,
tokens and cost, plus a per-config summary (mean/p95 latency, mean words,
counts over 40 and 60 words, refusals, total cost).

```bash
cd server
export ANTHROPIC_API_KEY=...          # local only, never committed
npm run eval                          # all seeds, all configs (~240 calls)
npm run eval -- --characters einstein,mlk --configs haiku-5-5@low
npm run eval -- --dry-run             # print the plan and a sample request, no calls
```

Commit the report. Replies are raw (the "shipped words" column shows what the
sentence-boundary length rule would deliver) so the report shows how
each config obeys the 40-word rule on its own.

## Voices (spec 10)

Each character's seed JSON carries a `voice` block
(`languageCode`, `voiceName`, `speakingRate`, `pitch`). How much of it is sent
depends on the **voice family**, decided in one place — `src/voice.js` — and
used by both the live proxy and the offline WAV renderer, so pre-rendered clips
always match live replies:

| Voice family | What is sent |
|---|---|
| `*Chirp*` (e.g. `en-US-Chirp3-HD-Charon`, MLK) | `audioEncoding` + `sampleRateHertz` **only** |
| Everything else (Neural2, Studio, WaveNet…) | the above **plus** `pitch` and `speakingRate` |

**Chirp 3: HD voices ignore — and can reject — `pitch`, `speakingRate`, and
SSML.** Keep both fields in every seed file anyway for schema consistency; for
a Chirp voice they are simply never sent (set them to `1.0` / `0.0` so nobody
mistakes them for tuned values). Input is always plain text; never send SSML.

Tuning a Chirp character therefore means choosing a different Chirp voice, not
adjusting rate/pitch. Two troubleshooting notes if a Chirp name is rejected:
the lowercase-locale spelling (`en-us-Chirp3-HD-Charon`) also appears in
Google's docs, and Chirp 3: HD is served from the global/us/eu endpoints, so an
availability error is a region matter rather than a code one.

### Generative voices are gated (spec 12 Part C)

Chirp 3: HD is a generative model with different data terms, so **any
`voiceName` containing `Chirp` is refused unless `ALLOW_GENERATIVE_TTS=true`**
(default: off). The rule lives in `src/voice.js` and is enforced in three
places:

| Where | With the flag off |
|---|---|
| `PUT /admin/characters/:id` | 400 — the seed is not written |
| `scripts/render-speech-wav.js` / `render-crisis-wav.js` | throws before rendering |
| live `googleTts.synthesize` | returns `failed: true, blocked: true`; the reply ships text-only and `tts.generative_voice_blocked` is logged with the voice name |

The Chirp handling above is kept intact for when the flag is on. MLK, Lincoln
and Caesar currently ship on Chirp voices and must be recast before the
spec 12 cutover (or the flag set): `scripts/list-voices.js` prints the
non-generative candidates for `en-US`, `en-GB`, `it-IT`, and the list captured
on 2026-10-14 is in `eval/voice-candidates-2026-10-14.md`.

```bash
GOOGLE_TTS_API_KEY=... node server/scripts/list-voices.js            # en-US en-GB it-IT
GOOGLE_TTS_API_KEY=... node server/scripts/list-voices.js en-GB --md  # Markdown table
```

### Re-rendering a character's pre-rendered clips

After changing a `voice` block, re-seed the character **and** re-render its
crisis + intro WAVs so the shipped audio matches the live voice:

```bash
# from the REPO ROOT, with GOOGLE_TTS_API_KEY set
node server/scripts/render-crisis-wav.js server/scripts/seed-mlk.json crisis-mlk.wav
node server/scripts/render-speech-wav.js server/scripts/seed-mlk.json intro-mlk.wav @server/scripts/intro-mlk.txt
```

## Data handling (COPPA posture — input for legal/DPA review)

- **Student audio is processed in memory only.** It is sent to the STT
  provider for transcription and discarded; it is never written to disk or
  storage and never logged, on the server or the device.
- **Full conversation text lives only in the `sessions` collection**, solely
  for conversation continuity, and **expires after 24 hours** (Firestore TTL
  on `expiresAt`, refreshed per turn; only the last 16 messages are kept).
- **`events` snippets (spec 12 Part K):** the only conversation text that
  lands in the `events` collection is the flagged snippet (≤300 chars) on a
  **server-detected `crisis`** event. `profanity`, `jailbreak`, `safety_block`,
  typed-crisis reports and every ops event (`turn_ok`, `provider_error`,
  `llm_empty`) are metadata-only. **Every event expires after 30 days.**
- **No student PII anywhere.** There are no student accounts or names.
  Device tokens identify headsets, not people; schools are identified by a
  name Daniel assigns.
- **No provider keys on the device.** The headset holds only the proxy URL
  and a per-headset revocable device token.
- Crisis messages are surfaced to a monitored Slack channel immediately so a
  human can follow the school's protocol. **Slack payloads carry no student
  text** (spec 12 Part G): every alert is limited to event type, school,
  headset label, character, timestamp and the Firestore event ID; staff open
  the `events` doc by ID for the flagged snippet. Session IDs are not posted.
- **Typed-path crises** caught by the headset's local filter are reported via
  `POST /crisis-report` with `{ sessionId, characterId, source: "typed" }` —
  the student's text never leaves the device — and produce the same `crisis`
  event (no snippet, 30-day TTL) and the same Slack alert.
- **"Report this answer" (spec 12 Part M):** the headset sends
  `POST /report` with `{ sessionId, characterId }` only. The server reads the
  **last character reply** from the session document and writes a
  `user_report` event whose `reply` field holds that reply and nothing else —
  no student turn is ever copied onto it (the smoke test asserts this). The
  event expires after 30 days and raises a `📣 REPORT` alert in #hsvr-safety
  with the same text-free payload as every other alert. One report is
  accepted per character reply; repeats are answered `200 { ignored:
  "duplicate" }` and write nothing.

## Deployment runbook (section 9 — run by hand)

### 1. Project setup

```bash
# Install the gcloud CLI: https://cloud.google.com/sdk/docs/install
gcloud auth login
gcloud projects create history-speaks-prod --name="History Speaks"
gcloud config set project history-speaks-prod
# Link billing (find the account ID with: gcloud billing accounts list)
gcloud billing projects link history-speaks-prod --billing-account=XXXXXX-XXXXXX-XXXXXX
```

### 2. Enable APIs

```bash
gcloud services enable \
  run.googleapis.com \
  cloudbuild.googleapis.com \
  artifactregistry.googleapis.com \
  firestore.googleapis.com \
  texttospeech.googleapis.com
```

The LLM is the Anthropic API (a key in an env var); no Google AI API or IAM
binding is needed for it.

### 3. Firestore (Native mode) + TTL

```bash
gcloud firestore databases create --location=us-east1

# TTL on expiresAt — deletes expired sessions (24h) and events (30/90 days):
gcloud firestore fields ttls update expiresAt --collection-group=sessions --enable-ttl
gcloud firestore fields ttls update expiresAt --collection-group=events --enable-ttl
```

### 4. Slack webhooks

1. In Slack, create channels `#hsvr-safety` and `#hsvr-ops`.
2. Create an app at https://api.slack.com/apps → **Incoming Webhooks** →
   activate → **Add New Webhook to Workspace**, once per channel.
3. Copy both webhook URLs for the deploy step.

### 5. Deploy to Cloud Run

```bash
# From the REPO ROOT. Generate ADMIN_KEY first:
node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"

gcloud run deploy hsvr-proxy \
  --source server/ \
  --region us-east1 \
  --allow-unauthenticated \
  --set-env-vars "ANTHROPIC_API_KEY=<anthropic-key>,CLAUDE_INFERENCE_GEO=us,DEEPGRAM_API_KEY=<deepgram-key>,GOOGLE_TTS_API_KEY=<tts-key>,SLACK_WEBHOOK_SAFETY=<safety-webhook-url>,SLACK_WEBHOOK_OPS=<ops-webhook-url>,ADMIN_KEY=<generated-admin-key>"
```

`ANTHROPIC_API_KEY` is the only LLM credential (create it in the Anthropic
Console under a workspace dedicated to this app). There is no Vertex/Gemini
configuration any more.

`--allow-unauthenticated` is correct: our own token middleware enforces auth
(invalid tokens get 401). Note the service URL the deploy prints — it is the
`BASE` below and the `proxyBaseUrl` in Unity.

### 6. Seed the characters

```bash
BASE=https://hsvr-proxy-XXXXXXXX-ue.a.run.app   # from the deploy output
ADMIN=<generated-admin-key>

for c in einstein mlk cleopatra caesar lincoln; do
  curl -X PUT "$BASE/admin/characters/$c" \
    -H "X-Admin-Key: $ADMIN" \
    -H "Content-Type: application/json" \
    --data-binary @server/scripts/seed-$c.json
done
```

On Windows use `curl.exe` and `--data "@server/scripts/seed-einstein.json"`.
Every seed carries `"modelName": "claude-haiku-5-5"`, `"effort": "low"` and
`"thinking": "disabled"` (spec 12 Part N); the PUT rejects any model not in
the price map, an unknown effort or thinking value, or thinking disabled at
`xhigh`/`max`.

### 7. Provision schools and headsets

```bash
# Internal dev school first, then real schools (e.g. Brunswick):
curl -X POST "$BASE/admin/schools" \
  -H "X-Admin-Key: $ADMIN" -H "Content-Type: application/json" \
  -d '{ "name": "Dev/Testing", "monthlyBudgetUsd": 20 }'
# -> { "schoolId": "..." }

curl -X POST "$BASE/admin/headsets" \
  -H "X-Admin-Key: $ADMIN" -H "Content-Type: application/json" \
  -d '{ "schoolId": "<schoolId from above>", "label": "Dev Quest 3 #1" }'
# -> { "deviceToken": "...", "headsetId": "hs_..." }
# RECORD THE TOKEN SOMEWHERE SAFE — it is shown once and is the device's credential.

# Lost/stolen headset kill-switch:
curl -X POST "$BASE/admin/headsets/<deviceToken>/deactivate" -H "X-Admin-Key: $ADMIN"

# The "dashboard":
curl "$BASE/admin/usage?schoolId=<schoolId>&month=$(date +%Y-%m)" -H "X-Admin-Key: $ADMIN"
curl "$BASE/admin/events?type=crisis&limit=20" -H "X-Admin-Key: $ADMIN"
```

### 8. Unity

In MainScene, select the controller object holding **ProxyClient** and set in
the Inspector:
- `Proxy Base Url` = the Cloud Run service URL (no trailing slash)
- `Device Token` = the dev headset's token from step 7

Build to Quest and run the section 10 test checklist from the spec.

### 9. Rotate ALL old API keys

Once the proxy is confirmed working on-device, kill every key that ever lived in
an Inspector field / scene file (they exist in git history):
- **Gemini:** delete the old Developer-API key ("HS - Gemini API Key") — nothing
  uses it; the LLM is now the Anthropic API.
- **Deepgram and Google TTS:** **revoke and regenerate**; the new keys live only
  in Cloud Run env vars.

## Spec 12 cutover runbook (Part I — run by hand, in order)

Facts recorded on 2026-10-14, before the cutover:

| | |
|---|---|
| Project | `project-e8bab267-6dfb-401d-a39` |
| Service / region | `hsvr-proxy` / `us-east1` |
| Service URL | `https://hsvr-proxy-gya3sp4npq-ue.a.run.app` |
| Runtime SA | `697766265632-compute@developer.gserviceaccount.com` |
| **Previous revision (rollback target)** | **`hsvr-proxy-00006-gj6`** (100% of traffic before the cutover) |
| Scaling | min-instances 1, max 10, concurrency 80 |
| Env vars present on the old revision | `DEEPGRAM_API_KEY`, `GOOGLE_TTS_API_KEY`, `SLACK_WEBHOOK_SAFETY`, `SLACK_WEBHOOK_OPS`, `ADMIN_KEY`, `VERTEX_PROJECT_ID` |

Shell variables used below (Windows: use `curl.exe` and `--data "@file"`):

```bash
PROJECT_ID=project-e8bab267-6dfb-401d-a39
BASE=https://hsvr-proxy-gya3sp4npq-ue.a.run.app
ADMIN=<admin key>
PREVIOUS_REVISION=hsvr-proxy-00006-gj6
```

### 0. Before deploying

- MLK, Lincoln and Caesar keep their Chirp voices until counsel answers, so
  step 2 sets `ALLOW_GENERATIVE_TTS=true` (spec 12 Part N decision). Recast
  candidates are in `eval/voice-candidates-2026-10-14.md` for later; once
  recast, drop the flag again. With the flag off, those three cannot be
  re-seeded and their live replies ship text-only.
- Run `npm run smoke` locally: must print `SMOKE PASSED`.
- Have the Anthropic key ready (Console → a workspace for this app).

### 1. Put the Anthropic key on the service

Secret Manager is the preferred home; a plain env var is acceptable.

```bash
# Option A — Secret Manager
gcloud services enable secretmanager.googleapis.com --project "$PROJECT_ID"
printf '%s' '<anthropic-key>' | gcloud secrets create ANTHROPIC_API_KEY --data-file=- --project "$PROJECT_ID"
gcloud secrets add-iam-policy-binding ANTHROPIC_API_KEY --project "$PROJECT_ID" \
  --member="serviceAccount:697766265632-compute@developer.gserviceaccount.com" \
  --role="roles/secretmanager.secretAccessor"
# The secret is attached in step 2 with --set-secrets.

# Option B — plain env var (attach in step 2 with --set-env-vars instead)
```

### 2. Deploy WITHOUT traffic, tagged `spec12`

```bash
# From the REPO ROOT, on branch spec12-claude-migration.
gcloud run deploy hsvr-proxy --source server/ --region us-east1 --project "$PROJECT_ID" \
  --no-traffic --tag spec12 \
  --min-instances 1 --max-instances 10 --concurrency 80 \
  --set-secrets "ANTHROPIC_API_KEY=ANTHROPIC_API_KEY:latest" \
  --update-env-vars "CLAUDE_INFERENCE_GEO=us,ALLOW_GENERATIVE_TTS=true" \
  --remove-env-vars VERTEX_PROJECT_ID
# ALLOW_GENERATIVE_TTS=true keeps the Chirp voices for MLK, Lincoln and Caesar
# (spec 12 Part N); remove it once those three are recast.
# Option B instead of --set-secrets:
#   --update-env-vars "ANTHROPIC_API_KEY=<anthropic-key>,CLAUDE_INFERENCE_GEO=us,ALLOW_GENERATIVE_TTS=true"
```

The command prints a tagged URL of the form
`https://spec12---hsvr-proxy-gya3sp4npq-ue.a.run.app`. Live traffic still goes
to `$PREVIOUS_REVISION`.

### 3. Smoke the tagged revision

```bash
TAGGED=https://spec12---hsvr-proxy-gya3sp4npq-ue.a.run.app
curl "$TAGGED/status"                     # -> { "ok": true }

# Re-seed every live character against the TAGGED URL (new modelName/effort/thinking).
for c in einstein mlk cleopatra caesar lincoln; do
  curl -X PUT "$TAGGED/admin/characters/$c" -H "X-Admin-Key: $ADMIN" \
    -H "Content-Type: application/json" --data-binary @server/scripts/seed-$c.json
done
# Each must return { "ok": true }. A 400 names the problem (unpriced model,
# bad effort/thinking, or a Chirp voice while ALLOW_GENERATIVE_TTS is off).

# One real turn through Claude + TTS with a dev headset token:
curl -X POST "$TAGGED/converse" -H "Authorization: Bearer <dev deviceToken>" \
  -H "Content-Type: application/json" \
  -d '{ "sessionId": "runbook-0001", "characterId": "einstein", "text": "What is light?" }'
# -> 200 with replyText and non-empty audioBase64.

# Typed-crisis report path:
curl -X POST "$TAGGED/crisis-report" -H "Authorization: Bearer <dev deviceToken>" \
  -H "Content-Type: application/json" \
  -d '{ "sessionId": "runbook-0001", "characterId": "einstein", "source": "typed" }'
# -> 202 { ok, eventId } and a 🚨 CRISIS (typed) alert in #hsvr-safety with no text.

# "Report this answer" path (spec 12 Part M) — reports the reply from the turn above:
curl -X POST "$TAGGED/report" -H "Authorization: Bearer <dev deviceToken>" \
  -H "Content-Type: application/json" \
  -d '{ "sessionId": "runbook-0001", "characterId": "einstein" }'
# -> 202 { ok, eventId } and a 📣 REPORT alert with no text; the user_report
#    event's `reply` field holds the character's reply only. Run it a second
#    time: -> 200 { ok, ignored: "duplicate" } and no new event.
```

Characters re-seeded here are shared by both revisions (same Firestore), so do
step 4 promptly: the old revision ignores `effort` and would try the Claude
model name on Vertex — it keeps its own cached docs for up to 5 minutes.

### 4. Switch traffic

```bash
gcloud run services update-traffic hsvr-proxy --region us-east1 --project "$PROJECT_ID" --to-latest
curl "$BASE/status"
```

Then on a Quest: talk to Einstein, MLK, Cleopatra, Caesar and Lincoln — text
AND voice for each (MLK/Lincoln/Caesar will be text-only if still on Chirp
with the flag off). Check `#hsvr-ops` for 🔥 ERRORS and
`$BASE/admin/events?type=llm_empty` for empty-reply events.

### 5. Rollback (if needed)

```bash
gcloud run services update-traffic hsvr-proxy --region us-east1 --project "$PROJECT_ID" \
  --to-revisions "$PREVIOUS_REVISION=100"
# The old revision expects Gemini seeds: re-seed with
#   git show pre-spec12:server/scripts/seed-<c>.json
# for each character if you roll back for more than a few minutes.
```

### 6. After 24 h of clean traffic — remove the Vertex/Gemini leftovers

```bash
gcloud projects remove-iam-policy-binding "$PROJECT_ID" \
  --member="serviceAccount:697766265632-compute@developer.gserviceaccount.com" \
  --role="roles/aiplatform.user"
gcloud services disable aiplatform.googleapis.com --project "$PROJECT_ID"
gcloud services disable generativelanguage.googleapis.com --project "$PROJECT_ID"   # also still enabled
# Console → APIs & Services → Credentials: delete "HS - Gemini API Key".
```

### 7. Stop retaining client IP addresses (spec 12 Part K)

Cloud Run writes a platform request log (`run.googleapis.com/requests`) for
every call, and its `httpRequest` block carries the headset's public IP. Our
code never reads it, but the `_Default` log bucket keeps it for 30 days. Add
an exclusion on the `_Default` sink so those entries are dropped before they
are stored (syntax from the current `gcloud logging sinks update` reference:
`--add-exclusion=[description=…],[disabled=…],[filter=…],[name=…]`):

```bash
gcloud logging sinks update _Default --project "$PROJECT_ID" \
  --add-exclusion="name=drop-hsvr-proxy-request-logs,description=Spec 12 Part K: do not retain Cloud Run request logs (client IPs) for hsvr-proxy,filter=logName=\"projects/$PROJECT_ID/logs/run.googleapis.com%2Frequests\" AND resource.type=\"cloud_run_revision\" AND resource.labels.service_name=\"hsvr-proxy\""

# Verify:
gcloud logging sinks describe _Default --project "$PROJECT_ID" --format="yaml(exclusions)"
```

Only the platform request log is excluded. Our own structured stdout logs
(`run.googleapis.com/stdout`) are unaffected, and they carry no IPs and no
conversation text. Error Reporting and the 🟥 5xx alert are driven by our own
logs and the error handler, not by the request log.

### 8. Housekeeping

- Merge `spec12-claude-migration` into `develop` (never directly into `main`).
- Re-render the intro WAV for any recast character
  (`scripts/render-speech-wav.js`); crisis WAVs are out of scope for spec 12.

## Operational notes

- **Costs are estimates** for monitoring, not billing-grade. Prices live in
  one constants object in `src/metering.js` — re-verify them periodically.
- **Budget alerts, not cutoffs:** at 80%/100% of a school's monthly budget a
  💰 alert fires once per month in `#hsvr-ops`; requests keep being served
  (classrooms are never cut off mid-lesson) and the usage doc is marked
  `overBudget`.
- **Alert throttles** (state in `alertState/`, survives restarts): 🚨 CRISIS
  never throttled; ⚠️ PATTERN ≥5 profanity/jailbreak events per headset in
  30 min, 60-min cooldown; 🔥 provider errors and 🟥 server 5xx ≥3 in 5 min,
  15-min cooldown.
- `GET /admin/events` with both `type` and `schoolId` filters may ask for a
  Firestore composite index — the error message contains a link that creates
  it in one click.
- Character changes (`PUT /admin/characters/einstein`) are picked up by other
  running instances within 5 minutes (in-memory cache TTL); no app rebuild.
- **LLM cost** is computed from the token counts Claude reports (not chars/4),
  at the prices in `src/metering.js`, times 1.1 when `inference_geo` is `us`.
  The usage doc keeps the `llmInputChars`/`llmOutputChars` counters and adds
  `llmInputTokens`/`llmOutputTokens`.
