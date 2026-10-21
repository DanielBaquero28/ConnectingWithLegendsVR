// POST /report (spec 12 Part M) — the student pressed "Report this answer".
// The headset sends { sessionId, characterId } and nothing else; the server
// looks up the LAST CHARACTER REPLY in that session's document and writes a
// `user_report` event carrying that reply only — never the student's text —
// with the usual 30-day TTL, then fires the text-free #hsvr-safety alert.
// One report per character reply: a repeat for the same reply is ignored
// (200, { ignored: "duplicate" }) rather than written twice.
//
// Fire-and-forget on the device, so the handler is deliberately cheap.

const express = require('express');
const { deviceAuth } = require('./auth');
const { getDb } = require('./db');
const characters = require('./characters');
const sessions = require('./sessions');
const events = require('./events');
const alerts = require('./alerts');
const log = require('./log');

const router = express.Router();

const ALLOWED_FIELDS = ['sessionId', 'characterId'];

async function schoolName(schoolId) {
  try {
    const snap = await getDb().collection('schools').doc(schoolId).get();
    return (snap.exists && snap.data().name) || schoolId;
  } catch (err) {
    return schoolId;
  }
}

router.post('/', deviceAuth, async (req, res, next) => {
  try {
    const body = req.body && typeof req.body === 'object' ? req.body : {};
    const { sessionId, characterId } = body;

    if (!sessions.isValidSessionId(sessionId)) {
      return res.status(400).json({ error: 'Missing or invalid sessionId' });
    }
    if (!characters.isValidCharacterId(characterId)) {
      return res.status(400).json({ error: 'Missing or invalid characterId' });
    }
    // Defence in depth: refuse any body that tries to carry text.
    for (const key of Object.keys(body)) {
      if (!ALLOWED_FIELDS.includes(key)) {
        return res.status(400).json({ error: `Unexpected field "${key}"` });
      }
    }

    const device = req.device;
    const claim = await sessions.claimLastReplyForReport(sessionId, device.headsetId);
    if (claim.status === 'owned_by_other') {
      return res.status(403).json({ error: 'Session belongs to another device' });
    }
    if (claim.status === 'no_reply') {
      // Nothing to report yet (no stored reply, e.g. a redirect-only session).
      return res.status(409).json({ error: 'No character reply to report' });
    }
    if (claim.status === 'duplicate') {
      log.info('report.ignored_duplicate', { headsetId: device.headsetId, characterId });
      return res.status(200).json({ ok: true, ignored: 'duplicate' });
    }

    const eventId = await events.logEvent({
      type: 'user_report', severity: 'warning',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      reply: claim.replyText,
      detail: { characterId, sessionCharacterId: claim.characterId },
    });
    alerts.alertUserReport({
      schoolName: await schoolName(device.schoolId),
      headsetLabel: device.label,
      characterId,
      eventId,
    });
    log.warn('report.received', { headsetId: device.headsetId, characterId, eventId });
    return res.status(202).json({ ok: true, eventId });
  } catch (err) {
    return next(err);
  }
});

module.exports = { router };
