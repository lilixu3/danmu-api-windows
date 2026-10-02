'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function waitFor(fn, timeout = 7000) {
  const until = Date.now() + timeout; let last;
  while (Date.now() < until) { try { const value = await fn(); if (value) return value; } catch (error) { last = error; } await delay(25); }
  throw last || new Error('Timed out');
}
function alive(pid) {
  try { process.kill(pid, 0); return true; } catch (error) { if (error.code === 'ESRCH') return false; throw error; }
}
async function killChild(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const closed = once(child, 'close'); child.kill('SIGKILL'); await closed;
}
function temporary() { return fs.mkdtempSync(path.join(os.tmpdir(), 'danmu outbound 中文 ')); }
function writeConfig(file, config) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file + '.pending', JSON.stringify(config)); fs.renameSync(file + '.pending', file);
}
module.exports = { delay, waitFor, alive, killChild, temporary, writeConfig, hostRoot: path.resolve(__dirname, '../runtime/node-host'), fixturePath: path.join(__dirname, 'fake-outbound-helper.cjs') };
