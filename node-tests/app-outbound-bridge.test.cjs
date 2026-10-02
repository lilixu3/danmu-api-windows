'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const https = require('node:https');
const { once } = require('node:events');
const { pathToFileURL } = require('node:url');
const path = require('node:path');
const zlib = require('node:zlib');
const { installBridge, eligible } = require('../runtime/node-host/app-outbound-bridge.js');
const { validateConfig } = require('../runtime/node-host/app-outbound-runtime.js');
const config = { schemaVersion:1, enabled:true, sources:['bahamut','tmdb'],httpVersion:'auto', dohUrl:'', connectTimeoutMs:3000 };

async function fixture(fn, { fetchImpl, requestImpl } = {}) {
  const seen=[],received=[];
  const server = http.createServer((req,res)=>{
    received.push({headers:req.headers,url:req.url});
    const body=[];
    req.on('data',chunk=>body.push(chunk));
    req.on('end',()=>{
      const target=req.headers['x-danmu-outbound-target'];
      seen.push({target, method:req.method, headers:req.headers, body:Buffer.concat(body).toString()});
      if (target && req.headers['x-danmu-outbound-token']!=='offline-secret') {res.writeHead(401,{'X-Danmu-Outbound-Error':'1'});res.end();return;}
      const url=new URL(target || req.url, 'http://localhost');
      if (url.pathname==='/redirect307') {res.writeHead(307,{Location:'https://api.tmdb.org/final'});res.end();return;}
      if (url.pathname==='/redirect303') {res.writeHead(303,{Location:'https://api.tmdb.org/final'});res.end();return;}
      if (url.pathname==='/redirect') {res.writeHead(302,{Location:'https://api.tmdb.org/final'});res.end();return;}
      if (url.pathname==='/failed') {res.writeHead(502,{'X-Danmu-Outbound-Error':'1','X-Danmu-Outbound-Phase':'tls','X-Danmu-Outbound-Code':'ECH_REJECTED'});res.end(JSON.stringify({message:'ECH refused token=offline-secret',details:'https://api.tmdb.org/3?api_key=secret-key'}));return;}
      if (url.pathname==='/business502') {res.writeHead(502,{'X-Danmu-Outbound-Protocol':'h2','X-Danmu-Outbound-Ech':'false','X-Danmu-Outbound-Token':'offline-secret'});res.end('business unavailable');return;}
      if (url.pathname==='/binary') {res.writeHead(200,{'X-Danmu-Outbound-Protocol':'h3','X-Danmu-Outbound-Ech':'true'});res.end(Buffer.from([0,255,127,128,1]));return;}
      if (url.pathname==='/nativeRedirect') {res.writeHead(302,{Location:`http://127.0.0.1:${server.address().port}/ordinary`});res.end();return;}
      if (url.pathname==='/slow') {
        res.writeHead(200);res.write('begin');
        const timer=setTimeout(()=>res.end('end'),10000);timer.unref();req.on('close',()=>clearTimeout(timer));return;
      }
      const bytes=Buffer.from(JSON.stringify({method:req.method,body:Buffer.concat(body).toString(),contentType:req.headers['content-type'] || null}));
      res.setHeader('Set-Cookie',['a=1; Path=/','b=2; Path=/']);
      res.setHeader('Content-Type','application/json');
      if (url.pathname==='/gzip') {res.setHeader('Content-Encoding','gzip');res.end(zlib.gzipSync(bytes));}
      else res.end(bytes);
    });
  });
  server.listen(0,'127.0.0.1');await once(server,'listening');
  const endpoint=`http://127.0.0.1:${server.address().port}`;
  const originalFetch=globalThis.fetch;
  const originalRequest=https.request;
  if (fetchImpl) globalThis.fetch = fetchImpl;
  if (requestImpl) https.request = requestImpl;
  const bridge=installBridge({snapshot:{status:'ready',config,endpoint,token:'offline-secret'}});
  try {await fn({bridge,endpoint,seen,received});}
  finally {
    bridge.stop();assert.equal(globalThis.fetch,fetchImpl || originalFetch);assert.equal(https.request,requestImpl || originalRequest);
    globalThis.fetch=originalFetch;https.request=originalRequest;
    server.closeAllConnections();await new Promise(resolve=>server.close(resolve));
  }
}
test('unsupported ClientRequest raw target and explicit Host bypass before dispatch unchanged', async()=>{
  const native=[];
  const requestImpl=function(...args){ native.push(args); return {end(){}}; };
  await fixture(async({seen,bridge})=>{
    bridge.update({config,status:'failed',reason:'must not affect unsupported native route'});
    for(const options of [
      {hostname:'api.tmdb.org',path:'/a/../signed'},
      {hostname:'api.tmdb.org',path:'/signed',headers:{Host:'signed.example'}},
      {hostname:'api.tmdb.org',path:'/signed',headers:['hOsT','signed.example']},
      {hostname:'api.tmdb.org',path:'/signed#literal'},
    ]) { https.request(options); assert.equal(native.at(-1)[0],options); }
    assert.equal(native.length,4);assert.equal(seen.length,0);
  },{requestImpl});
});
test('mutable ClientRequest Host rejects before any helper or native dispatch',async()=>{
  const native=[];
  await fixture(async({seen,received})=>{
    for(const mutate of [
      request=>request.setHeader('Host','signed.example'),
      request=>request.appendHeader('hOsT','signed.example'),
      request=>request.setHeaders(new Map([['Host','signed.example']])),
      request=>request.removeHeader('Host'),
      request=>http.OutgoingMessage.prototype.setHeader.call(request,'Host','signed.example'),
    ]) {
      const request=https.request({hostname:'api.tmdb.org',path:'/signed',method:'POST'});
      const outcome=new Promise(resolve=>{
        request.once('error',error=>resolve({error}));
        request.once('response',response=>{response.resume();response.once('end',()=>resolve({response}));});
      });
      // Let the local socket connect: no business headers/body may be dispatched.
      await new Promise(resolve=>setImmediate(resolve));
      let thrown;
      try {mutate(request);} catch(error){thrown=error;}
      try {request.end('must-never-dispatch');} catch(error){thrown ||= error;}
      const result=await outcome;
      assert.equal(received.length,0,'mutable Host must be rejected before even helper request headers');
      assert.equal(native.length,0,'selected request must never be retried natively');
      assert.equal(result.error?.code,'OUTBOUND_UNSUPPORTED_HOST');assert.equal(thrown,result.error);assert.equal(request.destroyed,true);
    }
    assert.equal(seen.length,0);
  },{requestImpl:(...args)=>{native.push(args);throw new Error('unexpected native dispatch');}});
});
test('mutable ordinary ClientRequest headers still dispatch once with unchanged business values',async()=>fixture(async({seen,received})=>{
  await new Promise((resolve,reject)=>{
    const request=https.request({hostname:'api.tmdb.org',path:'/mutable-normal',method:'POST'},response=>{response.resume();response.once('end',resolve);});
    request.once('error',reject);request.setHeader('X-Business','first');request.appendHeader('X-Business','second');request.setHeaders(new Map([['X-Other','third']]));request.end('one-body');
  });
  assert.equal(received.length,1);assert.equal(seen.length,1);assert.equal(seen[0].headers['x-business'],'first, second');assert.equal(seen[0].headers['x-other'],'third');assert.equal(seen[0].body,'one-body');
}));
test('failed invalid routing selection blocks all supported sources but valid helper failure retains selected scope',()=>{
  const empty={...config,enabled:false,sources:[]};
  const invalid={config:empty,status:'failed',blockTargets:true,selectionInvalid:true};
  for(const host of ['api.gamer.com.tw','api.tmdb.org','api.danmaku.weeblify.app','api.bangumi.vip']) assert.equal(eligible(new URL('https://'+host+'/x'),invalid,{}),true);
  assert.equal(eligible(new URL('https://example.invalid/x'),invalid,{}),false);
  assert.equal(eligible(new URL('https://api.gamer.com.tw/x'),invalid,{PROXY_URL:'bahamut@https://proxy.invalid'}),false);
  assert.equal(eligible(new URL('https://api.gamer.com.tw/x'),invalid,{}, {dispatcher:{}}),false);
  assert.equal(eligible(new URL('https://api.gamer.com.tw/x'),{config:{...config,sources:['tmdb']},status:'failed',blockTargets:true,selectionInvalid:false},{}),false);
});
test('fetch integrity and Host bypass preserve Request/init identity with native semantics', async()=>{
  const nativeFetch=globalThis.fetch, native=[];
  const fetchImpl=(input,init)=>{ native.push({input,init});
    if(typeof input==='string') return nativeFetch(input,init);
    return Promise.reject(new TypeError('native integrity or Host route marker'));
  };
  await fixture(async({endpoint,seen})=>{
    const integrity='sha256-'+Buffer.alloc(32).toString('base64');
    const localInit={integrity};
    // Actual stock Node integrity rejects the response, unlike the former bridge.
    await assert.rejects(fetch(endpoint+'/ordinary',localInit),/fetch failed/);
    const request=new Request('https://api.tmdb.org/signed',{integrity});
    await assert.rejects(fetch(request),/native integrity/);assert.equal(native.at(-1).input,request);assert.equal(native.at(-1).init,undefined);
    const overriding={integrity};const clean=new Request('https://api.tmdb.org/signed');
    await assert.rejects(fetch(clean,overriding),/native integrity/);assert.equal(native.at(-1).input,clean);assert.equal(native.at(-1).init,overriding);
    const hostRequest=new Request('https://api.tmdb.org/signed',{headers:{Host:'custom.example'}});
    await assert.rejects(fetch(hostRequest),/Host route/);assert.equal(native.at(-1).input,hostRequest);
    const overrideHost={headers:{Host:'custom.example'}};
    await assert.rejects(fetch(clean,overrideHost),/Host route/);assert.equal(native.at(-1).init,overrideHost);
    assert.equal(seen.filter(item=>item.target).length,0);
  },{fetchImpl});
});
test('302/303 POST redirects remove every request body header',async()=>fixture(async({seen})=>{
  for(const redirect of ['redirect','redirect303']) {
    const response=await fetch('https://api.gamer.com.tw/'+redirect,{method:'POST',body:'upload',headers:{'Content-Type':'text/plain','Content-Encoding':'identity','Content-Language':'zh','Content-Location':'/upload','Content-Length':'6'}});
    await response.text();const redirected=seen.at(-1);assert.equal(redirected.method,'GET');assert.equal(redirected.body,'');
    for(const key of ['content-type','content-encoding','content-language','content-location','content-length']) assert.equal(redirected.headers[key],undefined,key);
  }
}));
test('target, proxy and explicit transport eligibility',()=>{
  const snapshot={config};
  assert.equal(eligible(new URL('https://api.gamer.com.tw/x'),snapshot,{}),true);
  for(const target of ['https://evil.api.gamer.com.tw/x','http://api.gamer.com.tw/x','https://api.gamer.com.tw:444/x','https://a:b@api.gamer.com.tw/x']) assert.equal(eligible(new URL(target),snapshot,{}),false);
  for(const proxy of ['bahamut@https://example.invalid','@https://example.invalid','http://127.0.0.1:7890']) assert.equal(eligible(new URL('https://api.gamer.com.tw/x'),snapshot,{PROXY_URL:proxy}),false);
  assert.equal(eligible(new URL('https://api.gamer.com.tw/x'),snapshot,{PROXY_URL:'tmdb@https://example.invalid'}),true);
  assert.equal(eligible(new URL('https://api.tmdb.org/x'),snapshot,{}, {dispatcher:{}}),false);
  assert.throws(()=>validateConfig({enabled:true,httpVersion:'h1'}));
  assert.throws(()=>validateConfig({dohUrl:'http://resolver.invalid'}));
});
test('custom HTTPS Agents retain connection failures without contacting the helper',async()=>fixture(async({seen})=>{
  let calls=0;
  const rejectConnection=(_options,callback)=>{calls++;callback(new Error('custom TLS agent enforced'));};
  const explicit=new https.Agent({ca:'fixture-custom-trust-root'});
  explicit.createConnection=rejectConnection;
  class CustomAgent extends https.Agent { createConnection(options,callback){return rejectConnection(options,callback);} }
  const subclass=new CustomAgent({keepAlive:true});
  const originalGlobal=https.globalAgent;
  const run=options=>new Promise((resolve,reject)=>{
    https.get({hostname:'api.tmdb.org',path:'/custom-policy',...options},response=>{
      response.resume();response.on('end',()=>resolve(response.statusCode));
    }).on('error',reject);
  });
  try {
    await assert.rejects(run({agent:explicit}),/custom TLS agent enforced/);
    await assert.rejects(run({agent:subclass}),/custom TLS agent enforced/);
    https.globalAgent=explicit;
    await assert.rejects(run({}),/custom TLS agent enforced/);
    assert.equal(calls,3);
    assert.equal(seen.length,0);
  } finally {https.globalAgent=originalGlobal;explicit.destroy();subclass.destroy();}
}));
test('custom trust and connection options preserve the original request route',async()=>fixture(async({seen})=>{
  const target=new URL('https://api.tmdb.org/custom-policy'),snapshot={config};
  const ordinary=new https.Agent({keepAlive:true});
  const customCA=new https.Agent({ca:'fixture-custom-trust-root'});
  const customLookup=new https.Agent({lookup(){throw new Error('not used by eligibility');}});
  try {
    assert.equal(eligible(target,snapshot,{}, {agent:customCA}),false);
    assert.equal(eligible(target,snapshot,{}, {agent:customLookup}),false);
    assert.equal(eligible(target,snapshot,{}, {agent:{addRequest(){}}}),false);
    for(const options of [{checkServerIdentity(){}},{servername:'custom.test'},{secureContext:{}},{lookup(){}}]) {
      assert.equal(eligible(target,snapshot,{}, {...options,agent:ordinary}),false);
    }
    let lookupCalls=0;
    await assert.rejects(new Promise((resolve,reject)=>{
      https.get({hostname:target.hostname,path:target.pathname,agent:ordinary,lookup(_host,_options,callback){
        lookupCalls++;callback(new Error('custom lookup enforced'));
      }},response=>{response.resume();response.on('end',resolve);}).on('error',reject);
    }),/custom lookup enforced/);
    assert.equal(lookupCalls,1);
    assert.equal(seen.length,0);
  } finally {ordinary.destroy();customCA.destroy();customLookup.destroy();}
}));
test('ordinary HTTPS keepalive Agents remain enhanced',async()=>fixture(async({seen})=>{
  const agent=new https.Agent({keepAlive:true,maxSockets:2});
  try {
    assert.equal(eligible(new URL('https://api.tmdb.org/ordinary-agent'),{config},{},{agent}),true);
    const body=await new Promise((resolve,reject)=>{
      https.get({hostname:'api.tmdb.org',path:'/ordinary-agent',agent},response=>{
        const chunks=[];response.on('data',chunk=>chunks.push(chunk));response.on('end',()=>resolve(JSON.parse(Buffer.concat(chunks))));
      }).on('error',reject);
    });
    assert.equal(body.method,'GET');
    assert.equal(seen.length,1);
    assert.equal(seen[0].target,'https://api.tmdb.org/ordinary-agent');
  } finally {agent.destroy();}
}));
test('stock HTTPS globalAgent and unset proxyEnv remain enhanced',async()=>fixture(async({seen})=>{
  const target=new URL('https://api.tmdb.org/global-agent');
  assert.equal(eligible(target,{config},{},{agent:https.globalAgent}),true);
  const unset=new https.Agent({proxyEnv:undefined});
  const proxy=new https.Agent({proxyEnv:{HTTPS_PROXY:'http://127.0.0.1:7890'}});
  try {
    assert.equal(eligible(target,{config},{},{agent:unset}),true);
    assert.equal(eligible(target,{config},{},{agent:proxy}),false);
    await new Promise((resolve,reject)=>{
      https.get(target,response=>{response.resume();response.on('end',resolve);}).on('error',reject);
    });
    assert.equal(seen.length,1);
    assert.equal(seen[0].target,target.href);
  } finally {unset.destroy();proxy.destroy();}
}));
test('native fetch body, URL, cookies and untouched ordinary requests',async()=>fixture(async({endpoint,seen})=>{
  const response=await fetch('https://api.gamer.com.tw/echo?x=1',{method:'POST',body:new URLSearchParams({a:'b'})});
  assert.equal(response.url,'https://api.gamer.com.tw/echo?x=1');
  assert.equal(response.headers.getSetCookie().length,2);
  const copy=response.clone();assert.equal(copy.url,response.url);await copy.text();
  const body=await response.json();assert.equal(body.body,'a=b');assert.match(body.contentType,/application\/x-www-form-urlencoded/);
  await fetch(endpoint+'/ordinary');assert.equal(seen[1].target,undefined);
}));
test('native cross-origin redirects strip credentials and remain enhanced',async()=>fixture(async({seen})=>{
  const response=await fetch('https://api.gamer.com.tw/redirect',{headers:{Authorization:'secret',Cookie:'cookie-secret'}});
  assert.equal(response.url,'https://api.tmdb.org/final');assert.equal(response.redirected,true);await response.text();
  assert.equal(seen.length,2);assert.equal(seen[1].headers.authorization,undefined);assert.equal(seen[1].headers.cookie,undefined);
}));
test('native fetch cancellation during response body',async()=>fixture(async()=>{
  const controller=new AbortController();
  const response=await fetch('https://api.gamer.com.tw/slow',{signal:controller.signal});
  const reading=response.text();controller.abort();await assert.rejects(reading);
}));
test('component failure never falls back to ordinary target fetch',async()=>fixture(async({bridge})=>{
  await assert.rejects(fetch('https://api.gamer.com.tw/failed'),/增强直连/);
  bridge.update({config,status:'failed',reason:'component failed'});
  await assert.rejects(fetch('https://api.gamer.com.tw/echo'),/component failed/);
}));
test('bundled node-fetch POST, gzip, repeated cookies and helper errors',async()=>fixture(async()=>{
  const {default:nodeFetch}=await import(pathToFileURL(path.resolve(__dirname,'../artifacts/bundled-runtime-node22.23.2-x64/nodejs-project/node_modules/node-fetch/src/index.js')));
  const response=await nodeFetch('https://api.gamer.com.tw/gzip',{method:'POST',body:'node-fetch-body'});
  assert.equal(response.headers.raw()['set-cookie'].length,2);
  assert.equal((await response.json()).body,'node-fetch-body');
  await assert.rejects(nodeFetch('https://api.gamer.com.tw/failed'),/增强直连/);
}));
test('https.request object overload, ESM exports and body writes',async()=>fixture(async()=>{
  const esm=await import('node:https');assert.equal(esm.request,https.request);
  const result=await new Promise((resolve,reject)=>{
    const request=esm.request({hostname:'api.tmdb.org',path:'/post',method:'POST'},res=>{
      const chunks=[];res.on('data',chunk=>chunks.push(chunk));res.on('end',()=>resolve(JSON.parse(Buffer.concat(chunks))));
    });request.on('error',reject);request.write('part1');request.end('part2');
  });
  assert.equal(result.body,'part1part2');
}));
test('native 307 redirects replay the buffered business body once per redirect',async()=>fixture(async({seen})=>{
  const response=await fetch('https://api.gamer.com.tw/redirect307',{method:'POST',body:'business'});
  await response.text();assert.equal(seen.length,2);assert.equal(seen[0].body,'business');assert.equal(seen[1].method,'POST');assert.equal(seen[1].body,'business');
}));
test('cancel blocked upload before contacting the helper',async()=>fixture(async({seen})=>{
  const controller=new AbortController();
  const body=new ReadableStream({pull(){},cancel(){return new Promise(()=>{});}});
  const pending=fetch('https://api.gamer.com.tw/upload',{method:'POST',body,duplex:'half',signal:controller.signal});
  controller.abort();await assert.rejects(pending);assert.equal(seen.length,0);
}));


test('new sources opt in exact hosts, preserve proxies and carry POST bodies once', async()=>fixture(async({bridge,seen,endpoint})=>{
  const groups={dandan:['api.danmaku.weeblify.app','nipaplay.aimes-soft.com'],animeko:['api.animeko.org','danmaku-global.myani.org','danmaku-cn.myani.org','s1.animeko.openani.org','api.bangumi.vip']};
  const extended={...config,sources:['bahamut','tmdb','dandan','animeko']};
  assert.deepEqual(validateConfig(extended).sources,extended.sources);
  for(const [source,hosts] of Object.entries(groups)) for(const host of hosts) {
    assert.equal(eligible(new URL('https://'+host+'/x'),{config},{}),false);
    assert.equal(eligible(new URL('https://'+host+'/x'),{config:extended},{}),true);
    assert.equal(eligible(new URL('https://'+host+'.evil.test/x'),{config:extended},{}),false);
    for(const proxy of [source+'@https://proxy.test','@https://proxy.test','http://127.0.0.1:7890']) {
      assert.equal(eligible(new URL('https://'+host+'/x'),{config:extended},{PROXY_URL:proxy}),false);
    }
  }
  bridge.update({config:extended,status:'ready',endpoint,token:'offline-secret'});
  for(const host of ['api.danmaku.weeblify.app','api.bangumi.vip','danmaku-global.myani.org']) {
    const response=await fetch('https://'+host+'/search',{method:'POST',body:'one-business-body'});
    assert.equal((await response.json()).body,'one-business-body');
  }
  assert.equal(seen.length,3);
  assert.ok(seen.every(request=>request.method==='POST' && request.body==='one-business-body'));
}));


test('selected-source diagnostics retain each actual protocol/ECH result and explicit failures',async()=>{
  const {diagnoseSource}=require('../runtime/node-host/app-outbound-diagnostics.js');
  const snapshot={status:'ready',endpoint:'http://127.0.0.1:1234',token:'offline-secret',config:{...config,sources:['bahamut','tmdb','dandan','animeko']}};
  const seen=[];
  const result=await diagnoseSource('animeko',snapshot,{}, {fetchImpl:async(url,init)=>{
    assert.equal(url,snapshot.endpoint+'/request');assert.equal(init.headers.Authorization,'Bearer offline-secret');
    seen.push(new URL(JSON.parse(init.body).url).hostname);
    if(seen.length===1) return Response.json({success:false,phase:'tls',errorCode:'ECH_REJECTED',message:'primary unavailable'});
    return Response.json({success:true,status:200,protocol:'h3',ech:true});
  }});
  assert.equal(result.ok,false);assert.equal(result.results.length,5);assert.equal(result.results[0].code,'ECH_REJECTED');
  assert.equal(result.results[1].protocol,'h3');assert.equal(result.results[1].ech,true);
  assert.equal((await diagnoseSource('animeko',snapshot,{PROXY_URL:'animeko@https://proxy.test'},{fetchImpl:()=>{throw new Error('must not send');}})).httpStatus,409);
  assert.equal((await diagnoseSource('dandan',snapshot,{}, {fetchImpl:async()=>new Response('<html>challenge</html>')})).ok,false);
  assert.equal((await diagnoseSource('tmdb',snapshot,{}, {fetchImpl:async()=>Response.json({success:true,status:401,protocol:'h2',ech:false})})).ok,true);
  const controller=new AbortController();controller.abort();
  await assert.rejects(diagnoseSource('animeko',snapshot,{}, {signal:controller.signal,fetchImpl:()=>{throw new Error('must not send');}}),{name:'AbortError'});
});

test('fetch and https responses strip every internal header, preserve upstream 502 and binary streams',async()=>fixture(async()=>{
  const business=await fetch('https://api.tmdb.org/business502');assert.equal(business.status,502);assert.equal(await business.text(),'business unavailable');
  assert.ok([...business.headers.keys()].every(name=>!name.startsWith('x-danmu-outbound-')));
  const response=await fetch('https://api.tmdb.org/binary');assert.deepEqual(Buffer.from(await response.arrayBuffer()),Buffer.from([0,255,127,128,1]));
  assert.equal(response.headers.get('x-danmu-outbound-protocol'),null);
  await new Promise((resolve,reject)=>{
    https.get('https://api.tmdb.org/business502',res=>{
      assert.equal(res.statusCode,502);assert.ok(Object.keys(res.headers).every(name=>!name.startsWith('x-danmu-outbound-')));
      assert.ok(res.rawHeaders.every(value=>!/^x-danmu-outbound-/i.test(value)));res.resume();res.on('end',resolve);
    }).on('error',reject);
  });
}));
test('helper failures retain phase/code/message and redact secrets for fetch and https',async()=>fixture(async()=>{
  const check=error=>{assert.equal(error.code,'ECH_REJECTED');assert.equal(error.phase,'tls');assert.match(error.message,/ECH refused/);assert.ok(!error.message.includes('offline-secret'));assert.ok(!error.details.includes('secret-key'));return true;};
  await assert.rejects(fetch('https://api.tmdb.org/failed'),check);
  await assert.rejects(new Promise((resolve,reject)=>{https.get('https://api.tmdb.org/failed',()=>reject(new Error('must not expose helper response'))).on('error',reject);}),check);
}));
test('redirect to ordinary local address strips credentials without leaking helper headers',async()=>fixture(async({seen})=>{
  const response=await fetch('https://api.gamer.com.tw/nativeRedirect',{headers:{Authorization:'secret',Cookie:'cookie-secret'}});await response.text();
  assert.equal(seen.length,2);assert.equal(seen[1].target,undefined);assert.equal(seen[1].headers.authorization,undefined);assert.equal(seen[1].headers.cookie,undefined);assert.equal(seen[1].headers['x-danmu-outbound-token'],undefined);
}));
test('node-fetch redirects through patched https request strip cross-origin sensitive headers',async()=>fixture(async({seen})=>{
  const {default:nodeFetch}=await import(pathToFileURL(path.resolve(__dirname,'../artifacts/bundled-runtime-node22.23.2-x64/nodejs-project/node_modules/node-fetch/src/index.js')));
  const response=await nodeFetch('https://api.gamer.com.tw/redirect',{headers:{Authorization:'secret',Cookie:'cookie-secret'}});await response.text();
  assert.equal(seen.length,2);assert.equal(seen[1].headers.authorization,undefined);assert.equal(seen[1].headers.cookie,undefined);
}));
