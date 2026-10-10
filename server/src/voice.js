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

// False for Chirp voices (no prosody params, no SSML), true for everything else.
function supportsProsodyParams(voiceName) {
  return !/chirp/i.test(String(voiceName || ''));
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

module.exports = { supportsProsodyParams, buildAudioConfig, SAMPLE_RATE_HZ };
