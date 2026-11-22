import assert from "node:assert/strict";
import { once } from "node:events";
import { launchFixture, openFixture, violate, origin } from "./fixture.mjs";
import { checkAck } from "./receipt-channel.mjs";

const [start] = await once(process, "message");
const { epoch, key, scenario } = start;
const browser = await launchFixture();
try {
  const page = await openFixture(browser);
  const session = await page.context().newCDPSession(page);
  const read = () => session.send("Audits.readBraidDispatch", { epoch });
  const arm = await session.send("Audits.armBraidDispatch", { epoch });
  process.send({ kind: "armed", epoch, generation: arm.generation });
  if (
    scenario === "real" ||
    scenario === "replacement" ||
    scenario === "duplicate" ||
    scenario === "late"
  )
    assert.ok(await violate(page));
  if (scenario === "duplicate") assert.ok(await violate(page));
  if (scenario === "replacement") await page.goto(origin + "/replacement");
  if (scenario === "child-frame") {
    await page.evaluate(
      () =>
        new Promise((resolve) => {
          const frame = document.createElement("iframe");
          frame.onload = () => resolve();
          frame.src = "/replacement";
          document.body.append(frame);
        }),
    );
    const child = page
      .frames()
      .find((f) => f.parentFrame() === page.mainFrame());
    assert.ok(await violate(child));
  }
  if (scenario === "pending") {
    // A real queued continuation still owns the possibility of publication.
    // Do not depend on a sleep or try to make this continuation disappear.
    await page.evaluate(() => {
      let release;
      const gate = new Promise((resolve) => {
        release = resolve;
      });
      window.fixtureRelease = release;
      window.fixtureArrival = new Promise((resolve) => {
        document.addEventListener(
          "securitypolicyviolation",
          (e) => resolve(e.isTrusted),
          { once: true },
        );
      });
      gate.then(() => {
        new Worker("/owned-worker.js").onerror = (e) => e.preventDefault();
      });
    });
    await assert.rejects(read(), /dispatch not settled/);
    assert.ok(
      await page.evaluate(() => {
        window.fixtureRelease();
        return window.fixtureArrival;
      }),
    );
  }
  if (scenario === "teardown") {
    assert.ok(await violate(page));
    await page.close();
    await assert.rejects(read(), /closed|detached/i);
  }
  if (scenario === "synthetic")
    await page.evaluate(() =>
      document.dispatchEvent(
        new SecurityPolicyViolationEvent("securitypolicyviolation", {
          effectiveDirective: "worker-src",
          violatedDirective: "worker-src",
          blockedURI: "http://braid.invalid/owned-worker.js",
          disposition: "enforce",
        }),
      ),
    );
  if (scenario === "wrong-epoch")
    await assert.rejects(
      session.send("Audits.readBraidDispatch", { epoch: "0".repeat(32) }),
      /original fixture lost/,
    );
  if (scenario === "late") {
    await read();
    assert.ok(await violate(page));
  }
  if (scenario === "real") {
    const { generation, dispatch } = await read();
    assert.equal(generation, arm.generation);
    const record = { kind: "dispatch-end", epoch, generation, dispatch };
    const waiting = once(process, "message");
    process.send(record);
    const [receipt] = await waiting;
    checkAck(key, epoch, generation, record, receipt);
    // Keep the original session until the exact Node receipt returns. This
    // check detects replacement or late work; it does not settle future work.
    assert.deepEqual(await read(), { generation, dispatch });
  } else if (scenario !== "teardown") {
    assert.ok(
      [
        "replacement",
        "duplicate",
        "late",
        "synthetic",
        "wrong-epoch",
        "missing",
        "child-frame",
        "pending",
      ].includes(scenario),
    );
    await assert.rejects(read(), /original fixture lost|dispatch not settled/);
  }
} finally {
  await browser.close();
}
process.send({ kind: "finished", scenario });
process.disconnect();
