// POST /converse — the one round trip the headset makes per turn (spec 4.4).
// Accepts audio/wav (voice path) or application/json (typed path) and returns
// { transcript, replyText, audioBase64 }.
//
// Providers are called through their module objects (claude.generateReply,
// not a destructured import) so the smoke test can mock them via the require
// cache.

const express = require('express');
const { deviceAuth } = require('./auth');
const characters = require('./characters');
const sessions = require('./sessions');
const claude = require('./providers/claude');
const googleTts = require('./providers/googleTts');
const deepgram = require('./providers/deepgram');
const safety = require('./safety');
const metering = require('./metering');
const events = require('./events');
const alerts = require('./alerts');
const log = require('./log');

const router = express.Router();

// Crisis/redirect lines must still speak even if the character doc is
// unreachable; matches the Einstein voice the device shipped with.
const FALLBACK_VOICE = { languageCode: 'en-GB', voiceName: 'en-GB-Neural2-B', speakingRate: 0.72, pitch: -8.0 };

// Spec 12 Part L: one deadline for the whole turn — STT + LLM + TTS. Each
// provider call is capped to the time left, and once it has passed the
// remaining work is skipped and the device gets a 504 (it shows its
// in-character network line; nothing is stored, no safety event is written).
// Env override exists for the smoke test only.
const CONVERSE_DEADLINE_MS = 20000;
function converseDeadlineMs() {
  const v = Number(process.env.CONVERSE_DEADLINE_MS);
  return Number.isFinite(v) && v > 0 ? v : CONVERSE_DEADLINE_MS;
}

router.post('/', deviceAuth, async (req, res, next) => {
  try {
    const deadlineAt = Date.now() + converseDeadlineMs();

    // Budget gate (spec 4.4 step 2) — runs before STT. Never blocks a lesson;
    // past 100% it marks the usage doc overBudget and keeps serving.
    const school = await metering.gate(req.device.schoolId);
    if (!school.allowed) {
      log.warn('converse.school_rejected', { schoolId: req.device.schoolId, reason: school.reason });
      return res.status(403).json({ error: school.reason });
    }
    req.school = school;

    if (req.is('audio/wav')) {
      // Voice path (spec 4.4 step 3). The WAV buffer lives in memory only —
      // it goes to Deepgram and nowhere else; never to disk, never to logs.
      const sessionId = req.get('X-Session-Id');
      const characterId = req.get('X-Character-Id') || 'einstein';

      if (!Buffer.isBuffer(req.body) || req.body.length === 0) {
        return res.status(400).json({ error: 'Empty audio body' });
      }

      const stt = await deepgram.transcribe(req.body, { deadlineAt });
      if (!stt.ok && stt.timedOut) {
        return timeout504(res, 'stt', deadlineAt);
      }
      if (!stt.ok) {
        await events.logEvent({
          type: 'provider_error', severity: 'error',
          schoolId: req.device.schoolId, headsetId: req.device.headsetId, sessionId,
          detail: { provider: 'deepgram' },
        });
        alerts.feedProviderError('deepgram', { schoolName: school.schoolName, headsetLabel: req.device.label });
        return res.status(502).json({ error: 'Speech recognition unavailable' });
      }
      if (stt.transcript === '') {
        // Device shows its existing "didn't catch that" handling.
        return res.json({ transcript: '', replyText: '', audioBase64: '', emptyTranscript: true });
      }

      return await runPipeline(req, res, {
        sessionId,
        characterId,
        userText: stt.transcript,
        transcript: stt.transcript,
        sttSeconds: stt.audioSeconds,
        deadlineAt,
      });
    }

    if (req.is('application/json')) {
      const { sessionId, characterId, text } = req.body || {};
      if (typeof text !== 'string' || text.trim() === '') {
        return res.status(400).json({ error: 'Missing text' });
      }
      return await runPipeline(req, res, {
        sessionId,
        characterId: characterId || 'einstein',
        userText: text.trim(),
        transcript: text.trim(),
        sttSeconds: 0,
        deadlineAt,
      });
    }

    return res.status(415).json({ error: 'Content-Type must be audio/wav or application/json' });
  } catch (err) {
    return next(err);
  }
});

// Spec 12 Part L: the turn ran out of time. No events, no history, no alerts —
// just a structured log line (stage + elapsed, nothing else) and a 504 the
// device turns into its in-character network line without retrying.
function timeout504(res, stage, deadlineAt) {
  log.warn('converse.deadline_exceeded', { stage, overshootMs: Date.now() - deadlineAt });
  return res.status(504).json({ error: 'Request timed out' });
}

// Spec 12 Part L: refusal and llm_empty turns ship no reply but Claude still
// bills their tokens (and STT already ran), so they are metered like any other
// turn. Nothing is stored in the session for these turns.
async function meterLlmOnly({ device, school, character, result, sttSeconds, ttsChars }) {
  return metering.record({
    schoolId: device.schoolId,
    headsetId: device.headsetId,
    monthlyBudgetUsd: school.monthlyBudgetUsd,
    modelName: character.modelName,
    inferenceGeo: result.inferenceGeo,
    sttSeconds: sttSeconds || 0,
    llmInputChars: result.inputChars || 0,
    llmOutputChars: 0,
    llmInputTokens: result.inputTokens || 0,
    llmOutputTokens: result.outputTokens || 0,
    ttsChars: ttsChars || 0,
  });
}

// Shared pipeline for both input paths (after STT has produced userText).
async function runPipeline(req, res, { sessionId, characterId, userText, transcript, sttSeconds, deadlineAt }) {
  const device = req.device;
  const school = req.school;
  const t0 = Date.now();
  if (!Number.isFinite(deadlineAt)) deadlineAt = t0 + converseDeadlineMs();

  if (!sessions.isValidSessionId(sessionId)) {
    return res.status(400).json({ error: 'Missing or invalid sessionId' });
  }

  // ===== Server safety pre-check (spec 4.4 step 4) =====
  const flag = safety.classify(userText);

  if (flag === 'crisis') {
    // Never call the LLM. Crisis message + TTS, critical event WITH snippet,
    // immediate Slack alert. The user turn is NOT written to the session.
    const character = await characters.getCharacter(characterId);
    const voice = character ? character.voice : FALLBACK_VOICE;
    const tts = await googleTts.synthesize(safety.CRISIS_MESSAGE, voice);
    const eventId = await events.logEvent({
      type: 'crisis', severity: 'critical',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      snippet: userText,
      detail: { source: 'server', characterId },
    });
    // Spec 12 Part G: the alert carries no student text and no session ID —
    // staff open the event by ID for the snippet.
    alerts.alertCrisis({
      schoolName: school.schoolName, headsetLabel: device.label, characterId, eventId, source: 'server',
    });
    return res.json({
      transcript, replyText: safety.CRISIS_MESSAGE, audioBase64: tts.audioBase64, emptyTranscript: false,
    });
  }

  if (flag === 'profanity' || flag === 'jailbreak') {
    // Non-blocking: the persona handles these in character. Log + feed the
    // per-headset pattern counter (one-offs never alert, patterns do).
    // Spec 12 Part K: metadata only — no snippet.
    await events.logEvent({
      type: flag, severity: 'info',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      detail: { characterId },
    });
    await alerts.feedPattern({
      headsetId: device.headsetId, schoolName: school.schoolName, headsetLabel: device.label,
    });
  }

  // ===== Character config + session history (spec 4.4 step 5) =====
  const character = await characters.getCharacter(characterId);
  if (!character) {
    return res.status(404).json({ error: 'Unknown character' });
  }
  const session = await sessions.loadSession(sessionId, device.headsetId);
  if (session.ownedByOther) {
    return res.status(403).json({ error: 'Session belongs to another device' });
  }

  // ===== LLM (spec 4.4 steps 5–6; spec 12: Claude Messages API + retry) =====
  if (Date.now() >= deadlineAt) return timeout504(res, 'pre_llm', deadlineAt);
  const tLlm = Date.now();
  const result = await claude.generateReply(character, session.turns, userText, { deadlineAt });
  const llmMs = Date.now() - tLlm;

  if (!result.ok && result.timedOut && Date.now() >= deadlineAt) {
    return timeout504(res, 'llm', deadlineAt);
  }

  if (!result.ok) {
    // All retries exhausted — the device shows its in-character network line.
    await events.logEvent({
      type: 'provider_error', severity: 'error',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      detail: { provider: 'claude' },
    });
    alerts.feedProviderError('claude', { schoolName: school.schoolName, headsetLabel: device.label });
    return res.status(502).json({ error: 'Upstream model unavailable' });
  }

  if (result.empty) {
    // No text came back (e.g. max_tokens spent on thinking). Not a safety
    // condition: ops-only llm_empty event with NO text, then 502 so the device
    // shows its in-character network line. Nothing is stored.
    await events.logEvent({
      type: 'llm_empty', severity: 'warning',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      detail: {
        provider: 'claude', characterId, modelName: character.modelName,
        stopReason: result.stopReason, outputTokens: result.outputTokens, llmMs,
      },
    });
    // Spec 12 Part L: the tokens were billed even though nothing shipped.
    await meterLlmOnly({ device, school, character, result, sttSeconds });
    return res.status(502).json({ error: 'Upstream model returned no text' });
  }

  if (result.safetyBlocked) {
    // Model refusal (stop_reason "refusal") -> in-character redirect; the user
    // turn is NOT stored (spec 4.4 step 6). Same message, same event as before.
    // Spec 12 Part K: metadata only — no snippet.
    await events.logEvent({
      type: 'safety_block', severity: 'warning',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      detail: { characterId, stopReason: result.stopReason },
    });
    const tts = await googleTts.synthesize(safety.SAFETY_REDIRECT, character.voice, { deadlineAt });
    if (tts.timedOut) return timeout504(res, 'tts', deadlineAt);
    // Spec 12 Part L: the refused turn still consumed tokens — meter them.
    await meterLlmOnly({
      device, school, character, result, sttSeconds,
      ttsChars: tts.cached || tts.failed ? 0 : safety.SAFETY_REDIRECT.length,
    });
    return res.json({
      transcript, replyText: safety.SAFETY_REDIRECT, audioBase64: tts.audioBase64, emptyTranscript: false,
    });
  }

  // ===== Post-process (spec 4.4 step 7) — same order as Unity =====
  let replyText = truncateToWordLimit(result.text, character.maxWords);
  replyText = sanitizeResponse(replyText);

  // ===== TTS (spec 4.4 step 8) =====
  if (Date.now() >= deadlineAt) return timeout504(res, 'pre_tts', deadlineAt);
  const tTts = Date.now();
  const tts = await googleTts.synthesize(replyText, character.voice, { deadlineAt });
  const ttsMs = Date.now() - tTts;
  if (tts.timedOut) return timeout504(res, 'tts', deadlineAt); // nothing stored, nothing metered
  if (tts.failed) {
    await events.logEvent({
      type: 'provider_error', severity: 'error',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      detail: { provider: 'googleTts' },
    });
    alerts.feedProviderError('googleTts', { schoolName: school.schoolName, headsetLabel: device.label });
    // Reply text still ships; the device just has no audio for this turn.
  }

  // ===== Persist + meter + ops event (spec 4.4 step 9) =====
  await sessions.appendExchange({
    sessionId,
    schoolId: device.schoolId,
    headsetId: device.headsetId,
    characterId,
    maxTurns: character.maxTurns,
    priorTurns: session.turns,
    userText,
    replyText,
  });

  const usage = await metering.record({
    schoolId: device.schoolId,
    headsetId: device.headsetId,
    monthlyBudgetUsd: school.monthlyBudgetUsd,
    modelName: character.modelName,
    inferenceGeo: result.inferenceGeo,
    sttSeconds: sttSeconds || 0,
    llmInputChars: result.inputChars,
    llmOutputChars: result.outputChars,
    llmInputTokens: result.inputTokens,
    llmOutputTokens: result.outputTokens,
    ttsChars: tts.cached || tts.failed ? 0 : replyText.length, // cache hits cost $0
  });
  if (usage.crossedWarn80) {
    alerts.alertBudget({
      schoolName: school.schoolName, percent: 80,
      totalCostUsd: usage.totalCostUsd, monthlyBudgetUsd: school.monthlyBudgetUsd,
    });
  }
  if (usage.crossedLimit100) {
    alerts.alertBudget({
      schoolName: school.schoolName, percent: 100,
      totalCostUsd: usage.totalCostUsd, monthlyBudgetUsd: school.monthlyBudgetUsd,
    });
  }

  // Metadata only — NO conversation text lands in turn_ok events (spec 4.8).
  await events.logEvent({
    type: 'turn_ok', severity: 'debug',
    schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
    detail: {
      totalMs: Date.now() - t0,
      llmMs,
      ttsMs,
      sttSeconds: sttSeconds || 0,
      costUsd: usage.costUsd,
      ttsCached: tts.cached === true,
      characterId,
      llmInputTokens: result.inputTokens,
      llmOutputTokens: result.outputTokens,
      stopReason: result.stopReason,
    },
  });

  return res.json({ transcript, replyText, audioBase64: tts.audioBase64, emptyTranscript: false });
}

// ===== Ported verbatim in behavior from AIResponseControllerV2 =====

function truncateToWordLimit(text, maxWords) {
  if (!text) return text;
  const words = text.split(' ').filter((w) => w.length > 0);
  if (words.length <= maxWords) return text;

  let truncated = words.slice(0, maxWords).join(' ');
  const lastChar = truncated[truncated.length - 1];
  if (lastChar !== '.' && lastChar !== '!' && lastChar !== '?') {
    truncated += '...';
  }
  return truncated;
}

// Strips markdown characters TTS would read aloud; normal punctuation stays.
function sanitizeResponse(text) {
  if (!text) return text;
  text = text
    .replace(/\*/g, '')  // bold/italic markers
    .replace(/#/g, '')   // heading markers
    .replace(/_/g, ' ')  // underscore emphasis (replace with space)
    .replace(/~/g, '')   // strikethrough markers
    .replace(/`/g, '');  // code markers
  while (text.includes('  ')) text = text.replace(/ {2}/g, ' ');
  return text.trim();
}

module.exports = { router, runPipeline };
