// Structured JSON logging to stdout; Cloud Run ingests `severity` and
// `message` natively. NEVER log student audio or conversation text here —
// flagged snippets go to the events collection only (spec 4.8).
//
// Spec 12 Part F guard: payload keys that could carry conversation text are
// dropped before serialisation, at every nesting level —
//   text, transcript, reply, snippet, prompt   always
//   message                                    when it is a string over 40 chars
// The top-level `message` (the event name, e.g. "claude.attempt_failed") is
// not a payload field and is kept. Error logs pass { error: msg.slice(0, 200) }
// so diagnostics survive the guard.

const DROPPED_KEYS = new Set(['text', 'transcript', 'reply', 'snippet', 'prompt']);
const MESSAGE_MAX_CHARS = 40;

function isPlainObject(v) {
  return v !== null && typeof v === 'object' && !Array.isArray(v) && !(v instanceof Date);
}

function sanitize(fields) {
  const out = {};
  for (const [key, value] of Object.entries(fields)) {
    if (DROPPED_KEYS.has(key)) continue;
    if (key === 'message' && typeof value === 'string' && value.length > MESSAGE_MAX_CHARS) continue;
    out[key] = isPlainObject(value) ? sanitize(value) : value;
  }
  return out;
}

// Reserved envelope keys; a payload may never overwrite them. A short payload
// `message` that survives the guard is kept under `payloadMessage`.
const RESERVED = { severity: 'payloadSeverity', message: 'payloadMessage', time: 'payloadTime' };

function write(severity, message, fields) {
  const entry = { severity, message, time: new Date().toISOString() };
  if (fields && typeof fields === 'object') {
    for (const [key, value] of Object.entries(sanitize(fields))) {
      entry[RESERVED[key] || key] = value;
    }
  }
  console.log(JSON.stringify(entry));
}

module.exports = {
  debug: (message, fields) => write('DEBUG', message, fields),
  info: (message, fields) => write('INFO', message, fields),
  warn: (message, fields) => write('WARNING', message, fields),
  error: (message, fields) => write('ERROR', message, fields),
  sanitize,
};
