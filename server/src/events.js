// Event logging (spec 4.3 events/{autoId}, 4.8 retention; spec 12 Part K
// data minimisation).
// The snippet on a server-detected `crisis` event is the ONLY student text
// that ever lands here — profanity, jailbreak and safety_block events are
// metadata-only, as are turn_ok and the ops events. A `user_report` event
// (spec 12 Part M) holds the CHARACTER's reported reply, never student text.
// Every event expires after 30 days via the Firestore TTL on expiresAt.

const { getDb } = require('./db');
const log = require('./log');

const DAY_MS = 24 * 60 * 60 * 1000;
const RETENTION_DAYS = 30;
const SNIPPET_EVENT_TYPES = new Set(['crisis']);
const MAX_SNIPPET_CHARS = 300;
// Spec 12 Part M: a `user_report` event carries the CHARACTER's reply the
// student reported (never their own text) under `reply`. Replies are capped
// at 40 words server-side, so 600 chars is already generous.
const REPLY_EVENT_TYPES = new Set(['user_report']);
const MAX_REPLY_CHARS = 600;

// Never throws — an event-write failure must not take down the request path.
// Returns the new event's document ID (spec 12 Part G: Slack alerts reference
// it instead of carrying any text), or null if the write failed.
async function logEvent({ type, severity, schoolId, headsetId, sessionId, snippet, reply, detail }) {
  try {
    const now = new Date();
    const retentionDays = RETENTION_DAYS;
    const doc = {
      type,
      severity,
      schoolId: schoolId || null,
      headsetId: headsetId || null,
      sessionId: sessionId || null,
      createdAt: now,
      expiresAt: new Date(now.getTime() + retentionDays * DAY_MS),
    };
    // Spec 12 Part K: a snippet is stored on crisis events only; any snippet
    // passed for another type is dropped here, not just at the call sites.
    if (snippet && SNIPPET_EVENT_TYPES.has(type)) doc.snippet = String(snippet).slice(0, MAX_SNIPPET_CHARS);
    // Spec 12 Part M: the reported character reply is stored on user_report
    // events only; any `reply` passed for another type is dropped here.
    if (reply && REPLY_EVENT_TYPES.has(type)) doc.reply = String(reply).slice(0, MAX_REPLY_CHARS);
    if (detail) doc.detail = detail;
    const ref = await getDb().collection('events').add(doc);
    return ref.id;
  } catch (err) {
    log.error('events.write_failed', { type, error: String(err.message || err).slice(0, 200) });
    return null;
  }
}

// Recent events for GET /admin/events. Filter combinations beyond a single
// field may need a composite index (Firestore's error includes a create link).
async function queryEvents({ type, schoolId, limit }) {
  let q = getDb().collection('events');
  if (type) q = q.where('type', '==', type);
  if (schoolId) q = q.where('schoolId', '==', schoolId);
  q = q.orderBy('createdAt', 'desc').limit(Math.min(Number(limit) || 50, 200));
  const snap = await q.get();
  return snap.docs.map((d) => ({ id: d.id, ...d.data() }));
}

module.exports = { logEvent, queryEvents };
