// Tests native lifecycle delivery on task-owned intercepted HTML only.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { once } from "node:events";
import { createReadStream } from "node:fs";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { isAbsolute, join } from "node:path";
import {
  proveNavigation,
  verifyNativeSource,
} from "./old-page-commit-trace.mjs";

const [scenario, logPath, expectedSource] = process.argv.slice(2);
if (scenario === "verify-native") {
  const log = await readFile(logPath, "utf8");
  const evidence = log
    .split("\n")
    .filter((line) => line.startsWith("BRAID_OLD_PAGE_PROOF "))
    .map((line) => JSON.parse(line.slice("BRAID_OLD_PAGE_PROOF ".length)));
  assert.equal(evidence.length, 1, "expected a single traced candidate run");
  assert.equal(evidence[0].scenario, `paired-${expectedSource}`);
  verifyNativeSource(log, evidence[0].nativeOriginal, expectedSource);
  console.log("native observer agrees on the original generation and source");
} else {
  await exercise(scenario);
}

async function verify(path, expected) {
  assert(path && isAbsolute(path), "absolute local artifact path required");
  assert.match(expected, /^[a-f0-9]{64}$/);
  const hash = createHash("sha256");
  for await (const part of createReadStream(path)) hash.update(part);
  assert.equal(hash.digest("hex"), expected, "artifact digest differs");
}

async function exercise(scenario) {
  assert(
    [
      "stock-proactive",
      "stock-default",
      "stock-coop",
      "paired-commit",
      "paired-unload",
    ].includes(scenario),
    "choose an explicit offline control",
  );
  const paired = scenario.startsWith("paired-");
  const coop = scenario === "stock-coop" || scenario === "paired-commit";
  const source =
    scenario === "stock-default" || scenario === "paired-unload"
      ? "unload"
      : "commit";
  const executablePath = process.env.BRAID_CHROME;
  const driver = process.env.BRAID_DRIVER;
  const [lo, ...otherLinks] = JSON.parse(
    execFileSync("/usr/bin/ip", ["-j", "link", "show"], { encoding: "utf8" }),
  );
  assert.equal(otherLinks.length, 0);
  assert.equal(lo?.ifname, "lo");
  assert(!lo.flags.includes("UP"), "expected the offline fixture namespace");
  assert.notEqual(process.getuid(), 0);
  assert.match(process.env.DISPLAY ?? "", /^:\d+$/);

  await verify(
    executablePath,
    paired
      ? process.env.BRAID_CHROME_SHA256
      : "b9f56b6d4d5e791dc5d56d8218abf2d7ca09bc218719b27043205b3ebf86c46d",
  );
  assert(driver && isAbsolute(driver));
  await verify(
    join(driver, "lib/coreBundle.js"),
    paired
      ? process.env.BRAID_BUNDLE_SHA256
      : "2325ca4c1c83070e89dc49fb82525508f0a68e0cf12e2a624277968da216b7e0",
  );
  const { chromium } = createRequire(import.meta.url)(driver);
  const browser = await chromium.launch({
    executablePath,
    chromiumSandbox: true,
    headless: false,
    timeout: 15000,
    // The other cases keep the exact paired driver's default launch flags.
    ...(scenario === "stock-proactive"
      ? { ignoreDefaultArgs: ["--disable-back-forward-cache"] }
      : {}),
    env: {
      HOME: process.env.HOME,
      TMPDIR: process.env.TMPDIR,
      PATH: "/usr/bin",
      DISPLAY: process.env.DISPLAY,
      XAUTHORITY: process.env.XAUTHORITY,
    },
  });
  try {
    assert.equal(browser.version(), "157.0.8090.0");
    const context = await browser.newContext({ serviceWorkers: "block" });
    const origin = "http://127.0.0.1:45921";
    const seed = "http://seed.invalid/seed";
    const original =
      origin +
      "/catalog/organizations/10000000-0000-4000-8000-000000000001/projects";
    const replacement = origin + "/replacement";
    const mark = `braid-old-page-${randomBytes(12).toString("hex")}`;
    const originalScript = `<script>
      const marker = ${JSON.stringify(mark)};
      document.addEventListener('visibilitychange', event => {
        if (event.target === document) {
          performance.mark(marker + ':visibility:' +
            (event.isTrusted ? 'trusted' : 'synthetic') + ':' + document.visibilityState);
        }
      });
    </script>`;
    await context.route("**/*", async (route) => {
      const url = route.request().url();
      if (![seed, original, replacement].includes(url)) return route.abort();
      const headers = { "content-type": "text/html; charset=utf-8" };
      if (coop && url === replacement) {
        // A response-local, standards-defined group swap. No launch or
        // browser security policy changes; 127.0.0.1 is a trustworthy origin.
        headers["cross-origin-opener-policy"] = "same-origin";
      }
      await route.fulfill({
        status: 200,
        headers,
        body: `<!doctype html><title>old-page commit control</title>${url === original ? originalScript : ""}`,
      });
    });

    const page = await context.newPage();
    page.setDefaultTimeout(5000);
    await page.goto(seed, { timeout: 5000 });
    const pageSession = await context.newCDPSession(page);
    const epoch = randomBytes(16).toString("hex");
    if (paired) {
      const { frameTree } = await pageSession.send("Page.getFrameTree");
      await pageSession.send("Audits.reserveBraidDocument", {
        epoch,
        frameId: frameTree.frame.id,
      });
    }
    const first = await page.goto(original, { timeout: 5000 });
    assert.equal(first.status(), 200);
    assert.equal(await first.headerValue("cross-origin-opener-policy"), null);
    assert.equal(
      await page.evaluate(() => document.visibilityState),
      "visible",
    );
    const nativeOriginal = paired
      ? (await pageSession.send("Audits.readBraidWork", { epoch })).generation
      : null;
    if (paired) {
      assert.match(nativeOriginal, /^[A-F0-9]{32}$/);
      const { state } = await pageSession.send("Audits.readBraidLifecycle", {
        epoch,
        generation: nativeOriginal,
      });
      // Native order: adds, removes, published, completed, ordinary updates,
      // then ordinary/commit/unload visibility deliveries, then failure.
      assert.match(state, /^\d+(?::\d+){8}$/);
      const counters = state.split(":").map(Number);
      assert(
        counters.every((value) => Number.isSafeInteger(value) && value >= 0),
      );
      assert(counters[0] > counters[1], "original listener not enrolled");
      assert.deepEqual(counters.slice(5, 8), [0, 0, 0]);
    }

    const owner = await browser.newBrowserCDPSession();
    const events = [];
    owner.on("Tracing.dataCollected", ({ value }) => events.push(...value));
    await owner.send("Tracing.start", {
      categories: "navigation,blink,blink.user_timing,devtools.timeline",
      options: "record-until-full",
    });
    await page.evaluate((marker) => {
      performance.mark(marker + ":original");
      document.dispatchEvent(new Event("visibilitychange"));
    }, mark);
    const response = await page.goto(replacement, { timeout: 5000 });
    assert.equal(response.status(), 200);
    assert.equal(page.url(), replacement);
    assert.equal(
      await response.headerValue("cross-origin-opener-policy"),
      coop ? "same-origin" : null,
    );
    await page.evaluate(
      (marker) => performance.mark(marker + ":replacement"),
      mark,
    );
    const traceComplete = once(owner, "Tracing.tracingComplete");
    await owner.send("Tracing.end");
    const [{ dataLossOccurred }] = await traceComplete;
    assert.equal(dataLossOccurred, false, "tracing dropped evidence");
    console.log(
      "BRAID_OLD_PAGE_PROOF " +
        JSON.stringify({
          scenario,
          nativeOriginal,
          ...proveNavigation(events, mark, source),
        }),
    );
    await context.close();
  } finally {
    await browser.close();
  }
}
