// Exercise a mapped document at its first parser script, not after goto().
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { launchFixture, origin } from "./fixture.mjs";
import { PrefixJournal } from "./receipt-probe.mjs";
import { acknowledge, checkAck } from "./receipt-channel.mjs";
const b = await launchFixture(),
  ctx = await b.newContext({ serviceWorkers: "block" });
const dir = await mkdtemp(join(tmpdir(), "braid-nav-")),
  sessions = [],
  ids = new Set(),
  reservations = new Set();
try {
  await ctx.route("**/*", (route) => {
    const u = route.request().url();
    if (![origin + "/seed", origin + "/fixture"].includes(u))
      return route.abort();
    return route.fulfill({
      contentType: "text/html",
      headers: { "Content-Security-Policy": "worker-src 'none'" },
      body: u.endsWith("/seed")
        ? "<!doctype html><title>seed</title>"
        : `<!doctype html><script>window.arrival=new Promise(resolve=>document.addEventListener('securitypolicyviolation',e=>resolve(e.isTrusted),{once:true}));new Worker('/owned-worker.js').onerror=e=>e.preventDefault();</script>`,
    });
  });
  async function reserve(page, target) {
    const c = await ctx.newCDPSession(page);
    sessions.push(c);
    const { frameTree } = await c.send("Page.getFrameTree");
    const frameId =
      target === "child"
        ? frameTree.childFrames[0].frame.id
        : frameTree.frame.id;
    const journal = new PrefixJournal(join(dir, String(sessions.length)));
    const { reservation } = await c.send("Audits.reserveBraidDocument", {
      epoch: journal.epoch,
      frameId,
    });
    assert.match(reservation, /^[A-F0-9]{32}$/);
    assert.ok(!reservations.has(reservation));
    reservations.add(reservation);
    return { journal, c, epoch: journal.epoch, reservation };
  }
  async function settle(f) {
    const r = await f.c.send("Audits.readBraidDispatch", { epoch: f.epoch });
    assert.ok(!ids.has(r.generation));
    ids.add(r.generation);
    const record = { kind: "dispatch-end", epoch: f.epoch, ...r };
    const key = generateKeyPairSync("ed25519");
    const ack = acknowledge(f.journal, r.generation, record, key.privateKey);
    checkAck(key.publicKey, f.epoch, r.generation, record, ack);
    assert.deepEqual(
      await f.c.send("Audits.readBraidDispatch", { epoch: f.epoch }),
      r,
    );
    f.journal.close();
  }
  async function prepare() {
    const p = await ctx.newPage();
    p.setDefaultTimeout(3500);
    await p.goto(origin + "/seed");
    return p;
  }
  const main = await prepare(),
    overview = await prepare();
  const a = await reserve(main),
    d = await reserve(overview);
  await main.goto(origin + "/fixture");
  await overview.goto(origin + "/fixture");
  assert.ok(await main.evaluate(() => window.arrival));
  assert.ok(await overview.evaluate(() => window.arrival));
  await settle(a);
  await settle(d);
  await overview.close();
  const n = await reserve(main);
  await main.reload();
  assert.ok(await main.evaluate(() => window.arrival));
  await settle(n);
  await assert.rejects(
    a.c.send("Audits.readBraidDispatch", { epoch: a.epoch }),
    /original fixture lost/,
  );
  await main.evaluate(
    () =>
      new Promise((resolve) => {
        const f = document.createElement("iframe");
        f.onload = () => resolve();
        f.src = "/seed";
        document.body.append(f);
      }),
  );
  const child = main.frames().find((f) => f.parentFrame() === main.mainFrame());
  const cf = await reserve(main, "child");
  await child.goto(origin + "/fixture");
  assert.ok(await child.evaluate(() => window.arrival));
  await settle(cf);
  await main.evaluate(() => document.querySelector("iframe").remove());
  await assert.rejects(
    cf.c.send("Audits.readBraidDispatch", { epoch: cf.epoch }),
    /original fixture lost/,
  );
  await main.close();
  assert.equal(ids.size, 4);
  console.log(
    "Two pages, reload and child frame: four separate native birth-to-dispatch controls; no catalog cutoff.",
  );
} finally {
  await ctx.close();
  await b.close();
  await rm(dir, { recursive: true });
}
