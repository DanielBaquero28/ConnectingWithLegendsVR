// Claude LLM provider — Anthropic Messages API, called directly (spec 12).
// Replaces the Vertex/Gemini provider with the same exported interface, so
// converse.js only changed where the result shape gained token counts.
//
// Request: system = persona + cutoff note + SAFETY_PREAMBLE (unchanged text),
// messages = few-shots as user/assistant pairs, the windowed live history,
// then the current user turn. Alternation is enforced defensively (merge
// same-role neighbours, drop a leading assistant turn) and the list always
// ends with a user turn. Thinking per the character's `thinking` field
// (default disabled, spec 12 Part N) at the character's effort (default low);
// temperature/top_p/top_k are never sent (Haiku 5.5 rejects them).
// Thinking blocks are read for nothing — only text blocks become the reply,
// and sessions.js stores plain text, so no thinking is ever persisted or
// resent.
//
// Retry: 3 attempts, 15s per-attempt timeout, 0.5s/1.5s backoff, retry on
// network errors, 429, 5xx and 529, honouring `retry-after`. On top of that an
// OVERALL deadline of 12s across all attempts (below the device's 15s) so the
// server never keeps retrying — and billing — after the headset has given up.
//
// Logs carry status/attempt/request_id only — never prompt or reply text.

const { SAFETY_PREAMBLE } = require('../safety');
const log = require('../log');

const ANTHROPIC_URL = 'https://api.anthropic.com/v1/messages';
const ANTHROPIC_VERSION = '2023-06-01'; // the only current version (docs, 2026-10)

const DEFAULT_MODEL = 'claude-haiku-5-5';
const DEFAULT_EFFORT = 'low';
// Values the current docs list for output_config.effort.
const EFFORT_VALUES = ['low', 'medium', 'high', 'xhigh', 'max'];
// Spec 12 Part N: the character's `thinking` field. "disabled" is the
// production decision (Part B eval); "adaptive" stays available per character.
// Per the current docs, effort "works whether or not thinking is enabled", so
// output_config.effort is still sent with thinking disabled. Haiku 5.5 accepts
// thinking "disabled" at effort high or below only; xhigh/max would 400, which
// admin.js rejects at seed time.
const THINKING_VALUES = ['disabled', 'adaptive'];
const DEFAULT_THINKING = 'disabled';
const EFFORT_REQUIRING_THINKING = ['xhigh', 'max'];
// Values the current docs list for the top-level inference_geo parameter.
const INFERENCE_GEO_VALUES = ['us', 'global'];

const MAX_TOKENS = 600; // thinking tokens count against this too

// Spec 12 Part O: the length rule travels with the CURRENT user turn, where
// Haiku obeys it, instead of only in the system prompt, where it did not
// (Part N re-run: 49/70 replies over 40 words). Appended in the request body
// only — sessions.js stores the student's text as sent, never this suffix,
// so history turns resent on later requests do not carry it.
const TURN_SUFFIX =
  ' (Reply in at most 40 words, in two or three sentences, ending with a short question when natural.)';

const MAX_ATTEMPTS = 3;
const ATTEMPT_TIMEOUT_MS = 15000;
const OVERALL_DEADLINE_MS = 12000; // across all attempts; device gives up at 15s
const BACKOFF_MS = [500, 1500];    // delays between attempts 1->2 and 2->3

// Data residency (spec 12 Part A): US-only inference unless overridden.
// "us" is billed at 1.1x — metering applies that multiplier.
function configuredInferenceGeo() {
  const raw = (process.env.CLAUDE_INFERENCE_GEO || 'us').trim().toLowerCase();
  return INFERENCE_GEO_VALUES.includes(raw) ? raw : 'us';
}

function resolveEffort(character) {
  const e = character && character.effort;
  return EFFORT_VALUES.includes(e) ? e : DEFAULT_EFFORT;
}

function resolveThinking(character) {
  const t = character && character.thinking;
  return THINKING_VALUES.includes(t) ? t : DEFAULT_THINKING;
}

// Stored session turns use Gemini's role name 'model' (Firestore schema is
// unchanged); map it at request time.
function toClaudeRole(role) {
  return role === 'assistant' || role === 'model' ? 'assistant' : 'user';
}

// Strict alternation: drop a leading assistant turn, merge consecutive
// same-role turns, drop empty text. Caller guarantees the final push is the
// current user turn, so the result always ends with role 'user'.
function normalizeMessages(raw) {
  const out = [];
  for (const m of raw) {
    const text = typeof m.text === 'string' ? m.text.trim() : '';
    if (!text) continue;
    const role = toClaudeRole(m.role);
    if (out.length === 0 && role === 'assistant') continue;
    const last = out[out.length - 1];
    if (last && last.role === role) {
      last.content += '\n\n' + text;
    } else {
      out.push({ role, content: text });
    }
  }
  return out;
}

function buildRequestBody(character, historyTurns, userText) {
  const system =
    character.systemPersona + '\n\n' + character.knowledgeCutoffNote + '\n\n' + SAFETY_PREAMBLE;

  const raw = [];

  // Few-shot examples injected every request as user/assistant pairs. They do
  // NOT count against the live history window.
  for (const ex of character.fewShotExamples || []) {
    if (!ex) continue;
    if (ex.studentSays) raw.push({ role: 'user', text: ex.studentSays });
    if (ex.characterReplies) raw.push({ role: 'assistant', text: ex.characterReplies });
  }

  // Live windowed history, text only (sessions.js already caps it; cap defensively).
  const maxMessages = (character.maxTurns || 8) * 2;
  const history = historyTurns || [];
  const windowed = history.length > maxMessages
    ? history.slice(history.length - maxMessages)
    : history;
  for (const turn of windowed) {
    raw.push({ role: turn.role, text: turn.text });
  }

  const currentTurn = userText + TURN_SUFFIX;
  raw.push({ role: 'user', text: currentTurn });

  const messages = normalizeMessages(raw);
  // normalizeMessages merges into the final user turn, never past it.
  if (messages.length === 0 || messages[messages.length - 1].role !== 'user') {
    messages.push({ role: 'user', content: currentTurn });
  }

  return {
    model: character.modelName || DEFAULT_MODEL,
    max_tokens: MAX_TOKENS,
    thinking: { type: resolveThinking(character) },
    output_config: { effort: resolveEffort(character) },
    inference_geo: configuredInferenceGeo(),
    system,
    messages,
  };
}

// Characters sent to the model — kept for the usage doc's existing char counters.
function countInputChars(body) {
  let chars = body.system.length;
  for (const m of body.messages) chars += m.content.length;
  return chars;
}

// retry-after may be seconds or an HTTP date. Returns ms, or 0 if absent/invalid.
function retryAfterMs(res) {
  const h = res.headers && res.headers.get && res.headers.get('retry-after');
  if (!h) return 0;
  const secs = Number(h);
  if (Number.isFinite(secs)) return Math.max(0, secs * 1000);
  const at = Date.parse(h);
  return Number.isFinite(at) ? Math.max(0, at - Date.now()) : 0;
}

function requestIdOf(res) {
  return (res && res.headers && res.headers.get && res.headers.get('request-id')) || null;
}

function isRetryableStatus(status) {
  return status === 429 || status === 529 || (status >= 500 && status <= 599);
}

// Returns:
//   { ok: true,  text, safetyBlocked: false, empty: false, inputChars, outputChars,
//                inputTokens, outputTokens, inferenceGeo, stopReason }
//   { ok: true,  safetyBlocked: true, ... }   -> caller sends the in-character redirect (stop_reason refusal)
//   { ok: true,  empty: true, ... }           -> no text (e.g. max_tokens spent on thinking); caller 502s + llm_empty
//   { ok: false, timedOut?: true }            -> retries/deadline exhausted or non-retryable error (caller 502s,
//                                                or 504s when the request-level deadline was the cause)
//
// options.deadlineAt (ms epoch, spec 12 Part L): the /converse request's own
// 20s deadline. The provider never works past min(own 12s deadline, deadlineAt).
async function generateReply(character, historyTurns, userText, options) {
  const body = buildRequestBody(character, historyTurns, userText);
  const inputChars = countInputChars(body);
  const payload = JSON.stringify(body);
  const requestDeadline = options && Number.isFinite(options.deadlineAt) ? options.deadlineAt : Infinity;
  const deadline = Math.min(Date.now() + OVERALL_DEADLINE_MS, requestDeadline);

  let json = null;
  let requestId = null;
  let timedOut = false;

  for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt++) {
    const remaining = deadline - Date.now();
    if (remaining <= 0) {
      log.error('claude.deadline_exceeded', { attempt: attempt + 1 });
      timedOut = true;
      break;
    }

    let res;
    try {
      res = await fetch(ANTHROPIC_URL, {
        method: 'POST',
        headers: {
          'x-api-key': process.env.ANTHROPIC_API_KEY || '',
          'anthropic-version': ANTHROPIC_VERSION,
          'content-type': 'application/json',
        },
        body: payload,
        signal: AbortSignal.timeout(Math.min(ATTEMPT_TIMEOUT_MS, remaining)),
      });
    } catch (err) {
      // Network error or timeout — both retryable. No message text in the log.
      // The per-attempt timeout is capped to the deadline, so a TimeoutError
      // means the deadline (ours or the request's) is what stopped us.
      if (err && (err.name === 'TimeoutError' || err.name === 'AbortError')) timedOut = true;
      log.error('claude.attempt_failed', { status: null, attempt: attempt + 1, request_id: null });
      const next = await waitForRetry(attempt, 0, deadline);
      if (next.deadline) timedOut = true;
      if (!next.retry) break;
      continue;
    }

    requestId = requestIdOf(res);

    if (res.ok) {
      try {
        json = await res.json();
      } catch (err) {
        log.error('claude.attempt_failed', { status: res.status, attempt: attempt + 1, request_id: requestId });
        const next = await waitForRetry(attempt, 0, deadline);
        if (next.deadline) timedOut = true;
        if (!next.retry) break;
        continue;
      }
      break;
    }

    log.error('claude.attempt_failed', { status: res.status, attempt: attempt + 1, request_id: requestId });
    if (!isRetryableStatus(res.status)) break;
    const next = await waitForRetry(attempt, retryAfterMs(res), deadline);
    if (next.deadline) timedOut = true;
    if (!next.retry) break;
  }

  if (!json) return timedOut ? { ok: false, timedOut: true } : { ok: false };

  const usage = json.usage || {};
  const inputTokens = Number(usage.input_tokens) || 0;
  const outputTokens = Number(usage.output_tokens) || 0;
  const inferenceGeo = usage.inference_geo || body.inference_geo;
  const stopReason = json.stop_reason || null;
  const base = { ok: true, inputChars, inputTokens, outputTokens, inferenceGeo, stopReason };

  if (stopReason === 'refusal') {
    // Model-side refusal -> the existing safety-block path (same redirect, same event).
    const category = json.stop_details && json.stop_details.category;
    log.warn('claude.refusal', { category: category || null, request_id: requestId });
    return { ...base, safetyBlocked: true, empty: false };
  }

  // Reply = concatenated text blocks. Thinking blocks are ignored entirely.
  const text = (Array.isArray(json.content) ? json.content : [])
    .filter((b) => b && b.type === 'text' && typeof b.text === 'string')
    .map((b) => b.text)
    .join(' ')
    .trim();

  if (!text) {
    // Typically max_tokens consumed by thinking. Not a safety condition.
    log.warn('claude.empty_text', { stop_reason: stopReason, output_tokens: outputTokens, request_id: requestId });
    return { ...base, safetyBlocked: false, empty: true };
  }

  return { ...base, text, safetyBlocked: false, empty: false, outputChars: text.length };
}

// Sleeps the backoff (or retry-after, whichever is longer) before the next
// attempt, but only if there IS a next attempt and it would start before the
// overall deadline. Returns { retry, deadline }: retry=false means stop;
// deadline=true means the deadline (not the attempt count) is the reason.
async function waitForRetry(attempt, retryAfter, deadline) {
  if (attempt >= MAX_ATTEMPTS - 1) return { retry: false, deadline: false };
  const delay = Math.max(BACKOFF_MS[attempt] || 0, retryAfter);
  if (Date.now() + delay >= deadline) {
    log.error('claude.deadline_exceeded', { attempt: attempt + 2 });
    return { retry: false, deadline: true };
  }
  await sleep(delay);
  return { retry: true, deadline: false };
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

module.exports = {
  generateReply,
  buildRequestBody,
  configuredInferenceGeo,
  ANTHROPIC_URL,
  DEFAULT_MODEL,
  DEFAULT_EFFORT,
  EFFORT_VALUES,
  THINKING_VALUES,
  DEFAULT_THINKING,
  EFFORT_REQUIRING_THINKING,
  MAX_TOKENS,
  TURN_SUFFIX,
};
