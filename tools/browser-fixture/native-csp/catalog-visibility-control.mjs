import { until } from "./catalog-test-deadline.mjs";
// Controlled catalog UI workflow. The published product serves all HTML, JS,
// CSS and response policies. Only the existing controlled-test API replies
// are synthetic. This is not the live DB/worker suite or a clean verdict.
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import {
  createHash,
  generateKeyPairSync,
  randomBytes,
  sign,
} from "node:crypto";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { createReadStream, readFileSync, unlinkSync } from "node:fs";
import { PrefixJournal } from "./receipt-probe.mjs";
const env = process.env,
  require = createRequire(import.meta.url),
  origin = "http://127.0.0.1:45921";
async function check(path, pin) {
  assert.match(pin, /^[a-f0-9]{64}$/);
  const h = createHash("sha256");
  for await (const part of createReadStream(path)) h.update(part);
  assert.equal(h.digest("hex"), pin);
}
const links = JSON.parse(
  execFileSync("/usr/bin/ip", ["-j", "link"], { encoding: "utf8" }),
);
assert.equal(links.length, 1);
assert.equal(links[0].ifname, "lo");
assert.ok(links[0].flags.includes("UP"));
assert.deepEqual(
  JSON.parse(execFileSync("/usr/bin/ip", ["-j", "route", "show", "default"])),
  [],
);
assert.notEqual(process.getuid(), 0);
assert.match(readFileSync("/proc/self/status", "utf8"), /^CapEff:\s+0+$/m);
await check(env.BRAID_CHROME, env.BRAID_CHROME_SHA256);
await check(env.BRAID_DRIVER + "/lib/coreBundle.js", env.BRAID_BUNDLE_SHA256);
const { chromium } = require(env.BRAID_DRIVER);
const { syntheticCatalog, synthetic, projectsPath, catalogApi } = require(
  env.BRAID_UI_FIXTURE,
);
const server = spawn(env.DOTNET_ROOT + "/dotnet", [env.BRAID_SERVER], {
  stdio: ["ignore", "pipe", "ignore"],
  env: {
    HOME: env.HOME,
    TMPDIR: env.TMPDIR,
    PATH: "/usr/bin",
    DOTNET_ROOT: env.DOTNET_ROOT,
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    BRAID_DEV_LISTEN_ADDR: "127.0.0.1:45921",
  },
});
const exited = once(server, "exit");
let browser, echo, refused;
const journalPath =
  env.TMPDIR + "/catalog-retirement-" + randomBytes(16).toString("hex");
const journal = new PrefixJournal(journalPath);
try {
  await new Promise((resolve, reject) => {
    const t = setTimeout(
      () => reject(Error("server readiness deadline")),
      10000,
    );
    let buf = "";
    server.stdout.on("data", (x) => {
      buf = (buf + x).slice(-4000);
      if (buf.includes("Now listening on: " + origin)) {
        clearTimeout(t);
        resolve();
      }
    });
    server.once("exit", () => {
      clearTimeout(t);
      reject(Error("server exited"));
    });
  });
  browser = await chromium.launch({
    executablePath: env.BRAID_CHROME,
    chromiumSandbox: true,
    headless: false,
    timeout: 10000,
    env: {
      HOME: env.HOME,
      TMPDIR: env.TMPDIR,
      PATH: "/usr/bin",
      DISPLAY: env.DISPLAY,
      XAUTHORITY: env.XAUTHORITY,
    },
  });
  assert.equal(browser.version(), "157.0.8090.0");
  const owner = await browser.newBrowserCDPSession();
  const list = async () =>
    (await owner.send("Target.getBrowserContexts")).browserContextIds;
  const initial = new Set(await list());
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  const added = (await list()).filter((x) => !initial.has(x));
  assert.equal(added.length, 1);
  const keys = generateKeyPairSync("ed25519");
  const epoch = journal.epoch,
    publicKey = Buffer.from(
      keys.publicKey.export({ format: "jwk" }).x,
      "base64url",
    ).toString("hex");
  const { runId } = await owner.send("Browser.beginBraidInventory", {
    epoch,
    publicKey,
    browserContextId: added[0],
  });
  await owner.send("Browser.enableBraidNavigations", { runId });
  const host = () => owner.send("Browser.readBraidNavigations", { runId });
  const page = await ctx.newPage();
  page.setDefaultTimeout(5000);
  await syntheticCatalog(page);
  const c = await ctx.newCDPSession(page);
  const types = new Map();
  page.on("request", (r) => {
    assert.equal(new URL(r.url()).origin, origin);
    types.set(r.resourceType(), 1 + (types.get(r.resourceType()) ?? 0));
  });
  await page.goto(origin + "/this-route-does-not-exist");
  async function start(path) {
    const { frameTree } = await c.send("Page.getFrameTree");
    await c.send("Audits.reserveBraidDocument", {
      epoch,
      frameId: frameTree.frame.id,
    });
    const r = await page.goto(origin + path);
    assert.equal(r.status(), 200);
    assert.match(
      await r.headerValue("content-security-policy"),
      /script-src 'self'/,
    );
  }
  const work = () => c.send("Audits.readBraidWork", { epoch });
  await start("/catalog/organizations/not-a-uuid/projects");
  await page
    .getByRole("heading", { name: "Invalid link", exact: true })
    .waitFor();
  assert.equal(await page.getByText("No projects", { exact: true }).count(), 0);
  const first = await work();
  assert.ok(first.jobs > 0 && first.returned > 0 && first.sends > 0);
  const successor = await ctx.newCDPSession(page);
  const { frameTree } = await successor.send("Page.getFrameTree");
  const epoch2 = randomBytes(16).toString("hex");
  await successor.send("Audits.reserveBraidDocument", {
    epoch: epoch2,
    frameId: frameTree.frame.id,
  });
  const path = projectsPath(synthetic.org),
    listResponse = (r) =>
      new URL(r.url()).pathname === catalogApi(path) &&
      r.request().method() === "GET";
  await successor.send("Network.enable");
  await page.goto(origin + path);
  const project = page.getByRole("link", {
    name: synthetic.project.name,
    exact: true,
  });
  await project.waitFor();
  const original = (
    await successor.send("Audits.readBraidWork", { epoch: epoch2 })
  ).generation;
  const lifecycle = async () => {
    const result = await successor.send("Audits.readBraidLifecycle", {
      epoch: epoch2,
      generation: original,
    });
    return result.state.split(":").map(Number);
  };
  const prior = await lifecycle();
  console.log(
    "BRAID_VIS_TEST original=" + original + " mode=" + env.BRAID_VIS_MODE,
  );
  assert.equal(
    env.BRAID_VIS_MODE,
    "ordinary",
    "use the exact-source paired control for replacements",
  );
  {
    await owner.send("Browser.exerciseBraidLifecycleUpdate", {
      runId,
      generation: original,
    });
    const current = await until(
      lifecycle,
      (row) => row[4] > prior[4],
      "original ordinary lifecycle update",
    );
    console.log(
      "BRAID_VIS_TEST ordinaryUpdates=" +
        (current[4] - prior[4]) +
        " trustedVisibility=" +
        (current[5] - prior[5]),
    );
    assert.ok(current[4] > prior[4]);
    assert.equal(current[5], prior[5]);
  }
  await ctx.close();
  await owner.detach();
} finally {
  try {
    if (browser) await browser.close();
  } finally {
    server.kill("SIGTERM");
    await exited;
    journal.close();
    unlinkSync(journalPath);
  }
}
