// Deepgram STT provider (spec 4.4 step 3).
// Same endpoint + params the Unity client used: Nova-3 pre-recorded with
// smart_format and punctuate. Raw WAV bytes in (16kHz LINEAR16 from the
// device), transcript out.
//
// COPPA posture (spec 4.8): the audio buffer is processed in memory only —
// never written to disk or storage, never logged.

const log = require('../log');

// mip_opt_out=true (spec 12 Part D): opts every request out of Deepgram's
// Model Improvement Program, so student audio is never used for training.
const DEEPGRAM_URL =
  'https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&punctuate=true&mip_opt_out=true';
const TIMEOUT_MS = 15000;

// Returns { ok: true, transcript, audioSeconds } or { ok: false, timedOut? }.
// transcript is '' (with ok: true) when Deepgram heard no speech.
// options.deadlineAt (spec 12 Part L): the /converse request deadline; the
// call's timeout is capped to the time left, and timedOut is set when the
// deadline — not the provider — ended the attempt.
async function transcribe(wavBuffer, options) {
  // Estimate duration from the WAV layout (16kHz, 16-bit mono, 44-byte header)
  // as a fallback for metering; prefer Deepgram's own measurement below.
  const estimatedSeconds = Math.max(0, (wavBuffer.length - 44) / (16000 * 2));

  const deadlineAt = options && Number.isFinite(options.deadlineAt) ? options.deadlineAt : Infinity;
  const remaining = deadlineAt - Date.now();
  if (remaining <= 0) return { ok: false, timedOut: true };

  try {
    const res = await fetch(DEEPGRAM_URL, {
      method: 'POST',
      headers: {
        'Authorization': `Token ${process.env.DEEPGRAM_API_KEY || ''}`,
        'Content-Type': 'audio/wav',
      },
      body: wavBuffer,
      signal: AbortSignal.timeout(Math.min(TIMEOUT_MS, remaining)),
    });

    if (!res.ok) {
      log.error('deepgram.failed', { status: res.status });
      return { ok: false };
    }

    const json = await res.json();
    const transcript =
      (json.results &&
        json.results.channels &&
        json.results.channels[0] &&
        json.results.channels[0].alternatives &&
        json.results.channels[0].alternatives[0] &&
        json.results.channels[0].alternatives[0].transcript) || '';
    const audioSeconds =
      (json.metadata && typeof json.metadata.duration === 'number')
        ? json.metadata.duration
        : estimatedSeconds;

    return { ok: true, transcript: transcript.trim(), audioSeconds };
  } catch (err) {
    const timedOut = !!(err && (err.name === 'TimeoutError' || err.name === 'AbortError')) && Date.now() >= deadlineAt;
    log.error('deepgram.failed', { error: String(err.message || err).slice(0, 200), timedOut });
    return timedOut ? { ok: false, timedOut: true } : { ok: false };
  }
}

module.exports = { transcribe, DEEPGRAM_URL };
