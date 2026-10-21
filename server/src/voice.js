// Spec 10 — voice capability rules.
//
// Google's Chirp 3: HD voices (e.g. en-US-Chirp3-HD-Charon) do NOT support
// SSML input, `pitch`, or `speakingRate`; sending those audio params can fail
// the synthesis request outright. Every other family we use (Neural2, Studio,
// WaveNet...) needs them, so this is a capability rule keyed on the voice name
// rather than a per-character special case.
//
// This module is the single source of truth for that rule: both the live proxy
// (src/providers/googleTts.js) and the offline WAV renderer
// (scripts/render-speech-wav.js) build their audioConfig here, so pre-rendered
// clips and live replies can never drift apart.
//
// Input is always PLAIN TEXT at both call sites (`input: { text }`, never
// `input: { ssml }`) — keep it that way; Chirp rejects SSML.

const SAMPLE_RATE_HZ = 24000;

// Spec 12 Part C — generative (Chirp) voices are gated. Chirp 3: HD is a
// generative model with different data terms from the Neural2/Studio/WaveNet
// families, so no Chirp voice may be seeded, rendered or synthesized unless
// ALLOW_GENERATIVE_TTS=true is set explicitly. The Chirp handling below
// (no pitch/rate/SSML) is kept intact for when the flag is on.
function isGenerativeVoice(voiceName) {
  return /chirp/i.test(String(voiceName || ''));
}

// Read at call time (not module load) so the smoke test can flip it, and so a
// Cloud Run env change needs no code change.
function generativeTtsAllowed() {
  return process.env.ALLOW_GENERATIVE_TTS === 'true';
}

// True when this voice may be used right now; false for a Chirp voice with the
// flag off. Pure (no throw) for the live path, which must fail soft.
function isVoiceAllowed(voiceName) {
  return !isGenerativeVoice(voiceName) || generativeTtsAllowed();
}

// Throws a clear message for the seed/render path, which must fail LOUDLY.
function assertVoiceAllowed(voiceName, context) {
  if (isVoiceAllowed(voiceName)) return;
  throw new Error(
    `${context ? context + ': ' : ''}voice "${voiceName}" is a generative (Chirp) voice, ` +
    'which is disabled. Recast to a Neural2/Studio/WaveNet voice, or set ALLOW_GENERATIVE_TTS=true.');
}

// False for Chirp voices (no prosody params, no SSML), true for everything else.
function supportsProsodyParams(voiceName) {
  return !isGenerativeVoice(voiceName);
}

// The audioConfig for a character's voice block. For Chirp voices the pitch and
// speakingRate KEYS are absent entirely — not null, not 0 — because the API
// rejects the params rather than ignoring them.
function buildAudioConfig(voice) {
  const audioConfig = {
    audioEncoding: 'LINEAR16',
    sampleRateHertz: SAMPLE_RATE_HZ,
  };

  if (supportsProsodyParams(voice && voice.voiceName)) {
    audioConfig.pitch = voice.pitch;
    audioConfig.speakingRate = voice.speakingRate;
  }

  return audioConfig;
}

module.exports = {
  supportsProsodyParams,
  buildAudioConfig,
  isGenerativeVoice,
  generativeTtsAllowed,
  isVoiceAllowed,
  assertVoiceAllowed,
  SAMPLE_RATE_HZ,
};
