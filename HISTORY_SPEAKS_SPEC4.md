# History Speaks VR — Spec 4: Migrate Gemini to Vertex AI

Surgical change. The server currently calls the **Gemini Developer API** (`generativelanguage.googleapis.com?key=GEMINI_API_KEY`), which bills through AI Studio prepay credits that the project's $300 Cloud trial does NOT cover — causing `429 RESOURCE_EXHAUSTED "prepayment credits depleted"`. This migrates the LLM call to **Vertex AI** (`aiplatform.googleapis.com`), which bills as a normal Google Cloud service (covered by Cloud billing/credits) and authenticates via the Cloud Run service account instead of an API key.

**Scope is one module + cleanup.** Do not touch STT (Deepgram), TTS (Google TTS — still uses its own key, unaffected), safety, metering, sessions, alerts, admin, or any Unity code.

## Decisions (locked)
- **Transport:** `google-auth-library` + REST `fetch` (NOT the `@google-cloud/vertexai` SDK). Minimal new dependency; we keep the existing request/response handling style.
- **Auth:** Application Default Credentials via the Cloud Run service account (no API key). `google-auth-library` mints an access token; send it as `Authorization: Bearer <token>`.
- **Region:** `us-east1` (matches Cloud Run + Firestore).
- **Clean cutover:** remove `GEMINI_API_KEY` and the old Developer-API code path entirely. No fallback.

## 1. Dependency
Add `google-auth-library` to `server/package.json` (latest stable). No other new deps. (`google-auth-library` is commonly already transitively present via `@google-cloud/firestore`, but declare it explicitly.)

## 2. Rewrite `server/src/providers/gemini.js`

Keep the module's **public interface identical** so `converse.js` needs no changes — same exported function name, same arguments, same return shape (the reply text, plus whatever finishReason/safety signal it returns today). Only the internals change.

### 2.1 Endpoint & request
- Base URL (Vertex generateContent, regional):
  ```
  https://us-east1-aiplatform.googleapis.com/v1/projects/{PROJECT_ID}/locations/us-east1/publishers/google/models/{MODEL}:generateContent
  ```
- `PROJECT_ID` comes from env `GOOGLE_CLOUD_PROJECT` (Cloud Run sets this automatically; also accept an explicit `VERTEX_PROJECT_ID` override, default to `GOOGLE_CLOUD_PROJECT`).
- `MODEL` from the character config's `modelName` (currently `gemini-2.5-flash-lite`). On Vertex the bare model id (`gemini-2.5-flash-lite`) is correct in this path — do not prepend anything.
- **Request body is nearly identical to today's**, with one schema difference: Vertex uses `system_instruction` as well, and the same `contents`, `safetySettings`, and `generationConfig` shapes carry over. Keep:
  - `system_instruction` (persona + cutoff + SAFETY_PREAMBLE) — unchanged.
  - `contents` (few-shots prepended + windowed history + user turn) — unchanged.
  - `safetySettings` — same four categories/thresholds (sexually-explicit BLOCK_LOW_AND_ABOVE, others BLOCK_MEDIUM_AND_ABOVE). Vertex accepts these same enum strings.
  - `generationConfig`: `thinkingConfig.thinkingBudget: 0`, `maxOutputTokens: 100`, `temperature: 0.7` — unchanged.

### 2.2 Auth (the core change)
- Use `google-auth-library`'s `GoogleAuth` with scope `https://www.googleapis.com/auth/cloud-platform`.
- Get an access token and send `Authorization: Bearer <token>`; `Content-Type: application/json`. **No `?key=` query param** anywhere.
- Create the `GoogleAuth` client **once at module load** (not per request) and reuse it; the library caches/refreshes tokens internally. Call `auth.getAccessToken()` (or get a client and `getAccessToken`) per request — it returns the cached token until near expiry.

### 2.3 Response parsing
- Vertex's response shape matches the Developer API for this call: `candidates[0].content.parts[0].text`, `candidates[0].finishReason`, and `promptFeedback.blockReason`. Keep the existing JSON parsing and the existing safety-block handling (finishReason SAFETY / blockReason / missing text → in-character redirect, do not store the user turn). Confirm field names match by logging the raw response once during local testing, then remove that log.

### 2.4 Retry & errors
- Keep the existing retry policy (3 attempts, 15s timeout, 0.5s/1.5s backoff, retry only on network/5xx). 
- Vertex returns `429` for genuine rate limits too — keep treating 429 as retryable, but note that with Cloud billing active these should be rare. Keep the existing `gemini.attempt_failed` structured log (status + attempt) — it's what surfaced this whole issue, so preserve that observability.
- On final failure, same behavior as today (the in-character network line bubbles up to the device).

## 3. Remove the old key path
- Delete all use of `GEMINI_API_KEY` from `gemini.js` and anywhere else it's referenced.
- Remove `GEMINI_API_KEY` from `.env.example` and from the README's env-var list; add `GOOGLE_CLOUD_PROJECT` (note Cloud Run provides it automatically) and document that Gemini now uses Vertex via the service account, no key.
- Update `server/README.md`: the "Data handling" / architecture notes and the deploy section should reflect that the LLM is Vertex AI (service-account auth) and that `GEMINI_API_KEY` is no longer used. Keep `DEEPGRAM_API_KEY` and `GOOGLE_TTS_API_KEY` as-is.

## 4. IAM (Daniel runs this; put the exact command in the README)
The Cloud Run runtime service account needs Vertex access. The runbook command:
```
PROJECT_ID = project-e8bab267-6dfb-401d-a39
RUNTIME_SA = 697766265632-compute@developer.gserviceaccount.com
gcloud services enable aiplatform.googleapis.com --project project-e8bab267-6dfb-401d-a39
gcloud projects add-iam-policy-binding project-e8bab267-6dfb-401d-a39 --member="serviceAccount:697766265632-compute@developer.gserviceaccount.com" --role="roles/aiplatform.user"
```
Document that these must be run before the redeploy, and that IAM propagation can take ~1 minute.

## 5. Smoke test
- Update `server/scripts/smoke.js` so the Gemini provider is exercised against a mock (the smoke test must not require real Vertex creds or network). Keep it green offline.
- Add a brief note in the README: a real end-to-end Vertex check happens on-device / via the deployed service, not in the offline smoke test.

## 6. Verification gates
- `npm install` clean; `npm run smoke` green.
- No remaining reference to `GEMINI_API_KEY` or `generativelanguage.googleapis.com` anywhere in `server/`.

## 7. Out of scope
TTS provider/auth (unchanged), Unity client (unchanged), the Vertex SDK (we chose auth-library+REST), streaming, and any model change (still `gemini-2.5-flash-lite`).

## Deployment (Daniel, after Claude Code finishes — exact commands in README)
1. Run the section 4 IAM commands (enable `aiplatform.googleapis.com`, grant `roles/aiplatform.user` to the runtime SA). Wait ~1 min.
2. Redeploy, and in the SAME command REMOVE the old key var:
   `gcloud run services update hsvr-proxy --region us-east1 --remove-env-vars GEMINI_API_KEY`
   then redeploy code: `gcloud run deploy hsvr-proxy --source server/ --region us-east1`
   (Order: deploy the new code first so it no longer reads the key, then the var removal is harmless either way. Implementer: put the precise sequence in the README.)
3. Test `/status` → `{"ok":true}`.
4. On the Quest: ask a question → expect text AND voice (Gemini now returns a reply via Vertex, TTS runs on it).
5. Re-run the usage curl → should now return 200 with cost data, and Gemini spend will appear under Vertex AI in Cloud billing (covered by the $300).

## Kickoff instruction (paste into Claude Code)

> Read HISTORY_SPEAKS_SPEC4.md in the repo root and implement it exactly. This is a surgical migration of ONLY the Gemini call from the Developer API (api key) to Vertex AI (service-account auth via google-auth-library + REST), with a clean removal of GEMINI_API_KEY. Keep gemini.js's public interface identical so converse.js is untouched. Do not modify STT, TTS, safety, metering, sessions, alerts, admin, or any Unity code. Finish with a clean npm install, a green offline smoke test (Gemini mocked), and update server/README.md including the section 4 IAM commands and the deployment sequence. Show me each file change before moving on.
