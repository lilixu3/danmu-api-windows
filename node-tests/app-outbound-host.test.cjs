'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { once } = require('node:events');
const { spawn } = require('node:child_process');
const { Worker } = require('node:worker_threads');
const { defaultConfig } = require('../runtime/node-host/app-outbound-runtime.js');
const { temporary, writeConfig, waitFor, alive, killChild, hostRoot } = require('./support.cjs');
const enabled = () => ({ ...defaultConfig(), enabled: true });
async function freePort() { const server = net.createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening'); const port = server.address().port; await new Promise(resolve => server.close(resolve)); return port; }
async function hostFixture(workerEnabled, fn, workerFailure = false, options = {}) {
  const directory = temporary(); let child, logs = '', helperPid;
  try {
    for (const file of fs.readdirSync(hostRoot)) if (file.endsWith('.js') || file === 'package.json') fs.copyFileSync(path.join(hostRoot, file), path.join(directory, file));
    const configPath = path.join(directory, 'config', 'outbound', 'settings.json'); writeConfig(configPath, options.settings || enabled());
    const port = await freePort(), base = `http://127.0.0.1:${port}`;
    const envFile = path.join(directory, 'config', '.env');
    function writeEnv(variant = 'stable', outbound = 'auto') {
      fs.writeFileSync(envFile, `TOKEN=\nDANMU_API_VARIANT=${variant}\nOUTBOUND_MODE=${outbound}\nDANMU_API_WORKER=${workerEnabled}\nDANMU_API_HOT_RELOAD=false\nDANMU_API_PORT=${port}\nDANMU_API_HOST=127.0.0.1\nPROXY_URL=\n`);
    }
    writeEnv();
    for (const variant of ['stable', 'custom']) {
      const coreDir = path.join(directory, 'danmu_api_' + variant); fs.mkdirSync(coreDir); fs.writeFileSync(path.join(coreDir, 'package.json'), '{"type":"module"}');
      fs.writeFileSync(path.join(coreDir, 'worker.js'), `import {get} from 'node:https';import {isMainThread} from 'node:worker_threads';import fs from 'node:fs';
        ${workerFailure ? "if(!isMainThread)throw new Error('fixture Worker bridge initialization refused');fs.writeFileSync('direct-fallback-was-used','bad');" : ''}
        ${options.delayImport ? `fs.writeFileSync('import-pending-${variant}','ready');while(!fs.existsSync('release-import-${variant}'))await new Promise(r=>setTimeout(r,20));` : ''}
        const importedProbe=await fetch('https://api.gamer.com.tw/import-probe').then(r=>r.json());
        export async function handleRequest(req,env){
          const query=new URL(req.url).searchParams;
          if(query.has('drain')){fs.writeFileSync('draining-${variant}','ready');while(!fs.existsSync('release-draining-${variant}'))await new Promise(r=>setTimeout(r,20));}
          if(query.has('env'))return Response.json({variant:'${variant}',coreOutbound:env.OUTBOUND_MODE});
          const host=query.get('source')==='dandan'?'api.danmaku.weeblify.app':query.get('source')==='animeko'?'danmaku-global.myani.org':'api.gamer.com.tw';
          const result=query.has('https')?await new Promise((resolve,reject)=>get('https://'+host+'/static-esm-probe',res=>{const chunks=[];res.on('data',c=>chunks.push(c));res.on('end',()=>resolve(JSON.parse(Buffer.concat(chunks))));}).on('error',reject)):await fetch('https://'+host+'/core-probe').then(r=>r.json());
          return Response.json({variant:'${variant}',coreOutbound:env.OUTBOUND_MODE,importedProbe,result,isMainThread});
        }`);
    }
    const childEnv = { ...process.env, DANMU_API_RUNTIME_IDENTITY: 'desktop-host-isolated' };
    for (const key of ['DANMU_API_HOME', 'DANMU_API_PORT', 'DANMU_API_HOST', 'DANMU_API_VARIANT', 'DANMU_APP_OUTBOUND_CONFIG', 'DANMU_APP_OUTBOUND_HELPER']) delete childEnv[key];
    child = spawn(process.execPath, ['--require', path.join(__dirname, 'host-fixture-preload.cjs'), path.join(directory, 'main.js')], { cwd: directory, env: childEnv, stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
    child.stdout.on('data', chunk => logs += chunk); child.stderr.on('data', chunk => logs += chunk);
    if (!workerFailure && !options.delayImport) await waitFor(async () => { const response = await fetch(base + '/__health'); return response.status === 200; });
    const sessionPath = path.join(path.dirname(configPath), 'session.json');
    if (fs.existsSync(sessionPath)) helperPid = JSON.parse(fs.readFileSync(sessionPath)).helperPid;
    await fn({ child, directory, configPath, base, envFile, writeEnv, logs: () => logs, sessionPath });
  } catch (error) { error.message += '\nHost output:\n' + logs.slice(-5000); throw error; }
  finally {
    await killChild(child);
    if (helperPid) await waitFor(() => !alive(helperPid));
    const sessionPath = path.join(directory, 'config', 'outbound', 'session.json');
    if (fs.existsSync(sessionPath)) { const pid = JSON.parse(fs.readFileSync(sessionPath)).helperPid; if (alive(pid)) { process.kill(pid, 'SIGKILL'); await waitFor(() => !alive(pid)); } }
    fs.rmSync(directory, { recursive: true, force: true });
  }
}
for (const workerEnabled of [true, false]) test(`host ${workerEnabled ? 'Worker' : 'direct import'} takes over before import, shares helper on core switch and restores outbound mode`, { timeout: 25000 }, async () => hostFixture(workerEnabled, async ({ child, configPath, base, writeEnv, sessionPath }) => {
  const health = await (await fetch(base + '/__health')).json();
  assert.equal(health.runtimeIdentity, 'desktop-host-isolated'); assert.equal(health.pid, child.pid); assert.equal(health.ports.main, Number(new URL(base).port)); assert.equal(health.cwd, health.resolvedHome);
  assert.equal(health.appOutbound.status, 'ready'); assert.equal(health.appOutbound.token, undefined); assert.equal(health.appOutbound.endpoint, undefined);
  const first = await (await fetch(base + '/api/probe')).json(); assert.equal(first.variant, 'stable'); assert.equal(first.coreOutbound, 'off'); assert.equal(first.isMainThread, !workerEnabled);
  assert.equal(first.result.via, 'app-helper'); assert.equal(first.importedProbe.helperPid, first.result.helperPid);
  const session = JSON.parse(fs.readFileSync(sessionPath)); assert.equal(session.nodePid, child.pid); assert.equal(session.helperPid, first.result.helperPid);
  for (const source of ['dandan', 'animeko']) {
    const result = await (await fetch(base + '/api/probe?source=' + source + '&https=1')).json();
    assert.equal(result.result.helperPid, first.result.helperPid); assert.equal(result.result.targetHost, source === 'dandan' ? 'api.danmaku.weeblify.app' : 'danmaku-global.myani.org');
  }
  writeEnv('custom', 'h3');
  const next = await waitFor(async () => { const result = await (await fetch(base + '/api/probe')).json(); return result.variant === 'custom' ? result : null; });
  assert.equal(next.importedProbe.helperPid, first.result.helperPid); assert.equal(next.result.helperPid, first.result.helperPid); assert.equal(next.coreOutbound, 'off');
  writeConfig(configPath, { ...enabled(), enabled: false });
  await waitFor(async () => { const result = await (await fetch(base + '/api/probe?env=1')).json(); return result.coreOutbound === 'h3'; });
  await waitFor(() => !alive(first.result.helperPid)); assert.equal(fs.existsSync(sessionPath), false);
  writeConfig(configPath, enabled());
  await waitFor(async () => (await (await fetch(base + '/__health')).json()).appOutbound.status === 'ready');
  const reenabled = await (await fetch(base + '/api/probe?https=1')).json(); assert.equal(reenabled.coreOutbound, 'off'); assert.notEqual(reenabled.result.helperPid, first.result.helperPid);
  child.send('test-shutdown'); await waitFor(() => child.exitCode !== null, 7000); assert.equal(child.exitCode, 0); assert.equal(alive(reenabled.result.helperPid), false);
  assert.equal(fs.existsSync(sessionPath), false); assert.equal(JSON.parse(fs.readFileSync(path.join(path.dirname(configPath), 'status.json'))).status, 'off');
}));
test('enabled Worker initialization failure is explicit and cannot fall back to successful direct import', { timeout: 15000 }, async () => hostFixture(true, async ({ child, directory, logs }) => {
  await waitFor(() => child.exitCode !== null, 10000); assert.equal(child.exitCode, 1); assert.equal(fs.existsSync(path.join(directory, 'direct-fallback-was-used')), false);
  assert.match(logs(), /Worker init failed/); assert.equal(logs().includes('fallback to direct import'), false);
}, true));
test('candidate Worker receives helper reload while core import is pending', { timeout: 10000 }, async () => hostFixture(true, async ({ child, directory, configPath, base, sessionPath }) => {
  await waitFor(() => fs.existsSync(path.join(directory, 'import-pending-stable')));
  const first = JSON.parse(fs.readFileSync(sessionPath));
  writeConfig(configPath, { ...enabled(), httpVersion: 'h3' });
  const next = await waitFor(() => { const current = JSON.parse(fs.readFileSync(sessionPath)); return current.helperPid !== first.helperPid ? current : null; });
  fs.writeFileSync(path.join(directory, 'release-import-stable'), 'go');
  const result = await waitFor(async () => (await fetch(base + '/api/probe')).json());
  assert.equal(result.importedProbe.helperPid, next.helperPid); assert.equal(result.result.helperPid, next.helperPid); assert.equal(alive(first.helperPid), false);
  child.send('test-shutdown'); await waitFor(() => child.exitCode !== null); assert.equal(child.exitCode, 0); assert.equal(alive(next.helperPid), false);
}, false, { delayImport: true }));
test('candidate Worker receives failed config before pending import sends any target request', { timeout: 10000 }, async () => hostFixture(true, async ({ child, directory, configPath, sessionPath, logs }) => {
  await waitFor(() => fs.existsSync(path.join(directory, 'import-pending-stable')));
  const first = JSON.parse(fs.readFileSync(sessionPath));
  fs.writeFileSync(configPath + '.pending', '{"enabled":true,"enabled":false}'); fs.renameSync(configPath + '.pending', configPath);
  await waitFor(() => JSON.parse(fs.readFileSync(path.join(path.dirname(configPath), 'status.json'))).status === 'failed');
  fs.writeFileSync(path.join(directory, 'release-import-stable'), 'go');
  await waitFor(() => child.exitCode !== null); assert.equal(child.exitCode, 1); assert.equal(alive(first.helperPid), false);
  assert.match(logs(), /duplicate fields/); assert.match(logs(), /Worker init failed/); assert.equal(logs().includes('fallback to direct import'), false);
}, false, { delayImport: true }));
test('draining Worker receives latest ready helper session after core switch', { timeout: 12000 }, async () => hostFixture(true, async ({ child, directory, configPath, base, writeEnv, sessionPath }) => {
  const first = JSON.parse(fs.readFileSync(sessionPath));
  const pending = fetch(base + '/api/probe?drain=1').then(response => response.json()).then(value => ({ value }), error => ({ error }));
  await waitFor(() => fs.existsSync(path.join(directory, 'draining-stable')));
  writeEnv('custom');
  await waitFor(async () => (await (await fetch(base + '/api/probe?env=1')).json()).variant === 'custom');
  writeConfig(configPath, { ...enabled(), httpVersion: 'h3' });
  const next = await waitFor(() => { const current = JSON.parse(fs.readFileSync(sessionPath)); return current.helperPid !== first.helperPid ? current : null; });
  fs.writeFileSync(path.join(directory, 'release-draining-stable'), 'go');
  const outcome = await pending; if (outcome.error) throw outcome.error;
  const result = outcome.value; assert.equal(result.variant, 'stable'); assert.equal(result.result.helperPid, next.helperPid); assert.equal(alive(first.helperPid), false);
  child.send('test-shutdown'); await waitFor(() => child.exitCode !== null); assert.equal(child.exitCode, 0); assert.equal(alive(next.helperPid), false);
}));
test('public host health omits custom DoH query credentials while local status preserves full config', { timeout: 10000 }, async () => {
  const settings = { ...enabled(), dohUrl: 'https://dns.invalid/dns-query?api_key=credential-fixture-secret' };
  await hostFixture(true, async ({ child, configPath, base }) => {
    const response = await fetch(base + '/__health'), text = await response.text(), health = JSON.parse(text);
    assert.equal(text.includes('credential-fixture-secret'), false); assert.equal(health.appOutbound.config.dohUrl, undefined);
    assert.deepEqual(health.appOutbound.config.sources, settings.sources); assert.equal(health.appOutbound.status, 'ready');
    const status = JSON.parse(fs.readFileSync(path.join(path.dirname(configPath), 'status.json'))); assert.deepEqual(status.config, settings);
    child.send('test-shutdown'); await waitFor(() => child.exitCode !== null); assert.equal(child.exitCode, 0);
  }, false, { settings });
});
test('Worker rejects unknown or missing helper session before importing core', { timeout: 10000 }, async () => {
  const directory = temporary(); fs.mkdirSync(path.join(directory, 'danmu_api_stable')); fs.writeFileSync(path.join(directory, 'danmu_api_stable', 'package.json'), '{"type":"module"}');
  fs.writeFileSync(path.join(directory, 'danmu_api_stable', 'worker.js'), "import fs from 'node:fs';fs.writeFileSync('bad-session-imported','bad');export function handleRequest(){return Response.json({bad:true});}");
  try {
    for (const snapshot of [undefined, { config: enabled(), status: 'ready', reason: '', endpoint: 'http://0.0.0.0:1234', token: 'a'.repeat(64), helperPid: 1234, protocolVersion: 1 }, { config: enabled(), status: 'ready', reason: '', endpoint: 'http://127.0.0.1:1234', token: 'invalid', helperPid: 1234, protocolVersion: 1 }]) {
      const worker = new Worker(path.join(hostRoot, 'worker-proxy.js'), { workerData: { variantDir: 'danmu_api_stable', projectDir: directory, env: {}, outbound: snapshot } });
      const error = await new Promise(resolve => worker.once('error', resolve)); await worker.terminate(); assert.match(error.message, /outbound snapshot|outbound session/);
    }
    assert.equal(fs.existsSync(path.join(directory, 'bad-session-imported')), false);
  } finally { fs.rmSync(directory, { recursive: true, force: true }); }
});
