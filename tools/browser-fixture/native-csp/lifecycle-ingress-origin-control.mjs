// Stock behavior control. This does not ask for a retirement receipt.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { once } from "node:events";
import { createRequire } from "node:module";
import { resolve } from "node:path";

const entries = (await readFile("/proc/net/dev", "utf8"))
  .trim()
  .split("\n")
  .slice(2)
  .map((line) => line.split(":")[0].trim());
assert.deepEqual(entries, ["lo"]);
const [lo] = JSON.parse(
  execFileSync("/usr/bin/ip", ["-j", "link", "show", "dev", "lo"], {
    encoding: "utf8",
  }),
);
assert.equal(lo.flags.includes("UP"), false);

const { chromium } = createRequire(import.meta.url)(
  resolve(process.env.BRAID_DRIVER),
);
const script = `<script>
  for (const [target, type] of [[window, 'focus'], [document, 'visibilitychange']]) {
    target.addEventListener(type, event => {
      const label = 'braid-origin:' + type + ':' + event.isTrusted + ':' + document.visibilityState;
      console.log(label);
      performance.mark(label);
    });
  }
</script>`;
const base = "http://braid.invalid";
const original = `${base}/original`;
const sameSite = `${base}/replacement`;
const otherSite = "http://other.invalid/replacement";
const known = new Set([original, sameSite, otherSite]);

async function exercise(name, backForwardCache, action) {
  const browser = await chromium.launch({
    executablePath: resolve(process.env.BRAID_CHROME),
    chromiumSandbox: true,
    headless: false,
    timeout: 8000,
    ignoreDefaultArgs: [
      "--disable-backgrounding-occluded-windows",
      "--disable-renderer-backgrounding",
      ...(backForwardCache ? ["--disable-back-forward-cache"] : []),
    ],
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
    await context.route("**/*", async (route) => {
      const url = route.request().url();
      return known.has(url)
        ? route.fulfill({
            status: 200,
            contentType: "text/html",
            body: `<!doctype html><title>lifecycle fixture</title>${url === original ? script : ""}`,
          })
        : route.abort();
    });
    const page = await context.newPage();
    const pageSession = await context.newCDPSession(page);
    await page.goto(original, { timeout: 5000 });
    await pageSession.send("Emulation.setFocusEmulationEnabled", {
      enabled: false,
    });
    const window = execFileSync(
      "/usr/bin/xdotool",
      ["search", "--onlyvisible", "--name", "lifecycle fixture"],
      { encoding: "utf8" },
    )
      .trim()
      .split("\n")
      .at(-1);
    execFileSync("/usr/bin/xdotool", ["windowfocus", "--sync", window]);
    const cdp = await browser.newBrowserCDPSession();
    const events = [];
    cdp.on("Tracing.dataCollected", ({ value }) => events.push(...value));
    await cdp.send("Tracing.start", {
      categories: "content,navigation,blink,blink.user_timing",
      options: "record-until-full",
    });
    await action(page, pageSession, window);
    const done = once(cdp, "Tracing.tracingComplete");
    await cdp.send("Tracing.end");
    await done;
    console.log(
      name,
      events
        .map(({ name }) => name)
        .filter((entry) => entry.startsWith("braid-origin:")),
    );
    return events;
  } finally {
    await browser.close();
  }
}

const visibility = "braid-origin:visibilitychange:true:hidden";
const requested = process.argv.slice(2);
const selected = (caseName) =>
  requested.length === 0 || requested.includes(caseName);
const strictOrdinary = requested.includes("ordinary-visibility");
assert(
  requested.every((name) =>
    ["ordinary", "ordinary-visibility", "commit", "unload"].includes(name),
  ),
);
if (selected("ordinary") || strictOrdinary) {
  const ordinary = await exercise(
    "ordinary browser lifecycle",
    true,
    async (page, pageSession, window) => {
      await page.evaluate(() => {
        window.dispatchEvent(new Event("focus"));
        document.dispatchEvent(new Event("visibilitychange"));
      });
      // Stimulate native browser focus from the private X server, not dispatchEvent.
      execFileSync("/usr/bin/xdotool", ["windowfocus", "--sync", "1"]);
      execFileSync("/usr/bin/xdotool", ["windowfocus", "--sync", window]);
      // Pinned PageHandler calls WebContents::WasHidden before freezing.
      // This is a branch stimulus, never a retirement mechanism.
      await pageSession.send("Page.setWebLifecycleState", { state: "frozen" });
      await pageSession.send("Page.setWebLifecycleState", { state: "active" });
      await page.evaluate(() => document.visibilityState); // Renderer round trip.
    },
  );
  const ordinaryNames = ordinary.map(({ name }) => name);
  assert(ordinaryNames.includes("braid-origin:focus:false:visible"));
  assert(ordinaryNames.includes("braid-origin:visibilitychange:false:visible"));
  assert(ordinaryNames.includes("braid-origin:focus:true:visible"));
  assert(ordinaryNames.includes("WebViewImpl::SetPageLifecycleState"));
  if (!ordinaryNames.includes(visibility)) {
    assert(
      !strictOrdinary,
      "ordinary update did not actually change visibility",
    );
    console.log("ordinary visibility delivery not exercised");
  }
}

if (selected("commit")) {
  const commit = await exercise("same-site replacement", true, (page) =>
    page.goto(sameSite, { timeout: 5000 }),
  );
  const commitNames = commit.map(({ name }) => name);
  assert(commitNames.includes(visibility));
  assert(
    commitNames.includes("WebViewImpl::SetPageLifecycleStateFromNewPageCommit"),
    "commit branch not exercised (same-site proactive swap with renderer reuse required)",
  );
}

if (selected("unload")) {
  const unload = await exercise("unload via replacement", false, (page) =>
    page.goto(otherSite, { timeout: 5000 }),
  );
  const unloadNames = unload.map(({ name }) => name);
  assert(unloadNames.includes(visibility));
  assert(unloadNames.includes("Document::DispatchUnloadEvents"));
  assert(
    !unloadNames.includes(
      "WebViewImpl::SetPageLifecycleStateFromNewPageCommit",
    ),
  );
}
