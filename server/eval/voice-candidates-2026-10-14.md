# Voice recast candidates — 2026-10-14

Spec 12 Part C. Output of `scripts/list-voices.js --md` (Google Cloud TTS `voices:list`) for the three locales whose characters currently use Chirp voices: MLK (`en-US-Chirp3-HD-Charon`), Lincoln (`en-US-Chirp3-HD-Achird`), Caesar (`it-IT-Chirp3-HD-Algieba`). Generative voices (any `*Chirp*` name, and the bare Chirp 3: HD names such as `Charon`, `Puck`, `Achird`, `Algieba` that the API lists without a locale prefix) are excluded: they are disabled unless `ALLOW_GENERATIVE_TTS=true`.

Nothing here is a recommendation — Daniel chooses. Male Neural2/Studio voices are the closest match to the current casting; all listed voices accept `pitch`/`speakingRate`, so the seed's prosody fields apply again. After recasting, re-seed and re-render the character's intro WAV (`scripts/render-speech-wav.js`); the crisis WAVs are out of scope for this spec.


### en-US (36 non-generative voices)

| voice | family | gender | sample rate |
|---|---|---|---|
| `en-US-Casual-K` | Casual | MALE | 24000 |
| `en-US-Neural2-A` | Neural2 | MALE | 24000 |
| `en-US-Neural2-C` | Neural2 | FEMALE | 24000 |
| `en-US-Neural2-D` | Neural2 | MALE | 24000 |
| `en-US-Neural2-E` | Neural2 | FEMALE | 24000 |
| `en-US-Neural2-F` | Neural2 | FEMALE | 24000 |
| `en-US-Neural2-G` | Neural2 | FEMALE | 24000 |
| `en-US-Neural2-H` | Neural2 | FEMALE | 24000 |
| `en-US-Neural2-I` | Neural2 | MALE | 24000 |
| `en-US-Neural2-J` | Neural2 | MALE | 24000 |
| `en-US-News-K` | News | FEMALE | 24000 |
| `en-US-News-L` | News | FEMALE | 24000 |
| `en-US-News-N` | News | MALE | 24000 |
| `en-US-Polyglot-1` | Polyglot | MALE | 24000 |
| `en-US-Standard-A` | Standard | MALE | 24000 |
| `en-US-Standard-B` | Standard | MALE | 24000 |
| `en-US-Standard-C` | Standard | FEMALE | 24000 |
| `en-US-Standard-D` | Standard | MALE | 24000 |
| `en-US-Standard-E` | Standard | FEMALE | 24000 |
| `en-US-Standard-F` | Standard | FEMALE | 24000 |
| `en-US-Standard-G` | Standard | FEMALE | 24000 |
| `en-US-Standard-H` | Standard | FEMALE | 24000 |
| `en-US-Standard-I` | Standard | MALE | 24000 |
| `en-US-Standard-J` | Standard | MALE | 24000 |
| `en-US-Studio-O` | Studio | FEMALE | 24000 |
| `en-US-Studio-Q` | Studio | MALE | 24000 |
| `en-US-Wavenet-A` | Wavenet | MALE | 24000 |
| `en-US-Wavenet-B` | Wavenet | MALE | 24000 |
| `en-US-Wavenet-C` | Wavenet | FEMALE | 24000 |
| `en-US-Wavenet-D` | Wavenet | MALE | 24000 |
| `en-US-Wavenet-E` | Wavenet | FEMALE | 24000 |
| `en-US-Wavenet-F` | Wavenet | FEMALE | 24000 |
| `en-US-Wavenet-G` | Wavenet | FEMALE | 24000 |
| `en-US-Wavenet-H` | Wavenet | FEMALE | 24000 |
| `en-US-Wavenet-I` | Wavenet | MALE | 24000 |
| `en-US-Wavenet-J` | Wavenet | MALE | 24000 |

### en-GB (30 non-generative voices)

| voice | family | gender | sample rate |
|---|---|---|---|
| `en-GB-Neural2-A` | Neural2 | FEMALE | 24000 |
| `en-GB-Neural2-B` | Neural2 | MALE | 24000 |
| `en-GB-Neural2-C` | Neural2 | FEMALE | 24000 |
| `en-GB-Neural2-D` | Neural2 | MALE | 24000 |
| `en-GB-Neural2-F` | Neural2 | FEMALE | 24000 |
| `en-GB-Neural2-N` | Neural2 | FEMALE | 24000 |
| `en-GB-Neural2-O` | Neural2 | MALE | 24000 |
| `en-GB-News-G` | News | FEMALE | 24000 |
| `en-GB-News-H` | News | FEMALE | 24000 |
| `en-GB-News-I` | News | FEMALE | 24000 |
| `en-GB-News-J` | News | MALE | 24000 |
| `en-GB-News-K` | News | MALE | 24000 |
| `en-GB-News-L` | News | MALE | 24000 |
| `en-GB-News-M` | News | MALE | 24000 |
| `en-GB-Standard-A` | Standard | FEMALE | 24000 |
| `en-GB-Standard-B` | Standard | MALE | 24000 |
| `en-GB-Standard-C` | Standard | FEMALE | 24000 |
| `en-GB-Standard-D` | Standard | MALE | 24000 |
| `en-GB-Standard-F` | Standard | FEMALE | 24000 |
| `en-GB-Standard-N` | Standard | FEMALE | 24000 |
| `en-GB-Standard-O` | Standard | MALE | 24000 |
| `en-GB-Studio-B` | Studio | MALE | 24000 |
| `en-GB-Studio-C` | Studio | FEMALE | 24000 |
| `en-GB-Wavenet-A` | Wavenet | FEMALE | 24000 |
| `en-GB-Wavenet-B` | Wavenet | MALE | 24000 |
| `en-GB-Wavenet-C` | Wavenet | FEMALE | 24000 |
| `en-GB-Wavenet-D` | Wavenet | MALE | 24000 |
| `en-GB-Wavenet-F` | Wavenet | FEMALE | 24000 |
| `en-GB-Wavenet-N` | Wavenet | FEMALE | 24000 |
| `en-GB-Wavenet-O` | Wavenet | MALE | 24000 |

### it-IT (7 non-generative voices)

| voice | family | gender | sample rate |
|---|---|---|---|
| `it-IT-Neural2-A` | Neural2 | FEMALE | 24000 |
| `it-IT-Neural2-E` | Neural2 | FEMALE | 24000 |
| `it-IT-Neural2-F` | Neural2 | MALE | 24000 |
| `it-IT-Standard-E` | Standard | FEMALE | 24000 |
| `it-IT-Standard-F` | Standard | MALE | 24000 |
| `it-IT-Wavenet-E` | Wavenet | FEMALE | 24000 |
| `it-IT-Wavenet-F` | Wavenet | MALE | 24000 |
