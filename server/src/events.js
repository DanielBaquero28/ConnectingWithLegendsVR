// Event logging (spec 4.3 events/{autoId}, 4.8 retention).
// Flagged snippets (crisis/profanity/jailbreak/safety_block) are the ONLY
// student text that ever lands here; turn_ok events are metadata-only.
// Retention via Firestore TTL on expiresAt: safety events 90 days, ops 30.

const { getDb } = require('./db');
const log = require('./log');

const DAY_MS = 24 * 60 * 60 * 1000;
const SAFETY_EVENT_TYPES = new Set(['crisis', 'profanity', 'jailbreak', 'safety_block']);
const MAX_SNIPPET_CHARS = 300;

// Never throws — an event-write failure must not take down the request path.
async function logEvent({ type, severity, schoolId, headsetId, sessionId, snippet, detail }) {
  try {
    const now = new Date();
    const retentionDays = SAFETY_EVENT_TYPES.has(type) ? 90 : 30;
    const doc = {
      type,
      severity,
      schoolId: schoolId || null,
      headsetId: headsetId || null,
      sessionId: sessionId || null,
      createdAt: now,
      expiresAt: new Date(now.getTime() + retentionDays * DAY_MS),
    };
    if (snippet) doc.snippet = String(snippet).slice(0, MAX_SNIPPET_CHARS);
    if (detail) doc.detail = detail;
    await getDb().collection('events').add(doc);
  } catch (err) {
    log.error('events.write_failed', { type, message: err.message });
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
