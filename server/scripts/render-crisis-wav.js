// Renders the crisis message in a character's voice.
//
// Spec 8, item 3: now character-parameterised. Both arguments are optional and
// default to the original Einstein behaviour, so existing instructions still work:
//
//   GOOGLE_TTS_API_KEY=<key> node server/scripts/render-crisis-wav.js
//     -> Einstein voice, writes crisis.wav to the repo root (unchanged)
//
//   GOOGLE_TTS_API_KEY=<key> node server/scripts/render-crisis-wav.js server/scripts/seed-mlk.json crisis-mlk.wav
//     -> MLK's voice block (en-US-Chirp3-HD-Charon), writes crisis-mlk.wav
//
// The audioConfig comes from renderSpeech, which applies the shared voice
// capability rule (src/voice.js) — Chirp voices are rendered with no
// pitch/speakingRate, exactly as the live proxy sends them.
//
// The text is always CRISIS_MESSAGE from src/safety.js — the single source of
// truth for that wording, which is pending legal/school review and must never be
// retyped or paraphrased here.

const path = require('path');

const { CRISIS_MESSAGE } = require('../src/safety');
const { renderSpeech } = require('./render-speech-wav');

const DEFAULT_SEED = path.join(__dirname, 'seed-einstein.json');
const DEFAULT_OUT = 'crisis.wav';

async function main() {
  const [seedPath = DEFAULT_SEED, outPath = DEFAULT_OUT] = process.argv.slice(2);

  await renderSpeech({
    seedPath,
    outPath,
    text: CRISIS_MESSAGE,
  });
}

main().catch((err) => {
  console.error(err.message);
  process.exit(1);
});
