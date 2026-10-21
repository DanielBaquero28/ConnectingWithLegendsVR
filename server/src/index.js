// History Speaks VR proxy — Express app and routes (spec 3/4).
// The app object is exported so scripts/smoke.js can drive it in-process;
// listen() only runs when this file is the entrypoint.

const express = require('express');
const log = require('./log');

const app = express();

// Request size limits (spec 4.9): a 12s 16kHz WAV is ~380 KB, so 5 MB is roomy.
// Raw body for the voice path; JSON for the typed path and admin endpoints.
app.use(express.raw({ type: 'audio/wav', limit: '5mb' }));
app.use(express.json({ limit: '5mb' }));

// Uptime check (spec 4.9). Route is /status, not /healthz: Cloud Run's Google
// Frontend reserves /healthz and intercepts it before it reaches the app.
app.get('/status', (req, res) => {
  res.json({ ok: true });
});

// The single conversation endpoint (spec 4.4).
app.use('/converse', require('./converse').router);

// Device-reported typed-path crisis (spec 12 Part G): metadata only, no text.
app.use('/crisis-report', require('./crisisReport').router);

// "Report this answer" (spec 12 Part M): metadata only, the server attaches
// the last character reply itself.
app.use('/report', require('./report').router);

// Provisioning/inspection endpoints behind X-Admin-Key (spec 4.6).
app.use('/admin', require('./admin').router);

// Unhandled errors: log as structured JSON, feed the 🟥 5xx alert counter,
// and return a generic 500.
app.use((err, req, res, next) => {
  if (err && err.type === 'entity.too.large') {
    return res.status(413).json({ error: 'Request too large' });
  }
  log.error('server.unhandled_error', { path: req.path, error: String((err && err.message) || err).slice(0, 200) });
  require('./alerts').feedServerError();
  if (res.headersSent) return next(err);
  return res.status(500).json({ error: 'Internal server error' });
});

if (require.main === module) {
  const port = process.env.PORT || 8080;
  app.listen(port, () => log.info('server.listening', { port: Number(port) }));
}

module.exports = app;
