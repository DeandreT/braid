import assert from 'node:assert/strict';
import {randomBytes,generateKeyPairSync} from 'node:crypto';
import {launchFixture,origin} from './fixture.mjs';
const b=await launchFixture();
try{
 const owner=await b.newBrowserCDPSession();const list=async()=>(await owner.send('Target.getBrowserContexts')).browserContextIds;const before=new Set(await list());const ctx=await b.newContext({serviceWorkers:'block'});const ids=(await list()).filter(x=>!before.has(x));assert.equal(ids.length,1);
 const epoch=randomBytes(16).toString('hex'),publicKey=Buffer.from(generateKeyPairSync('ed25519').publicKey.export({format:'jwk'}).x,'base64url').toString('hex');const {runId}=await owner.send('Browser.beginBraidInventory',{epoch,publicKey,browserContextId:ids[0]});await owner.send('Browser.enableBraidNavigations',{runId});const nav=()=>owner.send('Browser.readBraidNavigations',{runId});
 let release,arrive;const hold=new Promise(r=>release=r),at=new Promise(r=>arrive=r);await ctx.route('**/*',async r=>{if(r.request().url()===origin+'/fixture'){arrive();await hold;}return r.fulfill({contentType:'text/html',headers:{'Content-Security-Policy':"worker-src 'none'"},body:'<!doctype html><link rel=icon href=data:,><title>bounded</title>'});});
 const p=await ctx.newPage();await p.goto(origin+'/seed');const c=await ctx.newCDPSession(p);const {frameTree}=await c.send('Page.getFrameTree');await c.send('Audits.reserveBraidDocument',{epoch,frameId:frameTree.frame.id});const navigation=p.goto(origin+'/fixture');await at;const pending=await nav();assert.ok(pending.pending>0);release();await navigation;const committed=await nav();assert.equal(committed.pending,0);assert.equal(committed.lost,false);
 const read=()=>c.send('Audits.readBraidWork',{epoch});const evaluate=async expression=>{const r=await c.send('Runtime.evaluate',{expression,returnByValue:true});assert.ok(!r.exceptionDetails);return r.result.value;};
 const a=await read();assert.ok(committed.records.split('\n').some(r=>r.startsWith('C:')&&r.endsWith(':'+a.generation)));assert.equal(a.lost,false);
 await evaluate(`const ch=new MessageChannel();window.receive=0;ch.port1.addEventListener('message',()=>{window.receive++;Promise.resolve().then(()=>{Promise.resolve().then(()=>{window.follow=1});});});window.startPort=()=>ch.port1.start();let release;window.gate=new Promise(r=>release=r);window.openGate=release;window.gate.then(()=>{ch.port2.postMessage(1);return 17});void 0`);
 const held=await read();assert.equal(held.generation,a.generation);assert.ok(held.jobs>held.returned && held.pendingValues>0);assert.equal(held.posted,a.posted);
 await evaluate('openGate();void 0');const sent=await read();assert.ok(sent.returned>held.returned);assert.equal(sent.posted-sent.received,1);assert.equal(await evaluate('receive'),0);
 // Use the actual receiving handler / nested reaction as the synchronization point.
 const done=p.waitForEvent('console',m=>m.type()==='info' && m.text()==='credit-chain-returned');await evaluate(`ch.port1.addEventListener('message',()=>{queueMicrotask(()=>queueMicrotask(()=>console.info('credit-chain-returned')))},{once:true});startPort();void 0`);await done;
 const end=await read();assert.equal(await evaluate('follow'),1);assert.equal(end.generation,a.generation);assert.equal(end.posted,end.received);assert.ok(end.received>sent.received);assert.ok(end.nested>held.nested);assert.ok(end.returned>sent.returned);console.log('Mapped lifecycle:',JSON.stringify({held,sent,end}));assert.equal(end.lost,false);assert.equal(end.jobs,end.returned);assert.equal(end.pendingValues,0);
  await evaluate(`window.heldTimer=setTimeout(()=>console.error('unexpected held timer'),60000);void 0`);
  const timer=await read();assert.equal(timer.pendingCallbacks,end.pendingCallbacks+1);
  await evaluate(`clearTimeout(heldTimer);void 0`);
  const cleared=await read();assert.equal(cleared.pendingCallbacks,end.pendingCallbacks);assert.equal(cleared.removed,timer.removed+1);
  const ran=p.waitForEvent('console',m=>m.text()==='frame-after-timer');
  await evaluate(`setTimeout(()=>requestAnimationFrame(()=>console.info('frame-after-timer')),0);void 0`);
  await ran;const frame=await read();assert.equal(frame.generation,end.generation);assert.equal(frame.executingCallbacks,0);assert.equal(frame.pendingCallbacks,end.pendingCallbacks);assert.equal(frame.fired,cleared.fired+2);assert.equal(frame.lost,false);
  await p.goto(origin+'/seed');await assert.rejects(read(),/original work unavailable/);await ctx.close();await owner.detach();
}finally{await b.close();}
