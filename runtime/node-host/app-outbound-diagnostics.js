'use strict';
const { eligible } = require('./app-outbound-bridge.js');
const { outboundError, describeError } = require('./app-outbound-errors.js');
const targets = {
  bahamut: ['https://api.gamer.com.tw/mobile_app/anime/v1/search.php?kw=' + encodeURIComponent('葬送的芙莉蓮')],
  tmdb: ['https://api.tmdb.org/3/configuration'],
  dandan: ['https://api.danmaku.weeblify.app/ddp/v1?path=/v2/search/anime?keyword=' + encodeURIComponent('葬送的芙莉莲')],
  animeko: ['https://api.animeko.org/v2/subjects/400602', 'https://danmaku-global.myani.org/v2/subjects/400602', 'https://danmaku-cn.myani.org/v2/subjects/400602', 'https://s1.animeko.openani.org/v2/subjects/400602', 'https://api.bangumi.vip/v0/episodes?subject_id=400602&limit=1'],
};
// Each target keeps its own result; a different successful endpoint cannot hide a failure.
// This module does not create a host HTTP API. Desktop diagnostics use private session /request.
async function diagnoseSource(source, snapshot, env, { signal, fetchImpl = globalThis.fetch } = {}) {
  if (!targets[source]) throw new Error('Invalid source');
  if (!targets[source].every(target => eligible(new URL(target), snapshot, env))) return { ok: false, reason: 'Source is disabled or uses an existing proxy', httpStatus: 409, results: [] };
  if (snapshot.status !== 'ready' || !snapshot.endpoint || !snapshot.token) throw outboundError(new Error(snapshot.reason || 'helper not ready'), 'availability', 'OUTBOUND_NOT_READY');
  const results = [];
  for (const target of targets[source]) {
    signal?.throwIfAborted();
    const host = new URL(target).hostname, started = Date.now();
    try {
      const response = await fetchImpl(snapshot.endpoint + '/request', {
        method: 'POST', redirect: 'error', headers: { Authorization: 'Bearer ' + snapshot.token, 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: target, method: 'GET', headers: [['User-Agent', 'danmu-api-windows/connectivity']], body: '', timeoutMs: 15000 }),
        signal: AbortSignal.any([...(signal ? [signal] : []), AbortSignal.timeout(16000)]),
      });
      const data = await response.json();
      if (!data || typeof data.success !== 'boolean') throw outboundError(new Error('helper diagnostic missing success'), 'diagnostic-parse', 'OUTBOUND_DIAGNOSTIC');
      if (!data.success) throw outboundError(data, 'request', 'OUTBOUND_REQUEST', [snapshot.token]);
      if (response.status !== 200 || !Number.isInteger(data.status) || data.status < 100 || data.status > 599 || !['h2', 'h3'].includes(data.protocol) || typeof data.ech !== 'boolean') throw outboundError(new Error(`helper diagnostic invalid metadata HTTP=${response.status}`), 'diagnostic-parse', 'OUTBOUND_DIAGNOSTIC');
      const valid = source === 'tmdb' ? [200, 401].includes(data.status) : data.status === 200;
      results.push({ host, ok: valid, status: data.status, protocol: data.protocol, ech: data.ech, durationMs: Date.now() - started, reason: valid ? '' : 'Unexpected upstream HTTP status' });
    } catch (error) {
      signal?.throwIfAborted();
      const failure = outboundError(error, 'diagnostic', 'OUTBOUND_DIAGNOSTIC', [snapshot.token]);
      results.push({ host, ok: false, durationMs: Date.now() - started, code: failure.code, phase: failure.phase, reason: describeError(failure) });
    }
  }
  return { ok: results.every(result => result.ok), results };
}
module.exports = { diagnoseSource, diagnosticSources: Object.keys(targets) };
