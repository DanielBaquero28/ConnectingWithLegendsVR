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
| `thinking` | `{ "type": "adaptive" }` |
| `output_config.effort` | the character's `effort` (default `low`; `low`/`medium`/`high`/`xhigh`/`max`) |
| `inference_geo` | `CLAUDE_INFERENCE_GEO` env, default `us` (data residency; billed 1.1x) |
| `system` | persona + knowledge-cutoff note + `SAFETY_PREAMBLE` |
| `messages` | few-shots as user/assistant pairs, windowed text-only history, current user turn |

No `temperature`/`top_p`/`top_k` are sent (Haiku 5.5 rejects non-defaults).
Thinking blocks are never stored or resent: only text blocks become the reply,
and sessions store plain text.

Response handling: `stop_reason: "refusal"` → the existing in-character
`SAFETY_REDIRECT` + `safety_block` event; no text at all (e.g. `max_tokens`
spent on thinking) → an ops-only `llm_empty` event (no text) and a 502, so the
device shows its in-character network line.

Retries: 3 attempts, 15 s per attempt, 0.5 s / 1.5 s backoff, on network
errors, 429, 5xx and 529, honouring `retry-after` — under an **overall 12 s
deadline** across all attempts, so the server stops (and stops billing) before
the headset's 15 s timeout.

`PUT /admin/characters/:id` rejects a `modelName` that is not in the price map
in `src/metering.js` and an `effort` outside the documented values, so a bad
seed fails at seed time.

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
- **`events` snippets:** the only conversation text that lands in the
  `events` collection is the flagged snippet on `crisis`, `profanity`,
  `jailbreak`, and `safety_block` events. Safety events expire after
  **90 days**; ops events (e.g. `turn_ok`, which is metadata-only — durations
  and costs, zero conversation text) after **30 days**.
- **No student PII anywhere.** There are no student accounts or names.
  Device tokens identify headsets, not people; schools are identified by a
  name Daniel assigns.
- **No provider keys on the device.** The headset holds only the proxy URL
  and a per-headset revocable device token.
- Crisis messages are surfaced to a monitored Slack channel immediately so a
  human can follow the school's protocol.

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
Every seed carries `"modelName": "claude-haiku-5-5"` and `"effort": "low"`;
the PUT rejects any model not in the price map or an unknown effort.

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

## Migrating to the Claude API (Spec 12)

See the **Spec 12 cutover runbook** section below (Part I) — it is run by hand,
in order, and records the previous Cloud Run revision for rollback.

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
