# History Speaks VR — Backend Proxy

Node 20 + Express proxy on Google Cloud Run. The Quest headset makes **one
authenticated call per turn** (`POST /converse`, audio or text); this server
owns the provider credentials (Deepgram STT and Google TTS keys; the Gemini LLM
runs on **Vertex AI** authenticated by the Cloud Run service account — no key),
the character persona, safety checks, conversation history, cost metering, and
Slack alerting. Built from `HISTORY_SPEAKS_SPEC3.md`; the LLM was migrated from
the Gemini Developer API to Vertex AI per `HISTORY_SPEAKS_SPEC4.md`.

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
      gemini.js         LLM — Vertex AI, service-account auth (safetySettings, generationConfig, retry)
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
    seed-einstein.json  the full Einstein character doc
    smoke.js            in-process smoke test (mocked providers, fake Firestore)
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

Gemini is mocked here, so the smoke test needs no Vertex credentials or network.
The real end-to-end Vertex AI round-trip is verified on-device / against the
deployed service, not in this offline test.

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
  aiplatform.googleapis.com \
  texttospeech.googleapis.com
```

Then grant the Cloud Run runtime service account access to Vertex AI (the LLM
authenticates as this SA — see "Migrating to Vertex AI" below for the exact
project ID / SA values and IAM propagation note):

```bash
gcloud projects add-iam-policy-binding <PROJECT_ID> \
  --member="serviceAccount:<RUNTIME_SA>" \
  --role="roles/aiplatform.user"
```

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
  --set-env-vars "DEEPGRAM_API_KEY=<deepgram-key>,GOOGLE_TTS_API_KEY=<tts-key>,SLACK_WEBHOOK_SAFETY=<safety-webhook-url>,SLACK_WEBHOOK_OPS=<ops-webhook-url>,ADMIN_KEY=<generated-admin-key>"
```

There is no `GEMINI_API_KEY`: the LLM uses Vertex AI via the runtime service
account (granted `roles/aiplatform.user` in step 2). Cloud Run injects
`GOOGLE_CLOUD_PROJECT` automatically, which the Vertex call uses to build its
endpoint.

`--allow-unauthenticated` is correct: our own token middleware enforces auth
(invalid tokens get 401). Note the service URL the deploy prints — it is the
`BASE` below and the `proxyBaseUrl` in Unity.

### 6. Seed Einstein

```bash
BASE=https://hsvr-proxy-XXXXXXXX-ue.a.run.app   # from the deploy output
ADMIN=<generated-admin-key>

curl -X PUT "$BASE/admin/characters/einstein" \
  -H "X-Admin-Key: $ADMIN" \
  -H "Content-Type: application/json" \
  --data-binary @server/scripts/seed-einstein.json
```

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
- **Gemini:** simply **revoke** the old Developer-API key — it is no longer used
  at all (the LLM now runs on Vertex AI via the service account, with no key).
- **Deepgram and Google TTS:** **revoke and regenerate**; the new keys live only
  in Cloud Run env vars.

## Migrating to Vertex AI (Spec 4)

The LLM call moved from the Gemini Developer API
(`generativelanguage.googleapis.com`, billed via AI Studio prepay credits the
Cloud trial does not cover) to **Vertex AI** (`aiplatform.googleapis.com`,
billed as a normal Cloud service), authenticated by the Cloud Run runtime
service account instead of `GEMINI_API_KEY`. To migrate an existing deployment,
run these once, in order.

### 1. Grant the runtime service account Vertex access

```bash
PROJECT_ID=project-e8bab267-6dfb-401d-a39
RUNTIME_SA=697766265632-compute@developer.gserviceaccount.com

gcloud services enable aiplatform.googleapis.com --project "$PROJECT_ID"
gcloud projects add-iam-policy-binding "$PROJECT_ID" \
  --member="serviceAccount:$RUNTIME_SA" \
  --role="roles/aiplatform.user"
```

IAM propagation can take ~1 minute — wait before redeploying.

### 2. Redeploy the new code, then drop the old key var

Deploy the new code first (it no longer reads `GEMINI_API_KEY`), then remove the
now-unused env var — the order is harmless either way:

```bash
gcloud run deploy hsvr-proxy --source server/ --region us-east1
gcloud run services update hsvr-proxy --region us-east1 --remove-env-vars GEMINI_API_KEY
```

### 3. Verify

```bash
curl "$BASE/status"        # -> { "ok": true }
# On the Quest: ask a question -> expect text AND voice (Vertex reply + TTS).
curl "$BASE/admin/usage?schoolId=<schoolId>&month=$(date +%Y-%m)" -H "X-Admin-Key: $ADMIN"
# -> 200 with cost data; Gemini spend now appears under "Vertex AI" in Cloud billing.
```

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
