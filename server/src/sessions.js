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

// Spec 12 Part M: POST /report. Finds the last character ("model") reply in
// the session and claims it for a user report, exactly once. The claim is a
// transaction on the session doc: `reportedReplyAt` records the `updatedAt`
// of the exchange that was reported, so a second report of the same reply
// is a duplicate, and the next appendExchange (a full set() with a fresh
// updatedAt and no reportedReplyAt) re-arms it for the following reply.
// Returns { status, replyText, characterId } where status is one of
// 'ok' | 'duplicate' | 'no_reply' | 'owned_by_other'. Only the character's
// reply is ever returned — never a user turn.
async function claimLastReplyForReport(sessionId, headsetId) {
  const ref = getDb().collection('sessions').doc(sessionId);
  return getDb().runTransaction(async (tx) => {
    const snap = await tx.get(ref);
    if (!snap.exists) return { status: 'no_reply' };
    const data = snap.data();
    if (data.headsetId && data.headsetId !== headsetId) return { status: 'owned_by_other' };

    const turns = Array.isArray(data.turns) ? data.turns : [];
    const last = turns.length > 0 ? turns[turns.length - 1] : null;
    if (!last || last.role !== 'model' || typeof last.text !== 'string' || last.text === '') {
      return { status: 'no_reply' };
    }

    const replyAt = toMillis(data.updatedAt);
    const reportedAt = toMillis(data.reportedReplyAt);
    if (replyAt !== null && reportedAt !== null && reportedAt === replyAt) {
      return { status: 'duplicate' };
    }
    tx.set(ref, { reportedReplyAt: data.updatedAt || new Date() }, { merge: true });
    return { status: 'ok', replyText: last.text, characterId: data.characterId || null };
  });
}

// Firestore hands back a Timestamp; the fake store and fresh writes hold a Date.
function toMillis(v) {
  if (!v) return null;
  if (v instanceof Date) return v.getTime();
  if (typeof v.toMillis === 'function') return v.toMillis();
  if (typeof v === 'number') return v;
  return null;
}

module.exports = { isValidSessionId, loadSession, appendExchange, claimLastReplyForReport };
