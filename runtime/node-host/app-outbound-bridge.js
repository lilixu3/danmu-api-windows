'use strict';
const http = require('node:http');
const https = require('node:https');
const { urlToHttpOptions } = require('node:url');
const { syncBuiltinESMExports } = require('node:module');
const { outboundError, describeError } = require('./app-outbound-errors.js');
const SOURCES = new Map([
  ['api.gamer.com.tw', 'bahamut'],
  ['api.tmdb.org', 'tmdb'],
  ['api.themoviedb.org', 'tmdb'],
  ['api.danmaku.weeblify.app', 'dandan'],
  ['nipaplay.aimes-soft.com', 'dandan'],
  ['api.animeko.org', 'animeko'],
  ['danmaku-global.myani.org', 'animeko'],
  ['danmaku-cn.myani.org', 'animeko'],
  ['s1.animeko.openani.org', 'animeko'],
  ['api.bangumi.vip', 'animeko'],
]);
const PREFIX = 'x-danmu-outbound-';
const CONNECTION_OPTIONS = [
  'ca', 'cert', 'key', 'pfx', 'passphrase', 'crl', 'dhparam', 'ciphers', 'sigalgs', 'ecdhCurve',
  'secureContext', 'secureProtocol', 'secureOptions', 'minVersion', 'maxVersion', 'honorCipherOrder',
  'servername', 'rejectUnauthorized', 'checkServerIdentity', 'session', 'requestOCSP', 'ALPNProtocols',
  'pskCallback', 'enableTrace', 'minDHSize', 'createConnection', 'lookup', 'localAddress', 'localPort',
  'family', 'hints', 'autoSelectFamily', 'autoSelectFamilyAttemptTimeout', 'socket', '_defaultAgent',
];
const POOL_OPTIONS = new Set([
  'keepAlive', 'keepAliveMsecs', 'maxSockets', 'maxFreeSockets', 'maxTotalSockets', 'scheduling',
  'timeout', 'noDelay', 'maxCachedSessions',
]);
function ordinaryAgent(agent) {
  if (agent == null || agent === false) return true;
  // Only the stock HTTPS Agent with pooling options can hand its transport to Go.
  // Subclasses and instance overrides may enforce routing, client certificates or trust.
  if (Object.getPrototypeOf(agent) !== https.Agent.prototype || agent.protocol !== 'https:' || agent.defaultPort !== 443) return false;
  for (const method of ['createConnection', 'addRequest', 'createSocket', 'getName', 'reuseSocket', 'keepSocketAlive']) {
    if (agent[method] !== https.Agent.prototype[method]) return false;
  }
  return Object.entries(agent.options || {}).every(([name, value]) =>
    POOL_OPTIONS.has(name) || name === 'path' && value === null ||
    name === 'defaultPort' && value === 443 || name === 'protocol' && value === 'https:' ||
    name === 'proxyEnv' && value === undefined);
}

function hasProxy(source, env) {
  return String(env.PROXY_URL || '').split(',').some(raw => {
    const entry = raw.trim();
    if (!entry) return false;
    if (entry.startsWith(source + '@') || entry.startsWith('@')) return true;
    return !entry.includes('@') || /^https?:\/\//i.test(entry);
  });
}
function eligible(target, snapshot, env, options = {}) {
  if (!snapshot?.config?.enabled && !snapshot?.blockTargets) return false;
  const source = SOURCES.get(target.hostname);
  if (!source || target.protocol !== 'https:' || (target.port && target.port !== '443') || target.username || target.password) return false;
  // Failed config reads invalidate routing selection, not just helper readiness.
  // Prior sources (even a valid disabled empty list) are diagnostics, not consent
  // to bypass enhancement. Healthy config/helper failures still respect selection.
  if ((!snapshot.selectionInvalid && !snapshot.config.sources.includes(source)) || hasProxy(source, env)) return false;
  // Respect explicitly selected transports and proxy agents. Ordinary keepalive
  // agents remain eligible; pooling happens in Go for those requests.
  if (options.dispatcher || !ordinaryAgent(options.agent)) return false;
  if (CONNECTION_OPTIONS.some(key => key in options)) return false;
  return true;
}
function helperHeaders(headers, target, snapshot, budget) {
  const result = new Headers(headers);
  for (const name of [...result.keys()]) {
    if (name.startsWith(PREFIX) || ['host','connection','proxy-connection','proxy-authorization','transfer-encoding'].includes(name)) result.delete(name);
  }
  result.set('X-Danmu-Outbound-Token', snapshot.token);
  result.set('X-Danmu-Outbound-Target', target.href);
  result.set('X-Danmu-Outbound-Timeout', String(budget));
  return result;
}
async function readBody(request, deadline) {
  if (!request.body) return null;
  const signal = AbortSignal.any([request.signal, AbortSignal.timeout(Math.max(1, deadline - Date.now()))]);
  const reader = request.body.getReader();
  const chunks = []; let size = 0;
  const reportCancel = error => console.error('[app-outbound]', describeError(outboundError(error, 'upload-cancel', 'OUTBOUND_CANCEL')));
  const abort = () => { reader.cancel(signal.reason).catch(reportCancel); };
  signal.addEventListener('abort', abort, {once:true});
  try {
    signal.throwIfAborted();
    while (true) {
      const part = await reader.read(); signal.throwIfAborted();
      if (part.done) break;
      size += part.value.byteLength;
      if (size > 32 * 1024 * 1024) throw new RangeError('增强直连请求体超过 32 MiB');
      chunks.push(Buffer.from(part.value));
    }
    return Buffer.concat(chunks, size);
  } catch (error) { reader.cancel(error).catch(reportCancel); throw error; }
  finally { signal.removeEventListener('abort', abort); reader.releaseLock(); }
}
function decorateResponse(response, url, redirected, type) {
  Object.defineProperties(response, {
    url:{value:url, configurable:true}, redirected:{value:redirected, configurable:true}, type:{value:type, configurable:true},
    clone:{value:function () { return decorateResponse(Response.prototype.clone.call(this), url, redirected, type); }, configurable:true},
  });
  return response;
}
function ready(snapshot) {
  if (snapshot.status !== 'ready' || !snapshot.endpoint || !snapshot.token) {
    throw outboundError(new Error(snapshot.reason || '增强直连组件尚未就绪'), 'availability', 'OUTBOUND_NOT_READY', [snapshot.token]);
  }
}
function responseError(text, headers, snapshot, env) {
  const secrets = [snapshot.token, ...Object.entries(env).filter(([key]) => /TOKEN|COOKIE|API_KEY|PASSWORD|_KEY$/i.test(key)).map(([, value]) => value)];
  let input;
  try { input = JSON.parse(text); }
  catch (error) { input = {message:`增强直连连接失败; helper diagnostic JSON invalid (${error.name})`}; }
  if (!input || typeof input !== 'object') input = {message:'增强直连连接失败; helper diagnostic must be an object'};
  return outboundError({
    message: '增强直连连接失败: ' + (input.message || input.error || 'helper failure'),
    phase: input.phase || headers['x-danmu-outbound-phase'] || 'proxy',
    errorCode: input.errorCode || input.code || headers['x-danmu-outbound-code'] || 'OUTBOUND_PROXY',
    details: input.details,
  }, 'proxy', 'OUTBOUND_PROXY', secrets);
}
async function fetchResponseError(response, snapshot, env) {
  const reader = response.body?.getReader(); const chunks = []; let size = 0;
  if (reader) {
    try {
      while (true) {
        const part = await reader.read(); if (part.done) break;
        size += part.value.length;
        if (size > 65536) { await reader.cancel(); throw outboundError(new Error('helper diagnostic exceeds 64 KiB'), 'proxy', 'OUTBOUND_DIAGNOSTIC_SIZE'); }
        chunks.push(Buffer.from(part.value));
      }
    } finally { reader.releaseLock(); }
  }
  return responseError(Buffer.concat(chunks).toString('utf8'), Object.fromEntries(response.headers), snapshot, env);
}
function stripInternalHeaders(headers) {
  const clean = new Headers(headers);
  for (const name of [...clean.keys()]) if (name.startsWith(PREFIX)) clean.delete(name);
  return clean;
}
function normalizeRequest(args, protocol) {
  let options, callback, base;
  if (typeof args[0] === 'string' || args[0] instanceof URL) {
    base = new URL(args[0]);
    options = { ...urlToHttpOptions(base), ...(typeof args[1] === 'object' ? args[1] : {}) };
    callback = typeof args[1] === 'function' ? args[1] : args[2];
  } else {
    options = { ...(args[0] || {}) };
    callback = args[1];
  }
  const hostname = options.hostname || options.host || 'localhost';
  const target = new URL(`${options.protocol || protocol}//${hostname}${options.port ? ':' + options.port : ''}`);
  const requestedPath = options.path || '/';
  const parsedPath = new URL(requestedPath, target);
  target.pathname = parsedPath.pathname;
  target.search = parsedPath.search;
  target.hash = '';
  if (options.auth) {
    const split = options.auth.indexOf(':');
    target.username = split < 0 ? options.auth : options.auth.slice(0, split);
    target.password = split < 0 ? '' : options.auth.slice(split + 1);
  }
  // ClientRequest sends options.path verbatim. WHATWG URL normalization cannot
  // represent signed dot segments, fragments, absolute-form targets, etc.
  const rawTargetSupported = requestedPath === target.pathname + target.search;
  const hasHost = nodeHeaders(options.headers).has('host');
  return { options, callback, target, rawTargetSupported, hasHost };
}
function nodeHeaders(headers) {
  const result = new Headers();
  if (Array.isArray(headers)) {
    for (let i = 0; i < headers.length; i += 2) result.append(headers[i], headers[i + 1]);
  } else {
    for (const [name, value] of Object.entries(headers || {})) {
      for (const part of Array.isArray(value) ? value : [value]) result.append(name, String(part));
    }
  }
  return result;
}

// Install before importing any core. Each worker has its own JS global/module
// cache; all workers receive the same endpoint owned by the host runtime.
function installBridge({ snapshot = {}, env = () => process.env } = {}) {
  let current = snapshot;
  let stopped = false;
  const requests = new Set();
  const nativeFetch = globalThis.fetch;
  const originals = { httpRequest: http.request, httpGet: http.get, httpsRequest: https.request, httpsGet: https.get };

  async function enhancedFetch(input, init) {
    const initialURL = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    // Integrity is enforced by native fetch while reading the final response.
    // We cannot reproduce it on the local proxy response. Request + init uses
    // init overrides, including an explicitly empty integrity/header override.
    const integrity = init?.integrity !== undefined ? init.integrity : input?.integrity;
    const inputHeaders = init?.headers !== undefined ? init.headers : input?.headers;
    if (stopped || integrity || new Headers(inputHeaders).has('host') || !eligible(initialURL, current, env(), init || {})) return nativeFetch(input, init);
    ready(current);
    let request = new Request(input, init);
    const deadline = Date.now() + 30000;
    const redirectMode = request.redirect;
    let replayBody = await readBody(request, deadline);
    request = new Request(request.url, {method:request.method, headers:request.headers, body:replayBody, signal:request.signal, redirect:redirectMode});
    let redirected = false;
    for (let count = 0; count <= 20; count++) {
      const target = new URL(request.url); target.hash = '';
      request.signal.throwIfAborted();
      const budget = deadline - Date.now();
      if (budget <= 0) throw new DOMException('增强直连请求超时', 'TimeoutError');
      let response;
      if (eligible(target, current, env(), init || {})) {
        ready(current);
        const controller = new AbortController(); requests.add(controller);
        let timer;
        try {
          timer = setTimeout(() => controller.abort(new DOMException('增强直连请求超时', 'TimeoutError')), budget); timer.unref?.();
          response = await nativeFetch(current.endpoint + '/proxy', {
            method: request.method,
            headers: helperHeaders(request.headers, target, current, budget),
            body: replayBody,
            signal: AbortSignal.any([request.signal, controller.signal]),
            redirect: 'manual',
          });
          if (response.headers.has('X-Danmu-Outbound-Error')) {
            throw await fetchResponseError(response, current, env());
          }
          // Keep the deadline/cancellation active through body consumption.
          const original = response;
          const reader = original.body?.getReader();
          const cleanup = () => { clearTimeout(timer); requests.delete(controller); };
          const body = reader ? new ReadableStream({
            async pull(stream) {
              try { const part = await reader.read(); if (part.done) { cleanup(); stream.close(); } else stream.enqueue(part.value); }
              catch (error) { cleanup(); stream.error(error); }
            },
            async cancel(reason) { cleanup(); await reader.cancel(reason); },
          }) : null;
          response = new Response(body, {status: original.status, statusText: original.statusText, headers: stripInternalHeaders(original.headers)});
          decorateResponse(response, target.href, redirected, original.type);
          if (!reader) cleanup();
        } catch (error) {
          clearTimeout(timer); requests.delete(controller);
          if (request.signal.aborted || controller.signal.aborted || error.name === 'AbortError' || error.name === 'TimeoutError') throw error;
          throw outboundError(error, 'proxy-connect', 'OUTBOUND_CONNECTION', [current.token]);
        }
      } else {
        response = await nativeFetch(request, { redirect: 'manual', signal:AbortSignal.any([request.signal, AbortSignal.timeout(budget)]) });
      }
      const location = response.headers.get('location');
      if (!location || ![301,302,303,307,308].includes(response.status) || redirectMode === 'manual') return response;
      if (redirectMode === 'error') { await response.body?.cancel(); throw new TypeError('Redirect disallowed'); }
      if (count === 20) { await response.body?.cancel(); throw new TypeError('Too many redirects'); }
      const next = new URL(location, target);
      if (!['https:', 'http:'].includes(next.protocol) || next.username || next.password) { await response.body?.cancel(); throw new TypeError('Invalid redirect'); }
      const headers = new Headers(request.headers);
      if (next.origin !== target.origin) { for (const name of ['authorization','cookie','proxy-authorization']) headers.delete(name); }
      let method = request.method;
      if ((response.status === 303 && method !== 'HEAD') || ([301,302].includes(response.status) && method === 'POST')) {
        method = 'GET'; replayBody = null;
        // Fetch's request-body-header names, plus explicit wire length.
        for (const name of ['content-encoding', 'content-language', 'content-location', 'content-type', 'content-length']) headers.delete(name);
      }
      await response.body?.cancel();
      request = new Request(next, {method, headers, body:replayBody, signal:request.signal, redirect:redirectMode});
      redirected = true;
    }
  }
  function wrapRequest(original, protocol) {
    return function (...args) {
      if (stopped) return original.apply(this, args);
      let parsed;
      try { parsed = normalizeRequest(args, protocol); } catch { return original.apply(this, args); }
      const effectiveOptions = { ...parsed.options, agent: parsed.options.agent ?? (protocol === 'https:' ? https.globalAgent : http.globalAgent) };
      if (!parsed.rawTargetSupported || parsed.hasHost || !eligible(parsed.target, current, env(), effectiveOptions)) return original.apply(this, args);
      ready(current);
      const endpoint = new URL(current.endpoint);
      const budget = 30000;
      const headers = Object.fromEntries(helperHeaders(nodeHeaders(parsed.options.headers), parsed.target, current, budget));
      const request = originals.httpRequest.call(http, {
        hostname:endpoint.hostname, port:endpoint.port, path:'/proxy', method:parsed.options.method || 'GET',
        headers, agent:false, signal:parsed.options.signal, timeout:parsed.options.timeout,
      });
      // Once enhancement has been selected, a mutable Host cannot be represented
      // by this adapter: helper owns the target Host, while this ClientRequest is
      // addressed to loopback. Refuse/destroy BEFORE serialization, never reroute
      // or retry business data after dispatch. Constructor Host already bypasses.
      const localHost = request.getHeader('host');
      const rejectMutableHost = () => {
        const error = outboundError(new Error('增强直连不支持创建请求后修改 Host；请在创建请求的 headers 中指定 Host 以保留原生路径'), 'request-semantics', 'OUTBOUND_UNSUPPORTED_HOST');
        request.destroy(error);
        throw error;
      };
      for (const name of ['setHeader', 'appendHeader', 'removeHeader']) {
        const originalMethod = request[name];
        request[name] = function (header, ...values) {
          if (!this.headersSent && typeof header === 'string' && header.toLowerCase() === 'host') rejectMutableHost();
          return originalMethod.call(this, header, ...values);
        };
      }
      // Also protect the send boundary if a caller uses the prototype method
      // directly instead of the normal mutable header API.
      const storeHeader = request._storeHeader;
      request._storeHeader = function (...values) {
        if (this.getHeader('host') !== localHost) rejectMutableHost();
        return storeHeader.apply(this, values);
      };
      requests.add(request);
      const timer = setTimeout(() => request.destroy(new Error('增强直连请求超时')), budget); timer.unref?.();
      const cleanup = () => { clearTimeout(timer); requests.delete(request); };
      request.once('close', cleanup);
      const emit = request.emit;
      request.emit = function (event, ...values) {
        if (event === 'response') {
          const response = values[0];
          if (Object.hasOwn(response.headers, 'x-danmu-outbound-error')) {
            const chunks = []; let size = 0;
            response.on('data', chunk => {
              size += chunk.length;
              if (size > 65536) request.destroy(outboundError(new Error('helper diagnostic exceeds 64 KiB'), 'proxy', 'OUTBOUND_DIAGNOSTIC_SIZE'));
              else chunks.push(chunk);
            });
            response.once('end', () => request.destroy(responseError(Buffer.concat(chunks).toString('utf8'), response.headers, current, env())));
            response.once('error', error => request.destroy(outboundError(error, 'proxy-body', 'OUTBOUND_RESPONSE', [current.token])));
            return true;
          }
          for (const name of Object.keys(response.headers)) if (name.toLowerCase().startsWith(PREFIX)) delete response.headers[name];
          response.rawHeaders = response.rawHeaders.filter((_value, index, list) => !list[index - index % 2].toLowerCase().startsWith(PREFIX));
          response.once('end', cleanup); response.once('close', cleanup);
        }
        return emit.call(this, event, ...values);
      };
      if (typeof parsed.callback === 'function') request.on('response', parsed.callback);
      return request;
    };
  }
  globalThis.fetch = enhancedFetch;
  http.request = wrapRequest(originals.httpRequest, 'http:');
  https.request = wrapRequest(originals.httpsRequest, 'https:');
  http.get = function (...args) { const request = http.request(...args); request.end(); return request; };
  https.get = function (...args) { const request = https.request(...args); request.end(); return request; };
  syncBuiltinESMExports();
  return {
    update(next) { current = next || {}; },
    stop() {
      stopped = true;
      for (const request of requests) {
        if (request instanceof AbortController) request.abort(); else request.destroy(new Error('增强直连已停止'));
      }
      requests.clear();
      if (globalThis.fetch === enhancedFetch) globalThis.fetch = nativeFetch;
      http.request = originals.httpRequest; http.get = originals.httpGet;
      https.request = originals.httpsRequest; https.get = originals.httpsGet;
      syncBuiltinESMExports();
    },
  };
}
module.exports = { installBridge, eligible, hasProxy };
