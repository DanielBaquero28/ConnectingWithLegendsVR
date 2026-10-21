#!/usr/bin/env node
// Lists Google Cloud TTS voices for recasting (spec 12 Part C). Chirp
// (generative) voices are excluded by default because they are disabled unless
// ALLOW_GENERATIVE_TTS=true; pass --include-chirp to see them anyway.
//
//   GOOGLE_TTS_API_KEY=... node server/scripts/list-voices.js [en-US en-GB it-IT] [--include-chirp] [--md]
//
// Default languages: en-US, en-GB, it-IT (MLK, Lincoln, Caesar). Output is one
// line per voice: name, gender, sample rate. --md prints a Markdown table for
// the report. This script chooses nothing — Daniel picks the voices.

'use strict';

const VOICES_URL = 'https://texttospeech.googleapis.com/v1/voices';

async function listVoices(languageCode, apiKey) {
  const res = await fetch(`${VOICES_URL}?languageCode=${encodeURIComponent(languageCode)}`, {
    headers: { 'x-goog-api-key': apiKey },
    signal: AbortSignal.timeout(20000),
  });
  if (!res.ok) throw new Error(`voices:list ${languageCode} failed: ${res.status} ${await res.text()}`);
  const json = await res.json();
  return (json.voices || [])
    .filter((v) => (v.languageCodes || []).includes(languageCode))
    .sort((a, b) => a.name.localeCompare(b.name));
}

function family(name) {
  const m = /^[a-z]{2}-[A-Z]{2}-([A-Za-z0-9]+)/.exec(name);
  return m ? m[1] : 'generative';
}

// voices:list also returns the Chirp 3: HD / Gemini-TTS voices as bare names
// ("Charon", "Puck", ...) with no locale prefix. Those are generative too.
function isGenerative(name) {
  return /chirp/i.test(name) || !/^[a-z]{2}-[A-Z]{2}-/.test(name);
}

async function main() {
  const args = process.argv.slice(2);
  const includeChirp = args.includes('--include-chirp');
  const md = args.includes('--md');
  const langs = args.filter((a) => !a.startsWith('--'));
  const languages = langs.length ? langs : ['en-US', 'en-GB', 'it-IT'];

  const apiKey = process.env.GOOGLE_TTS_API_KEY;
  if (!apiKey) {
    console.error('GOOGLE_TTS_API_KEY is not set.');
    process.exit(1);
  }

  for (const lang of languages) {
    const voices = (await listVoices(lang, apiKey))
      .filter((v) => includeChirp || !isGenerative(v.name));
    if (md) {
      console.log(`\n### ${lang} (${voices.length} non-generative voices)\n`);
      console.log('| voice | family | gender | sample rate |');
      console.log('|---|---|---|---|');
      for (const v of voices) {
        console.log(`| \`${v.name}\` | ${family(v.name)} | ${v.ssmlGender} | ${v.naturalSampleRateHertz} |`);
      }
    } else {
      console.log(`\n${lang}: ${voices.length} voices`);
      for (const v of voices) {
        console.log(`  ${v.name.padEnd(28)} ${family(v.name).padEnd(10)} ${String(v.ssmlGender).padEnd(8)} ${v.naturalSampleRateHertz}`);
      }
    }
  }
}

main().catch((err) => {
  console.error(err.message);
  process.exit(1);
});
