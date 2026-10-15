// Character config loading (spec 4.3 characters/{characterId}) with an
// in-memory cache so a hot path costs zero Firestore reads. Updating a
// character via PUT /admin/characters/:id invalidates the cache entry, and
// other Cloud Run instances pick the change up within CACHE_TTL_MS.

const { getDb } = require('./db');

const CACHE_TTL_MS = 5 * 60 * 1000;
const cache = new Map(); // characterId -> { data, fetchedAt }

const ID_PATTERN = /^[a-z0-9_-]{1,64}$/i;

function isValidCharacterId(id) {
  return typeof id === 'string' && ID_PATTERN.test(id);
}

async function getCharacter(characterId) {
  if (!isValidCharacterId(characterId)) return null;

  const hit = cache.get(characterId);
  if (hit && Date.now() - hit.fetchedAt < CACHE_TTL_MS) return hit.data;

  const snap = await getDb().collection('characters').doc(characterId).get();
  if (!snap.exists) return null;

  const raw = snap.data();
  const data = {
    characterId,
    displayName: raw.displayName || characterId,
    systemPersona: raw.systemPersona || '',
    knowledgeCutoffNote: raw.knowledgeCutoffNote || '',
    fewShotExamples: Array.isArray(raw.fewShotExamples) ? raw.fewShotExamples : [],
    voice: {
      languageCode: (raw.voice && raw.voice.languageCode) || 'en-GB',
      voiceName: (raw.voice && raw.voice.voiceName) || 'en-GB-Neural2-B',
      speakingRate: (raw.voice && raw.voice.speakingRate) || 1.0,
      pitch: raw.voice && typeof raw.voice.pitch === 'number' ? raw.voice.pitch : 0.0,
    },
    // Spec 12: Claude model + adaptive-thinking effort (validated on PUT).
    modelName: raw.modelName || 'claude-haiku-5-5',
    effort: raw.effort || 'low',
    // Spec 12 Part N: "disabled" (production decision) or "adaptive".
    thinking: raw.thinking || 'disabled',
    maxWords: raw.maxWords || 40,
    maxTurns: raw.maxTurns || 8,
  };

  cache.set(characterId, { data, fetchedAt: Date.now() });
  return data;
}

function invalidate(characterId) {
  cache.delete(characterId);
}

module.exports = { getCharacter, invalidate, isValidCharacterId };
