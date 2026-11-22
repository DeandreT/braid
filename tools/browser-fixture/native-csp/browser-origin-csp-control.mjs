// Offline fixture only: no production endpoints, credentials or CSP overrides.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { once } from "node:events";
import { createReadStream } from "node:fs";
import { createRequire } from "node:module";
import { isAbsolute, join } from "node:path";
import { proveCspOrigin } from "./browser-origin-csp-trace.mjs";

const a = "http://braid.invalid";
const b = "http://child.invalid";
const c = "http://destination.invalid";
const original = `${a}/fixture`;
const child = `${b}/child`;
const redirect = `${b}/redirect`;
const deny = `${c}/blocked`;
const ancestor = `${c}/ancestor`;
const pixel = `${a}/pixel`;
const scenario = process.argv[2];
assert(["blink", "redirect", "ancestors"].includes(scenario));
const mode = process.argv[3];
const paired = mode === "--paired";
assert(
  process.argv.length === 4 && (paired || mode === "--stock-oracle"),
  "select an explicit scenario and --paired or --stock-oracle",
);

async function verify(path, pin) {
  assert(path && isAbsolute(path));
  assert.match(pin ?? "", /^[a-f0-9]{64}$/);
  const hash = createHash("sha256");
  for await (const bytes of createReadStream(path)) hash.update(bytes);
  assert.equal(hash.digest("hex"), pin);
}

const [lo, ...links] = JSON.parse(
  execFileSync("/usr/bin/ip", ["-j", "link", "show"], { encoding: "utf8" }),
);
assert.equal(lo?.ifname, "lo");
assert(!lo.flags.includes("UP") && links.length === 0);
assert.notEqual(process.getuid(), 0);
assert.match(process.env.DISPLAY ?? "", /^:\d+$/);
const executablePath = process.env.BRAID_CHROME;
const driver = process.env.BRAID_DRIVER;
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
  headless: false,
  chromiumSandbox: true,
  timeout: 15000,
  env: {
    HOME: process.env.HOME,
    TMPDIR: process.env.TMPDIR,
    PATH: "/usr/bin",
    DISPLAY: process.env.DISPLAY,
    XAUTHORITY: process.env.XAUTHORITY,
  },
});

const marker = `braid-csp-origin-${randomBytes(12).toString("hex")}`;
const script = `<script>
  const owner = document;
  const marker = ${JSON.stringify(marker)};
  window.fixtureEvents = [];
  document.addEventListener("securitypolicyviolation", event => {
    window.fixtureEvents.push({
      directive: event.effectiveDirective, disposition: event.disposition,
      blocked: event.blockedURI, uri: event.documentURI,
      trusted: event.isTrusted, originalTarget: event.target === owner
    });
    performance.mark(marker + ":event:" + event.effectiveDirective + ":" + event.isTrusted);
  });
</script>`;

try {
  assert.equal(browser.version(), "157.0.8090.0");
  const context = await browser.newContext({ serviceWorkers: "block" });
  const hits = [];
  await context.route("**/*", async (route) => {
    const url = route.request().url();
    hits.push(url);
    if (url === original) {
      await route.fulfill({
        status: 200,
        headers: {
          "content-type": "text/html",
          "content-security-policy":
            `img-src 'none'; frame-src ${b}` +
            (scenario === "ancestors" ? ` ${c}` : ""),
        },
        body: `<!doctype html><title>original</title>${script}<iframe id="child" src="${child}"></iframe>`,
      });
    } else if (url === child) {
      await route.fulfill({
        status: 200,
        contentType: "text/html",
        body: `<!doctype html><title>child</title><a href="${redirect}">Navigate</a><a href="${ancestor}">Check ancestors</a>`,
      });
    } else if (url === redirect) {
      await route.fulfill({ status: 302, headers: { location: deny } });
    } else if (url === ancestor) {
      await route.fulfill({
        status: 200,
        headers: {
          "content-type": "text/html",
          "content-security-policy": "frame-ancestors 'none'",
        },
        body: "<!doctype html><title>must not commit</title>",
      });
    } else {
      await route.abort();
    }
  });
  const page = await context.newPage();
  page.setDefaultTimeout(5000);
  assert.equal((await page.goto(original, { timeout: 8000 })).status(), 200);
  const frame = page.frames().find((entry) => entry.url() === child);
  assert(frame);
  const cdp = await context.newCDPSession(page);
  const inspector = [];
  cdp.on("Audits.issueAdded", ({ issue }) => {
    const details = issue.details.contentSecurityPolicyIssueDetails;
    if (details) inspector.push(details);
  });
  await cdp.send("Audits.enable");
  const { frameTree } = await cdp.send("Page.getFrameTree");
  assert.equal(frameTree.frame.url, original);
  const auditReport = async (directive) => {
    let listener, timer;
    const result = new Promise((resolve, reject) => {
      listener = ({ issue }) => {
        const details = issue.details.contentSecurityPolicyIssueDetails;
        if (details?.violatedDirective === directive) resolve(details);
      };
      timer = setTimeout(
        () => reject(new Error(`missing ${directive} audit`)),
        6000,
      );
      cdp.on("Audits.issueAdded", listener);
    });
    try {
      return await result;
    } finally {
      clearTimeout(timer);
      cdp.off("Audits.issueAdded", listener);
    }
  };
  const owner = await browser.newBrowserCDPSession();
  const { processInfo } = await owner.send("SystemInfo.getProcessInfo");
  const browserProcesses = processInfo.filter(({ type }) => type === "browser");
  assert.equal(browserProcesses.length, 1);
  const browserPid = browserProcesses[0].id;
  const events = [];
  owner.on("Tracing.dataCollected", ({ value }) => events.push(...value));
  await owner.send("Tracing.start", {
    categories:
      "navigation,blink,blink.user_timing,devtools.timeline,mojom,toplevel.flow,mojom.flow",
    options: "record-until-full",
  });
  await page.evaluate((label) => performance.mark(label + ":original"), marker);
  await frame.evaluate((label) => performance.mark(label + ":oopif"), marker);

  if (scenario !== "blink") {
    const expected = scenario === "redirect" ? "frame-src" : "frame-ancestors";
    const audit = auditReport(expected);
    const failed = page.waitForEvent("requestfailed", {
      predicate: (request) =>
        request.isNavigationRequest() &&
        [redirect, deny, ancestor].includes(request.url()),
    });
    const link = scenario === "redirect" ? "Navigate" : "Check ancestors";
    const [details, failure] = await Promise.all([
      audit,
      failed,
      frame.getByRole("link", { name: link }).click(),
    ]);
    assert.equal(
      failure.failure()?.errorText,
      scenario === "redirect"
        ? "net::ERR_BLOCKED_BY_CSP"
        : "net::ERR_BLOCKED_BY_RESPONSE",
    );
    assert.equal(details.isReportOnly, false);
    assert.equal(details.contentSecurityPolicyViolationType, "kURLViolation");
    assert.equal(new URL(details.blockedURL).origin, c);
    if (scenario === "ancestors") {
      assert.equal(details.frameAncestor?.frameId, frameTree.frame.id);
    } else {
      assert.equal(details.frameAncestor, undefined);
    }
  }
  // The image CSP check queues a Blink-local event on the same original
  // networking runner. In ancestors mode this is the FIFO dispatch barrier,
  // after the native audit notification (which comes after any CSP enqueue).
  await page.evaluate((url) => {
    const image = document.createElement("img");
    image.src = url;
    document.body.append(image);
  }, pixel);
  await page.waitForFunction(() =>
    window.fixtureEvents.some((event) => event.directive === "img-src"),
  );
  await page.evaluate((label) => performance.mark(label + ":end"), marker);
  const completed = once(owner, "Tracing.tracingComplete");
  await owner.send("Tracing.end");
  const [{ dataLossOccurred }] = await completed;
  assert.equal(dataLossOccurred, false);
  const { frameTree: after } = await cdp.send("Page.getFrameTree");
  assert.equal(after.frame.id, frameTree.frame.id);
  assert.equal(after.frame.loaderId, frameTree.frame.loaderId);
  const observed = await page.evaluate(() => window.fixtureEvents);
  const directives =
    scenario === "redirect" ? ["frame-src", "img-src"] : ["img-src"];
  assert.deepEqual(
    observed.map((event) => event.directive),
    directives,
  );
  for (const event of observed) {
    assert.equal(event.uri, original);
    assert.equal(event.originalTarget, true);
    assert.equal(event.trusted, true);
    assert.equal(event.disposition, "enforce");
    assert.equal(
      new URL(event.blocked).origin,
      event.directive === "frame-src" ? c : a,
    );
  }
  assert.deepEqual(
    inspector.map(({ violatedDirective }) => violatedDirective).sort(),
    (scenario === "ancestors"
      ? ["frame-ancestors", "img-src"]
      : directives
    ).toSorted(),
  );
  assert.deepEqual(
    hits,
    [
      original,
      child,
      ...(scenario === "redirect" ? [redirect] : []),
      ...(scenario === "ancestors" ? [ancestor] : []),
    ],
    "unexpected fixture request or blocked destination reached",
  );
  await context.close();
  console.log(
    JSON.stringify({
      scenario,
      paired,
      ...proveCspOrigin(events, marker, scenario, browserPid),
    }),
  );
} finally {
  await browser.close();
}
