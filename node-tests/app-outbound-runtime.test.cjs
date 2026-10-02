'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const https = require('node:https');
const { createAppOutboundRuntime, validateConfig, defaultConfig } = require('../runtime/node-host/app-outbound-runtime.js');
const { temporary, writeConfig, waitFor, delay, alive, killChild, fixturePath, hostRoot } = require('./support.cjs');
const enabled = () => ({ ...defaultConfig(), enabled: true });
async function fixture(mode, fn, options = {}) {
  const directory = temporary(), configPath = path.join(directory, 'config', 'outbound', 'settings.json');
  writeConfig(configPath, enabled()); const logs = [];
  const runtime = createAppOutboundRuntime({ configPath, helperPath: process.execPath, runtimeIdentity: 'desktop-test-identity', log: (...parts) => logs.push(parts.join(' ')), testOnly: { helperArgs: [fixturePath, mode], startTimeoutMs: 500, prepareSecureDirectory() {}, ...options } });
  try { await runtime.start(); await fn({ runtime, configPath, directory, logs }); }
  finally { await runtime.stop().catch(error => { assert.equal(runtime.snapshot().status, 'failed', error.message); }); fs.rmSync(directory, { recursive: true, force: true }); }
}
test('invalid config after disabled empty selection blocks every supported route with zero native dispatch',async()=>{
  const originalFetch=globalThis.fetch, originalRequest=https.request;
  const native=[];
  globalThis.fetch=async(...args)=>{native.push(['fetch',...args]);return new Response('native route marker');};
  https.request=(...args)=>{native.push(['https',...args]);return {end(){}};};
  const directory=temporary(),configPath=path.join(directory,'outbound','settings.json');
  const disabled={...defaultConfig(),sources:[]};writeConfig(configPath,disabled);
  const runtime=createAppOutboundRuntime({configPath,helperPath:path.join(directory,'must-not-start.exe'),log:()=>{}});
  const targets=['api.gamer.com.tw','api.tmdb.org','api.danmaku.weeblify.app','api.bangumi.vip'];
  try {
    await runtime.start();assert.equal(runtime.snapshot().status,'off');
    await fetch('https://api.tmdb.org/disabled');https.request('https://api.tmdb.org/disabled');assert.equal(native.length,2);native.length=0;
    for(const document of [JSON.stringify({...disabled,enabled:true}),'{malformed-json']) {
      fs.writeFileSync(configPath,document);await runtime.refresh();
      assert.equal(runtime.snapshot().status,'failed');assert.deepEqual(runtime.snapshot().config,disabled);assert.equal(runtime.snapshot().blockTargets,true);assert.equal(runtime.snapshot().selectionInvalid,true);
      const errors=[];
      for(const host of targets) {
        try {await fetch('https://'+host+'/must-not-dispatch');}catch(error){errors.push(error);}
        try {https.request({hostname:host,path:'/must-not-dispatch'});}catch(error){errors.push(error);}
      }
      assert.equal(native.length,0,'invalid configuration must not use prior empty sources to dispatch natively');
      assert.equal(errors.length,targets.length*2);assert.ok(errors.every(error=>/OUTBOUND_CONFIG_INVALID/.test(error.message)));
      writeConfig(configPath,disabled);await runtime.refresh();assert.equal(runtime.snapshot().status,'off');assert.equal(runtime.snapshot().selectionInvalid,false);
      await fetch('https://api.tmdb.org/explicitly-disabled-again');assert.equal(native.length,1);native.length=0;
    }
  } finally {
    await runtime.stop();globalThis.fetch=originalFetch;https.request=originalRequest;fs.rmSync(directory,{recursive:true,force:true});
  }
});
test('session ACL preparation precedes helper token and verify precedes every pending session byte',async()=>{
  const stages=[];const original=fs.writeFileSync;
  try {
    fs.writeFileSync=function(file,data,...args){
      if(String(file).includes('session.json') && String(file).endsWith('.pending')) {
        assert.deepEqual(stages,['prepare','verify']);assert.match(JSON.parse(data).token,/^[a-f0-9]{64}$/);
      }
      return original.call(this,file,data,...args);
    };
    await fixture('normal',async({runtime})=>{assert.equal(runtime.snapshot().status,'ready');}, {prepareSecureDirectory(_directory,mode){stages.push(mode);}});
  } finally {fs.writeFileSync=original;}
});
test('ACL rejection blocks helper startup and writes no token session or pending file',async()=>fixture('normal',async({runtime,configPath})=>{
  assert.equal(runtime.snapshot().status,'failed');assert.equal(runtime.snapshot().helperPid,null);assert.match(runtime.snapshot().reason,/OUTBOUND_SESSION_ACL/);
  assert.ok(!fs.readdirSync(path.dirname(configPath)).some(name=>name.startsWith('session.json')));
  await assert.rejects(fetch('https://api.tmdb.org/denied'),/OUTBOUND_SESSION_ACL/);
},{prepareSecureDirectory(){throw new Error('unsafe ancestor fixture');}}));
test('strict schema requires every field and rejects malformed types without defaults', () => {
  const config = enabled(); assert.deepEqual(validateConfig(config), config);
  for (const key of Object.keys(config)) { const bad = { ...config }; delete bad[key]; assert.throws(() => validateConfig(bad), /missing/); }
  for (const value of [null, [], {}, { ...config, schemaVersion: 2 }, { ...config, enabled: 'true' }, { ...config, sources: ['tmdb', 'tmdb'] }, { ...config, sources: ['evil'] }, { ...config, sources: 'tmdb' }, { ...config, connectTimeoutMs: '3000' }, { ...config, connectTimeoutMs: 1.5 }, { ...config, connectTimeoutMs: 0 }, { ...config, httpVersion: 'h1' }, { ...config, dohUrl: 123 }, { ...config, dohUrl: 'http://dns.invalid' }, { ...config, dohUrl: 'https://a:b@dns.invalid' }, { ...config, extra: true }]) assert.throws(() => validateConfig(value));
});
test('settings file rejects duplicate JSON keys including escaped equivalent names', async () => {
  for (const duplicate of ['"enabled":true,"enabled":false', '"enabled":true,"en\\u0061bled":false']) {
    const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json');
    fs.mkdirSync(path.dirname(configPath)); fs.writeFileSync(configPath, JSON.stringify(defaultConfig()).replace('"enabled":false', duplicate));
    const runtime = createAppOutboundRuntime({ configPath, helperPath: path.join(directory, 'missing.exe'), log: () => {} });
    try { await runtime.start(); assert.equal(runtime.snapshot().status, 'failed'); assert.match(runtime.snapshot().reason, /duplicate/); assert.equal(runtime.snapshot().helperPid, null); }
    finally { await runtime.stop(); fs.rmSync(directory, { recursive: true, force: true }); }
  }
});
test('enabled requires a source while disabled permits an empty sources array', () => {
  assert.throws(() => validateConfig({ ...enabled(), sources: [] }), /at least one source/);
  assert.deepEqual(validateConfig({ ...defaultConfig(), sources: [] }).sources, []);
});
test('DoH rejects embedded whitespace, backslash, fragments and userinfo with an 8192 character limit', () => {
  for (const value of ['https://dns.invalid/dns-\nquery', 'https://dns.invalid/\tdns-query', 'https://dns.invalid/dns query', 'https://dns.invalid/\u0085dns-query', 'https://dns.invalid/\u00a0dns-query', 'https://dns.invalid/\\dns-query', 'https://dns.invalid/#', 'https://dns.invalid/#fragment', 'https://user:secret@dns.invalid/dns-query', 'https://@dns.invalid/dns-query']) {
    assert.throws(() => validateConfig({ ...defaultConfig(), dohUrl: value }), /DoH/);
  }
  const limit = 'https://dns.invalid/' + 'a'.repeat(8192 - 'https://dns.invalid/'.length);
  assert.equal(validateConfig({ ...defaultConfig(), dohUrl: limit }).dohUrl.length, 8192);
  assert.throws(() => validateConfig({ ...defaultConfig(), dohUrl: limit + 'a' }), /DoH/);
});
async function checkSettingsDocument(content, status, reason) {
  const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json'), logs = [];
  fs.mkdirSync(path.dirname(configPath)); fs.writeFileSync(configPath, content);
  const runtime = createAppOutboundRuntime({ configPath, helperPath: path.join(directory, 'missing.exe'), log: (...parts) => logs.push(parts.join(' ')) });
  try {
    await runtime.start(); assert.equal(runtime.snapshot().status, status, runtime.snapshot().reason);
    if (reason) assert.match(runtime.snapshot().reason, reason);
    assert.equal(runtime.snapshot().helperPid, null); assert.equal(fs.existsSync(path.join(path.dirname(configPath), 'session.json')), false);
    assert.equal(logs.join('\n').includes('credential-fixture-secret'), false);
    if (status === 'failed') await assert.rejects(fetch('https://api.tmdb.org/strict-config'), /OUTBOUND_CONFIG_INVALID|config/);
  } finally { await runtime.stop(); fs.rmSync(directory, { recursive: true, force: true }); }
}
test('settings document accepts exactly 1 MiB and rejects larger UTF-8 byte counts', async () => {
  const text = JSON.stringify(defaultConfig()), size = 1_048_576;
  await checkSettingsDocument(text + ' '.repeat(size - Buffer.byteLength(text)), 'off');
  await checkSettingsDocument(text + ' '.repeat(size - Buffer.byteLength(text) + 1), 'failed', /1 MiB/);
  const oversizedUnicode = '{"dohUrl":"' + '\u00e9'.repeat(size / 2) + '"}';
  assert.ok(oversizedUnicode.length < size && Buffer.byteLength(oversizedUnicode) > size);
  await checkSettingsDocument(oversizedUnicode, 'failed', /1 MiB/);
});
test('settings JSON depth boundary matches C# depth 16 before schema validation', async () => {
  await checkSettingsDocument('{"schemaVersion":' + '['.repeat(15) + '1' + ']'.repeat(15) + '}', 'failed', /settings missing enabled/);
  await checkSettingsDocument('{"schemaVersion":' + '['.repeat(16) + '1' + ']'.repeat(16) + '}', 'failed', /depth 16/);
});
test('settings tokenizer rejects malformed JSON and UTF-8 without exposing document contents', async () => {
  for (const text of ['{"password":"credential-fixture-secret",}', '{"schemaVersion":01}', '{"schemaVersion":1e}', '{"enabled":false} false', '{"enabled":false,"sources":["tmdb",]}', '{"dohUrl":"\\x41"}', '\ufeff' + JSON.stringify(defaultConfig())]) {
    await checkSettingsDocument(text, 'failed', /not valid JSON/);
  }
  await checkSettingsDocument(Buffer.from([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]), 'failed', /UTF-8/);
  await checkSettingsDocument(JSON.stringify(defaultConfig()).replace('"enabled"', '"en\\u0061bled"'), 'off');
});
test('missing settings is explicit off default and removes stale session', async () => {
  const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json');
  fs.mkdirSync(path.dirname(configPath)); fs.writeFileSync(path.join(path.dirname(configPath), 'session.json'), '{"stale":true}');
  const runtime = createAppOutboundRuntime({ configPath, helperPath: 'missing.exe', log: () => {} });
  try { await runtime.start(); assert.equal(runtime.snapshot().status, 'off'); assert.deepEqual(runtime.snapshot().config, defaultConfig()); assert.equal(fs.existsSync(path.join(path.dirname(configPath), 'session.json')), false); }
  finally { await runtime.stop(); fs.rmSync(directory, { recursive: true, force: true }); }
});
test('authenticated ready writes strict public status and private session; heartbeat <=5s', { timeout: 10000 }, async () => fixture('normal', async ({ runtime, configPath }) => {
  const snapshot = runtime.snapshot(); assert.equal(snapshot.status, 'ready'); assert.equal(snapshot.protocolVersion, 1); assert.equal(snapshot.helperVersion, 'windows-fixture-v1');
  const statusPath = path.join(path.dirname(configPath), 'status.json'), sessionPath = path.join(path.dirname(configPath), 'session.json');
  const status = JSON.parse(fs.readFileSync(statusPath)), session = JSON.parse(fs.readFileSync(sessionPath));
  assert.deepEqual(Object.keys(status).sort(), ['schemaVersion', 'status', 'reason', 'nodePid', 'helperPid', 'runtimeIdentity', 'heartbeatUnixMs', 'config'].sort());
  assert.equal(status.nodePid, process.pid); assert.equal(status.runtimeIdentity, 'desktop-test-identity'); assert.deepEqual(status.config, enabled()); assert.equal(status.helperPid, snapshot.helperPid); assert.equal(status.token, undefined);
  assert.deepEqual(Object.keys(session).sort(), ['schemaVersion', 'endpoint', 'token', 'nodePid', 'helperPid', 'runtimeIdentity'].sort());
  assert.match(session.token, /^[a-f0-9]{64}$/); assert.equal(session.endpoint, snapshot.endpoint); assert.equal(session.schemaVersion, 1);
  assert.equal((await fetch(session.endpoint + '/health')).status, 401);
  assert.equal((await (await fetch(session.endpoint + '/health', { headers: { Authorization: 'Bearer ' + session.token } })).json()).pid, snapshot.helperPid);
  await waitFor(() => JSON.parse(fs.readFileSync(statusPath)).heartbeatUnixMs > status.heartbeatUnixMs, 5000);
  const pid = snapshot.helperPid; await runtime.stop(); assert.equal(alive(pid), false); assert.equal(fs.existsSync(sessionPath), false); assert.equal(JSON.parse(fs.readFileSync(statusPath)).status, 'off');
}));
test('missing helper produces explicit failure and eligible fetch cannot fall back', async () => {
  const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json'); writeConfig(configPath, enabled());
  const runtime = createAppOutboundRuntime({ configPath, helperPath: path.join(directory, 'missing.exe'), log: () => {} });
  try { await runtime.start(); assert.equal(runtime.snapshot().status, 'failed'); assert.match(runtime.snapshot().reason, /ENOENT/); await assert.rejects(fetch('https://api.gamer.com.tw/fail-closed'), /ENOENT/); }
  finally { await runtime.stop(); fs.rmSync(directory, { recursive: true, force: true }); }
});
for (const mode of ['exit-before-ready', 'hang', 'wrong-protocol', 'wrong-pid', 'health-failed', 'lan-ready']) test(`helper ${mode} remains failed with no session`, async () => fixture(mode, async ({ runtime, configPath }) => {
  assert.equal(runtime.snapshot().status, 'failed'); assert.equal(fs.existsSync(path.join(path.dirname(configPath), 'session.json')), false); assert.ok(runtime.snapshot().reason.includes('phase='));
}));
test('invalid executable spawn error retains code and phase', async () => {
  const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json'), helperPath = path.join(directory, 'invalid.exe'); writeConfig(configPath, enabled()); fs.writeFileSync(helperPath, 'invalid executable');
  const runtime = createAppOutboundRuntime({ configPath, helperPath, log: () => {}, testOnly: { prepareSecureDirectory() {} } });
  try { await runtime.start(); assert.equal(runtime.snapshot().status, 'failed'); assert.match(runtime.snapshot().reason, /phase=spawn/); }
  finally { await runtime.stop(); fs.rmSync(directory, { recursive: true, force: true }); }
});
test('helper crash does not retry, redacts token, requires explicit apply and rotates session', async () => fixture('normal', async ({ runtime, configPath, logs }) => {
  const first = runtime.snapshot(); await fetch(first.endpoint + '/kill', { headers: { Authorization: 'Bearer ' + first.token } });
  await waitFor(() => runtime.snapshot().status === 'failed'); await waitFor(() => !alive(first.helperPid));
  await delay(2300); assert.equal(runtime.snapshot().status, 'failed'); assert.equal(fs.existsSync(path.join(path.dirname(configPath), 'session.json')), false);
  assert.equal(logs.join('\n').includes(first.token), false); await assert.rejects(fetch('https://api.tmdb.org/explicit-failure'), /helper exited/);
  await runtime.refresh(); const next = runtime.snapshot(); assert.equal(next.status, 'ready'); assert.notEqual(next.helperPid, first.helperPid); assert.notEqual(next.token, first.token);
}));
test('atomic config hot changes disable, reenable, malformed file and repair are observable', async () => fixture('normal', async ({ runtime, configPath }) => {
  const first = runtime.snapshot(); writeConfig(configPath, { ...enabled(), enabled: false }); await waitFor(() => runtime.snapshot().status === 'off' && runtime.snapshot().helperPid === null); assert.equal(alive(first.helperPid), false);
  writeConfig(configPath, { ...enabled(), httpVersion: 'h3' }); await waitFor(() => runtime.snapshot().status === 'ready'); assert.equal(runtime.snapshot().config.httpVersion, 'h3'); const second = runtime.snapshot().helperPid;
  fs.writeFileSync(configPath + '.pending', '{bad json'); fs.renameSync(configPath + '.pending', configPath); await waitFor(() => runtime.snapshot().status === 'failed'); await waitFor(() => !alive(second));
  await assert.rejects(fetch('https://api.tmdb.org/invalid-config'), /config/); writeConfig(configPath, enabled()); await waitFor(() => runtime.snapshot().status === 'ready');
}));
test('stop waits for actual delayed EOF exit', async () => fixture('eof-delay', async ({ runtime }) => {
  const pid = runtime.snapshot().helperPid, started = Date.now(); await runtime.stop(); assert.ok(Date.now() - started >= 200); assert.equal(alive(pid), false); assert.equal(runtime.snapshot().status, 'off');
}));
test('EOF timeout kills and awaits true exit but reports failed, never premature off', async () => fixture('ignore-eof', async ({ runtime }) => {
  const pid = runtime.snapshot().helperPid; await assert.rejects(runtime.stop(), /stdin EOF/); assert.equal(alive(pid), false); assert.equal(runtime.snapshot().status, 'failed'); assert.match(runtime.snapshot().reason, /OUTBOUND_EOF_TIMEOUT/);
}, { stopTimeoutMs: 150 }));
test('status write failure remains explicit and removes private session', async () => fixture('normal', async ({ runtime, configPath, logs }) => {
  const file = path.join(path.dirname(configPath), 'status.json'); fs.unlinkSync(file); fs.mkdirSync(file);
  await waitFor(() => runtime.snapshot().status === 'failed', 5000); assert.match(runtime.snapshot().reason, /status-write/); assert.equal(fs.existsSync(path.join(path.dirname(configPath), 'session.json')), false); assert.ok(logs.some(line => /status-write/.test(line)));
  fs.rmdirSync(file);
}));
test('watch failure is explicit, cancels helper, never degraded silently', async () => {
  const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json'); writeConfig(configPath, enabled());
  const original = fs.watch, logs = []; fs.watch = () => { const error = new Error('watch fixture denied'); error.code = 'EACCES'; throw error; };
  const runtime = createAppOutboundRuntime({ configPath, helperPath: process.execPath, log: (...args) => logs.push(args.join(' ')), testOnly: { helperArgs: [fixturePath, 'normal'], prepareSecureDirectory() {} } });
  try { await runtime.start(); assert.equal(runtime.snapshot().status, 'failed'); assert.match(runtime.snapshot().reason, /watch fixture denied/); assert.ok(logs.length); }
  finally { fs.watch = original; await runtime.stop(); fs.rmSync(directory, { recursive: true, force: true }); }
});
test('twenty helper lifecycle cycles leave no helper PIDs, session or pending files', { timeout: 30000 }, async () => {
  const pids = []; for (let i = 0; i < 20; i++) await fixture('normal', async ({ runtime, configPath }) => {
    const pid = runtime.snapshot().helperPid; pids.push(pid); await runtime.stop(); assert.equal(alive(pid), false);
    assert.equal(fs.existsSync(path.join(path.dirname(configPath), 'session.json')), false); assert.equal(fs.readdirSync(path.dirname(configPath)).some(name => name.endsWith('.pending')), false);
  }); assert.ok(pids.every(pid => !alive(pid)));
});
test('parent Node hard crash closes stdin and helper exits without a process tree kill', { timeout: 10000 }, async () => {
  const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json'); writeConfig(configPath, enabled()); let parent, helperPid;
  try {
    const code = `const {createAppOutboundRuntime}=require(${JSON.stringify(path.join(hostRoot, 'app-outbound-runtime.js'))});const r=createAppOutboundRuntime({configPath:${JSON.stringify(configPath)},helperPath:process.execPath,testOnly:{helperArgs:[${JSON.stringify(fixturePath)},'normal'],prepareSecureDirectory(){}}});r.start().then(()=>{if(r.snapshot().status!=='ready')process.exit(2);setInterval(()=>{},1000);});`;
    parent = spawn(process.execPath, ['--eval', code], { stdio: ['ignore', 'pipe', 'pipe'] }); let stderr = ''; parent.stderr.on('data', chunk => stderr += chunk);
    const session = await waitFor(() => JSON.parse(fs.readFileSync(path.join(path.dirname(configPath), 'session.json')))); helperPid = session.helperPid;
    assert.ok(alive(helperPid), stderr); await killChild(parent); await waitFor(() => !alive(helperPid), 5000);
  } finally { await killChild(parent); if (helperPid && alive(helperPid)) { process.kill(helperPid, 'SIGKILL'); await waitFor(() => !alive(helperPid)); } fs.rmSync(directory, { recursive: true, force: true }); }
});
// Real OS ACL preflight adds two PowerShell launches/cycle: measured 54s for
// twenty cycles, versus the former 30s whole-test budget. Product budgets unchanged.
test('native Windows EXE authenticated offline handshake and twenty lifecycle cycles', { skip: !process.env.DANMU_TEST_OUTBOUND_EXE, timeout: 90000 }, async (t) => {
  const directory = temporary(), configPath = path.join(directory, 'outbound', 'settings.json'); writeConfig(configPath, enabled()); const pids = [];
  try {
    for (let i = 0; i < 20; i++) {
      // The real trust check includes host module ancestry, not just helper.
      const trustedHost = path.join(directory, 'host'); fs.mkdirSync(trustedHost, {recursive:true});
      for (const name of ['app-outbound-runtime.js', 'app-outbound-bridge.js', 'app-outbound-errors.js']) fs.copyFileSync(path.join(hostRoot,name),path.join(trustedHost,name));
      const {createAppOutboundRuntime: createNativeRuntime} = require(path.join(trustedHost,'app-outbound-runtime.js'));
      const runtime = createNativeRuntime({ configPath, helperPath: process.env.DANMU_TEST_OUTBOUND_EXE, log: () => {} });
      try {
        const began = Date.now(); await runtime.start(); const startupMs = Date.now() - began;
        assert.equal(runtime.snapshot().status, 'ready', runtime.snapshot().reason); assert.equal(runtime.snapshot().protocolVersion, 1); assert.equal(runtime.snapshot().helperVersion,'1.0.1+6004732');
        pids.push(runtime.snapshot().helperPid); const stopping = Date.now(); await runtime.stop(); assert.equal(alive(pids.at(-1)), false);
        t.diagnostic(`native cycle=${i+1} startupWithACL=${startupMs}ms stop=${Date.now()-stopping}ms exited=true`);
      }
      finally { await runtime.stop(); }
    }
    assert.ok(pids.every(pid => !alive(pid))); assert.equal(fs.existsSync(path.join(path.dirname(configPath), 'session.json')), false);
  } finally { fs.rmSync(directory, { recursive: true, force: true }); }
});
