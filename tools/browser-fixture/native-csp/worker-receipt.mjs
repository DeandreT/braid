// Private IPC worker receipt fixture, not a browser-wide ledger. Only two
// transitions are admitted: reserve the original worker, then its completion.
import assert from "node:assert/strict";
import { fork } from "node:child_process";
import { createHash, generateKeyPairSync, sign, verify } from "node:crypto";
import { once } from "node:events";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";
import { launchFixture, origin } from "./fixture.mjs";
import { PrefixJournal } from "./receipt-probe.mjs";
const zero = "0".repeat(64),
  native = /^[0-9A-F]{32}$/;
function bytes(r) {
  return Buffer.from(
    JSON.stringify([
      1,
      r.epoch,
      r.seq,
      r.kind,
      r.document,
      r.reservation,
      r.worker,
      r.dispatch,
      r.prev,
    ]),
  );
}
async function driver() {
  const [{ epoch, key, mode }] = await once(process, "message");
  const b = await launchFixture();
  try {
    const ctx = await b.newContext({ serviceWorkers: "block" });
    const p = await ctx.newPage();
    p.setDefaultTimeout(3000);
    await ctx.route("**/*", (route) => {
      const u = route.request().url();
      if (u === origin + "/worker-fixture")
        return route.fulfill({
          contentType: "text/html",
          headers: { "Content-Security-Policy": "worker-src 'self'" },
          body: "<!doctype html><title>private worker control</title>",
        });
      if (u === origin + "/classic.js")
        return route.fulfill({
          contentType: "application/javascript",
          headers: { "Content-Security-Policy": "connect-src 'none'" },
          body: `onmessage=e=>{if(e.data==='go'){addEventListener('securitypolicyviolation',v=>postMessage(v.isTrusted?'observed':'wrong'),{once:true});fetch('/blocked').catch(()=>{});}else if(e.data==='close'){close();}};postMessage('ready');`,
        });
      return route.abort();
    });
    await p.goto(origin + "/worker-fixture");
    const s = await ctx.newCDPSession(p);
    const { generation: document } = await s.send("Audits.armBraidWorker", {
      epoch,
    });
    await p.evaluate(
      (type) =>
        new Promise((res, rej) => {
          const w = (window.workerFixture = new Worker("/classic.js", {
            type,
          }));
          w.onmessage = (e) =>
            e.data === "ready" ? res() : rej(Error("unexpected"));
          w.onerror = () => rej(Error("worker failed"));
        }),
      mode === "unsupported" ? "module" : "classic",
    );
    const read = (terminal) =>
      s.send("Audits.readBraidWorker", { epoch, terminal });
    if (mode === "unsupported") {
      await assert.rejects(read(false), /original worker unavailable/);
    } else {
      const { token: worker, dispatch: d, retired } = await read(false);
      assert.equal(d, "");
      assert.equal(retired, false);
      let prev = zero;
      async function deliver(seq, kind, dispatch) {
        const record = {
          epoch,
          seq,
          kind,
          document,
          reservation: 1,
          worker,
          dispatch,
          prev,
        };
        const wait = once(process, "message");
        process.send(record);
        const [ack] = await wait;
        assert.match(ack.digest, /^[a-f0-9]{64}$/);
        assert.ok(
          verify(
            null,
            Buffer.concat([bytes(record), Buffer.from(ack.digest, "hex")]),
            key,
            Buffer.from(ack.signature, "hex"),
          ),
        );
        prev = ack.digest;
      }
      await deliver(1, "reserved", "");
      await p.evaluate(
        () =>
          new Promise((res, rej) => {
            window.workerFixture.onmessage = (e) =>
              e.data === "observed" ? res() : rej(Error("unexpected"));
            window.workerFixture.postMessage("go");
          }),
      );
      const pending = await read(true);
      assert.equal(pending.retired, false);
      assert.equal(pending.dispatch, "");
      await p.evaluate(() => window.workerFixture.postMessage("close"));
      const deadline = performance.now() + 3000;
      let r;
      do {
        r = await read(true);
        if (r.retired) break;
        await delay(10);
      } while (performance.now() < deadline);
      assert.equal(r.retired, true);
      assert.equal(r.token, worker);
      assert.match(r.dispatch, native);
      await deliver(2, "retired-dispatch", r.dispatch);
      assert.deepEqual(await read(true), r);
    }
    await p.close();
    await ctx.close();
  } finally {
    await b.close();
  }
  process.send({ finished: mode });
  process.disconnect();
}
async function owner(mode) {
  const dir = await mkdtemp(join(tmpdir(), "braid-w-"));
  const journal = new PrefixJournal(join(dir, "journal"));
  const { privateKey, publicKey } = generateKeyPairSync("ed25519");
  const child = fork(fileURLToPath(import.meta.url), ["driver"], {
    stdio: ["ignore", "ignore", "inherit", "ipc"],
  });
  const exit = once(child, "exit");
  let failure,
    seq = 0,
    doc,
    token,
    prev = zero,
    done = false;
  const timer = setTimeout(() => child.kill(), 20000);
  child.on("message", (r) => {
    try {
      assert.ok(!done);
      if (r.finished) {
        assert.deepEqual(r, { finished: mode });
        assert.equal(seq, mode === "unsupported" ? 0 : 2);
        done = true;
        return;
      }
      assert.equal(mode, "classic");
      assert.deepEqual(Object.keys(r).sort(), [
        "dispatch",
        "document",
        "epoch",
        "kind",
        "prev",
        "reservation",
        "seq",
        "worker",
      ]);
      assert.equal(r.seq, ++seq);
      assert.ok(seq <= 2);
      assert.equal(r.epoch, journal.epoch);
      assert.equal(r.reservation, 1);
      assert.equal(r.prev, prev);
      assert.match(r.document, native);
      assert.match(r.worker, native);
      if (seq === 1) {
        assert.equal(r.kind, "reserved");
        assert.equal(r.dispatch, "");
        doc = r.document;
        token = r.worker;
      } else {
        assert.equal(r.kind, "retired-dispatch");
        assert.equal(r.document, doc);
        assert.equal(r.worker, token);
        assert.match(r.dispatch, native);
      }
      const data = bytes(r);
      const ack = journal.accept(
        journal.epoch,
        seq,
        1,
        prev,
        createHash("sha256").update(data).digest("hex"),
      );
      prev = ack.digest;
      child.send({
        digest: prev,
        signature: sign(
          null,
          Buffer.concat([data, Buffer.from(prev, "hex")]),
          privateKey,
        ).toString("hex"),
      });
    } catch (e) {
      failure = e;
      child.kill();
    }
  });
  try {
    child.send({
      epoch: journal.epoch,
      mode,
      key: publicKey.export({ type: "spki", format: "pem" }),
    });
    const [code] = await exit;
    if (failure) throw failure;
    assert.equal(code, 0);
    assert.ok(done);
    console.log(`${mode}: worker control passed; full cutoff unavailable`);
  } finally {
    clearTimeout(timer);
    journal.close();
    await rm(dir, { recursive: true });
  }
}
if (process.argv[2] === "driver") await driver();
else {
  await owner("classic");
  await owner("unsupported");
}
