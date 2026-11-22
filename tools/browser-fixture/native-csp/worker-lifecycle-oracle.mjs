// Stock-only behavior oracle. Policy and responses belong to this fixture.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { once } from "node:events";
import { createReadStream } from "node:fs";
import { createRequire } from "node:module";
import { isAbsolute, join } from "node:path";
import {
  checkWorkerObservation,
  origin,
  ownerUrl,
  workletModule,
  moduleWorker,
  dependency,
} from "./worker-oracle-expectations.mjs";

const modes = ["classic", "module", "worklet"];
const requested = process.argv.slice(2);
assert(requested.every((mode) => modes.includes(mode)));
assert.equal(new Set(requested).size, requested.length);

const [lo, ...rest] = JSON.parse(
  execFileSync("/usr/bin/ip", ["-j", "link", "show"], { encoding: "utf8" }),
);
assert.equal(rest.length, 0);
assert.equal(lo?.ifname, "lo");
assert(!lo.flags.includes("UP"));
assert.notEqual(process.getuid(), 0);

async function verify(path, pin) {
  assert(path && isAbsolute(path));
  const hash = createHash("sha256");
  for await (const bytes of createReadStream(path)) hash.update(bytes);
  assert.equal(hash.digest("hex"), pin, "stock artifact digest mismatch");
}
const chrome = process.env.BRAID_CHROME;
const driver = process.env.BRAID_DRIVER;
assert(driver && isAbsolute(driver));
await verify(
  chrome,
  "b9f56b6d4d5e791dc5d56d8218abf2d7ca09bc218719b27043205b3ebf86c46d",
);
await verify(
  join(driver, "lib/coreBundle.js"),
  "2325ca4c1c83070e89dc49fb82525508f0a68e0cf12e2a624277968da216b7e0",
);

const { chromium } = createRequire(import.meta.url)(driver);
const browser = await chromium.launch({
  executablePath: chrome,
  chromiumSandbox: true,
  headless: true,
  timeout: 15000,
  env: {
    HOME: process.env.HOME,
    TMPDIR: process.env.TMPDIR,
    PATH: "/usr/bin",
  },
});

const classic = (id) => `
  const fixtureId = ${JSON.stringify(id)};
  performance.mark("braid-worker-ready:" + fixtureId);
  postMessage({kind: "ready", fixtureId});
  onmessage = ({data}) => {
    if (data !== fixtureId) throw new Error("wrong fixture ID");
    addEventListener("securitypolicyviolation", (v) => {
      performance.mark("braid-worker-csp:" + fixtureId);
      postMessage({
        kind: "violation", fixtureId, trusted: v.isTrusted,
        directive: v.effectiveDirective, disposition: v.disposition,
        blocked: v.blockedURI, document: v.documentURI,
        originalWorker: v.target === self && v.currentTarget === self,
      });
    }, {once:true});
    fetch("/blocked-" + fixtureId).catch(() => {});
  };`;

async function run(mode, allowed) {
  const context = await browser.newContext({ serviceWorkers: "block" });
  try {
    const hits = [];
    const scriptPolicy =
      mode === "module" || (mode === "worklet" && !allowed)
        ? "'none'"
        : "'self'";
    const workerPolicy =
      mode === "module" && !allowed ? moduleWorker : "'self'";
    const ownerPolicy = `worker-src ${workerPolicy}; script-src ${scriptPolicy}; connect-src 'self'; img-src 'none'`;
    await context.route("**/*", async (route) => {
      const url = route.request().url();
      if (url !== `${origin}/favicon.ico`) hits.push(url);
      const send = (body, policy) =>
        route.fulfill({
          status: 200,
          headers: {
            "content-type": "application/javascript",
            ...(policy ? { "content-security-policy": policy } : {}),
          },
          body,
        });
      if (url === ownerUrl) {
        return route.fulfill({
          status: 200,
          headers: {
            "content-type": "text/html",
            "content-security-policy": ownerPolicy,
          },
          body: "<!doctype html><title>Worker lifecycle oracle</title>",
        });
      }
      if (mode === "classic") {
        for (const id of ["old", "new"]) {
          if (url === `${origin}/classic-${id}.js`) {
            return send(classic(id), "connect-src 'none'");
          }
        }
      } else if (mode === "worklet") {
        if (url === workletModule) return send("export {};");
      } else {
        if (url === moduleWorker) {
          // Both contexts serve the same graph and no worker response CSP.
          // worker-src allows this root in both contexts but restricts the
          // dependent import in the denied context.
          return send(
            `import ${JSON.stringify(dependency)}; postMessage("module-ran");`,
          );
        }
        if (url === dependency) return send("export {};");
      }
      return route.abort();
    });

    const page = await context.newPage();
    page.setDefaultTimeout(6500);
    const logs = [];
    page.on("console", (message) => logs.push(message.text()));
    const session = await browser.newBrowserCDPSession();
    const destroy = new Set();
    session.on("Target.targetDestroyed", ({ targetId }) =>
      destroy.add(targetId),
    );
    await session.send("Target.setDiscoverTargets", { discover: true });
    const trace = [];
    session.on("Tracing.dataCollected", ({ value }) => trace.push(...value));
    assert.equal((await page.goto(ownerUrl, { timeout: 8000 })).status(), 200);
    await session.send("Tracing.start", {
      categories: "blink,blink.worker,blink.user_timing,devtools.timeline",
      options: "record-until-full",
    });
    await page.evaluate(() => {
      const original = document;
      performance.mark("braid-original-owner");
      window.oracleEvents = [];
      document.addEventListener("securitypolicyviolation", (event) => {
        performance.mark("braid-owner-csp:" + event.effectiveDirective);
        window.oracleEvents.push({
          trusted: event.isTrusted,
          directive: event.effectiveDirective,
          disposition: event.disposition,
          blocked: event.blockedURI,
          document: event.documentURI,
          original: event.target === original && document === original,
        });
      });
    });

    let execution;
    try {
      execution =
        mode === "classic"
          ? await exerciseClassic(page, session, destroy)
          : mode === "module"
            ? await exerciseModule(page)
            : await exerciseWorklet(page);
    } catch (error) {
      console.error(JSON.stringify({ hits, logs }));
      throw error;
    }
    // Positive same-document control after the tested operations. Not a
    // universal fence against another thread's future forward.
    await page.evaluate(
      () => (document.createElement("img").src = "/owner-pixel"),
    );
    await page.waitForFunction(() =>
      window.oracleEvents.some((event) => event.directive === "img-src"),
    );
    const events = await page.evaluate(() => {
      performance.mark("braid-owner-snapshot");
      return window.oracleEvents;
    });
    const complete = once(session, "Tracing.tracingComplete");
    await session.send("Tracing.end");
    const [{ dataLossOccurred }] = await complete;
    assert.equal(dataLossOccurred, false);
    const result = checkWorkerObservation({
      mode,
      allowed,
      hits,
      logs,
      execution,
      events,
      trace,
    });
    await session.detach();
    console.log(JSON.stringify(result));
  } finally {
    await context.close();
  }
}

async function exerciseClassic(page, session, destroyed) {
  const seen = [];
  for (const fixtureId of ["old", "new"]) {
    const url = `${origin}/classic-${fixtureId}.js`;
    const workerArrival = page.waitForEvent("worker", {
      predicate: (candidate) => candidate.url() === url,
    });
    await page.evaluate((fixtureId) => {
      const worker = new Worker(`/classic-${fixtureId}.js`);
      const messages = [];
      worker.onmessage = (event) => messages.push(event.data);
      window.oracleWorker = { worker, messages };
    }, fixtureId);
    const worker = await workerArrival;
    await page.waitForFunction(() => window.oracleWorker.messages.length === 1);
    const { targetInfos } = await session.send("Target.getTargets");
    const targets = targetInfos.filter(
      (entry) => entry.type === "worker" && entry.url === url,
    );
    assert.equal(targets.length, 1);
    const { targetId } = targets[0];
    assert(!destroyed.has(targetId));
    await page.evaluate(
      (id) => window.oracleWorker.worker.postMessage(id),
      fixtureId,
    );
    await page.waitForFunction(() => window.oracleWorker.messages.length === 2);
    const messages = await page.evaluate(() => window.oracleWorker.messages);
    const deadline = AbortSignal.timeout(6000);
    const closed = once(worker, "close", { signal: deadline });
    const browserDestroyed = (async () => {
      while (!destroyed.has(targetId)) {
        await once(session, "Target.targetDestroyed", { signal: deadline });
      }
    })();
    await Promise.all([
      closed,
      browserDestroyed,
      page.evaluate(() => window.oracleWorker.worker.terminate()),
    ]);
    assert(destroyed.has(targetId));
    seen.push({
      fixtureId,
      targetId,
      messages,
      targetDestroyed: destroyed.has(targetId),
    });
  }
  return seen;
}

async function exerciseWorklet(page) {
  return page.evaluate(async (url) => {
    if (!isSecureContext || !CSS.paintWorklet?.addModule)
      throw new Error("stock paint worklet unavailable");
    performance.mark("braid-worklet-add-module");
    try {
      await CSS.paintWorklet.addModule(url);
      return "loaded";
    } catch (error) {
      return error.name;
    } finally {
      performance.mark("braid-worklet-return");
    }
  }, workletModule);
}

async function exerciseModule(page) {
  return page.evaluate(
    (url) =>
      new Promise((resolve) => {
        const worker = new Worker(url, { type: "module" });
        worker.onmessage = (event) => {
          worker.terminate();
          resolve(event.data);
        };
        worker.onerror = (event) => {
          event.preventDefault();
          resolve("error");
        };
      }),
    moduleWorker,
  );
}

try {
  assert.equal(browser.version(), "157.0.8090.0");
  for (const mode of requested.length ? requested : modes) {
    if (mode === "classic") {
      await run(mode);
    } else {
      await run(mode, true);
      await run(mode, false);
    }
  }
} finally {
  await browser.close();
}
