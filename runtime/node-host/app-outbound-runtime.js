'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
const { randomBytes } = require('node:crypto');
const { installBridge } = require('./app-outbound-bridge.js');
const { redact, outboundError, describeError } = require('./app-outbound-errors.js');
const SOURCES = ['bahamut', 'tmdb', 'dandan', 'animeko'];
const CONFIG_KEYS = ['schemaVersion', 'enabled', 'sources', 'httpVersion', 'dohUrl', 'connectTimeoutMs'];
const MAX_DOCUMENT_BYTES = 1_048_576;
const MAX_JSON_DEPTH = 16;
// Validate tokens before JSON.parse collapses object members. A reviver cannot
// detect duplicate keys, including names spelled with equivalent Unicode escapes.
function parseSettingsJson(text) {
  if (Buffer.byteLength(text, 'utf8') > MAX_DOCUMENT_BYTES) throw new Error('settings exceeds 1 MiB');
  let index = 0;
  const number = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;
  const invalid = () => { throw new Error(`settings is not valid JSON (offset ${index})`); };
  const whitespace = () => { while (/[ \t\r\n]/.test(text[index] || '\0')) index++; };
  function stringToken() {
    const start = index;
    if (text[index++] !== '"') invalid();
    while (index < text.length) {
      const character = text[index++];
      if (character === '"') {
        try { return JSON.parse(text.slice(start, index)); } catch { invalid(); }
      }
      if (character.charCodeAt(0) < 32) invalid();
      if (character === '\\') {
        const escape = text[index++];
        if (escape === 'u') {
          if (!/^[a-fA-F0-9]{4}$/.test(text.slice(index, index + 4))) invalid();
          index += 4;
        } else if (!['"', '\\', '/', 'b', 'f', 'n', 'r', 't'].includes(escape)) invalid();
      }
    }
    invalid();
  }
  function value(depth) {
    whitespace();
    const token = text[index];
    if (token === '{' || token === '[') {
      if (depth >= MAX_JSON_DEPTH) throw new Error('settings JSON exceeds depth 16');
      index++; whitespace();
      const close = token === '{' ? '}' : ']';
      if (text[index] === close) { index++; return; }
      const keys = new Set();
      while (true) {
        if (token === '{') {
          const key = stringToken();
          if (keys.has(key)) throw new Error('settings JSON contains duplicate fields');
          keys.add(key); whitespace();
          if (text[index++] !== ':') invalid();
        }
        value(depth + 1); whitespace();
        if (text[index] === close) { index++; return; }
        if (text[index++] !== ',') invalid();
        whitespace();
      }
    }
    if (token === '"') { stringToken(); return; }
    for (const literal of ['true', 'false', 'null']) if (text.startsWith(literal, index)) { index += literal.length; return; }
    number.lastIndex = index;
    const match = number.exec(text);
    if (!match) invalid();
    index = number.lastIndex;
  }
  value(0); whitespace();
  if (index !== text.length) invalid();
  try { return JSON.parse(text); } catch { invalid(); }
}
function readSettingsDocument(file) {
  let descriptor;
  try { descriptor = fs.openSync(file, 'r'); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
  try {
    const stat = fs.fstatSync(descriptor);
    if (stat.size > MAX_DOCUMENT_BYTES) throw new Error('settings exceeds 1 MiB');
    const bytes = Buffer.alloc(MAX_DOCUMENT_BYTES + 1); let length = 0;
    while (length < bytes.length) {
      const count = fs.readSync(descriptor, bytes, length, bytes.length - length, null);
      if (count === 0) break;
      length += count;
      if (length > MAX_DOCUMENT_BYTES) throw new Error('settings exceeds 1 MiB');
    }
    let text;
    try { text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes.subarray(0, length)); }
    catch { throw new Error('settings is not valid UTF-8'); }
    return { text, stat };
  } finally { fs.closeSync(descriptor); }
}
function defaultConfig() {
  return { schemaVersion: 1, enabled: false, sources: [...SOURCES], httpVersion: 'auto', dohUrl: '', connectTimeoutMs: 3000 };
}
function validateConfig(input) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('settings must be an object');
  for (const key of CONFIG_KEYS) if (!Object.hasOwn(input, key)) throw new Error(`settings missing ${key}`);
  if (Object.keys(input).some(key => !CONFIG_KEYS.includes(key))) throw new Error('settings contains unknown fields');
  if (input.schemaVersion !== 1) throw new Error('settings schemaVersion must be 1');
  if (typeof input.enabled !== 'boolean') throw new Error('settings enabled must be boolean');
  if (!Array.isArray(input.sources) || input.sources.some(source => typeof source !== 'string' || !SOURCES.includes(source)) || new Set(input.sources).size !== input.sources.length) throw new Error('settings sources must contain unique supported sources');
  if (input.enabled && input.sources.length === 0) throw new Error('settings enabled requires at least one source');
  if (typeof input.httpVersion !== 'string' || !['auto', 'h2', 'h3'].includes(input.httpVersion)) throw new Error('settings httpVersion must be auto, h2 or h3');
  if (typeof input.dohUrl !== 'string') throw new Error('settings dohUrl must be a string');
  if (!Number.isInteger(input.connectTimeoutMs) || input.connectTimeoutMs < 1 || input.connectTimeoutMs > 60000) throw new Error('settings connectTimeoutMs must be an integer in 1..60000');
  if (input.dohUrl) {
    // Unicode White_Space includes U+0085, matching C# char.IsWhiteSpace.
    // Reject raw syntax before WHATWG URL removes newlines or normalizes slashes.
    if (input.dohUrl.length > 8192 || /[\p{White_Space}\\#]/u.test(input.dohUrl)) throw new Error('DoH must be HTTPS without whitespace, backslashes, fragments or credentials; maximum length 8192');
    let url;
    try { url = new URL(input.dohUrl); } catch { throw new Error('DoH must be a valid absolute HTTPS URL'); }
    if (!/^https:\/\//i.test(input.dohUrl) || url.protocol !== 'https:' || !url.hostname || url.username || url.password || url.hash || /^https:\/\/[^/?#]*@/i.test(input.dohUrl)) throw new Error('DoH must be HTTPS without credentials or fragments');
  }
  return Object.fromEntries(CONFIG_KEYS.map(key => [key, key === 'sources' ? [...input.sources] : input[key]]));
}
function atomicJson(file, output, mode) {
  const pending = `${file}.${process.pid}.${randomBytes(6).toString('hex')}.pending`;
  let originalError;
  try {
    fs.writeFileSync(pending, JSON.stringify(output) + '\n', { mode, flag: 'wx' });
    fs.renameSync(pending, file);
    const actual = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (JSON.stringify(actual) !== JSON.stringify(output)) throw new Error('atomic JSON readback mismatch');
  } catch (error) { originalError = error; throw error; }
  finally {
    try { fs.unlinkSync(pending); }
    catch (error) { if (error.code !== 'ENOENT') { if (originalError) originalError.message += `; pending cleanup failed (${error.code})`; else throw error; } }
  }
}
// The native executable cannot establish its own trust before execution. Use
// the OS-owned PowerShell/.NET ACL reader first, with no session token present.
function verifyWindowsLaunchTrust(helperPath) {
  if (process.platform !== 'win32') throw new Error('Windows outbound ACL verification requires Windows');
  const hostFiles = fs.readdirSync(__dirname).filter(name => name.endsWith('.js')).map(name => path.join(__dirname, name));
  const paths = Buffer.from(JSON.stringify([path.resolve(helperPath), path.dirname(path.resolve(helperPath)), process.execPath, path.dirname(process.execPath), __dirname, ...hostFiles]), 'utf8').toString('base64');
  const script = `
$ErrorActionPreference='Stop'
$trusted=@([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
$paths=ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${paths}')))
foreach($entry in $paths) {
 $p=[IO.Path]::GetFullPath($entry); $first=$true
 if($p -notmatch '^[A-Za-z]:\\\\') { throw 'ACL trust requires a local drive path' }
 while($p) {
  $item=Get-Item -LiteralPath $p -Force
  if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ACL trust rejects reparse paths' }
  $acl=Get-Acl -LiteralPath $p
  if($trusted -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw 'ACL trust rejects untrusted owner' }
  $danger=[int64]0x500D0040
  if($first) { $danger=$danger -bor 0x116 }
  foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
   if(($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
   if($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $trusted -notcontains $rule.IdentityReference.Value -and (([int64]$rule.FileSystemRights -band $danger) -ne 0)) { throw 'ACL trust rejects other-user executable or ancestor replacement rights' }
  }
  $first=$false; $parent=[IO.Directory]::GetParent($p); if($null -eq $parent) { break }; $p=$parent.FullName
 }
}
Write-Output '{"success":true,"secureDirectoryProtocol":1}'`;
  const executable = path.join(process.env.SystemRoot || '', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  if (!path.isAbsolute(executable)) throw new Error('trusted Windows PowerShell path unavailable');
  const result = spawnSync(executable, ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')], { windowsHide: true, encoding: 'utf8', timeout: 10000, maxBuffer: 16384 });
  if (result.error || result.status !== 0) throw new Error(`Windows launch ACL trust failed: ${result.error?.message || result.stderr || `exit=${result.status}`}`);
  const output = JSON.parse(result.stdout.trim());
  if (output.success !== true || output.secureDirectoryProtocol !== 1) throw new Error('Windows launch ACL protocol mismatch');
}
function secureSessionDirectory(helperPath, directory, mode, testOnly) {
  if (testOnly?.prepareSecureDirectory) { testOnly.prepareSecureDirectory(directory, mode); return; }
  verifyWindowsLaunchTrust(helperPath);
  const result = spawnSync(helperPath, ['--secure-directory', path.resolve(directory), '--secure-directory-mode', mode], { windowsHide: true, encoding: 'utf8', timeout: 10000, maxBuffer: 16384 });
  if (result.error || result.status !== 0) throw new Error(`Windows session ACL ${mode} failed: ${result.error?.message || result.stderr || `exit=${result.status}`}`);
  const output = JSON.parse(result.stdout.trim());
  if (output.success !== true || output.secureDirectoryProtocol !== 1 || output.mode !== mode) throw new Error('Windows session ACL helper protocol mismatch');
}
function createAppOutboundRuntime({
  configPath = path.join(__dirname, 'config', 'outbound', 'settings.json'),
  helperPath = path.join(__dirname, 'outbound', 'danmu-outbound.exe'),
  runtimeIdentity = String(process.env.DANMU_API_RUNTIME_IDENTITY || '').trim(),
  onSnapshot = () => {}, log = (...args) => console.error(...args), testOnly,
} = {}) {
  // Fixtures inject executable/args only through this constructor, never process env.
  if (testOnly && process.env.NODE_TEST_CONTEXT === undefined) throw new Error('testOnly helper options require node --test');
  const nativeFetch = globalThis.fetch;
  const statusPath = path.join(path.dirname(configPath), 'status.json');
  const sessionPath = path.join(path.dirname(configPath), 'session.json');
  let state = { status: 'off', config: defaultConfig(), reason: '', helperPid: null, helperVersion: null, protocolVersion: null, endpoint: '', token: '', blockTargets: false, selectionInvalid: false };
  let child = null, watcher = null, debounce = null, heartbeat = null, stopped = false, started = false;
  let queued = Promise.resolve(), fingerprint = null, configStamp = null;
  const bridge = installBridge();
  function snapshot() { return structuredClone(state); }
  function safeSnapshot() { return { status: state.status, reason: state.reason, config: structuredClone(state.config), helperPid: state.helperPid, helperVersion: state.helperVersion, protocolVersion: state.protocolVersion }; }
  function publicStatus() {
    return { schemaVersion: 1, status: state.status, reason: state.reason, nodePid: process.pid, helperPid: state.helperPid, runtimeIdentity, heartbeatUnixMs: Date.now(), config: state.config };
  }
  function removeSession() { try { fs.unlinkSync(sessionPath); } catch (error) { if (error.code !== 'ENOENT') throw error; } }
  function writeStatus() { atomicJson(statusPath, publicStatus(), 0o644); }
  function notify() { bridge.update(snapshot()); onSnapshot(snapshot()); }
  function publish(next = {}) {
    state = { ...state, ...next };
    if (state.status !== 'ready') removeSession();
    writeStatus(); notify();
  }
  function fail(error, phase = 'runtime', code = 'OUTBOUND_FAILED') {
    const diagnostic = outboundError(error, phase, code, [state.token, child?.token]);
    state = { ...state, status: 'failed', reason: describeError(diagnostic), endpoint: '', token: '', blockTargets: true, helperPid: child?.process.pid || null };
    log('[app-outbound]', state.reason);
    for (const [name, action] of [['session cleanup', removeSession], ['snapshot delivery', notify], ['status write', writeStatus]]) {
      try { action(); }
      catch (failure) {
        const detail = describeError(outboundError(failure, name.replace(' ', '-'), 'OUTBOUND_STATE_IO'));
        state.reason += '; ' + detail; log('[app-outbound]', detail);
        // Keep the bridge fail-closed even when its observer or filesystem fails.
        bridge.update(snapshot());
      }
    }
  }
  function enqueue(operation) {
    const result = queued.then(operation);
    queued = result.catch(error => { fail(error); });
    return result;
  }
  function timeout(promise, ms) {
    return new Promise(resolve => {
      const timer = setTimeout(() => resolve(false), ms);
      promise.then(() => { clearTimeout(timer); resolve(true); });
    });
  }
  async function stopChild() {
    const previous = child;
    if (!previous) return;
    previous.intentional = true;
    if (!previous.done) {
      previous.process.stdin.end();
      const exited = await timeout(previous.exited, testOnly?.stopTimeoutMs || 1500);
      if (!exited) {
        const detail = outboundError(new Error('helper did not exit after stdin EOF; forcing termination'), 'stop', 'OUTBOUND_EOF_TIMEOUT');
        log('[app-outbound]', describeError(detail));
        if (!previous.process.kill('SIGKILL')) throw outboundError(new Error('helper termination could not be sent'), 'stop', 'OUTBOUND_KILL_FAILED');
        if (!await timeout(previous.exited, 2000)) throw outboundError(new Error('helper still alive after termination timeout'), 'stop', 'OUTBOUND_EXIT_TIMEOUT');
        if (child === previous) child = null;
        throw detail;
      }
    }
    if (child === previous) child = null;
    if (previous.code !== 0 && previous.code !== null) throw outboundError(new Error(`helper exit code=${previous.code}; stderr=${previous.stderr}`), 'stop', 'OUTBOUND_STOP_EXIT');
  }
  async function startChild() {
    fs.accessSync(helperPath, fs.constants.F_OK);
    const config = state.config;
    try { secureSessionDirectory(helperPath, path.dirname(configPath), 'prepare', testOnly); }
    catch (error) { throw outboundError(error, 'session-acl', 'OUTBOUND_SESSION_ACL'); }
    const token = randomBytes(32).toString('hex');
    const args = ['--http-version', config.httpVersion, '--connect-timeout-ms', String(config.connectTimeoutMs), '--doh-url', config.dohUrl];
    let instance;
    try { instance = spawn(helperPath, [...(testOnly?.helperArgs || []), ...args], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, DANMU_OUTBOUND_TOKEN: token } }); }
    catch (error) { throw outboundError(error, 'spawn', 'OUTBOUND_SPAWN', [token]); }
    const record = { process: instance, token, intentional: false, done: false, code: null, stderr: '' };
    record.exited = new Promise(resolve => {
      instance.once('close', (code, signal) => { record.done = true; record.code = code; record.signal = signal; resolve(); });
    });
    child = record;
    instance.stdin.on('error', error => {
      // Broken stdin before close is a lifecycle failure, including during shutdown.
      record.stdinError = outboundError(error, 'stdin', 'OUTBOUND_STDIN', [token]);
      log('[app-outbound]', describeError(record.stdinError));
      if (!record.intentional) { fail(record.stdinError); void enqueue(stopChild).catch(error => fail(error, 'stop')); }
    });
    instance.stderr.on('data', chunk => {
      const text = redact(chunk.toString(), [token]); record.stderr = (record.stderr + text).slice(-4096);
      if (text.trim()) log('[app-outbound]', text.trim());
    });
    const endpoint = await new Promise((resolve, reject) => {
      let output = '', settled = false;
      const finish = (error, value) => { if (settled) return; settled = true; clearTimeout(timer); error ? reject(error) : resolve(value); };
      const timer = setTimeout(() => finish(outboundError(new Error('helper ready handshake timed out'), 'handshake', 'OUTBOUND_START_TIMEOUT')), testOnly?.startTimeoutMs || 5000);
      instance.stdout.on('data', chunk => {
        if (settled) return;
        output += chunk.toString();
        if (output.length > 4096) { finish(outboundError(new Error('helper ready handshake exceeds 4096 bytes'), 'handshake', 'OUTBOUND_HANDSHAKE')); return; }
        const newline = output.indexOf('\n'); if (newline < 0) return;
        try {
          const ready = JSON.parse(output.slice(0, newline)); const url = new URL(ready.url);
          if (ready.ready !== true || ready.appProtocol !== 1 || url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.username || url.password || url.search || url.hash) throw new Error('invalid helper loopback endpoint or protocol');
          finish(null, url.origin);
        } catch (error) { finish(outboundError(error, 'handshake', 'OUTBOUND_HANDSHAKE', [token])); }
      });
      instance.once('error', error => finish(outboundError(error, 'spawn', 'OUTBOUND_SPAWN', [token])));
      instance.once('exit', (code, signal) => {
        record.code = code; record.signal = signal;
        const error = outboundError(new Error(`helper exited code=${code} signal=${signal}; stderr=${record.stderr}`), 'exit', 'OUTBOUND_HELPER_EXIT', [token]);
        finish(error);
        if (child === record && !record.intentional) { fail(error); void enqueue(async () => { await record.exited; if (child === record) child = null; if (state.status === 'failed') { state.helperPid = null; try { publish(); } catch (failure) { fail(failure, 'status-write'); } } }).catch(failure => fail(failure)); }
      });
    });
    const response = await nativeFetch(endpoint + '/health', { headers: { 'Authorization': 'Bearer ' + token, 'X-Danmu-Outbound-Token': token }, signal: AbortSignal.timeout(2000), redirect: 'error' });
    if (response.status !== 200) throw outboundError(new Error(`helper health HTTP ${response.status}`), 'health', 'OUTBOUND_HEALTH');
    const health = await response.json();
    if (health.success !== true || health.protocolVersion !== 1 || health.pid !== instance.pid || typeof health.version !== 'string' || !health.version) throw outboundError(new Error('helper health protocol/pid/version mismatch'), 'health', 'OUTBOUND_HEALTH');
    if (record.done || instance.exitCode !== null || instance.signalCode !== null || state.status === 'failed') throw outboundError(new Error('helper exited before ready publication'), 'health', 'OUTBOUND_HELPER_EXIT');
    try { secureSessionDirectory(helperPath, path.dirname(configPath), 'verify', testOnly); }
    catch (error) { throw outboundError(error, 'session-acl', 'OUTBOUND_SESSION_ACL', [token]); }
    atomicJson(sessionPath, { schemaVersion: 1, endpoint, token, nodePid: process.pid, helperPid: instance.pid, runtimeIdentity }, 0o600);
    publish({ status: 'ready', reason: '', helperPid: instance.pid, helperVersion: health.version, protocolVersion: health.protocolVersion, endpoint, token, blockTargets: false });
  }
  function readConfig() {
    try {
      const document = readSettingsDocument(configPath);
      if (document === null) return { config: defaultConfig(), stamp: 'absent', key: 'absent' };
      const { text, stat } = document;
      return { config: validateConfig(parseSettingsJson(text)), stamp: `${stat.mtimeMs}:${stat.ctimeMs}:${stat.size}`, key: text };
    } catch (error) {
      throw outboundError(error, 'config', 'OUTBOUND_CONFIG_INVALID');
    }
  }
  async function apply(force = false) {
    if (stopped) return;
    let loaded;
    try { loaded = readConfig(); }
    catch (error) {
      // Keep the last-good config for diagnostics only. An unreadable/invalid
      // selection cannot authorize any supported source to use the native route.
      state.selectionInvalid = true;
      fail(error, 'config');
      try { await stopChild(); state.helperPid = null; publish(); } catch (failure) { fail(failure, 'stop'); }
      return;
    }
    if (!force && loaded.key === fingerprint && loaded.stamp === configStamp) return;
    const changed = JSON.stringify(loaded.config) !== JSON.stringify(state.config);
    if (!force && !changed && state.status !== 'failed') { fingerprint = loaded.key; configStamp = loaded.stamp; return; }
    fingerprint = loaded.key; configStamp = loaded.stamp;
    try {
      // Invalidate the session before disposing the previous authenticated transport.
      publish({ config: loaded.config, status: loaded.config.enabled ? 'starting' : 'off', reason: '', endpoint: '', token: '', blockTargets: false, selectionInvalid: false });
      await stopChild();
      publish({ helperPid: null });
      if (loaded.config.enabled) await startChild();
    } catch (error) {
      fail(error, error.phase || 'start');
      try { await stopChild(); state.helperPid = null; publish(); } catch (failure) { fail(failure, 'stop'); }
    }
  }
  function runtimeExit() {
    stopped = true;
    try { removeSession(); } catch (error) { log('[app-outbound]', describeError(outboundError(error, 'exit-session', 'OUTBOUND_STATE_IO'))); }
    if (child && !child.done) child.process.stdin.end();
  }
  return {
    snapshot, safeSnapshot,
    refresh: () => enqueue(() => apply(true)),
    async start() {
      if (started) throw new Error('outbound runtime already started'); started = true;
      process.once('exit', runtimeExit);
      try {
        fs.mkdirSync(path.dirname(configPath), { recursive: true }); removeSession();
        watcher = fs.watch(path.dirname(configPath), { persistent: false }, (_event, name) => {
          if (name && String(name) !== path.basename(configPath)) return;
          clearTimeout(debounce);
          debounce = setTimeout(() => { void enqueue(() => apply(false)).catch(error => fail(error)); }, 150); debounce.unref();
        });
        watcher.on('error', error => { fail(error, 'watch', 'OUTBOUND_WATCH'); void enqueue(stopChild).catch(failure => fail(failure, 'stop')); });
        await enqueue(() => apply(true));
      } catch (error) { fail(error, 'watch', 'OUTBOUND_WATCH'); }
      heartbeat = setInterval(() => {
        if (stopped) return;
        try { writeStatus(); }
        catch (error) { fail(error, 'status-write', 'OUTBOUND_STATE_IO'); void enqueue(stopChild).catch(failure => fail(failure, 'stop')); }
      }, 3000); heartbeat.unref();
    },
    async stop() {
      stopped = true; watcher?.close(); clearTimeout(debounce); clearInterval(heartbeat);
      await queued;
      try {
        removeSession(); await stopChild();
        publish({ status: 'off', reason: '', helperPid: null, endpoint: '', token: '', blockTargets: false });
      } catch (error) { fail(error, 'stop'); throw outboundError(error, 'stop'); }
      finally { bridge.stop(); process.removeListener('exit', runtimeExit); }
    },
  };
}
module.exports = { createAppOutboundRuntime, validateConfig, defaultConfig, atomicJson };
