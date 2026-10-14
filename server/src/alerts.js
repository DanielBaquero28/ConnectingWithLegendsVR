// Slack alerting (spec 4.7). Two webhooks: safety and ops. Throttle state
// lives in Firestore alertState/ docs so it survives instance restarts.
//
// Alert SENDS must never break the request path: they are fire-and-forget
// with their own try/catch and are never awaited by callers. The Firestore
// throttle bookkeeping IS awaited (it decides whether to fire and must be
// race-safe), but every public function here swallows its own errors.

const { getDb } = require('./db');
const log = require('./log');

const MIN = 60 * 1000;
const RULES = {
  pattern: { windowMs: 30 * MIN, threshold: 5, cooldownMs: 60 * MIN }, // per headset
  providerErrors: { windowMs: 5 * MIN, threshold: 3, cooldownMs: 15 * MIN },
  serverErrors: { windowMs: 5 * MIN, threshold: 3, cooldownMs: 15 * MIN },
};

function send(webhookEnvName, text) {
  const url = process.env[webhookEnvName];
  if (!url) {
    log.warn('alerts.webhook_not_configured', { webhook: webhookEnvName });
    return;
  }
  // Fire-and-forget: not awaited, can never throw into the request path.
  fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
    signal: AbortSignal.timeout(10000),
  })
    .then((res) => {
      if (!res.ok) log.error('alerts.send_failed', { webhook: webhookEnvName, status: res.status });
    })
    .catch((err) => log.error('alerts.send_failed', { webhook: webhookEnvName, error: String(err.message || err).slice(0, 200) }));
}

function stamp() {
  return new Date().toISOString();
}

// Sliding-window throttle on alertState/{key}: appends a hit, drops hits
// older than the window, and reports whether the alert should fire now.
// Transactional so concurrent instances can't double-fire.
async function shouldFire(key, rule) {
  const ref = getDb().collection('alertState').doc(key);
  return getDb().runTransaction(async (tx) => {
    const snap = await tx.get(ref);
    const now = Date.now();
    const data = snap.exists ? snap.data() : {};
    const cooldownUntil = data.cooldownUntil || 0;
    const hits = (Array.isArray(data.hits) ? data.hits : []).filter((t) => now - t < rule.windowMs);
    hits.push(now);

    if (now >= cooldownUntil && hits.length >= rule.threshold) {
      tx.set(ref, { hits: [], cooldownUntil: now + rule.cooldownMs });
      return true;
    }
    tx.set(ref, { hits, cooldownUntil });
    return false;
  });
}

// 🚨 CRISIS — immediate, never throttled. Snippet + sessionId included so a
// human can act on it (spec 4.7).
function alertCrisis({ schoolName, headsetLabel, sessionId, snippet }) {
  try {
    send(
      'SLACK_WEBHOOK_SAFETY',
      `🚨 CRISIS — school: ${schoolName} — headset: ${headsetLabel} — session: ${sessionId}\n` +
        `> "${snippet}"\n${stamp()}`
    );
  } catch (err) {
    log.error('alerts.crisis_failed', { error: String(err.message || err).slice(0, 200) });
  }
}

// ⚠️ PATTERN — one headset accruing ≥5 profanity/jailbreak events in 30 min.
// Individual teenage one-offs do NOT alert; only patterns.
async function feedPattern({ headsetId, schoolName, headsetLabel }) {
  try {
    if (await shouldFire(`pattern_${headsetId}`, RULES.pattern)) {
      send(
        'SLACK_WEBHOOK_SAFETY',
        `⚠️ PATTERN — school: ${schoolName} — headset: ${headsetLabel} — ` +
          `5+ profanity/jailbreak events within 30 minutes.\n${stamp()}`
      );
    }
  } catch (err) {
    log.error('alerts.pattern_failed', { error: String(err.message || err).slice(0, 200) });
  }
}

// 💰 BUDGET — fired by the caller when metering reports a newly crossed
// threshold (thresholdsFired guarantees once per school per month).
function alertBudget({ schoolName, percent, totalCostUsd, monthlyBudgetUsd }) {
  try {
    send(
      'SLACK_WEBHOOK_OPS',
      `💰 BUDGET — school: ${schoolName} reached ${percent}% of monthly budget ` +
        `($${totalCostUsd.toFixed(2)} of $${monthlyBudgetUsd.toFixed(2)}).\n${stamp()}`
    );
  } catch (err) {
    log.error('alerts.budget_failed', { error: String(err.message || err).slice(0, 200) });
  }
}

// 🔥 ERRORS — ≥3 provider failures (post-retry) within 5 minutes.
async function feedProviderError(provider, { schoolName, headsetLabel } = {}) {
  try {
    if (await shouldFire('provider_errors', RULES.providerErrors)) {
      send(
        'SLACK_WEBHOOK_OPS',
        `🔥 ERRORS — 3+ provider failures within 5 minutes ` +
          `(latest: ${provider}, school: ${schoolName || 'n/a'}, headset: ${headsetLabel || 'n/a'}).\n${stamp()}`
      );
    }
  } catch (err) {
    log.error('alerts.provider_errors_failed', { error: String(err.message || err).slice(0, 200) });
  }
}

// 🟥 5xx — the server itself threw unhandled errors.
async function feedServerError() {
  try {
    if (await shouldFire('server_errors', RULES.serverErrors)) {
      send('SLACK_WEBHOOK_OPS', `🟥 5xx — 3+ unhandled server errors within 5 minutes.\n${stamp()}`);
    }
  } catch (err) {
    log.error('alerts.server_errors_failed', { error: String(err.message || err).slice(0, 200) });
  }
}

module.exports = { alertCrisis, feedPattern, alertBudget, feedProviderError, feedServerError };
