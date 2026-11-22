// A bounded native load receipt is not a document-, target- or browser-cutoff.
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { launchFixture, origin } from "./fixture.mjs";
const browser = await launchFixture();
try {
  for (const mode of [
    "plain",
    "pending",
    "port-after-return",
    "await-port",
    "port-only",
    "settled-only",
    "network",
    "database",
  ]) {
    const ctx = await browser.newContext({ serviceWorkers: "block" });
    try {
      const page = await ctx.newPage();
      page.setDefaultTimeout(3000);
      let started, release;
      const inFlight = new Promise((r) => (started = r)),
        hold = new Promise((r) => (release = r));
      await ctx.route("**/*", async (r) => {
        if (r.request().url() === origin + "/delayed-image") {
          started();
          await hold;
          return r.fulfill({ status: 404, body: "" });
        }
        return r.fulfill({
          contentType: "text/html",
          headers: { "content-security-policy": "worker-src 'none'" },
          body: "<!doctype html><link rel=icon href=data:,><title>bounded load</title>",
        });
      });
      await page.goto(origin + "/seed");
      const session = await ctx.newCDPSession(page);
      const { frameTree } = await session.send("Page.getFrameTree");
      const epoch = randomBytes(16).toString("hex");
      await session.send("Audits.reserveBraidDocument", {
        epoch,
        frameId: frameTree.frame.id,
      });
      await session.send("Page.enable");
      const loaded = new Promise((resolve) =>
        session.once("Page.loadEventFired", resolve),
      );
      await session.send("Page.navigate", { url: origin + "/fixture" });
      await loaded;
      const read = () => session.send("Audits.readBraidBoundedLoad", { epoch });
      if (mode === "plain") {
        const r = await read();
        assert.match(r.producer, /^[A-F0-9]{32}$/);
        assert.deepEqual(await read(), r);
      } else {
        // Configure via a raw protocol call so runner-injected promises are not a
        // prerequisite for these controls. Never use a page response as a native receipt.
        async function evaluate(expression, awaitPromise = false) {
          const x = await session.send("Runtime.evaluate", {
            expression,
            awaitPromise,
            returnByValue: true,
          });
          assert.ok(!x.exceptionDetails);
          return x.result.value;
        }
        await evaluate(
          `window.fired=0;document.addEventListener('securitypolicyviolation',e=>{if(e.isTrusted&&e.effectiveDirective==='worker-src')++window.fired});window.effect=()=>{new Worker('/owned-worker.js').onerror=e=>e.preventDefault()};`,
        );
        if (mode === "network") {
          // No JS promise, port or timer on the page. The outstanding image's
          // failure callback is already published before the bounded load read.
          await evaluate(
            `window.img=new Image();img.onerror=effect;img.src='/delayed-image';void 0`,
          );
          await inFlight;
          await assert.rejects(read(), /unowned fixture publication/);
          // Only now install a test-side wait for the actual violation, then release I/O.
          const fired = page.waitForEvent(
            "console",
            (m) =>
              m.type() === "info" && m.text() === "fixture-native-policy-event",
          );
          await evaluate(
            `document.addEventListener('securitypolicyviolation',e=>{if(e.isTrusted&&e.effectiveDirective==='worker-src')console.info('fixture-native-policy-event')},{once:true})`,
          );
          release();
          await fired;
          assert.equal(await evaluate("fired"), 1);
          await assert.rejects(
            read(),
            /unowned fixture publication|unsettled CSP work|zero producer unresolved/,
          );
          console.log(
            "network: pending resource refused before releasing its CSP-producing callback",
          );
          continue;
        }
        if (mode === "database") {
          const signal = (name) =>
            page.waitForEvent(
              "console",
              (m) => m.type() === "info" && m.text() === name,
            );
          const opened = signal("fixture-open");
          await evaluate(
            `const owner=indexedDB.open('local-control',1);owner.onsuccess=()=>{window.db=owner.result;console.info('fixture-open')};void 0`,
          );
          await opened;
          const blocked = signal("fixture-blocked");
          await evaluate(
            `const upgrade=indexedDB.open('local-control',2);upgrade.onblocked=()=>console.info('fixture-blocked');upgrade.onsuccess=()=>{upgrade.result.close();effect()};void 0`,
          );
          await blocked;
          // No wait/drain heuristic: the browser database coordinator has
          // acknowledged a blocked upgrade and the old connection is still held.
          const original = await session.send("Audits.readBraidZero", {
            epoch,
          });
          assert.match(original.producer, /^[A-F0-9]{32}$/);
          await assert.rejects(read(), /unowned fixture publication: database/);
          const arrived = signal("fixture-native-event");
          await evaluate(
            `document.addEventListener('securitypolicyviolation',e=>{if(e.isTrusted&&e.effectiveDirective==='worker-src')console.info('fixture-native-event')},{once:true});db.close()`,
          );
          await arrived;
          assert.equal(await evaluate("fired"), 1);
          await assert.rejects(
            read(),
            /unowned fixture publication|unsettled CSP work|zero producer unresolved/,
          );
          console.log(
            "database: old load receipt preceded a CSP-producing database callback; stricter scope refused before release",
          );
          continue;
        }
        if (mode === "pending")
          await evaluate(
            `let release;const gate=new Promise(r=>release=r);window.resume=release;gate.then(effect);void 0`,
          );
        else if (mode === "settled-only")
          await evaluate(`Promise.resolve(1).then(x=>x+1)`, true);
        else {
          await evaluate(
            `const ch=new MessageChannel();window.port=ch.port1;window.sendPort=ch.port2;port.addEventListener('message',effect,{once:true});void 0`,
          );
          if (mode === "port-only") await evaluate(`sendPort.postMessage(1)`);
          else if (mode === "await-port")
            assert.equal(
              await evaluate(
                `(async()=>{await Promise.resolve();sendPort.postMessage(1);return 17})()`,
                true,
              ),
              17,
            );
          else
            assert.equal(
              await evaluate(
                `Promise.resolve().then(()=>{sendPort.postMessage(1);return 17})`,
                true,
              ),
              17,
            );
        }
        assert.equal(await evaluate("fired"), 0);
        const refusal =
          mode === "port-only"
            ? /unowned fixture publication: port/
            : /unowned fixture publication/;
        await assert.rejects(read(), refusal);
        if (mode !== "settled-only") {
          assert.equal(
            await evaluate(
              `new Promise(r=>{document.addEventListener('securitypolicyviolation',e=>r(e.isTrusted&&e.effectiveDirective==='worker-src'),{once:true});${mode === "pending" ? "resume()" : "port.start()"}})`,
              true,
            ),
            true,
          );
          assert.equal(await evaluate("fired"), 1);
        }
        await assert.rejects(read(), /unowned fixture publication/);
      }
      console.log(mode + ": bounded native control passed; no document cutoff");
    } finally {
      await ctx.close();
    }
  }
} finally {
  await browser.close();
}
