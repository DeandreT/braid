// Short synthetic process-tree fixtures, never an Angular build or a capture.
import { mkdir, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const require = createRequire(import.meta.url);
export async function lifecycleFixture(
  directory,
  { mode = "hold", globalTimeout = 4000, port = 0 } = {},
) {
  await mkdir(directory, { recursive: true });
  const receipt = join(directory, "processes.jsonl");
  const worker = join(directory, "worker.mjs");
  const builder = join(directory, "builder.mjs");
  const grandchild = join(directory, "grandchild.mjs");
  const session = new URL("./session.mjs", import.meta.url).href;
  const server = new URL("./server.mjs", import.meta.url).href;
  const record = `import {appendFileSync} from 'node:fs';
    const root=process.argv[3] ?? process.argv[2];
    const record=(role)=>appendFileSync(${JSON.stringify(receipt)},JSON.stringify({role,pid:process.pid,parent:process.ppid,root,expiresAt:Date.now()+15000})+'\\n');
    // Busy build descendants deliberately resist console Ctrl+C; the runner's
    // registered tree cleanup, not signal cooperation, must settle them.
    process.on('SIGINT',()=>{});
    setTimeout(()=>process.exit(99),15000);
  `;
  await writeFile(
    grandchild,
    record + "record('grandchild'); setInterval(()=>{},1000);",
  );
  await writeFile(
    builder,
    record +
      `
    import {spawn} from 'node:child_process';
    record('builder'); spawn(process.execPath,[${JSON.stringify(grandchild)},root],{stdio:'inherit'});
    setInterval(()=>{},1000);
  `,
  );
  await writeFile(
    worker,
    record +
      `
    import {spawn} from 'node:child_process';
    import {writeFile} from 'node:fs/promises';
    import {join} from 'node:path';
    import {startDocsServer} from ${JSON.stringify(server)};
    record('worker');
    const token=process.argv[4];
    const report=(event)=>console.log('WEFT_DOCS_'+token+' '+JSON.stringify(event));
    const stopping=new Promise(resolve=>process.stdin.once('end',resolve)); process.stdin.resume();
    let host;
    try {
      if (${JSON.stringify(mode)}==='hold') {
        spawn(process.execPath,[${JSON.stringify(builder)},root],{stdio:'inherit'});
        await new Promise(()=>{});
      }
      if (${JSON.stringify(mode)}==='error') throw new Error('synthetic build failed');
      await writeFile(join(root,'index.html'),'<script src="/main.js" type="module"></script>');
      host=await startDocsServer(root,${port});
      report({event:'ready'}); await stopping; await host.stop();
    } catch(error) {
      report({event:'failed',message:error.message,code:error.code});
      process.exitCode=1; await stopping; await host?.stop();
    } finally {process.stdin.destroy();}
    process.exit();
  `,
  );
  await writeFile(
    join(directory, "setup.mjs"),
    `
    import {startDocsSession} from ${JSON.stringify(session)};
    export default async()=>{await startDocsSession({worker:${JSON.stringify(worker)}});};
  `,
  );
  await writeFile(
    join(directory, "teardown.mjs"),
    `
    import {appendFileSync} from 'node:fs';
    import {stopDocsSessions} from ${JSON.stringify(session)};
    export default async()=>{
      appendFileSync(${JSON.stringify(join(directory, "teardown.jsonl"))},'"started"\\n');
      await stopDocsSessions();
      appendFileSync(${JSON.stringify(join(directory, "teardown.jsonl"))},'"settled"\\n');
    };
  `,
  );
  await writeFile(
    join(directory, "never.spec.mjs"),
    `
    import {test} from ${JSON.stringify(pathToFileURL(require.resolve("@playwright/test")).href)};
    test('setup must not finish',()=>{throw new Error('Synthetic build unexpectedly completed');});
  `,
  );
  const config = join(directory, "playwright.config.mjs");
  await writeFile(
    config,
    `export default {
    testDir:${JSON.stringify(directory)}, testMatch:'never.spec.mjs',
    globalSetup:'./setup.mjs', globalTeardown:'./teardown.mjs',
    globalTimeout:${globalTimeout}, timeout:1000, workers:1, retries:0,
    reporter:'list', outputDir:${JSON.stringify(join(directory, "results"))}
  };`,
  );
  return { config, receipt, worker };
}
