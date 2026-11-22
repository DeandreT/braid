import { bounded, until } from "./catalog-test-deadline.mjs";
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
    headless: true,
    timeout: 10000,
    env: { HOME: env.HOME, TMPDIR: env.TMPDIR, PATH: "/usr/bin" },
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
  const work = () => c.send("Audits.readBraidWork", { epoch });
  const mode = process.argv[2];
  assert.ok(
    [
      "success",
      "missing",
      "wrong",
      "late",
      "lost-reply",
      "hold-challenge",
      "replacement",
      "target-close",
      "refused-enrollment",
    ].includes(mode),
  );
  const path = projectsPath(synthetic.org);
  const eligible = mode !== "refused-enrollment";
  await c.send("Network.enable");
  const { frameTree } = await c.send("Page.getFrameTree");
  await c.send("Audits.reserveBraidDocument", {
    epoch: eligible ? epoch : "0".repeat(32),
    frameId: frameTree.frame.id,
  });
  const traceChild = mode === "hold-challenge" || mode === "success";
  if (traceChild) {
    await page.goto(origin + path);
    await page
      .getByRole("link", { name: synthetic.project.name, exact: true })
      .waitFor();
  }
  let arrived, release;
  const wait = new Promise((r) => (arrived = r)),
    gate = new Promise((r) => (release = r));
  await page.route(
    "**" + catalogApi(path) + "?*",
    async (route) => {
      arrived();
      await gate;
      await route.fallback();
    },
    { times: 1 },
  );
  if (["missing", "wrong", "lost-reply", "hold-challenge"].includes(mode))
    await owner.send("Browser.armBraidReceiverFault", { runId, mode });
  const readChildren = async (generation) =>
    (await c.send("Audits.readBraidEdges", { epoch, generation })).records
      .trim()
      .split("\n")
      .filter(Boolean)
      .map((x) => x.split(":"));
  const priorIds = traceChild
    ? new Set((await readChildren((await work()).generation)).map((e) => e[0]))
    : null;
  if (traceChild)
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  else await page.goto(origin + path);
  try {
    await bounded(wait, "held GET");
    const h = await host();
    const rows = h.resourceRecords.split("\n").map((r) => r.split(":"));
    const open = rows.filter(
      ([k, id]) => k === "C" && !rows.some(([st, i]) => i === id && st === "F"),
    );
    assert.equal(open.length, 1);
    const [, id, g] = open[0];
    const stages = (h) =>
      h.resourceRecords
        .split("\n")
        .map((r) => r.split(":"))
        .filter(([, i, gen]) => i === id && g === gen)
        .map(([k]) => k);
    assert.deepEqual(
      stages(h).filter((k) => !eligible || k !== "I"),
      ["S", "C", "R", "O"],
    );
    if (eligible)
      await until(host, (h) => stages(h).includes("I"), "original Open");
    if (eligible) assert.equal((await work()).generation, g);
    else
      await assert.rejects(
        c.send("Audits.readBraidWork", { epoch: "0".repeat(32) }),
        /original work unavailable/,
      );
    let child;
    if (traceChild) {
      const selected = (await readChildren(g)).filter(
        ([edgeId, parent, kind]) =>
          kind === "fetch-client" && !priorIds.has(edgeId),
      );
      assert.equal(selected.length, 1);
      child = selected[0];
      assert.deepEqual(child.slice(3), ["open", "live"]);
    }
    if (mode === "hold-challenge") {
      release();
      await until(host, (h) => stages(h).includes("P"), "challenge issued");
    }
    if (["late", "hold-challenge"].includes(mode)) {
      const inv = await owner.send("Browser.readBraidInventory", { runId });
      const receipt = journal.accept(
        epoch,
        1,
        inv.revision,
        "0".repeat(64),
        createHash("sha256")
          .update(JSON.stringify([runId, g, id, inv, h]))
          .digest("hex"),
      );
      const prefix = "1:" + receipt.digest;
      const checkpoint =
        prefix +
        ":" +
        sign(
          null,
          Buffer.from(
            `braid-fence-v1:${runId}:${epoch}:${inv.revision}:${inv.digest}:${prefix}`,
          ),
          keys.privateKey,
        ).toString("hex");
      const ticket = await owner.send("Browser.prepareBraidFence", {
        runId,
        ...inv,
        checkpoint,
      });
      echo = {
        runId,
        revision: inv.revision,
        digest: inv.digest,
        checkpoint,
        snapshotToken: ticket.snapshotToken,
      };
      refused = ticket.disposition;
    }
    if (mode === "replacement")
      await page.goto(origin + "/this-route-does-not-exist");
    if (mode === "target-close") await page.close();
    if (["replacement", "target-close"].includes(mode))
      await until(
        () => owner.send("Browser.readBraidInventory", { runId }),
        (inv) =>
          inv.inventory
            .split("\n")
            .some((row) => row.startsWith("D:" + g + ":")),
        "original death before release",
      );
    release();
    if (!["replacement", "target-close"].includes(mode))
      await page
        .getByRole("link", { name: synthetic.project.name, exact: true })
        .waitFor();
    const wanted = {
      success: "A",
      missing: "M",
      wrong: "X",
      late: "L",
      "lost-reply": "M",
      "hold-challenge": "X",
      "refused-enrollment": "F",
    }[mode];
    if (wanted)
      await until(host, (h) => stages(h).includes(wanted), "original verdict");
    if (traceChild) {
      const desired =
        mode === "success"
          ? ["receiver-ack", "retired"]
          : ["refused-open", "live"];
      const original = child.slice(0, 3);
      const result = await until(
        () => readChildren(g),
        (edges) =>
          edges.some(
            (e) =>
              e[0] === child[0] && e[3] === desired[0] && e[4] === desired[1],
          ),
        "exact original fetch child",
      );
      const matches = result.filter((e) => e[0] === child[0]);
      assert.deepEqual(matches, [[...original, ...desired]]);
      console.log("original fetch child:", g, ...matches[0]);
    }
    await ctx.close();
    const state = stages(await host());
    console.log(mode, g, id, state.join(" "));
    assert.equal(state.includes("O"), mode !== "success");
    assert.equal(state.includes("A"), mode === "success");
    if (mode === "success")
      assert.ok(
        state.indexOf("P") > state.indexOf("F") &&
          state.indexOf("A") > state.indexOf("P"),
      );
    if (mode === "hold-challenge")
      assert.ok(
        state.indexOf("P") >= 0 && state.indexOf("X") > state.indexOf("P"),
      );
    if (mode === "lost-reply")
      assert.ok(state.includes("P") && state.includes("M"));
    if (mode === "refused-enrollment")
      assert.deepEqual(state, ["S", "C", "R", "F", "O"]);
    if (["late", "wrong", "missing"].includes(mode))
      assert.ok(state.includes({ late: "L", wrong: "X", missing: "M" }[mode]));
    if (echo) {
      const v = await owner.send("Browser.verifyBraidDisposal", echo);
      assert.equal(v.disposed, true);
      assert.equal(v.disposition, refused);
    }
    await owner.detach();
  } finally {
    release();
  }
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
