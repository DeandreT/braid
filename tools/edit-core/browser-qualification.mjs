import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import http from "node:http";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { chromium } from "@playwright/test";
import { operationCases } from "./operation-cases.mjs";

const [moduleDirectory, nativePath, report, ...fixtures] =
  process.argv.slice(2);
assert.ok(moduleDirectory && nativePath && report && fixtures.length);
const wasm = readFileSync(
  resolve(moduleDirectory, "braid_ferrule_edit_wasm_bg.wasm"),
);
const glue = readFileSync(
  resolve(moduleDirectory, "braid_ferrule_edit_wasm.js"),
);
const sri = `sha256-${createHash("sha256").update(wasm).digest("base64")}`;
const attempted = [];
const worker = `import {initSync,qualify_checkpoint,EditorSession} from './core.js';
self.onmessage = async ({data}) => {
 try {
  initSync({module:data.module});
  const checkpoint=JSON.parse(qualify_checkpoint(new Uint8Array(data.native),data.operations));
  const session=new EditorSession(new Uint8Array(data.native),data.operations);
  const view=JSON.parse(session.view());session.free();
  const blocked={};
  try {await fetch('/outside-fetch');blocked.fetch=false;}catch{blocked.fetch=true;}
  try {const x=new XMLHttpRequest();x.open('GET','/outside-xhr');x.send();blocked.xhr=await new Promise(resolve=>{x.onerror=()=>resolve(true);x.onload=()=>resolve(false)});}catch{blocked.xhr=true;}
  try {const w=new Worker('/edit/nested.js');blocked.worker=await new Promise(resolve=>{w.onerror=e=>{e.preventDefault();resolve(true)};w.onmessage=()=>resolve(false)});w.terminate();}catch{blocked.worker=true;}
  try {await import('/outside-module.js');blocked.import=false;}catch{blocked.import=true;}
  try {const w=new WebSocket('ws://'+location.host+'/outside-websocket');blocked.websocket=await new Promise(resolve=>{w.onerror=()=>resolve(true);w.onopen=()=>{w.close();resolve(false)}});}catch{blocked.websocket=true;}
  self.postMessage({checkpoint,view,blocked});
 }catch(e){self.postMessage({error:String(e)})}
};`;
const server = http.createServer((request, response) => {
  const path = request.url;
  if (path?.startsWith("/outside") || path === "/edit/nested.js")
    attempted.push(path);
  const origin = `http://127.0.0.1:${server.address().port}`;
  response.setHeader(
    "Content-Security-Policy",
    path?.startsWith("/edit/")
      ? `default-src 'none'; script-src ${origin}/edit/ 'wasm-unsafe-eval'; worker-src 'none'`
      : "default-src 'none'; script-src 'self'; connect-src 'self'; worker-src 'self'; frame-src 'none'; object-src 'none'",
  );
  response.setHeader("X-Content-Type-Options", "nosniff");
  if (path === "/edit/core.js") {
    response.setHeader("Content-Type", "text/javascript");
    response.end(glue);
  } else if (path === "/edit/core.wasm") {
    response.setHeader("Content-Type", "application/wasm");
    response.end(wasm);
  } else if (path === "/edit/nested.js") {
    response.setHeader("Content-Type", "text/javascript");
    response.end("self.postMessage('nested worker ran');");
  } else if (path === "/edit/probe.js") {
    response.setHeader("Content-Type", "text/javascript");
    response.end(worker);
  } else if (path === "/") {
    response.setHeader("Content-Type", "text/html");
    response.end(
      "<!doctype html><title>Isolated edit core qualification</title><p>Dedicated worker qualification</p>",
    );
  } else {
    response.statusCode = 404;
    response.end();
  }
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
let browser;
const checks = [];
try {
  browser = await chromium.launch({
    headless: false,
    executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE,
    args: ["--ozone-platform=x11", "--disable-dev-shm-usage"],
  });
  const page = await browser.newPage();
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  assert.equal(
    await page.evaluate(async () => {
      try {
        await WebAssembly.compile(
          new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]),
        );
        return false;
      } catch {
        return true;
      }
    }),
    true,
  );
  assert.equal(
    await page.evaluate(async () => {
      try {
        await fetch("/edit/core.wasm", {
          integrity: "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        });
        return false;
      } catch {
        return true;
      }
    }),
    true,
  );
  for (const directory of fixtures)
    for (const file of readdirSync(directory).filter((file) =>
      file.endsWith(".json"),
    )) {
      const native = readFileSync(resolve(directory, file));
      const empty = { format: "ferrule.edit.v1", operations: [] };
      const scripts =
        file === "edit-v1.json" ? [empty, ...operationCases] : [empty];
      for (const script of scripts) {
        const expected = checkpoint(native, script);
        const actual = await page.evaluate(
          async ({ native, operations, sri }) => {
            const module = await (
              await fetch("/edit/core.wasm", { integrity: sri })
            ).arrayBuffer();
            const bytes = Uint8Array.from(native).buffer;
            const worker = new Worker("/edit/probe.js", { type: "module" });
            try {
              return await new Promise((resolve, reject) => {
                const timer = setTimeout(
                  () => reject(new Error("Worker timeout")),
                  10000,
                );
                worker.onmessage = (e) => {
                  clearTimeout(timer);
                  resolve(e.data);
                };
                worker.onerror = () => reject(new Error("Worker failed"));
                worker.postMessage({ module, native: bytes, operations }, [
                  module,
                  bytes,
                ]);
              });
            } finally {
              worker.terminate();
            }
          },
          { native: [...native], operations: JSON.stringify(script), sri },
        );
        assert.equal(actual.error, undefined, file);
        assert.deepEqual(actual.checkpoint, expected, file);
        assert.deepEqual(actual.view.projection, expected.projection, file);
        assert.deepEqual(actual.view.validation, expected.validation, file);
        assert.deepEqual(
          actual.blocked,
          {
            fetch: true,
            xhr: true,
            worker: true,
            import: true,
            websocket: true,
          },
          file,
        );
        checks.push(`${file}:${script.operations.at(-1)?.op ?? "empty"}`);
      }
    }
  assert.deepEqual(
    attempted,
    [],
    "CSP must block requests, not merely their responses",
  );
  writeFileSync(
    report,
    JSON.stringify(
      {
        checks,
        nativeWasmEqual: true,
        pageWasmBlocked: true,
        integrityMismatchBlocked: true,
        workerNetworkAndNestingBlocked: true,
      },
      null,
      2,
    ),
  );
  console.log(
    `Chromium dedicated-worker/native checkpoints agree for ${checks.length} fixtures; strict CSP and integrity checks pass.`,
  );
} finally {
  await browser?.close();
  await new Promise((resolve) => server.close(resolve));
}

function checkpoint(native, operations) {
  const log = Buffer.from(JSON.stringify(operations));
  const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");
  const frame = (bytes) => {
    const size = Buffer.alloc(4);
    size.writeUInt32BE(bytes.length);
    return Buffer.concat([size, bytes]);
  };
  const request = {
    protocolVersion: "braid.ferrule-edit.v1",
    requestId: "11111111-1111-4111-8111-111111111111",
    operation: "edit",
    revision: {
      organizationId: "22222222-2222-4222-8222-222222222222",
      projectId: "33333333-3333-4333-8333-333333333333",
      mappingId: "44444444-4444-4444-8444-444444444444",
      revisionId: "55555555-5555-4555-8555-555555555555",
    },
    nativeDigest: digest(native),
    documentId: "entry.native.json",
    operationsDigest: digest(log),
  };
  const result = spawnSync(nativePath, [], {
    input: Buffer.concat([
      frame(Buffer.from(JSON.stringify(request))),
      frame(native),
      frame(log),
    ]),
    timeout: 10000,
    maxBuffer: 32 * 1024 * 1024,
    env: {},
  });
  assert.equal(result.status, 0);
  assert.equal(result.stderr.length, 0);
  const length = result.stdout.readUInt32BE();
  const response = JSON.parse(result.stdout.subarray(4, 4 + length));
  assert.equal(response.failure, null, JSON.stringify(response.failure));
  const value = response.result;
  return {
    nativeDigest: value.nativeDigest,
    nativeByteCount: value.nativeByteCount,
    validation: value.validation,
    projection: value.projection,
  };
}
