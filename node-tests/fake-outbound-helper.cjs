'use strict';
const http = require('node:http');
const mode = process.argv[2] || 'normal';
if (mode === 'exit-before-ready') { console.error('fixture startup failed'); process.exit(7); }
if (mode === 'hang') { process.stdin.resume(); process.stdin.on('end', () => process.exit(0)); return; }
const server = http.createServer((req, res) => {
  const token = process.env.DANMU_OUTBOUND_TOKEN;
  const supplied = req.url === '/proxy' ? req.headers['x-danmu-outbound-token'] : String(req.headers.authorization || '').replace(/^Bearer /, '');
  if (supplied !== token) { res.writeHead(401, {'X-Danmu-Outbound-Error': '1', 'Content-Type': 'application/json'}); res.end(JSON.stringify({ success: false, errorCode: 'UNAUTHORIZED', phase: 'auth', message: 'unauthorized' })); return; }
  if (req.url === '/health') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ success: mode !== 'health-failed', protocolVersion: mode === 'wrong-protocol' ? 99 : 1, pid: mode === 'wrong-pid' ? process.pid + 1 : process.pid, version: 'windows-fixture-v1' })); return; }
  if (req.url === '/kill') { res.end('bye'); setTimeout(() => { console.error('fixture crash token=' + token); process.exit(7); }, 10); return; }
  const chunks = [];
  req.on('data', chunk => chunks.push(chunk));
  req.on('end', () => {
    const target = req.headers['x-danmu-outbound-target'];
    if (!target) { res.writeHead(404); res.end(); return; }
    res.setHeader('Content-Type', 'application/json'); res.setHeader('X-Danmu-Outbound-Protocol', 'h2'); res.setHeader('X-Danmu-Outbound-Ech', 'true');
    res.end(JSON.stringify({ via: 'app-helper', helperPid: process.pid, targetHost: new URL(target).hostname, body: Buffer.concat(chunks).toString('utf8') }));
  });
});
server.listen(0, '127.0.0.1', () => {
  const host = mode === 'lan-ready' ? '0.0.0.0' : '127.0.0.1';
  console.log(JSON.stringify({ ready: true, appProtocol: 1, url: 'http://' + host + ':' + server.address().port }));
  if (mode === 'exit-after-ready') setTimeout(() => process.exit(7), 250);
});
function close() { server.closeAllConnections(); server.close(() => process.exit(0)); }
process.stdin.resume();
process.stdin.on('end', () => {
  if (mode === 'ignore-eof') return;
  if (mode === 'eof-delay') setTimeout(close, 250); else close();
});
