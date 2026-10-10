// Spec 8, items 3 + 4 — general-purpose "render one line in a character's voice"
// tool. Every pre-rendered WAV the app ships (crisis messages, character
// self-introductions) comes from here, so they all use exactly the same voice
// block the live proxy uses for that character — no drift between the spoken
// intro and the spoken replies.
//
// Usage:
//   GOOGLE_TTS_API_KEY=<key> node server/scripts/render-speech-wav.js <seed.json> <out.wav> <text>
//
// <text> may be either the literal line, or "@path/to/file.txt" to read it from
// a file — use the file form for anything with apostrophes, em-dashes or quotes,
// which shells mangle (this matters on Windows/PowerShell in particular).
//
// Examples:
//   node server/scripts/render-speech-wav.js server/scripts/seed-mlk.json intro-mlk.wav @server/scripts/intro-mlk.txt
//   node server/scripts/render-speech-wav.js server/scripts/seed-einstein.json hello.wav "Good day!"
//
// Paths are resolved relative to the current working directory; a bare output
// filename lands in the repo root (where the other rendered WAVs live).

const fs = require('fs');
const path = require('path');

const { buildAudioConfig, supportsProsodyParams } = require('../src/voice');

const TTS_URL = 'https://texttospeech.googleapis.com/v1/text:synthesize';
const REPO_ROOT = path.join(__dirname, '..', '..');

// Reads a character seed JSON and returns its voice block, failing loudly if the
// file is missing a field — a silently-undefined voiceName renders in the wrong
// voice, which is worse than a crash.
function loadVoice(seedPath) {
  const resolved = path.resolve(seedPath);
  if (!fs.existsSync(resolved)) {
    throw new Error(`Seed file not found: ${resolved}`);
  }

  const seed = JSON.parse(fs.readFileSync(resolved, 'utf8'));
  const voice = seed.voice;
  if (!voice) {
    throw new Error(`Seed ${resolved} has no "voice" block`);
  }

  // Chirp voices never send pitch/speakingRate (spec 10), so only the fields
  // that actually reach the API are required to be present.
  const required = supportsProsodyParams(voice.voiceName)
    ? ['languageCode', 'voiceName', 'speakingRate', 'pitch']
    : ['languageCode', 'voiceName'];

  for (const field of required) {
    if (voice[field] === undefined) {
      throw new Error(`Seed ${resolved} voice block is missing "${field}"`);
    }
  }

  return { voice, displayName: seed.displayName || path.basename(resolved) };
}

// "@file" reads the line from disk; anything else is the literal text.
function resolveText(textArg) {
  if (!textArg) {
    throw new Error('No text given');
  }
  if (!textArg.startsWith('@')) {
    return textArg;
  }

  const resolved = path.resolve(textArg.slice(1));
  if (!fs.existsSync(resolved)) {
    throw new Error(`Text file not found: ${resolved}`);
  }
  const text = fs.readFileSync(resolved, 'utf8').trim();
  if (!text) {
    throw new Error(`Text file is empty: ${resolved}`);
  }
  return text;
}

// Renders `text` in the seed's voice and writes a WAV to outPath. Exported so
// render-crisis-wav.js can reuse it without duplicating the request shape.
async function renderSpeech({ seedPath, text, outPath }) {
  const apiKey = process.env.GOOGLE_TTS_API_KEY;
  if (!apiKey) {
    throw new Error('GOOGLE_TTS_API_KEY is not set');
  }

  const { voice, displayName } = loadVoice(seedPath);
  // A bare filename goes to the repo root, matching where crisis.wav has always
  // been written; an explicit relative/absolute path is honoured as given.
  const resolvedOut = path.isAbsolute(outPath) || outPath.includes(path.sep) || outPath.includes('/')
    ? path.resolve(outPath)
    : path.join(REPO_ROOT, outPath);

  // Identical capability rule to the live proxy (shared src/voice.js): Chirp
  // voices get no pitch/speakingRate, everything else is unchanged. Plain-text
  // input only — never SSML.
  const audioConfig = buildAudioConfig(voice);

  const res = await fetch(TTS_URL, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-goog-api-key': apiKey,
    },
    body: JSON.stringify({
      input: { text },
      voice: { languageCode: voice.languageCode, name: voice.voiceName },
      audioConfig,
    }),
  });

  if (!res.ok) {
    throw new Error(`TTS request failed: ${res.status} ${await res.text()}`);
  }

  const json = await res.json();
  if (!json.audioContent) {
    throw new Error('TTS response had no audioContent');
  }

  // LINEAR16 comes back as a complete WAV container, so the decoded bytes are
  // already a valid .wav file.
  fs.writeFileSync(resolvedOut, Buffer.from(json.audioContent, 'base64'));
  const prosody = audioConfig.speakingRate === undefined
    ? 'no rate/pitch — Chirp voice'
    : `rate ${audioConfig.speakingRate}, pitch ${audioConfig.pitch}`;
  console.log(`Rendered ${displayName} (${voice.voiceName}, ${prosody}) -> ${resolvedOut}`);
  return resolvedOut;
}

async function main() {
  const [seedPath, outPath, ...textParts] = process.argv.slice(2);
  if (!seedPath || !outPath || textParts.length === 0) {
    throw new Error(
      'Usage: node server/scripts/render-speech-wav.js <seed.json> <out.wav> <text|@file.txt>'
    );
  }

  await renderSpeech({
    seedPath,
    outPath,
    // Join so an unquoted multi-word line still works.
    text: resolveText(textParts.join(' ')),
  });
}

// Only run the CLI when invoked directly, so requiring this module is side-effect free.
if (require.main === module) {
  main().catch((err) => {
    console.error(err.message);
    process.exit(1);
  });
}

module.exports = { renderSpeech, loadVoice, resolveText };
