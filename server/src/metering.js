// Per-request cost estimation + monthly aggregation (spec 4.5) and the
// budget gate (spec 4.4 step 2). Usage docs live at
// usage/{schoolId}/months/{YYYY-MM} and are updated in a single transaction
// so concurrent requests can't lose increments and the 80%/100% thresholds
// fire exactly once per school per month.

const { getDb } = require('./db');
const log = require('./log');

// ===== Provider prices (USD) — estimates for monitoring, NOT billing-grade.
// Re-verify these periodically against the providers' pricing pages.
const PRICES = {
  // Deepgram Nova-3 pre-recorded: $0.0043/min, billed per second.
  sttPerSecond: 0.0043 / 60,
  // Gemini, per 1M tokens (tokens estimated as chars/4).
  llmPerMTokens: {
    'gemini-2.5-flash-lite': { input: 0.10, output: 0.40 },
    'gemini-2.5-flash': { input: 0.30, output: 2.50 },
  },
  // Google TTS Neural2: $16 per 1M characters (cache hits cost $0).
  ttsPerChar: 16 / 1e6,
};

function estimateCostUsd({ modelName, sttSeconds = 0, llmInputChars = 0, llmOutputChars = 0, ttsChars = 0 }) {
  const llmPrices = PRICES.llmPerMTokens[modelName] || PRICES.llmPerMTokens['gemini-2.5-flash-lite'];
  const sttUsd = sttSeconds * PRICES.sttPerSecond;
  const llmUsd =
    (llmInputChars / 4 / 1e6) * llmPrices.input +
    (llmOutputChars / 4 / 1e6) * llmPrices.output;
  const ttsUsd = ttsChars * PRICES.ttsPerChar;
  return sttUsd + llmUsd + ttsUsd;
}

function currentMonth() {
  return new Date().toISOString().slice(0, 7); // YYYY-MM (UTC)
}

function usageRef(schoolId, month) {
  return getDb().collection('usage').doc(schoolId).collection('months').doc(month);
}

// Budget gate (spec 4.4 step 2): never blocks a classroom mid-lesson. If the
// school already hit 100% this month, mark the usage doc overBudget and keep
// serving — the Slack alert is the enforcement mechanism for now.
// Also resolves the school doc (name for alerts, active kill-switch).
async function gate(schoolId) {
  const db = getDb();
  const [schoolSnap, usageSnap] = await Promise.all([
    db.collection('schools').doc(schoolId).get(),
    usageRef(schoolId, currentMonth()).get(),
  ]);

  if (!schoolSnap.exists) return { allowed: false, reason: 'Unknown school' };
  const school = schoolSnap.data();
  if (school.active !== true) return { allowed: false, reason: 'School deactivated' };

  const usage = usageSnap.exists ? usageSnap.data() : {};
  const limit100Fired = !!(usage.thresholdsFired && usage.thresholdsFired.limit100);
  if (limit100Fired && usage.overBudget !== true) {
    await usageRef(schoolId, currentMonth()).set({ overBudget: true }, { merge: true });
  }

  return {
    allowed: true,
    schoolName: school.name || schoolId,
    monthlyBudgetUsd: school.monthlyBudgetUsd || 0,
    overBudget: limit100Fired,
  };
}

// Transactionally adds this request's usage to the month doc. Returns the
// request cost plus which budget thresholds were newly crossed (caller fires
// the Slack alerts after commit).
async function record({ schoolId, headsetId, monthlyBudgetUsd, modelName, sttSeconds = 0, llmInputChars = 0, llmOutputChars = 0, ttsChars = 0 }) {
  const costUsd = estimateCostUsd({ modelName, sttSeconds, llmInputChars, llmOutputChars, ttsChars });
  const month = currentMonth();
  const ref = usageRef(schoolId, month);
  const db = getDb();

  const result = await db.runTransaction(async (tx) => {
    const snap = await tx.get(ref);
    const u = snap.exists ? snap.data() : {};
    const fired = u.thresholdsFired || { warn80: false, limit100: false };

    const totalCostUsd = (u.totalCostUsd || 0) + costUsd;
    const perHeadset = u.perHeadset || {};
    const mine = perHeadset[headsetId] || { costUsd: 0, requestCount: 0 };
    perHeadset[headsetId] = {
      costUsd: mine.costUsd + costUsd,
      requestCount: mine.requestCount + 1,
    };

    let crossedWarn80 = false;
    let crossedLimit100 = false;
    if (monthlyBudgetUsd > 0) {
      if (!fired.warn80 && totalCostUsd >= 0.8 * monthlyBudgetUsd) {
        fired.warn80 = true;
        crossedWarn80 = true;
      }
      if (!fired.limit100 && totalCostUsd >= monthlyBudgetUsd) {
        fired.limit100 = true;
        crossedLimit100 = true;
      }
    }

    tx.set(ref, {
      totalCostUsd,
      requestCount: (u.requestCount || 0) + 1,
      sttSeconds: (u.sttSeconds || 0) + sttSeconds,
      llmInputChars: (u.llmInputChars || 0) + llmInputChars,
      llmOutputChars: (u.llmOutputChars || 0) + llmOutputChars,
      ttsChars: (u.ttsChars || 0) + ttsChars,
      perHeadset,
      thresholdsFired: fired,
      overBudget: u.overBudget === true,
      updatedAt: new Date(),
    });

    return { totalCostUsd, crossedWarn80, crossedLimit100 };
  });

  return { costUsd, ...result };
}

// Read a usage doc for GET /admin/usage.
async function getUsage(schoolId, month) {
  const snap = await usageRef(schoolId, month || currentMonth()).get();
  return snap.exists ? snap.data() : null;
}

module.exports = { PRICES, estimateCostUsd, currentMonth, gate, record, getUsage };
