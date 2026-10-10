// Auth middleware (spec 4.2).
// - Device requests: Authorization: Bearer <deviceToken>. The token is the doc
//   ID in headsets/; it must exist and be active. Attaches req.device =
//   { schoolId, headsetId, label, deviceToken }.
// - Admin requests: X-Admin-Key must equal the ADMIN_KEY env var.

const crypto = require('crypto');
const { getDb } = require('./db');
const log = require('./log');

async function deviceAuth(req, res, next) {
  try {
    const header = req.get('Authorization') || '';
    const match = header.match(/^Bearer\s+(.+)$/i);
    if (!match) {
      return res.status(401).json({ error: 'Missing or malformed Authorization header' });
    }
    const deviceToken = match[1].trim();

    // Token doubles as the doc ID; reject anything that can't be one.
    if (!deviceToken || deviceToken.includes('/')) {
      return res.status(401).json({ error: 'Invalid device token' });
    }

    const snap = await getDb().collection('headsets').doc(deviceToken).get();
    if (!snap.exists) {
      log.warn('auth.unknown_token');
      return res.status(401).json({ error: 'Unknown device token' });
    }
    const headset = snap.data();
    if (headset.active !== true) {
      log.warn('auth.inactive_token', { headsetId: headset.headsetId });
      return res.status(401).json({ error: 'Device token deactivated' });
    }

    req.device = {
      schoolId: headset.schoolId,
      // Short non-secret ID used everywhere downstream (events, usage,
      // sessions, alerts) so the secret token never leaves headsets/.
      headsetId: headset.headsetId || deviceToken.slice(0, 8),
      label: headset.label || 'unlabeled',
      deviceToken,
    };
    return next();
  } catch (err) {
    return next(err);
  }
}

function adminAuth(req, res, next) {
  const adminKey = process.env.ADMIN_KEY;
  const provided = req.get('X-Admin-Key') || '';
  // Refuse everything when ADMIN_KEY is not configured — never fail open.
  if (!adminKey || !timingSafeEqualStr(provided, adminKey)) {
    return res.status(401).json({ error: 'Unauthorized' });
  }
  return next();
}

function timingSafeEqualStr(a, b) {
  const ha = crypto.createHash('sha256').update(String(a)).digest();
  const hb = crypto.createHash('sha256').update(String(b)).digest();
  return crypto.timingSafeEqual(ha, hb);
}

module.exports = { deviceAuth, adminAuth };
