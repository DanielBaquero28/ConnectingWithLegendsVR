// Firestore access for the whole server goes through this module so the smoke
// test can swap in an in-memory fake with setDb(). Nothing else may import
// @google-cloud/firestore directly.
//
// Conventions (keep these so fakes stay trivial):
// - Timestamps are written as plain JS Date objects (the SDK stores them as
//   Firestore Timestamps, which is what the TTL policy on `expiresAt` needs).
// - Counters are updated with transactional read-modify-write, never
//   FieldValue sentinels.

let db = null;

function getDb() {
  if (!db) {
    // Lazy require so the smoke test can inject a fake before (instead of)
    // ever loading the real client.
    const { Firestore } = require('@google-cloud/firestore');
    db = new Firestore(); // Uses ADC: automatic on Cloud Run, gcloud login locally.
  }
  return db;
}

// Test hook: inject a fake Firestore-shaped object.
function setDb(fake) {
  db = fake;
}

module.exports = { getDb, setDb };
