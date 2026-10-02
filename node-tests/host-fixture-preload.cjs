'use strict';
if (!process.env.NODE_TEST_CONTEXT) throw new Error('fixture preload requires node --test');
const path = require('node:path');
const runtime = require(path.join(process.cwd(), 'app-outbound-runtime.js'));
const original = runtime.createAppOutboundRuntime;
runtime.createAppOutboundRuntime = options => original({ ...options, helperPath: process.execPath, testOnly: { helperArgs: [path.join(__dirname, 'fake-outbound-helper.cjs'), 'normal'], prepareSecureDirectory() {} } });
process.on('message', message => { if (message === 'test-shutdown') { process.emit('SIGTERM'); process.disconnect(); } });
process.channel?.unref();
