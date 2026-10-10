// Gemini LLM provider — Vertex AI (spec 4). Billed as a normal Cloud service
// (covered by Cloud billing/credits), NOT the Developer API / AI Studio prepay.
//
// The request body is built EXACTLY as before (system_instruction = persona +
// cutoff + SAFETY_PREAMBLE, few-shots prepended as turns, windowed live
// history, same safetySettings, same generationConfig) and the response is
// parsed the same way — Vertex's generateContent shape matches for this call.
// Retry policy is unchanged: 3 attempts, 15s timeout, 0.5s/1.5s backoff, retry
// only on network/5xx (plus 429 rate limits, rare with Cloud billing active).
//
// Auth is the core change: Application Default Credentials via the Cloud Run
// service account mint an access token (Authorization: Bearer), so there is no
// API key anywhere — no GEMINI_API_KEY, no ?key= query param.

const { GoogleAuth } = require('google-auth-library');
const { SAFETY_PREAMBLE } = require('../safety');
const log = require('../log');

// Vertex generateContent, regional endpoint. %PROJECT% from env, %MODEL% from
// the character config (bare model id is correct on Vertex — prepend nothing).
const LOCATION = 'us-east1';
const VERTEX_URL_FORMAT =
  `https://${LOCATION}-aiplatform.googleapis.com/v1/projects/%PROJECT%/locations/${LOCATION}/publishers/google/models/%MODEL%:generateContent`;

// One GoogleAuth client for the module's lifetime — it caches and refreshes the
// access token internally, returning the cached token until near expiry.
const auth = new GoogleAuth({ scopes: ['https://www.googleapis.com/auth/cloud-platform'] });

// Cloud Run sets GOOGLE_CLOUD_PROJECT automatically; VERTEX_PROJECT_ID overrides it.
function projectId() {
  return process.env.VERTEX_PROJECT_ID || process.env.GOOGLE_CLOUD_PROJECT || '';
}

const MAX_ATTEMPTS = 3;
const TIMEOUT_MS = 15000;
const BACKOFF_MS = [500, 1500]; // delays between attempts 1->2 and 2->3

function buildRequestBody(character, historyTurns, userText) {
  const systemInstruction =
    character.systemPersona + '\n\n' + character.knowledgeCutoffNote + '\n\n' + SAFETY_PREAMBLE;

  const contents = [];

  // Few-shot examples injected every request, alternating user/model. They do
  // NOT count against the live history window.
  for (const ex of character.fewShotExamples) {
    if (!ex) continue;
    if (ex.studentSays) contents.push({ role: 'user', parts: [{ text: ex.studentSays }] });
    if (ex.characterReplies) contents.push({ role: 'model', parts: [{ text: ex.characterReplies }] });
  }

  // Live windowed history (sessions.js already caps it, but cap defensively).
  const maxMessages = character.maxTurns * 2;
  const windowed = historyTurns.length > maxMessages
    ? historyTurns.slice(historyTurns.length - maxMessages)
    : historyTurns;
  for (const turn of windowed) {
    contents.push({ role: turn.role, parts: [{ text: turn.text }] });
  }

  contents.push({ role: 'user', parts: [{ text: userText }] });

  return {
    system_instruction: { parts: [{ text: systemInstruction }] },
    contents,
    safetySettings: [
      { category: 'HARM_CATEGORY_HARASSMENT', threshold: 'BLOCK_MEDIUM_AND_ABOVE' },
      { category: 'HARM_CATEGORY_HATE_SPEECH', threshold: 'BLOCK_MEDIUM_AND_ABOVE' },
      { category: 'HARM_CATEGORY_SEXUALLY_EXPLICIT', threshold: 'BLOCK_LOW_AND_ABOVE' },
      { category: 'HARM_CATEGORY_DANGEROUS_CONTENT', threshold: 'BLOCK_MEDIUM_AND_ABOVE' },
    ],
    generationConfig: { thinkingConfig: { thinkingBudget: 0 }, maxOutputTokens: 100, temperature: 0.7 },
  };
}

// Counts the characters actually sent to / received from the model, for the
// chars/4 token estimate in metering.
function countInputChars(body) {
  let chars = body.system_instruction.parts[0].text.length;
  for (const c of body.contents) for (const p of c.parts) chars += p.text.length;
  return chars;
}

// Returns:
//   { ok: true,  text, safetyBlocked: false, inputChars, outputChars }
//   { ok: true,  safetyBlocked: true, inputChars }   -> caller sends the in-character redirect
//   { ok: false }                                    -> all retries exhausted (caller 502s)
async function generateReply(character, historyTurns, userText) {
  const body = buildRequestBody(character, historyTurns, userText);
  const inputChars = countInputChars(body);
  const url = VERTEX_URL_FORMAT
    .replace('%PROJECT%', projectId())
    .replace('%MODEL%', character.modelName);
  const payload = JSON.stringify(body);

  let json = null;
  let gotResponse = false;

  for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt++) {
    try {
      // Cached service-account token (refreshed by the library near expiry). A
      // token failure throws and is treated like any other attempt failure.
      const token = await auth.getAccessToken();
      const res = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${token}`,
        },
        body: payload,
        signal: AbortSignal.timeout(TIMEOUT_MS),
      });

      if (res.ok) {
        json = await res.json();
        gotResponse = true;
        break;
      }

      // Retry on 5xx and on 429 (genuine rate limit); other 4xx is not retryable.
      const retryable = res.status === 429 || (res.status >= 500 && res.status <= 599);
      log.error('gemini.attempt_failed', { attempt: attempt + 1, status: res.status });
      if (retryable && attempt < MAX_ATTEMPTS - 1) {
        await sleep(BACKOFF_MS[attempt]);
        continue;
      }
      break;
    } catch (err) {
      // fetch throws on network errors and on AbortSignal timeout — both retryable.
      log.error('gemini.attempt_failed', { attempt: attempt + 1, message: err.message });
      if (attempt < MAX_ATTEMPTS - 1) {
        await sleep(BACKOFF_MS[attempt]);
        continue;
      }
    }
  }

  if (!gotResponse) return { ok: false };

  // Safety-block detection, ported from Unity's ParseGeminiResponse:
  // prompt-level block, candidate finishReason SAFETY, or no usable text.
  if (json.promptFeedback && json.promptFeedback.blockReason) {
    log.warn('gemini.prompt_blocked', { blockReason: json.promptFeedback.blockReason });
    return { ok: true, safetyBlocked: true, inputChars };
  }
  const candidate = json.candidates && json.candidates[0];
  if (candidate && candidate.finishReason === 'SAFETY') {
    log.warn('gemini.finish_reason_safety');
    return { ok: true, safetyBlocked: true, inputChars };
  }
  const text =
    candidate && candidate.content && candidate.content.parts &&
    candidate.content.parts[0] && candidate.content.parts[0].text;
  if (!text) {
    log.warn('gemini.no_usable_text');
    return { ok: true, safetyBlocked: true, inputChars };
  }

  return { ok: true, text, safetyBlocked: false, inputChars, outputChars: text.length };
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

module.exports = { generateReply, buildRequestBody };
