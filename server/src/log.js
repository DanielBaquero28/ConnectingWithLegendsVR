// Structured JSON logging to stdout; Cloud Run ingests `severity` and
// `message` natively. NEVER log student audio or conversation text here —
// flagged snippets go to the events collection only (spec 4.8).

function write(severity, message, fields) {
  const entry = { severity, message, time: new Date().toISOString() };
  if (fields && typeof fields === 'object') Object.assign(entry, fields);
  console.log(JSON.stringify(entry));
}

module.exports = {
  debug: (message, fields) => write('DEBUG', message, fields),
  info: (message, fields) => write('INFO', message, fields),
  warn: (message, fields) => write('WARNING', message, fields),
  error: (message, fields) => write('ERROR', message, fields),
};
