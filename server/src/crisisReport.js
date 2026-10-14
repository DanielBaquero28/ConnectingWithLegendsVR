// POST /crisis-report (spec 12 Part G) — the headset reports a crisis its
// local SafetyFilter caught on the TYPED path. The device never sends the
// student's text: the body is { sessionId, characterId, source: "typed" }
// and nothing else is accepted. The server writes a `crisis` event (90-day
// TTL, no snippet) and fires the same #hsvr-safety Slack alert the server-
// detected path uses — event type, school, headset label, character,
// timestamp, event ID only.
//
// Fire-and-forget on the device, so this handler is deliberately cheap and
// returns 202 as soon as the event is written.

const express = require('express');
const { deviceAuth } = require('./auth');
const { getDb } = require('./db');
const characters = require('./characters');
const sessions = require('./sessions');
const events = require('./events');
const alerts = require('./alerts');
const log = require('./log');

const router = express.Router();

const ALLOWED_SOURCES = new Set(['typed']);

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
    const { sessionId, characterId, source } = body;

    if (!sessions.isValidSessionId(sessionId)) {
      return res.status(400).json({ error: 'Missing or invalid sessionId' });
    }
    if (!characters.isValidCharacterId(characterId)) {
      return res.status(400).json({ error: 'Missing or invalid characterId' });
    }
    if (!ALLOWED_SOURCES.has(source)) {
      return res.status(400).json({ error: 'source must be "typed"' });
    }
    // Defence in depth: refuse any body that tries to carry text.
    for (const key of Object.keys(body)) {
      if (!['sessionId', 'characterId', 'source'].includes(key)) {
        return res.status(400).json({ error: `Unexpected field "${key}"` });
      }
    }

    const device = req.device;
    const eventId = await events.logEvent({
      type: 'crisis', severity: 'critical',
      schoolId: device.schoolId, headsetId: device.headsetId, sessionId,
      detail: { source, characterId },
    });
    alerts.alertCrisis({
      schoolName: await schoolName(device.schoolId),
      headsetLabel: device.label,
      characterId,
      eventId,
      source,
    });
    log.warn('crisis_report.received', { headsetId: device.headsetId, characterId, source, eventId });
    return res.status(202).json({ ok: true, eventId });
  } catch (err) {
    return next(err);
  }
});

module.exports = { router };
