// Run with the private paired fixture environment. This control has NO clean result.
import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, sign } from "node:crypto";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { mkdtemp, rm } from "node:fs/promises";
import { launchFixture, origin } from "./fixture.mjs";
import { PrefixJournal } from "./receipt-probe.mjs";
const hash = (x) => createHash("sha256").update(x).digest("hex");
const browser = await launchFixture();
try {
  for (const mode of [
    "zero",
    "continuation",
    "unresolved",
    "missing",
    "forged",
    "late",
    "wrong-ticket",
  ]) {
    const session = await browser.newBrowserCDPSession();
    const contexts = async () =>
      (await session.send("Target.getBrowserContexts")).browserContextIds;
    const old = new Set(await contexts());
    const ctx = await browser.newContext({ serviceWorkers: "block" });
    const added = (await contexts()).filter((x) => !old.has(x));
    assert.equal(added.length, 1);
    const dir = await mkdtemp(join(tmpdir(), "braid-fence-"));
    const journal = new PrefixJournal(join(dir, "journal"));
    const originals = new PrefixJournal(join(dir, "original-loads"));
    try {
      const epoch = journal.epoch;
      const { publicKey, privateKey } = generateKeyPairSync("ed25519");
      const pub = Buffer.from(
        publicKey.export({ format: "jwk" }).x,
        "base64url",
      ).toString("hex");
      const { runId } = await session.send("Browser.beginBraidInventory", {
        epoch,
        browserContextId: added[0],
        publicKey: pub,
      });
      let release;
      const pending = new Promise((r) => (release = r));
      const loads = [];
      let originalPrefix;
      async function retain(f) {
        const load = await f.read();
        if (mode === "zero") {
          assert.match(load.generation, /^[A-F0-9]{32}$/);
          assert.match(load.producer, /^[A-F0-9]{32}$/);
          assert.ok(
            !loads.some(
              (x) =>
                x.generation === load.generation ||
                x.producer === load.producer,
            ),
          );
          const snapshot = await session.send("Browser.readBraidInventory", {
            runId,
          });
          const lifetime = snapshot.inventory
            .split("\n")
            .map((x) => x.split(":"))
            .filter(([, id]) => id === load.generation);
          assert.equal(lifetime.length, 1);
          assert.equal(lifetime[0][0], "B");
          const record = hash(
            JSON.stringify([
              "bounded-load-v1",
              runId,
              epoch,
              snapshot.revision,
              snapshot.digest,
              load.generation,
              load.producer,
            ]),
          );
          originalPrefix = originals.accept(
            originals.epoch,
            loads.length + 1,
            snapshot.revision,
            originalPrefix?.digest ?? "0".repeat(64),
            record,
          );
        }
        loads.push(load);
      }

      await ctx.route("**/*", async (r) => {
        if (r.request().url() === origin + "/hold") {
          await pending;
          return r.fulfill({ contentType: "image/gif", body: "" });
        }
        return r.fulfill({
          contentType: "text/html",
          headers: { "content-security-policy": "worker-src 'none'" },
          body:
            mode === "unresolved" && r.request().url().endsWith("/fixture")
              ? '<!doctype html><img src="/hold">'
              : "<!doctype html><link rel=icon href=data:,><title>zero</title>",
        });
      });
      async function seed() {
        const p = await ctx.newPage();
        await p.goto(origin + "/seed");
        return p;
      }
      async function navigate(p, reload = false) {
        const c = await ctx.newCDPSession(p);
        const { frameTree } = await c.send("Page.getFrameTree");
        await c.send("Audits.reserveBraidDocument", {
          epoch,
          frameId: frameTree.frame.id,
        });
        const opts = {
          waitUntil: mode === "unresolved" ? "domcontentloaded" : "load",
          timeout: 3000,
        };
        if (reload) await p.reload(opts);
        else await p.goto(origin + "/fixture", opts);
        const method =
          mode === "zero"
            ? "Audits.readBraidBoundedLoad"
            : "Audits.readBraidZero";
        return { c, read: () => c.send(method, { epoch }) };
      }
      const p = await seed(),
        q = await seed();
      const first = await navigate(p);
      if (mode === "unresolved") {
        await assert.rejects(first.read(), /zero producer unresolved/);
        release();
        await p.waitForLoadState("load");
        await assert.rejects(first.read(), /zero producer unresolved/);
      } else {
        if (mode === "continuation") {
          // Publish a future producer BEFORE the zero receipt. A browser load event
          // cannot settle this promise reaction. Release it only after the read.
          await p.evaluate(() => {
            let release;
            const gate = new Promise((r) => (release = r));
            window.releaseProducer = release;
            window.arrived = new Promise((r) =>
              document.addEventListener(
                "securitypolicyviolation",
                (e) => r(e.isTrusted && e.effectiveDirective === "worker-src"),
                { once: true },
              ),
            );
            gate.then(() => {
              new Worker("/owned-worker.js").onerror = (e) =>
                e.preventDefault();
            });
          });
        }
        await retain(first);
        if (mode === "continuation") {
          assert.equal(
            await p.evaluate(() => {
              window.releaseProducer();
              return window.arrived;
            }),
            true,
          );
          await assert.rejects(
            first.read(),
            /unsettled CSP work|zero producer unresolved/,
          );
        }
        const replacement = await navigate(p, true);
        await retain(replacement);
        await assert.rejects(
          first.read(),
          mode === "zero"
            ? /zero producer unresolved|unowned fixture publication/
            : /zero producer unresolved/,
        );
        await retain(await navigate(q));
        assert.equal(new Set(loads.map((x) => x.generation)).size, 3);
        assert.equal(new Set(loads.map((x) => x.producer)).size, 3);
      }
      const inv = await session.send("Browser.readBraidInventory", { runId });
      assert.equal(inv.digest, hash(runId + ":" + epoch + ":" + inv.inventory));
      const rows = inv.inventory.split("\n").map((x) => x.split(":"));
      assert.equal(rows.length, inv.revision);
      for (const load of loads)
        assert.ok(
          rows.some(([kind, id]) => kind === "B" && id === load.generation),
        );
      const receipt = journal.accept(
        epoch,
        1,
        inv.revision,
        "0".repeat(64),
        hash(JSON.stringify([runId, inv, loads, originalPrefix ?? null])),
      );
      const message = `braid-fence-v1:${runId}:${epoch}:${inv.revision}:${inv.digest}:1:${receipt.digest}`;
      const signature = sign(null, Buffer.from(message), privateKey).toString(
        "hex",
      );
      const checkpoint = ["unresolved", "missing"].includes(mode)
        ? ""
        : `1:${receipt.digest}:${mode === "forged" ? "0".repeat(128) : signature}`;
      const prepared = await session.send("Browser.prepareBraidFence", {
        runId,
        ...inv,
        checkpoint,
      });
      const disposition =
        mode === "forged"
          ? "invalid-checkpoint-proof"
          : checkpoint
            ? "renderer-producer-coverage-unproven"
            : "missing-durable-checkpoint";
      assert.equal(prepared.disposition, disposition);
      if (mode === "late") await ctx.newPage();
      await ctx.close();
      const v = await session.send("Browser.verifyBraidDisposal", {
        runId,
        revision: inv.revision,
        digest: inv.digest,
        checkpoint,
        snapshotToken:
          mode === "wrong-ticket" ? "invalid" : prepared.snapshotToken,
      });
      assert.equal(v.disposed, true);
      assert.equal(
        v.disposition,
        mode === "late"
          ? "late-document"
          : mode === "wrong-ticket"
            ? "invalid-fence-echo"
            : disposition,
      );
      assert.ok(v.inventoryRevision > inv.revision);
      if (mode !== "late") {
        const live = new Set();
        for (const [kind, id] of rows)
          kind === "B" ? live.add(id) : live.delete(id);
        for (const line of v.transitions.split("\n")) {
          const [kind, id] = line.split(":");
          assert.equal(kind, "D");
          assert.ok(live.delete(id));
        }
        assert.equal(live.size, 0);
      }
      console.log(`${mode}: original inventory, cleanup and refusal verified`);
    } finally {
      await ctx.close();
      await session.detach();
      originals.close();
      journal.close();
      await rm(dir, { recursive: true });
    }
  }
} finally {
  await browser.close();
}
