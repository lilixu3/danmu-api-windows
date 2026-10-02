'use strict';
// Diagnostics must never expose helper credentials, URLs with queries, or core secrets.
function redact(value, secrets = []) {
  let text = String(value ?? '');
  for (const secret of secrets) if (secret) text = text.split(String(secret)).join('[redacted]');
  text = text.replace(/\b[a-f0-9]{64}\b/gi, '[redacted]');
  text = text.replace(/\b(?:https?|socks5?):\/\/[^\s<>"']+/gi, raw => {
    try { const url = new URL(raw); return `${url.protocol}//${url.hostname}${url.port ? ':' + url.port : ''}/[redacted]`; }
    catch { return '[redacted-url]'; }
  });
  text = text.replace(/\b(authorization|proxy-authorization|cookie|set-cookie|token|api[_-]?key|password)\b\s*[:=]\s*[^\r\n,;]+/gi, '$1=[redacted]');
  return text.slice(0, 2048);
}
function outboundError(error, phase = 'runtime', code = 'OUTBOUND_FAILED', secrets = []) {
  const result = new Error(redact(error?.message ?? error, secrets));
  result.name = 'AppOutboundError';
  const safeIdentifier = (value, fallback) => /^[a-zA-Z0-9_.-]{1,80}$/.test(String(value || '')) ? String(value) : fallback;
  result.code = safeIdentifier(error?.errorCode || error?.code, code);
  result.phase = safeIdentifier(error?.phase, phase);
  if (error?.details) result.details = redact(typeof error.details === 'string' ? error.details : JSON.stringify(error.details), secrets);
  return result;
}
function describeError(error) { return `[${error.code}] phase=${error.phase} ${error.message}${error.details ? ' details=' + error.details : ''}`; }
module.exports = { redact, outboundError, describeError };
