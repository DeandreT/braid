// Controlled catalog UI workflow. The published product serves all HTML, JS,
// CSS and response policies. Only the existing controlled-test API replies
// are synthetic. This is not the live DB/worker suite or a clean verdict.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {createHash,generateKeyPairSync,randomBytes,sign} from 'node:crypto';
import {execFileSync,spawn} from 'node:child_process';
import {once} from 'node:events';
import {createReadStream,readFileSync,unlinkSync} from 'node:fs';
import {PrefixJournal} from './receipt-probe.mjs';
const env=process.env, require=createRequire(import.meta.url), origin='http://127.0.0.1:45921';
async function check(path,pin){assert.match(pin,/^[a-f0-9]{64}$/);const h=createHash('sha256');for await(const part of createReadStream(path))h.update(part);assert.equal(h.digest('hex'),pin);}
const links=JSON.parse(execFileSync('/usr/bin/ip',['-j','link'],{encoding:'utf8'}));assert.equal(links.length,1);assert.equal(links[0].ifname,'lo');assert.ok(links[0].flags.includes('UP'));assert.deepEqual(JSON.parse(execFileSync('/usr/bin/ip',['-j','route','show','default'])),[]);assert.notEqual(process.getuid(),0);assert.match(readFileSync('/proc/self/status','utf8'),/^CapEff:\s+0+$/m);
await check(env.BRAID_CHROME,env.BRAID_CHROME_SHA256);await check(env.BRAID_DRIVER+'/lib/coreBundle.js',env.BRAID_BUNDLE_SHA256);
const {chromium}=require(env.BRAID_DRIVER);const {syntheticCatalog,synthetic,projectsPath,catalogApi}=require(env.BRAID_UI_FIXTURE);
const server=spawn(env.DOTNET_ROOT+'/dotnet',[env.BRAID_SERVER],{stdio:['ignore','pipe','ignore'],env:{HOME:env.HOME,TMPDIR:env.TMPDIR,PATH:'/usr/bin',DOTNET_ROOT:env.DOTNET_ROOT,DOTNET_ADD_GLOBAL_TOOLS_TO_PATH:'false',DOTNET_CLI_TELEMETRY_OPTOUT:'1',BRAID_DEV_LISTEN_ADDR:'127.0.0.1:45921'}});const exited=once(server,'exit');let browser,echo,refused;const journalPath=env.TMPDIR+'/catalog-retirement-'+randomBytes(16).toString('hex');const journal=new PrefixJournal(journalPath);
try {
 await new Promise((resolve,reject)=>{const t=setTimeout(()=>reject(Error('server readiness deadline')),10000);let buf='';server.stdout.on('data',x=>{buf=(buf+x).slice(-4000);if(buf.includes('Now listening on: '+origin)){clearTimeout(t);resolve();}});server.once('exit',()=>{clearTimeout(t);reject(Error('server exited'));});});
 browser=await chromium.launch({executablePath:env.BRAID_CHROME,chromiumSandbox:true,headless:true,timeout:10000,env:{HOME:env.HOME,TMPDIR:env.TMPDIR,PATH:'/usr/bin'}});assert.equal(browser.version(),'157.0.8090.0');
 const owner=await browser.newBrowserCDPSession();const list=async()=>(await owner.send('Target.getBrowserContexts')).browserContextIds;const initial=new Set(await list());const ctx=await browser.newContext({serviceWorkers:'block'});const added=(await list()).filter(x=>!initial.has(x));assert.equal(added.length,1);
 const keys=generateKeyPairSync('ed25519');const epoch=journal.epoch,publicKey=Buffer.from(keys.publicKey.export({format:'jwk'}).x,'base64url').toString('hex');const {runId}=await owner.send('Browser.beginBraidInventory',{epoch,publicKey,browserContextId:added[0]});await owner.send('Browser.enableBraidNavigations',{runId});const host=()=>owner.send('Browser.readBraidNavigations',{runId});
 const page=await ctx.newPage();page.setDefaultTimeout(5000);await syntheticCatalog(page);const c=await ctx.newCDPSession(page);const types=new Map();page.on('request',r=>{assert.equal(new URL(r.url()).origin,origin);types.set(r.resourceType(),1+(types.get(r.resourceType())??0));});
 await page.goto(origin+'/this-route-does-not-exist');
 async function start(path){const {frameTree}=await c.send('Page.getFrameTree');await c.send('Audits.reserveBraidDocument',{epoch,frameId:frameTree.frame.id});const r=await page.goto(origin+path);assert.equal(r.status(),200);assert.match(await r.headerValue('content-security-policy'),/script-src 'self'/);}
 const work=()=>c.send('Audits.readBraidWork',{epoch});
 await start('/catalog/organizations/not-a-uuid/projects');await page.getByRole('heading',{name:'Invalid link',exact:true}).waitFor();assert.equal(await page.getByText('No projects',{exact:true}).count(),0);const first=await work();assert.ok(first.jobs>0 && first.returned>0 && first.sends>0);
 const successor=await ctx.newCDPSession(page);const {frameTree}=await successor.send('Page.getFrameTree');const epoch2=randomBytes(16).toString('hex');await successor.send('Audits.reserveBraidDocument',{epoch:epoch2,frameId:frameTree.frame.id});
 let arrive,release;const arrived=new Promise(r=>arrive=r),gate=new Promise(r=>release=r);const path=projectsPath(synthetic.org);await page.goto(origin+path);await page.getByRole('link',{name:synthetic.project.name,exact:true}).waitFor();const peer=await ctx.newPage();await syntheticCatalog(peer);await peer.goto(origin+'/this-route-does-not-exist');await page.bringToFront();const isListResponse=r=>new URL(r.url()).pathname===catalogApi(path)&&r.request().method()==='GET';const priorVisibility=page.waitForResponse(isListResponse);await page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')));assert.equal((await priorVisibility).status(),200);await page.getByRole('link',{name:synthetic.project.name,exact:true}).waitFor();await page.route('**'+catalogApi(path)+'?*',async r=>{arrive();await gate;await r.fallback();},{times:1});
 try {const priorFocus=page.waitForResponse(isListResponse);await page.evaluate(()=>window.dispatchEvent(new Event('focus')));await arrived;const pending=await successor.send('Audits.readBraidWork',{epoch:epoch2});const serverPending=await host();assert.notEqual(first.generation,pending.generation);assert.ok(pending.sends>pending.finishes+pending.cancelled);assert.ok(serverPending.resourcePending>0);
 const prepare=()=>successor.send('Audits.prepareBraidIngress',{epoch:epoch2,generation:pending.generation});
 await assert.rejects(c.send('Audits.prepareBraidIngress',{epoch,generation:first.generation}),/original ingress unavailable/);
 const refusal=await prepare();assert.deepEqual(refusal,{focusOutstanding:true,visibilityOutstanding:true,disposition:'live-external-ingress'});
 const inv=await owner.send('Browser.readBraidInventory',{runId});const receipt=journal.accept(epoch,1,inv.revision,'0'.repeat(64),createHash('sha256').update(JSON.stringify([runId,inv,pending.generation,refusal,serverPending])).digest('hex'));
 const prefix='1:'+receipt.digest;const signature=sign(null,Buffer.from(`braid-fence-v1:${runId}:${epoch}:${inv.revision}:${inv.digest}:${prefix}`),keys.privateKey).toString('hex');const checkpoint=prefix+':'+signature;const ticket=await owner.send('Browser.prepareBraidFence',{runId,...inv,checkpoint});refused=ticket.disposition;assert.ok(['resource-lost','unowned-resource','pending-browser-resource','original-resource-receiver-unacknowledged'].includes(refused),refused);echo={runId,revision:inv.revision,digest:inv.digest,checkpoint,snapshotToken:ticket.snapshotToken};
 const beforeRelease=await successor.send('Audits.readBraidWork',{epoch:epoch2});assert.ok(beforeRelease.lost);assert.ok(beforeRelease.sends>beforeRelease.finishes+beforeRelease.cancelled);
 release();assert.equal((await priorFocus).status(),200);await page.getByRole('list',{name:'Projects',exact:true}).getByRole('link',{name:synthetic.project.name,exact:true}).waitFor();const end=await successor.send('Audits.readBraidWork',{epoch:epoch2});const terminal=await host();assert.equal(end.generation,pending.generation);assert.ok(end.finishes>pending.finishes);assert.ok(end.nested>0);const born=terminal.records.split('\n').filter(x=>x.startsWith('C:')).map(x=>x.split(':')[2]);assert.ok(born.includes(first.generation)&&born.includes(end.generation));
 const seen=new Map();for(const line of terminal.resourceRecords.split('\n')){const [stage,id,gen]=line.split(':');if(stage==='S'){assert.ok(!seen.has(id));seen.set(id,gen);}else{assert.ok(stage==='F'||stage==='R');assert.equal(seen.get(id),gen);}}
 const forwarded=terminal.resourceRecords.split('\n').filter(r=>r.startsWith('F:')&&r.endsWith(':'+end.generation)).length;assert.ok(forwarded>0);await assert.rejects(work(),/original work unavailable/);
 console.log(JSON.stringify({case:'packaged malformed link and controlled project list',first,pending,end,browserPending:serverPending.resourcePending,browserForwarded:forwarded,browserLoss:terminal.resourceLost,requestClasses:Object.fromEntries(types)}));
 // Challenge the existing catalog listener, not CSP provenance: this is an
 // ordinary synthetic focus event, not evidence of trusted browser delivery.
 const refresh=page.waitForResponse(r=>new URL(r.url()).pathname===catalogApi(path)&&r.request().method()==='GET');
 await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
 const refreshed=await refresh;assert.equal(refreshed.status(),200);
 const again=await successor.send('Audits.readBraidWork',{epoch:epoch2});
 assert.equal(again.generation,end.generation);assert.ok(again.sends>end.sends);
 assert.equal(again.lost,true);assert.deepEqual(await prepare(),refusal);
 const pc=await ctx.newCDPSession(peer);const peerEpoch=randomBytes(16).toString('hex');const ft=await pc.send('Page.getFrameTree');await pc.send('Audits.reserveBraidDocument',{epoch:peerEpoch,frameId:ft.frameTree.frame.id});await peer.goto(origin+path);await peer.getByRole('link',{name:synthetic.project.name,exact:true}).waitFor();const peerWork=()=>pc.send('Audits.readBraidWork',{epoch:peerEpoch});const pw=await peerWork();assert.notEqual(pw.generation,again.generation);assert.notEqual(pw.refusalCode,10);
 // Listener challenges do not establish that a native browser input was fenced.
 for(const target of [page,peer]){await target.bringToFront();const response=target.waitForResponse(r=>new URL(r.url()).pathname===catalogApi(path));await target.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')));assert.equal((await response).status(),200);}
 await page.goto(origin+'/this-route-does-not-exist');await assert.rejects(prepare(),/original ingress unavailable/);assert.equal((await peerWork()).generation,pw.generation);await peer.close();
 const obligations=new Set(terminal.resourceRecords.split('\n').filter(r=>r.startsWith('R:')));for(const line of terminal.resourceRecords.split('\n').filter(r=>r.startsWith('F:')))assert.ok(obligations.has('R:'+line.slice(2)));
 console.log('Live focus and visibility registrations refused during held GET; original response and listener challenges completed; peer stayed live across replacement.');
 }finally{release();}
 await page.close();await ctx.close();const verified=await owner.send('Browser.verifyBraidDisposal',echo);assert.equal(verified.disposed,true);assert.equal(verified.disposition,refused);console.log('Disposal preserved browser refusal:',refused);await owner.detach();
}finally{try{if(browser)await browser.close();}finally{server.kill('SIGTERM');await exited;journal.close();unlinkSync(journalPath);}}
