// Admin endpoints (spec 4.6), all behind X-Admin-Key. These are Daniel's
// provisioning/inspection tools until there is a dashboard.

const crypto = require('crypto');
const express = require('express');
const { adminAuth } = require('./auth');
const { getDb } = require('./db');
const characters = require('./characters');
const claude = require('./providers/claude');
const voice = require('./voice');
const metering = require('./metering');
const events = require('./events');
const log = require('./log');

const router = express.Router();
router.use(adminAuth);

// POST /admin/schools { name, monthlyBudgetUsd } -> { schoolId }
router.post('/schools', async (req, res, next) => {
  try {
    const { name, monthlyBudgetUsd } = req.body || {};
    if (!name || typeof name !== 'string') {
      return res.status(400).json({ error: 'Missing name' });
    }
    const budget = Number(monthlyBudgetUsd);
    if (!Number.isFinite(budget) || budget < 0) {
      return res.status(400).json({ error: 'monthlyBudgetUsd must be a non-negative number' });
    }
    const ref = await getDb().collection('schools').add({
      name,
      monthlyBudgetUsd: budget,
      active: true,
      createdAt: new Date(),
    });
    log.info('admin.school_created', { schoolId: ref.id });
    return res.status(201).json({ schoolId: ref.id });
  } catch (err) {
    return next(err);
  }
});

// POST /admin/headsets { schoolId, label } -> { deviceToken, headsetId }
// The token (doc ID) is the secret the device holds; headsetId is the short
// non-secret identifier used in sessions/usage/events/alerts.
router.post('/headsets', async (req, res, next) => {
  try {
    const { schoolId, label } = req.body || {};
    if (!schoolId || !label) {
      return res.status(400).json({ error: 'Missing schoolId or label' });
    }
    const school = await getDb().collection('schools').doc(schoolId).get();
    if (!school.exists) {
      return res.status(404).json({ error: 'Unknown school' });
    }

    const deviceToken = crypto.randomBytes(24).toString('hex'); // 48 hex chars
    const headsetId = 'hs_' + crypto.randomBytes(4).toString('hex');
    await getDb().collection('headsets').doc(deviceToken).set({
      schoolId,
      headsetId,
      label,
      active: true,
      createdAt: new Date(),
    });
    log.info('admin.headset_created', { schoolId, headsetId });
    return res.status(201).json({ deviceToken, headsetId });
  } catch (err) {
    return next(err);
  }
});

// POST /admin/headsets/:token/deactivate — lost/stolen kill-switch.
router.post('/headsets/:token/deactivate', async (req, res, next) => {
  try {
    const ref = getDb().collection('headsets').doc(req.params.token);
    const snap = await ref.get();
    if (!snap.exists) {
      return res.status(404).json({ error: 'Unknown headset token' });
    }
    await ref.set({ active: false }, { merge: true });
    log.info('admin.headset_deactivated', { headsetId: snap.data().headsetId });
    return res.json({ ok: true });
  } catch (err) {
    return next(err);
  }
});

// PUT /admin/characters/:characterId — full-doc upsert (Einstein seeding/updates).
router.put('/characters/:characterId', async (req, res, next) => {
  try {
    const characterId = req.params.characterId;
    if (!characters.isValidCharacterId(characterId)) {
      return res.status(400).json({ error: 'Invalid characterId' });
    }
    const doc = req.body;
    if (!doc || typeof doc !== 'object' || !doc.systemPersona) {
      return res.status(400).json({ error: 'Body must be a full character doc (systemPersona required)' });
    }
    // Spec 12: a model with no price entry would be metered at the wrong rate
    // (and may not exist); an effort the docs don't list would 400 at runtime.
    // Both fail here, at seed time, instead.
    if (doc.modelName !== undefined && !metering.isKnownModel(doc.modelName)) {
      return res.status(400).json({
        error: `Unknown modelName; must be one of: ${Object.keys(metering.PRICES.llmPerMTokens).join(', ')}`,
      });
    }
    if (doc.effort !== undefined && !claude.EFFORT_VALUES.includes(doc.effort)) {
      return res.status(400).json({ error: `Invalid effort; must be one of: ${claude.EFFORT_VALUES.join(', ')}` });
    }
    // Spec 12 Part C: no generative (Chirp) voice may be seeded while
    // ALLOW_GENERATIVE_TTS is off — fail here, loudly, not silently at synth time.
    const voiceName = doc.voice && doc.voice.voiceName;
    if (voiceName && !voice.isVoiceAllowed(voiceName)) {
      return res.status(400).json({
        error: `voice.voiceName "${voiceName}" is a generative (Chirp) voice, which is disabled ` +
          '(set ALLOW_GENERATIVE_TTS=true to allow it, or recast to a Neural2/Studio/WaveNet voice)',
      });
    }
    await getDb().collection('characters').doc(characterId).set(doc);
    characters.invalidate(characterId); // this instance serves the update immediately
    log.info('admin.character_upserted', { characterId });
    return res.json({ ok: true, characterId });
  } catch (err) {
    return next(err);
  }
});

// GET /admin/usage?schoolId=&month=YYYY-MM — the "dashboard" until there is one.
router.get('/usage', async (req, res, next) => {
  try {
    const { schoolId, month } = req.query;
    if (!schoolId) {
      return res.status(400).json({ error: 'Missing schoolId' });
    }
    const usage = await metering.getUsage(schoolId, month);
    if (!usage) {
      return res.status(404).json({ error: 'No usage recorded for that school/month' });
    }
    return res.json({ schoolId, month: month || metering.currentMonth(), usage });
  } catch (err) {
    return next(err);
  }
});

// GET /admin/events?type=&schoolId=&limit=
router.get('/events', async (req, res, next) => {
  try {
    const { type, schoolId, limit } = req.query;
    const list = await events.queryEvents({ type, schoolId, limit });
    return res.json({ events: list });
  } catch (err) {
    return next(err);
  }
});

module.exports = { router };
