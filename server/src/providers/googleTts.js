// Google Cloud TTS provider (spec 4.4 step 8).
// LINEAR16 WAV at an explicit 24000 Hz (the device's playback path reads the
// rate from the WAV header, but we keep it pinned anyway). In-memory LRU cache,
// 200 entries, keyed on text+voice — cache hits cost $0 and skip metering.

const log = require('../log');
const { buildAudioConfig, supportsProsodyParams, isVoiceAllowed } = require('../voice');

const TTS_URL = 'https://texttospeech.googleapis.com/v1/text:synthesize';
const TIMEOUT_MS = 15000;
const MAX_CACHE_ENTRIES = 200;

// Map preserves insertion order: delete+set on hit = LRU.
const cache = new Map(); // key -> audioBase64

// Keyed on what actually goes over the wire: for Chirp voices pitch/rate are
// never sent, so they must not split the cache either.
function cacheKey(text, voice) {
  const audioConfig = buildAudioConfig(voice);
  return [
    voice.languageCode,
    voice.voiceName,
    audioConfig.speakingRate,
    audioConfig.pitch,
    text,
  ].join('|');
}

// Returns { audioBase64, cached } — audioBase64 is '' on failure so the reply
// text still reaches the student (the request must not die because TTS did).
// options.deadlineAt (spec 12 Part L): the /converse request deadline; the
// call's timeout is capped to the time left and `timedOut` is set when the
// deadline — not the provider — ended the attempt.
async function synthesize(text, voice, options) {
  if (!text) return { audioBase64: '', cached: false };
  const deadlineAt = options && Number.isFinite(options.deadlineAt) ? options.deadlineAt : Infinity;

  // Spec 12 Part C: generative (Chirp) voices are blocked unless
  // ALLOW_GENERATIVE_TTS=true. Fail soft like any other TTS failure — the reply
  // text still reaches the student — but say why in the logs (voice name only).
  if (!isVoiceAllowed(voice.voiceName)) {
    log.error('tts.generative_voice_blocked', { voiceName: voice.voiceName });
    return { audioBase64: '', cached: false, failed: true, blocked: true };
  }

  const key = cacheKey(text, voice);
  if (cache.has(key)) {
    const audioBase64 = cache.get(key);
    cache.delete(key);
    cache.set(key, audioBase64);
    return { audioBase64, cached: true };
  }

  // Spec 10: Chirp voices get audioEncoding + sampleRateHertz only. Voice name
  // and the mode only — never the text (spec 4.8: no conversation text in logs).
  const prosody = supportsProsodyParams(voice.voiceName);
  log.debug('tts.request', { voiceName: voice.voiceName, prosodyParams: prosody });

  const remaining = deadlineAt - Date.now();
  if (remaining <= 0) return { audioBase64: '', cached: false, failed: true, timedOut: true };

  try {
    const res = await fetch(`${TTS_URL}`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-goog-api-key': process.env.GOOGLE_TTS_API_KEY || '',
      },
      body: JSON.stringify({
        // Plain text, never SSML — Chirp 3: HD does not accept SSML input.
        input: { text },
        voice: { languageCode: voice.languageCode, name: voice.voiceName },
        audioConfig: buildAudioConfig(voice),
      }),
      signal: AbortSignal.timeout(Math.min(TIMEOUT_MS, remaining)),
    });

    if (!res.ok) {
      // voiceName included so a rejected/unavailable voice (e.g. a Chirp name
      // or region issue) is diagnosable from the logs alone.
      log.error('tts.failed', { status: res.status, voiceName: voice.voiceName });
      return { audioBase64: '', cached: false, failed: true };
    }

    const json = await res.json();
    const audioBase64 = json.audioContent || '';
    if (!audioBase64) {
      log.error('tts.empty_audio_content');
      return { audioBase64: '', cached: false, failed: true };
    }

    if (cache.size >= MAX_CACHE_ENTRIES) {
      cache.delete(cache.keys().next().value); // evict least recently used
    }
    cache.set(key, audioBase64);
    return { audioBase64, cached: false };
  } catch (err) {
    const timedOut = !!(err && (err.name === 'TimeoutError' || err.name === 'AbortError')) && Date.now() >= deadlineAt;
    log.error('tts.failed', { error: String(err.message || err).slice(0, 200), timedOut });
    return { audioBase64: '', cached: false, failed: true, timedOut };
  }
}

module.exports = { synthesize };
