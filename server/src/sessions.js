// Conversation history (spec 4.3 sessions/{sessionId}).
// Full conversation text lives ONLY here, with a 24h TTL via `expiresAt`
// (Firestore TTL policy is enabled on that field by hand — see README).
// Turns are capped to the last maxTurns*2 messages on every write.

const { getDb } = require('./db');

const SESSION_TTL_MS = 24 * 60 * 60 * 1000;
// Device sends Guid.NewGuid().ToString(); accept that shape and nothing weirder.
const ID_PATTERN = /^[A-Za-z0-9-]{1,64}$/;

function isValidSessionId(id) {
  return typeof id === 'string' && ID_PATTERN.test(id);
}

// Returns { turns, ownedByOther }. ownedByOther guards against a (guessed)
// session ID from a different headset leaking conversation context.
async function loadSession(sessionId, headsetId) {
  const snap = await getDb().collection('sessions').doc(sessionId).get();
  if (!snap.exists) return { turns: [], ownedByOther: false };
  const data = snap.data();
  if (data.headsetId && data.headsetId !== headsetId) {
    return { turns: [], ownedByOther: true };
  }
  return { turns: Array.isArray(data.turns) ? data.turns : [], ownedByOther: false };
}

// Appends the user+model exchange, caps the window, refreshes the TTL.
async function appendExchange({ sessionId, schoolId, headsetId, characterId, maxTurns, priorTurns, userText, replyText }) {
  const turns = priorTurns.concat([
    { role: 'user', text: userText },
    { role: 'model', text: replyText },
  ]);
  const maxMessages = maxTurns * 2;
  const capped = turns.length > maxMessages ? turns.slice(turns.length - maxMessages) : turns;

  const now = new Date();
  await getDb().collection('sessions').doc(sessionId).set({
    schoolId,
    headsetId,
    characterId,
    turns: capped,
    updatedAt: now,
    expiresAt: new Date(now.getTime() + SESSION_TTL_MS),
  });
}

module.exports = { isValidSessionId, loadSession, appendExchange };
