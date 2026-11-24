import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash, createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { isDeepStrictEqual } from "node:util";
import {
  assertDriverInstance,
  assertLoopbackOnly,
  driverEntryFrom,
  isLoopbackOrigin,
  main,
  pairFrom,
  requestFrom,
  sandboxLaunchOptions,
  snapshotMount,
} from "./qualify-native-csp.mjs";
import { CatalogGenerations, runOnce } from "./qualification-generations.mjs";

const exactOrigin = "http://127.0.0.1:45921";
const pairPath = "/inputs/pair/pair.json";
const sha = (input) => createHash("sha256").update(input).digest("hex");
const pin = "a".repeat(64);
const args = [
  "--qualify",
  "--pair",
  pairPath,
  "--pair-sha256",
  pin,
  "--origin",
  exactOrigin,
];

// Exact upstream manifest from microsoft/playwright@606ecd8b34bac100db32561b6278ffd76e5eb3cd.
const manifestBytes = readFileSync(
  new URL("./fixtures/playwright-core-1.64.0.package.json", import.meta.url),
);
const manifest = JSON.parse(manifestBytes);

test("explicit command stays off by default, rejects overrides and does not start a driver", () => {
  const entry = fileURLToPath(
    new URL("./qualify-native-csp.mjs", import.meta.url),
  );
  const proc = spawnSync(process.execPath, [entry], {
    env: { BRAID_CATALOG_NATIVE_CSP: "1" },
    encoding: "utf8",
    timeout: 5000,
  });
  assert.equal(proc.status, 1);
  assert.match(proc.stderr, /explicit --qualify required/);
  let reads = 0;
  const inspect = () => {
    reads++;
  };
  assert.throws(() => main([], inspect), /disabled/);
  assert.throws(() => main([...args, "--retries", "1"], inspect), /unexpected/);
  assert.throws(
    () => main([...args, "--adapter", "/adapter"], inspect),
    /unexpected/,
  );
  assert.throws(
    () => main([...args, "--driver", "/stock"], inspect),
    /unexpected/,
  );
  assert.throws(
    () => main([...args, "--origin", exactOrigin], inspect),
    /duplicate/,
  );
  assert.equal(reads, 0);
  assert.throws(
    () => main(args, inspect),
    /native generation and browser-wide cutoff adapter is unavailable/,
  );
  assert.equal(reads, 1);
  assert.deepEqual(requestFrom(args), {
    record: pairPath,
    pin,
    origin: exactOrigin,
  });
});

test("reachable offline loopback and canonical product origin are mandatory", () => {
  assert.equal(isLoopbackOrigin(exactOrigin), true);
  for (const origin of [
    "http://localhost:45921",
    "http://127.0.0.2:45921",
    "http://[::1]:45921",
    "http://127.0.0.1:45921/path",
    "http://127.0.0.1:45921?secret=1",
    "http://127.0.0.1:45921/",
    "http://127.0.0.1:80",
    "http://127.0.0.1:65921",
    "https://127.0.0.1:45921",
    "http://127.0.0.1:45921\n",
  ])
    assert.equal(isLoopbackOrigin(origin), false, origin);
  const lo = { ifname: "lo", flags: ["LOOPBACK", "UP"] };
  assert.doesNotThrow(() => assertLoopbackOnly([lo]));
  assert.throws(
    () => assertLoopbackOnly([{ ...lo, flags: ["LOOPBACK"] }]),
    /reachable loopback/,
  );
  assert.throws(
    () => assertLoopbackOnly([lo, { ifname: "eth0" }]),
    /isolated network/,
  );
});

test("a protected ext4 snapshot rejects writable backing and writable child mounts", () => {
  const base = "16 10 1:1 / / rw - ext4 /dev/root rw";
  const snapshot = "22 16 1:2 / /inputs ro,nodev - ext4 /dev/input ro";
  const root = "/inputs/pair";
  assert.equal(snapshotMount(`${base}\n${snapshot}`, root), "22");
  assert.throws(
    () => snapshotMount(`${base}\n${snapshot.replace(/ ro$/, " rw")}`, root),
    /read-only ext4/,
  );
  const child = "23 22 1:3 / /inputs/pair/driver rw - tmpfs none rw";
  assert.throws(
    () => snapshotMount(`${base}\n${snapshot}\n${child}`, root),
    /read-only ext4/,
  );
  assert.throws(() => snapshotMount(base, root), /read-only ext4/);
});

test("the exact pinned Playwright Core manifest admits the CJS entry without main", () => {
  assert.equal(
    sha(manifestBytes),
    "1d6b6f80ffb0c52865af0f9fe01abf2140df019e49ae6ac134605e462d583f3a",
  );
  assert.equal(Object.hasOwn(manifest, "main"), false);
  assert.equal(driverEntryFrom(manifest, "1.64.0"), "driver/index.js");
});

test("driver admission refuses alternate paths and conditional layouts", () => {
  const changeRoot = (entry) => ({
    ...manifest,
    exports: { ...manifest.exports, ".": entry },
  });
  const root = manifest.exports["."];
  const { import: esm, ...noImport } = root;
  for (const invalid of [
    null,
    { ...manifest, name: "playwright" },
    { ...manifest, version: "1.64.1" },
    { ...manifest, main: "index.js" },
    { ...manifest, type: "module" },
    { ...manifest, exports: undefined },
    { ...manifest, exports: "./index.js" },
    changeRoot("./index.js"),
    changeRoot(noImport),
    changeRoot({ ...root, types: "./types/types.d.ts" }),
    changeRoot({ ...root, import: "./alternate.mjs" }),
    changeRoot({ ...root, require: "./lib/../index.js" }),
    changeRoot({ ...root, default: "./index.mjs" }),
    changeRoot({ node: "./other.js", ...root }),
    changeRoot({ default: root.default, ...noImport, import: esm }),
    {
      ...manifest,
      exports: { ...manifest.exports, "./lib/bootstrap": "./other.js" },
    },
    {
      ...manifest,
      exports: { ...manifest.exports, "./*": "./*" },
    },
  ]) {
    assert.throws(
      () => driverEntryFrom(invalid, "1.64.0"),
      /paired driver package entry layout or version differs/,
    );
  }
});

test("the independently pinned record binds every path and digest", () => {
  const inventory = [
    "chrome/chrome",
    "chrome/resources.pak",
    "driver/index.d.ts",
    "driver/index.js",
    "driver/index.mjs",
    "driver/lib/bootstrap.js",
    "driver/lib/coreBundle.js",
    "driver/lib/tools/cli-client/program.js",
    "driver/lib/utilsBundle.js",
    "driver/package.json",
  ];
  const readDigest = (path) =>
    sha(path === "driver/package.json" ? manifestBytes : path);
  const files = Object.fromEntries(
    inventory.map((path) => [path, readDigest(path)]),
  );
  const record = {
    schemaVersion: "braid.catalog-native-csp-pair.v1",
    chromeVersion: "157.0.8090.0",
    driverVersion: "1.64.0",
    files,
  };
  const bytes = Buffer.from(JSON.stringify(record));
  assert.deepEqual(pairFrom(bytes, sha(bytes), inventory, readDigest), record);
  assert.throws(
    () => pairFrom(bytes, pin, inventory, readDigest),
    /record digest mismatch/,
  );
  assert.throws(
    () =>
      pairFrom(
        bytes,
        sha(bytes),
        [...inventory, "driver/unlisted.js"],
        readDigest,
      ),
    /unsupported pair/,
  );
  assert.throws(
    () =>
      pairFrom(bytes, sha(bytes), inventory, (path) =>
        path.endsWith(".pak") ? pin : readDigest(path),
      ),
    /artifact digest mismatch/,
  );
  const unsupported = Buffer.from(
    JSON.stringify({ ...record, driverVersion: "1.63.0" }),
  );
  assert.throws(
    () => pairFrom(unsupported, sha(unsupported), inventory, readDigest),
    /unsupported pair/,
  );
  for (const entry of inventory.filter((name) => name.startsWith("driver/"))) {
    const { [entry]: omitted, ...otherFiles } = files;
    const reduced = Buffer.from(
      JSON.stringify({ ...record, files: otherFiles }),
    );
    const without = inventory.filter((name) => name !== entry);
    assert.equal(omitted, readDigest(entry));
    assert.throws(
      () => pairFrom(bytes, sha(bytes), without, readDigest),
      /unsupported pair/,
      `${entry} missing from disk`,
    );
    assert.throws(
      () => pairFrom(reduced, sha(reduced), inventory, readDigest),
      /unsupported pair/,
      `${entry} missing its pin`,
    );
    assert.throws(
      () => pairFrom(reduced, sha(reduced), without, readDigest),
      /unsupported pair/,
      `${entry} missing from both disk and record`,
    );
    assert.throws(
      () =>
        pairFrom(bytes, sha(bytes), inventory, (path) =>
          path === entry ? pin : readDigest(path),
        ),
      /pair artifact digest mismatch/,
      `${entry} has different bytes`,
    );
  }
});

test("the launch plan is explicitly sandboxed and only the exact driver instance matches", () => {
  const plan = {
    chrome: "/inputs/pair/chrome/chrome",
    driverEntry: "/inputs/pair/driver/index.js",
  };
  assert.deepEqual(
    sandboxLaunchOptions(plan, "/private/home", "/private/tmp"),
    {
      executablePath: plan.chrome,
      chromiumSandbox: true,
      timeout: 10_000,
      env: { HOME: "/private/home", TMPDIR: "/private/tmp", PATH: "/usr/bin" },
    },
  );
  assert.throws(
    () => sandboxLaunchOptions(plan, "/shared", "/shared"),
    /private launch/,
  );
  const core = { chromium: {} };
  assert.doesNotThrow(() =>
    assertDriverInstance(plan, core, plan.driverEntry, core, core.chromium),
  );
  assert.throws(
    () =>
      assertDriverInstance(plan, core, "/stock/index.js", core, core.chromium),
    /driver instance/,
  );
  assert.throws(
    () =>
      assertDriverInstance(
        plan,
        core,
        plan.driverEntry,
        { ...core },
        core.chromium,
      ),
    /driver instance/,
  );
  assert.throws(
    () => assertDriverInstance(plan, core, plan.driverEntry, core, {}),
    /driver instance/,
  );
});

const contextA = "native-A";
const contextB = "native-B";
const primary = {
  kind: "page",
  contextId: contextA,
  targetId: "page-1",
  generationId: "p1",
  parentGenerationId: null,
};
const second = { ...primary, targetId: "page-2", generationId: "p2" };
const otherContextPage = { ...second, contextId: contextB };
const frame = {
  kind: "frame",
  contextId: contextA,
  targetId: "frame-1",
  generationId: "f1",
  parentGenerationId: "p1",
};
const worker = {
  kind: "worker",
  contextId: contextA,
  targetId: "worker-1",
  generationId: "w1",
  parentGenerationId: "f1",
};
const replacement = { ...primary, generationId: "p3" };
const emptyWork = {
  producers: 0,
  dispatches: 0,
  receipts: 0,
  lateWork: 0,
  unrelatedWork: 0,
  droppedHandoffs: 0,
};
const noOp = async () => {};
async function enrollPrimary(lane) {
  await lane.enrollContext(contextA);
  await lane.enroll(primary);
}
const originFor = ({ generationId }) => ({
  originId: `source-${generationId}`,
  generationId,
  producerCovered: true,
  produced: 0,
  dispatched: 0,
  acknowledged: 0,
});

// In-memory controls of the runner contract only, not native receipts.
function control(overrides = {}) {
  const calls = [];
  const session = {};
  const original = [];
  let revision = 0;
  const native = {
    session,
    async enrollContext(epoch, contextId) {
      const reply = { epoch, session, contextId };
      return overrides.enrollContext ? overrides.enrollContext(reply) : reply;
    },
    async enroll(epoch, generation) {
      calls.push(`enroll ${generation.generationId}`);
      original.push(generation);
      revision++;
      return { epoch, session, generation };
    },
    async prepare(epoch, request, signal) {
      const { scope, expectedGenerations } = request;
      calls.push(
        `prepare ${scope} ${expectedGenerations.map((g) => g.generationId).join(",")}`,
      );
      const candidate = {
        session,
        epoch,
        scope,
        token: `snapshot-${++revision}`,
        revision,
        contextId: request.contextId,
        contextIds: request.expectedContexts,
        browserWide: scope === "browser-end",
        inventory: expectedGenerations,
        remaining: emptyWork,
        receiptCheckpoint: { sequence: 0, digest: "0".repeat(64) },
        origins: expectedGenerations.map(originFor),
      };
      return overrides.prepare
        ? overrides.prepare(candidate, request, signal)
        : candidate;
    },
    async verify(epoch, ticket, signal) {
      assert.equal(ticket.epoch, epoch);
      assert.notEqual(ticket.scope, "browser-end");
      assert.equal(Object.isFrozen(ticket), true);
      assert.equal(Object.isFrozen(ticket.receiptCheckpoint), true);
      assert.equal(Object.isFrozen(ticket.generations), true);
      calls.push(`verify ${ticket.scope} ${ticket.token}`);
      const proof = {
        session,
        ticket,
        inventoryRevision: ++revision,
        browserWide: false,
        retiredContextId:
          ticket.scope === "context-close" ? ticket.contextId : null,
        contextIds: ticket.contextIds.filter(
          (id) =>
            !(ticket.scope === "context-close" && id === ticket.contextId),
        ),
        finalizedHead: ticket.receiptCheckpoint,
        extension: {
          base: ticket.receiptCheckpoint,
          checkpoint: ticket.receiptCheckpoint,
          receipts: [],
        },
        settlements: ticket.origins.map((origin) => ({
          ...origin,
          retired: true,
        })),
        transitions: ticket.generations.map((g) => ({
          kind: "disposed",
          contextId: g.contextId,
          generationId: g.generationId,
        })),
        remaining: emptyWork,
      };
      return overrides.verify ? overrides.verify(proof, signal) : proof;
    },
    async authenticateJournal(epoch, claim, signal) {
      const response = {
        session,
        claim,
        authenticated: true,
        epochRecordIdsUnique: true,
        ...(claim.phase === "prepare"
          ? { checkpointAncestryVerified: true }
          : { finalizedHeadVerified: true }),
      };
      assert.equal(epoch, claim.ticket.epoch);
      assert.equal(Object.isFrozen(claim), true);
      return overrides.authenticate
        ? overrides.authenticate(response, signal)
        : response;
    },
  };
  const lane = new CatalogGenerations(native, {
    epoch: "control-epoch",
    timeoutMs: overrides.timeoutMs,
  });
  return { calls, native, lane, original };
}

test("second page and descendants settle before close/reload; disposal can advance inventory", async () => {
  const seen = [];
  const { lane, calls } = control({
    verify(proof) {
      assert.ok(proof.inventoryRevision > proof.ticket.revision);
      seen.push(proof.ticket);
      return proof;
    },
    prepare(candidate, request) {
      assert.deepEqual(request.previousTickets, seen);
      assert.ok(candidate.revision > request.afterRevision);
      return candidate;
    },
  });
  await enrollPrimary(lane);
  await lane.enroll(frame);
  await lane.enroll(worker);
  await lane.enroll(second);
  await lane.replace("p1", async () => {
    calls.push("reload");
    assert.equal(lane.observeDisposal("w1"), true);
    assert.equal(lane.observeDisposal("f1"), true);
    assert.equal(lane.observeDisposal("p1"), true);
    return replacement;
  });
  await lane.close("p2", async () => {
    calls.push("close second");
  });
  await lane.closeContext(contextA, async () => {
    calls.push("close context");
  });
  await lane.prepareBrowserEnd();
  assert.deepEqual(calls, [
    "enroll p1",
    "enroll f1",
    "enroll w1",
    "enroll p2",
    "prepare replace p1,f1,w1",
    "reload",
    "verify replace snapshot-5",
    "enroll p3",
    "prepare close p2",
    "close second",
    "verify close snapshot-8",
    "prepare context-close p3",
    "close context",
    "verify context-close snapshot-10",
    "prepare browser-end ",
  ]);
  assert.deepEqual(seen[0].generations, [primary, frame, worker]);
  assert.deepEqual(seen[2].generations, [replacement]);
});

test("explicit zero-event enrollment is required; no events is not proof", async () => {
  const clean = control();
  await enrollPrimary(clean.lane);
  await clean.lane.closeContext(contextA, noOp);
  const missing = control({
    prepare: (candidate) => ({ ...candidate, origins: [] }),
  });
  await enrollPrimary(missing.lane);
  await assert.rejects(
    missing.lane.closeContext(contextA, noOp),
    /missing native producer coverage/,
  );
  const undiscovered = control({
    prepare(candidate) {
      return { ...candidate, inventory: [primary, frame] };
    },
  });
  await enrollPrimary(undiscovered.lane);
  await assert.rejects(
    undiscovered.lane.closeContext(contextA, noOp),
    /exact inventory/,
  );
  const noGeneration = control();
  await assert.rejects(
    noGeneration.lane.closeContext(contextA, noOp),
    /unknown/,
  );
  const uncovered = control({
    prepare(candidate) {
      return {
        ...candidate,
        origins: [{ ...originFor(primary), producerCovered: false }],
      };
    },
  });
  await enrollPrimary(uncovered.lane);
  await assert.rejects(
    uncovered.lane.closeContext(contextA, noOp),
    /producer population or coverage/,
  );
});

test("duplicate, early and unowned close do not qualify or repeat a close", async () => {
  const { lane } = control();
  let closes = 0;
  const close = async () => {
    closes++;
  };
  await enrollPrimary(lane);
  await assert.rejects(lane.close("p2", close), /unknown/);
  assert.equal(closes, 0);
  await assert.rejects(
    lane.closeContext(contextA, close),
    /previously refused/,
  );
  await assert.rejects(lane.closeContext(contextA, close), /already closing/);
  assert.equal(closes, 1);

  const early = control();
  await enrollPrimary(early.lane);
  assert.equal(early.lane.observeDisposal("p1"), false);
  await assert.rejects(
    early.lane.closeContext(contextA, close),
    /previously refused/,
  );
  assert.equal(closes, 2);

  const duplicate = control();
  await enrollPrimary(duplicate.lane);
  await duplicate.lane.close("p1", close);
  await assert.rejects(duplicate.lane.close("p1", close), /already disposed/);
  assert.equal(closes, 3);
});

test("replacement does not acknowledge the predecessor or reuse its identity", async () => {
  const refusal = control({
    prepare(candidate) {
      return {
        ...candidate,
        origins: [originFor(replacement)],
      };
    },
  });
  await enrollPrimary(refusal.lane);
  let replaced = false;
  await assert.rejects(
    refusal.lane.replace("p1", async () => {
      replaced = true;
      return replacement;
    }),
    /producer population or coverage/,
  );
  assert.equal(replaced, false);
  const reused = control();
  await enrollPrimary(reused.lane);
  await assert.rejects(
    reused.lane.replace("p1", async () => primary),
    /reused generation/,
  );
});

test("disposal must retain the exact original ticket and report only authorized disposal", async () => {
  for (const alter of [
    (proof) => ({
      ...proof,
      ticket: { ...proof.ticket, revision: proof.inventoryRevision },
    }),
    (proof) => ({
      ...proof,
      ticket: { ...proof.ticket, token: "another-token" },
    }),
    (proof) => ({ ...proof, inventoryRevision: proof.ticket.revision - 1 }),
    (proof) => ({
      ...proof,
      transitions: [
        { kind: "disposed", contextId: contextA, generationId: "unrelated" },
      ],
    }),
    (proof) => ({
      ...proof,
      transitions: [
        { kind: "created", contextId: contextA, generationId: "p1" },
      ],
    }),
    (proof) => ({
      ...proof,
      transitions: [
        { kind: "disposed", contextId: contextB, generationId: "p1" },
      ],
    }),
    (proof) => ({ ...proof, transitions: [] }),
    (proof) => ({ ...proof, transitions: Array(1) }),
    (proof) => ({ ...proof, remaining: { ...emptyWork, lateWork: 1 } }),
    (proof) => ({ ...proof, browserWide: true }),
  ]) {
    const { lane } = control({ verify: alter });
    await enrollPrimary(lane);
    let closed = false;
    await assert.rejects(
      lane.closeContext(contextA, async () => {
        closed = true;
      }),
      /original fence/,
    );
    assert.equal(closed, true);
  }
});

test("a stale new fence, missing receipt, and a changed browser session refuse", async () => {
  const stale = control({
    prepare(candidate, request) {
      return request.scope === "context-close"
        ? { ...candidate, revision: request.afterRevision - 1 }
        : candidate;
    },
  });
  await enrollPrimary(stale.lane);
  await stale.lane.close("p1", noOp);
  await assert.rejects(
    stale.lane.closeContext(contextA, noOp),
    /exact inventory/,
  );
  const missing = control({
    verify: (proof) => ({
      ...proof,
      remaining: { ...emptyWork, receipts: 1 },
    }),
  });
  await enrollPrimary(missing.lane);
  await assert.rejects(
    missing.lane.closeContext(contextA, noOp),
    /original fence/,
  );
  const foreign = control({ verify: (proof) => ({ ...proof, session: {} }) });
  await enrollPrimary(foreign.lane);
  await assert.rejects(
    foreign.lane.closeContext(contextA, noOp),
    /browser session changed/,
  );
  const detached = control();
  await enrollPrimary(detached.lane);
  await assert.rejects(
    detached.lane.closeContext(contextA, async () => {
      detached.native.session = {};
    }),
    /session changed/,
  );
  assert.throws(
    () => new CatalogGenerations({}, { epoch: "epoch" }),
    /browser-wide generation provider required/,
  );
});

test("late disposal signals poison the result; native refusal does not prevent product reconciliation", async () => {
  const { lane } = control();
  await enrollPrimary(lane);
  let productCleanup = 0;
  await assert.rejects(
    runOnce(
      () =>
        lane.closeContext(contextA, async () => {
          assert.equal(lane.observeDisposal("p1"), true);
          assert.equal(lane.observeDisposal("p1"), false);
        }),
      async () => {
        productCleanup++;
      },
    ),
    /previously refused/,
  );
  assert.equal(productCleanup, 1);
});

test("timeouts and failures run cleanup once and preserve both errors", async () => {
  let aborted = false;
  const { lane } = control({
    timeoutMs: 10,
    prepare: (_, __, signal) =>
      new Promise(() => {
        signal.addEventListener(
          "abort",
          () => {
            aborted = true;
          },
          { once: true },
        );
      }),
  });
  await enrollPrimary(lane);
  let closes = 0;
  let reconciled = 0;
  await assert.rejects(
    runOnce(
      () =>
        lane.closeContext(contextA, async () => {
          closes++;
        }),
      async () => {
        reconciled++;
        throw Error("operator reconciliation");
      },
    ),
    (error) =>
      error instanceof AggregateError &&
      error.errors.map((item) => item.message).join(",") ===
        "native prepare timed out,operator reconciliation",
  );
  assert.equal(aborted, true);
  assert.equal(closes, 1);
  assert.equal(reconciled, 1);
});

test("the durable prefix cannot disappear or change at the same position on the next fence", async () => {
  for (const second of [
    { sequence: 0, digest: "0".repeat(64) },
    { sequence: 1, digest: "b".repeat(64) },
  ]) {
    const { lane } = control({
      prepare(candidate, request) {
        if (request.scope === "context-close")
          return { ...candidate, receiptCheckpoint: second };
        return {
          ...candidate,
          receiptCheckpoint: { sequence: 1, digest: "a".repeat(64) },
          origins: [
            {
              ...originFor(primary),
              produced: 1,
              dispatched: 1,
              acknowledged: 1,
            },
          ],
        };
      },
    });
    await enrollPrimary(lane);
    await lane.close("p1", noOp);
    await assert.rejects(lane.closeContext(contextA, noOp), /receipt prefix/);
  }
});

test("a failed context close cannot pass even if verify runs with the original ticket", async () => {
  const { calls, lane } = control();
  await enrollPrimary(lane);
  await assert.rejects(
    lane.closeContext(contextA, async () => {
      throw Error("context close failed");
    }),
    /context close failed/,
  );
  assert.equal(calls.at(-1), "verify context-close snapshot-2");
});

test("a context without live generations still needs its own native fence", async () => {
  const clean = control();
  await enrollPrimary(clean.lane);
  await clean.lane.close("p1", noOp);
  await clean.lane.closeContext(contextA, noOp);
  assert.deepEqual(clean.calls.slice(-2), [
    "prepare context-close ",
    "verify context-close snapshot-4",
  ]);
  const reused = control({
    prepare(candidate) {
      return { ...candidate, token: "single-token" };
    },
  });
  await enrollPrimary(reused.lane);
  await reused.lane.close("p1", noOp);
  await assert.rejects(
    reused.lane.closeContext(contextA, noOp),
    /receipt prefix/,
  );
});

// Test-only MAC to distinguish an authenticated claim from a merely
// well-shaped chain. The production launcher never loads this control.
function controlMac(epoch, receipt) {
  return createHmac("sha256", "in-memory qualification test key")
    .update(
      JSON.stringify([
        epoch,
        receipt.sequence,
        receipt.previousDigest,
        receipt.originId,
        receipt.generationId,
        receipt.recordId,
        receipt.commitment,
      ]),
    )
    .digest("hex");
}

const zero = { sequence: 0, digest: "0".repeat(64) };
function journalRows(recordIds, base = zero, binding = {}) {
  let head = base;
  return recordIds.map((recordId) => {
    const row = {
      sequence: head.sequence + 1,
      previousDigest: head.digest,
      originId: "source-p1",
      generationId: "p1",
      recordId,
      commitment: sha(recordId),
      ...binding,
    };
    row.digest = controlMac("control-epoch", row);
    head = row;
    return row;
  });
}
const journalCheckpoint = ({ sequence, digest }) => ({ sequence, digest });
const knownPrefix = journalRows([
  "earlier-1",
  "earlier-2",
  "already-acknowledged",
]);
const knownBase = journalCheckpoint(knownPrefix.at(-1));

function knownFence(candidate) {
  return {
    ...candidate,
    receiptCheckpoint: knownBase,
    origins: [
      {
        ...candidate.origins[0],
        produced: 3,
        dispatched: 2,
        acknowledged: 1,
      },
    ],
    remaining: { ...emptyWork, producers: 1, dispatches: 1, receipts: 1 },
  };
}

function withTeardownReceipts(
  proof,
  recordIds = ["pending-dispatch", "pending-ack", "born-during-close"],
) {
  const { ticket } = proof;
  const base = ticket.receiptCheckpoint;
  const receipts = journalRows(recordIds, base);
  const head = journalCheckpoint(receipts.at(-1) ?? base);
  return {
    ...proof,
    extension: { base, receipts, checkpoint: head },
    finalizedHead: { ...head },
    settlements: [
      {
        ...proof.settlements[0],
        produced: 4,
        dispatched: 4,
        acknowledged: 4,
      },
      ...proof.settlements.slice(1),
    ],
  };
}

// The mock keeps a separate journal. It deliberately checks more than
// the MACs in a submitted suffix: exact ancestry, identity uniqueness
// from genesis, and the actual finalized head.
function retainedJournal(rows, finalized = true) {
  const journal = structuredClone(rows);
  return (response) => {
    const { claim } = response;
    const at = (sequence) => (sequence === 0 ? zero : journal[sequence - 1]);
    const has = (checkpoint) => {
      const row = at(checkpoint.sequence);
      return (
        row &&
        row.sequence === checkpoint.sequence &&
        row.digest === checkpoint.digest
      );
    };
    let head = zero;
    const identities = new Set();
    const available =
      claim.phase === "prepare"
        ? journal.slice(0, claim.ticket.receiptCheckpoint.sequence)
        : journal;
    let authenticated = available.every((row) => {
      if (
        row.sequence !== head.sequence + 1 ||
        row.previousDigest !== head.digest ||
        controlMac(claim.ticket.epoch, row) !== row.digest ||
        identities.has(row.recordId)
      )
        return false;
      identities.add(row.recordId);
      head = row;
      return true;
    });
    if (claim.phase === "prepare") {
      authenticated &&=
        has(claim.previousCheckpoint) && has(claim.ticket.receiptCheckpoint);
    } else {
      const { extension, terminal } = claim;
      assert.equal(Object.isFrozen(extension), true);
      assert.equal(Object.isFrozen(extension.receipts), true);
      authenticated &&=
        finalized &&
        has(extension.base) &&
        has(terminal.finalizedHead) &&
        terminal.finalizedHead.sequence === journal.length &&
        isDeepStrictEqual(
          extension.receipts,
          journal.slice(extension.base.sequence),
        );
    }
    return { ...response, authenticated };
  };
}
const knownJournal = [
  ...knownPrefix,
  ...journalRows(
    ["pending-dispatch", "pending-ack", "born-during-close"],
    knownBase,
  ),
];
const authenticateControl = retainedJournal(knownJournal);

test("known-prefix extension settles pre-fence work and an event born during context close", async () => {
  let closed = false;
  const { lane } = control({
    prepare: knownFence,
    verify(proof) {
      assert.equal(closed, true);
      return withTeardownReceipts(proof);
    },
    authenticate: authenticateControl,
  });
  await enrollPrimary(lane);
  await lane.closeContext(contextA, async () => {
    closed = true;
  });
});

test("the next fence is anchored at the authenticated final prefix, not the prepare position", async () => {
  let finalCheckpoint;
  const { lane } = control({
    prepare(candidate, request) {
      if (request.scope === "close") return knownFence(candidate);
      assert.deepEqual(request.previousCheckpoint, finalCheckpoint);
      return { ...candidate, receiptCheckpoint: knownBase };
    },
    verify(proof) {
      const extended = withTeardownReceipts(proof);
      finalCheckpoint = extended.extension.checkpoint;
      return extended;
    },
    authenticate: authenticateControl,
  });
  await enrollPrimary(lane);
  await lane.close("p1", noOp);
  await assert.rejects(
    lane.closeContext(contextA, noOp),
    /receipt prefix regressed/,
  );
});

test("tampered or unauthenticated teardown extensions refuse without skipping cleanup", async () => {
  const editFirst = (proof, change) => {
    proof.extension.receipts[0] = { ...proof.extension.receipts[0], ...change };
    return proof;
  };
  for (const [tamper, reason] of [
    [
      (p) => {
        p.extension.base = { ...knownBase, digest: "8".repeat(64) };
        return p;
      },
      /original fence/,
    ],
    [(p) => editFirst(p, { previousDigest: "8".repeat(64) }), /gapped/],
    [(p) => editFirst(p, { sequence: 5 }), /gapped/],
    [(p) => editFirst(p, { generationId: "p3" }), /unbound origin/],
    [(p) => editFirst(p, { originId: "unrelated-source" }), /unbound origin/],
    [
      (p) => editFirst(p, { recordId: p.extension.receipts[1].recordId }),
      /duplicated/,
    ],
    [(p) => editFirst(p, { commitment: "9".repeat(64) }), /not authenticated/],
    [
      (p) => {
        p.extension.receipts.pop();
        return p;
      },
      /extension/,
    ],
    [
      (p) => {
        p.extension.checkpoint.digest = "8".repeat(64);
        return p;
      },
      /wrong final/,
    ],
    [
      (p) => {
        p.settlements[0].retired = false;
        return p;
      },
      /not retired/,
    ],
    [
      (p) => {
        p.settlements[0].acknowledged--;
        return p;
      },
      /fully settled/,
    ],
    [
      (p) => {
        p.remaining = { ...emptyWork, droppedHandoffs: 1 };
        return p;
      },
      /original fence/,
    ],
    [
      (p) => {
        p.remaining = { ...emptyWork, unrelatedWork: 1 };
        return p;
      },
      /original fence/,
    ],
  ]) {
    const { lane } = control({
      prepare: knownFence,
      verify: (proof) => tamper(withTeardownReceipts(proof)),
      authenticate: authenticateControl,
    });
    let closed = 0;
    let reconciled = 0;
    await enrollPrimary(lane);
    await assert.rejects(
      runOnce(
        () =>
          lane.closeContext(contextA, async () => {
            closed++;
          }),
        async () => {
          reconciled++;
        },
      ),
      reason,
    );
    assert.equal(closed, 1);
    assert.equal(reconciled, 1);
    await assert.rejects(lane.enroll(replacement), /not available/);
  }
});

test("a journal authenticator cannot return a verdict for a different immutable claim", async () => {
  const { native, lane } = control({
    authenticate: (response) =>
      response.claim.phase === "verify"
        ? { ...response, claim: { ...response.claim } }
        : response,
  });
  assert.throws(
    () =>
      new CatalogGenerations(
        { ...native, authenticateJournal: undefined },
        { epoch: "control-epoch" },
      ),
    /browser-wide generation provider required/,
  );
  await enrollPrimary(lane);
  await assert.rejects(lane.closeContext(contextA, noOp), /not authenticated/);
});

test("a generic authentication acknowledgment is not sufficient", async () => {
  for (const requirement of [
    "epochRecordIdsUnique",
    "checkpointAncestryVerified",
    "finalizedHeadVerified",
  ]) {
    const { lane } = control({
      authenticate: (response) => ({ ...response, [requirement]: undefined }),
    });
    await enrollPrimary(lane);
    await assert.rejects(
      lane.closeContext(contextA, noOp),
      /not authenticated/,
    );
  }
});

test("teardown accounts for each frozen origin, not just the generation total", async () => {
  const cases = [
    (proof) => proof,
    (proof) => ({ ...proof, settlements: proof.settlements.slice(0, 1) }),
    (proof) => {
      const receipt = proof.extension.receipts.at(-1);
      receipt.originId = "second-origin";
      receipt.digest = controlMac(proof.ticket.epoch, receipt);
      proof.extension.checkpoint.digest = receipt.digest;
      proof.finalizedHead.digest = receipt.digest;
      return proof;
    },
  ];
  for (const [index, alter] of cases.entries()) {
    const { lane } = control({
      prepare(candidate) {
        const fence = knownFence(candidate);
        fence.origins.push({
          ...originFor(primary),
          originId: "second-origin",
        });
        return fence;
      },
      verify: (proof) => alter(withTeardownReceipts(proof)),
      authenticate: authenticateControl,
    });
    await enrollPrimary(lane);
    const result = lane.closeContext(contextA, noOp);
    // The first fixture preserves both native origins; the next two change
    // the covered origin set or attribution without changing the generation.
    if (index === 0) await result;
    else await assert.rejects(result, /origin retirement|fully settled/);
  }
});

test("authentication sees the entire prepare and terminal statement before advancing the fence", async () => {
  for (const field of ["none", "prepare remaining", "terminal revision"]) {
    let observedAfterRevision;
    let cleaned = 0;
    const { lane } = control({
      prepare(candidate, request) {
        if (request.scope === "context-close")
          observedAfterRevision = request.afterRevision;
        return field === "prepare remaining"
          ? { ...candidate, remaining: { ...emptyWork, producers: 1 } }
          : candidate;
      },
      verify(proof) {
        return field === "terminal revision"
          ? { ...proof, inventoryRevision: proof.inventoryRevision + 100 }
          : proof;
      },
      authenticate(response) {
        const { claim } = response;
        assert.ok(Object.isFrozen(claim.ticket.remaining));
        if (claim.phase === "prepare") {
          assert.equal(
            claim.ticket.browserWide,
            claim.ticket.scope === "browser-end",
          );
          assert.deepEqual(claim.previousCheckpoint, zero);
          assert.equal(
            claim.afterRevision,
            claim.ticket.scope === "close" ? -1 : 3,
          );
          return {
            ...response,
            authenticated: isDeepStrictEqual(claim.ticket.remaining, emptyWork),
          };
        }
        const expected = {
          scope: claim.ticket.scope,
          contextId: contextA,
          contextIds: claim.ticket.scope === "close" ? [contextA] : [],
          retiredContextId: claim.ticket.scope === "close" ? null : contextA,
          browserWide: false,
          inventoryRevision: claim.ticket.scope === "close" ? 3 : 5,
          transitions:
            claim.ticket.scope === "close"
              ? [{ kind: "disposed", contextId: contextA, generationId: "p1" }]
              : [],
          remaining: emptyWork,
          finalizedHead: zero,
        };
        assert.ok(Object.isFrozen(claim.terminal));
        assert.ok(Object.isFrozen(claim.terminal.remaining));
        assert.ok(Object.isFrozen(claim.terminal.finalizedHead));
        assert.ok(Object.isFrozen(claim.terminal.transitions));
        assert.ok(claim.terminal.transitions.every(Object.isFrozen));
        return {
          ...response,
          authenticated: isDeepStrictEqual(claim.terminal, expected),
        };
      },
    });
    await enrollPrimary(lane);
    const result = lane.close("p1", async () => {
      cleaned++;
    });
    if (field === "none") await result;
    else await assert.rejects(result, /not authenticated/);
    await (field === "none"
      ? lane.closeContext(contextA, noOp)
      : assert.rejects(
          lane.closeContext(contextA, noOp),
          /previously refused/,
        ));
    assert.equal(observedAfterRevision, field === "none" ? 3 : undefined);
    assert.equal(cleaned, 1);
  }
});

test("a valid prefix cannot replace the retained finalized head", async () => {
  for (const substituteHead of [false, true]) {
    const prefix = withTeardownReceipts;
    const { lane } = control({
      prepare: knownFence,
      verify(proof) {
        const full = prefix(proof);
        const rows = full.extension.receipts.slice(0, 2);
        const head = journalCheckpoint(rows.at(-1));
        return {
          ...full,
          extension: { ...full.extension, receipts: rows, checkpoint: head },
          finalizedHead: substituteHead ? head : full.finalizedHead,
          settlements: [
            {
              ...full.settlements[0],
              produced: 3,
              dispatched: 3,
              acknowledged: 3,
            },
          ],
        };
      },
      authenticate: authenticateControl,
    });
    await enrollPrimary(lane);
    await assert.rejects(
      lane.closeContext(contextA, noOp),
      substituteHead ? /not authenticated/ : /finalized journal head/,
    );
  }
  const unfinished = control({
    prepare: knownFence,
    verify: (proof) => withTeardownReceipts(proof),
    authenticate: retainedJournal(knownJournal, false),
  });
  await enrollPrimary(unfinished.lane);
  await assert.rejects(
    unfinished.lane.closeContext(contextA, noOp),
    /not authenticated/,
  );
});

test("re-MACed prefix replay is refused even when it matches the retained byte sequence", async () => {
  const replay = [
    "pending-dispatch",
    "pending-ack",
    knownPrefix.at(-1).recordId,
  ];
  const retained = [...knownPrefix, ...journalRows(replay, knownBase)];
  for (const row of retained)
    assert.equal(controlMac("control-epoch", row), row.digest);
  const { lane } = control({
    prepare: knownFence,
    verify: (proof) => withTeardownReceipts(proof, replay),
    authenticate: retainedJournal(retained),
  });
  await enrollPrimary(lane);
  let closed = false;
  await assert.rejects(
    lane.closeContext(contextA, async () => {
      closed = true;
    }),
    /not authenticated/,
  );
  assert.equal(closed, true);
});

test("the next context can extend the retired context's journal but cannot count its record again", async () => {
  for (const replay of [false, true]) {
    const initial = knownJournal;
    const boundary = journalCheckpoint(initial.at(-1));
    const next = journalRows(
      [replay ? "born-during-close" : "second-close"],
      boundary,
      { originId: "source-p2", generationId: "p2" },
    );
    const end = journalCheckpoint(next.at(-1));
    let closed = false;
    let firstRevision;
    const { lane } = control({
      prepare(candidate, request) {
        if (request.contextId === contextA) return knownFence(candidate);
        if (request.scope === "browser-end") {
          assert.equal(request.contextId, null);
          assert.deepEqual(request.expectedContexts, []);
          assert.deepEqual(
            request.previousTickets.map(({ contextId }) => contextId),
            [contextA, contextB],
          );
          assert.deepEqual(request.previousCheckpoint, end);
          return { ...candidate, receiptCheckpoint: end };
        }
        assert.deepEqual(request.previousCheckpoint, boundary);
        assert.equal(request.afterRevision, firstRevision);
        assert.deepEqual(
          request.previousTickets.map(({ contextId }) => contextId),
          [contextA],
        );
        return replay
          ? { ...candidate, receiptCheckpoint: boundary }
          : {
              ...candidate,
              receiptCheckpoint: end,
              origins: [
                {
                  ...originFor(otherContextPage),
                  produced: 1,
                  dispatched: 1,
                  acknowledged: 1,
                },
              ],
            };
      },
      verify(proof) {
        if (proof.ticket.contextId === contextA) {
          firstRevision = proof.inventoryRevision;
          return withTeardownReceipts(proof);
        }
        return {
          ...proof,
          finalizedHead: end,
          extension: {
            base: proof.ticket.receiptCheckpoint,
            receipts: replay ? next : [],
            checkpoint: end,
          },
          settlements: [
            {
              ...proof.settlements[0],
              produced: 1,
              dispatched: 1,
              acknowledged: 1,
            },
          ],
        };
      },
      authenticate(response) {
        return retainedJournal(
          response.claim.ticket.contextId === contextA
            ? initial
            : [...initial, ...next],
        )(response);
      },
    });
    await enrollPrimary(lane);
    await lane.closeContext(contextA, noOp);
    await lane.enrollContext(contextB);
    await lane.enroll(otherContextPage);
    const result = lane.closeContext(contextB, async () => {
      closed = true;
    });
    if (replay) await assert.rejects(result, /not authenticated/);
    else {
      await result;
      await lane.prepareBrowserEnd();
    }
    assert.equal(closed, true);
  }
});

test("the next context cannot reset to genesis or substitute a higher checkpoint on a valid fork", async () => {
  const fork = journalRows([
    "fork-a",
    "fork-b",
    "fork-c",
    "fork-d",
    "fork-e",
    "fork-f",
    "fork-g",
  ]);
  const different = journalCheckpoint(fork.at(-1));
  assert.ok(different.sequence > knownJournal.length);
  for (const reset of [true, false]) {
    const { lane } = control({
      prepare(candidate, request) {
        if (request.contextId === contextA) return knownFence(candidate);
        return reset
          ? candidate
          : { ...candidate, receiptCheckpoint: different };
      },
      verify: (proof) => withTeardownReceipts(proof),
      authenticate(response) {
        return response.claim.ticket.contextId === contextA
          ? authenticateControl(response)
          : retainedJournal(fork)(response);
      },
    });
    await enrollPrimary(lane);
    await lane.closeContext(contextA, noOp);
    await lane.enrollContext(contextB);
    await lane.enroll(otherContextPage);
    await assert.rejects(
      lane.closeContext(contextB, noOp),
      reset ? /receipt prefix regressed/ : /not authenticated/,
    );
    await assert.rejects(lane.prepareBrowserEnd(), /not available/);
  }
});

test("a later context cannot authorize retirement with the earlier context's ticket", async () => {
  for (const phase of ["prepare", "verify"]) {
    let firstTicket;
    const { lane } = control({
      prepare(reply) {
        return reply.contextId === contextB && phase === "prepare"
          ? { ...reply, token: firstTicket.token }
          : reply;
      },
      verify(proof) {
        if (proof.ticket.contextId === contextA) firstTicket = proof.ticket;
        return proof.ticket.contextId === contextB && phase === "verify"
          ? { ...proof, ticket: firstTicket }
          : proof;
      },
    });
    await lane.enrollContext(contextA);
    await lane.closeContext(contextA, noOp);
    await lane.enrollContext(contextB);
    let closes = 0;
    await assert.rejects(
      lane.closeContext(contextB, async () => {
        closes++;
      }),
      phase === "prepare" ? /exact inventory/ : /original fence/,
    );
    assert.equal(closes, 1);
    await assert.rejects(lane.prepareBrowserEnd(), /not available/);
  }
});

test("scoped close refuses surviving-origin rows in the global journal and never filters them", async () => {
  for (const filter of [false, true]) {
    const { lane } = control({
      prepare: knownFence,
      verify(proof) {
        const full = withTeardownReceipts(proof);
        const [unrelated] = journalRows(
          ["surviving-page"],
          full.finalizedHead,
          { originId: "source-p2", generationId: "p2" },
        );
        const finalizedHead = journalCheckpoint(unrelated);
        return {
          ...full,
          finalizedHead,
          extension: filter
            ? full.extension
            : {
                ...full.extension,
                receipts: [...full.extension.receipts, unrelated],
                checkpoint: finalizedHead,
              },
        };
      },
      authenticate: authenticateControl,
    });
    await enrollPrimary(lane);
    await lane.enroll(second);
    await assert.rejects(
      lane.close("p1", noOp),
      filter ? /finalized journal head/ : /unbound origin/,
    );
    await assert.rejects(
      lane.closeContext(contextA, noOp),
      /previously refused/,
    );
  }
});

test("context close freezes all and only its members; the other context stays live", async () => {
  const seen = [];
  const retired = [];
  const { lane, native, calls } = control({
    authenticate(response) {
      const { claim } = response;
      assert.equal(Object.isFrozen(claim.ticket.contextIds), true);
      if (claim.phase === "prepare") seen.push(claim.ticket);
      else {
        assert.equal(Object.isFrozen(claim.terminal.contextIds), true);
        retired.push(claim.terminal.retiredContextId);
      }
      return response;
    },
  });
  await enrollPrimary(lane);
  await lane.enroll(frame);
  await lane.enroll(worker);
  await lane.enrollContext(contextB);
  await lane.enroll(otherContextPage);
  await lane.closeContext(contextA, async () => {
    assert.equal(lane.observeContextDisposal(contextA), true);
    assert.equal(lane.observeDisposal("w1"), true);
  });
  await lane.closeContext(contextB, noOp);
  const prepared = await lane.prepareBrowserEnd();
  assert.deepEqual(
    seen.map((t) => [t.scope, t.contextId, t.contextIds, t.generations]),
    [
      [
        "context-close",
        contextA,
        [contextA, contextB],
        [primary, frame, worker],
      ],
      ["context-close", contextB, [contextB], [otherContextPage]],
      ["browser-end", null, [], []],
    ],
  );
  assert.deepEqual(retired, [contextA, contextB]);
  assert.ok(Object.isFrozen(prepared));
  assert.equal(prepared.session, native.session);
  assert.equal(prepared.ticket, seen.at(-1));
  assert.ok(!calls.some((call) => call.startsWith("verify browser-end")));
  await assert.rejects(lane.prepareBrowserEnd(), /not available/);
  await assert.rejects(lane.enrollContext("native-C"), /not available/);
});

test("context admission and same-context topology are mandatory before descendants or replacement", async () => {
  const absent = control();
  await assert.rejects(
    absent.lane.enroll(primary),
    /no live enrolled native context/,
  );
  await assert.rejects(absent.lane.enrollContext(contextA), /not available/);
  const falseEcho = control({
    enrollContext: (reply) => ({ ...reply, contextId: contextB }),
  });
  await assert.rejects(falseEcho.lane.enrollContext(contextA), /not enrolled/);
  const foreignSession = control({
    enrollContext: (reply) => ({ ...reply, session: {} }),
  });
  await assert.rejects(
    foreignSession.lane.enrollContext(contextA),
    /browser session changed/,
  );
  // Only native ownership and history can detect these on an opaque ID.
  for (const reason of ["default", "foreign", "previously active"]) {
    const { lane } = control({
      enrollContext() {
        throw Error(`native context refused: ${reason}`);
      },
    });
    await assert.rejects(
      lane.enrollContext(contextA),
      /native context refused/,
    );
    await assert.rejects(lane.enroll(primary), /not available/);
  }
  for (const child of [frame, worker]) {
    const { lane } = control();
    await enrollPrimary(lane);
    if (child === worker) await lane.enroll(frame);
    await lane.enrollContext(contextB);
    await assert.rejects(
      lane.enroll({ ...child, contextId: contextB }),
      /missing parent/,
    );
    await assert.rejects(lane.prepareBrowserEnd(), /not available/);
  }
  const { lane } = control();
  await enrollPrimary(lane);
  await lane.enrollContext(contextB);
  await assert.rejects(
    lane.replace("p1", async () => ({ ...replacement, contextId: contextB })),
    /changed target ownership/,
  );
});

test("unknown, duplicate or lost contexts refuse without repeating ordinary close", async () => {
  let closes = 0;
  const close = async () => {
    closes++;
  };
  const unknown = control();
  await unknown.lane.enrollContext(contextA);
  await assert.rejects(unknown.lane.closeContext(contextB, close), /unknown/);
  assert.equal(closes, 0);
  await assert.rejects(
    unknown.lane.closeContext(contextA, close),
    /previously refused/,
  );
  assert.equal(closes, 1);
  for (const operation of ["re-enroll", "re-close", "descendant"]) {
    const { lane } = control();
    await lane.enrollContext(contextA);
    await lane.closeContext(contextA, close);
    if (operation === "re-enroll")
      await assert.rejects(lane.enrollContext(contextA), /reused/);
    if (operation === "re-close")
      await assert.rejects(lane.closeContext(contextA, close), /retired/);
    if (operation === "descendant")
      await assert.rejects(
        lane.enroll(primary),
        /no live enrolled native context/,
      );
    await assert.rejects(lane.prepareBrowserEnd(), /not available/);
  }
  assert.equal(closes, 4);
  for (const lost of [contextA, contextB]) {
    const { lane } = control();
    await lane.enrollContext(contextA);
    assert.equal(lane.observeContextDisposal(lost), false);
    await assert.rejects(
      lane.closeContext(contextA, close),
      /previously refused/,
    );
  }
  assert.equal(closes, 6);
});

test("late admission or loss outside the selected context poisons an active context close", async () => {
  for (const event of [
    "late-context",
    "late-generation",
    "survivor-lost",
    "unknown-lost",
  ]) {
    const { lane } = control();
    await enrollPrimary(lane);
    await lane.enrollContext(contextB);
    await assert.rejects(
      lane.closeContext(contextA, async () => {
        if (event === "late-context")
          await assert.rejects(lane.enrollContext("native-C"), /not available/);
        if (event === "late-generation")
          await assert.rejects(lane.enroll(frame), /not available/);
        if (event === "survivor-lost")
          assert.equal(lane.observeContextDisposal(contextB), false);
        if (event === "unknown-lost")
          assert.equal(lane.observeContextDisposal("native-C"), false);
      }),
      /previously refused/,
    );
    await assert.rejects(
      lane.closeContext(contextB, noOp),
      /previously refused/,
    );
    await assert.rejects(lane.prepareBrowserEnd(), /not available/);
  }
});

test("an empty context needs exact prepare and retirement proof, not an empty journal", async () => {
  const good = control();
  await good.lane.enrollContext(contextA);
  await good.lane.closeContext(contextA, noOp);
  await good.lane.prepareBrowserEnd();
  for (const overrides of [
    { prepare: (reply) => ({ ...reply, contextId: contextB }) },
    { prepare: (reply) => ({ ...reply, contextIds: [] }) },
    { prepare: (reply) => ({ ...reply, contextIds: [contextA, contextB] }) },
    { verify: (proof) => ({ ...proof, retiredContextId: contextB }) },
    { verify: (proof) => ({ ...proof, retiredContextId: undefined }) },
    { verify: (proof) => ({ ...proof, contextIds: [contextB] }) },
    {
      verify: (proof) => ({
        ...proof,
        ticket: { ...proof.ticket, contextId: contextB },
      }),
    },
  ]) {
    const { lane } = control(overrides);
    let closed = 0;
    await lane.enrollContext(contextA);
    await assert.rejects(
      lane.closeContext(contextA, async () => {
        closed++;
      }),
      /exact inventory|original fence/,
    );
    assert.equal(closed, 1);
    await assert.rejects(lane.prepareBrowserEnd(), /not available/);
  }
});

test("browser-end prepare refuses live contexts, foreign sessions and incomplete browser-wide coverage", async () => {
  const live = control();
  await live.lane.enrollContext(contextA);
  await live.lane.closeContext(contextA, noOp);
  await live.lane.enrollContext(contextB);
  await assert.rejects(
    live.lane.prepareBrowserEnd(),
    /every native context to be retired/,
  );
  await assert.rejects(
    live.lane.closeContext(contextB, noOp),
    /previously refused/,
  );
  assert.ok(live.calls.every((call) => !call.includes("browser-end")));

  for (const overrides of [
    {
      prepare: (reply) =>
        reply.scope === "browser-end"
          ? { ...reply, contextIds: [contextB] }
          : reply,
    },
    {
      prepare: (reply) =>
        reply.scope === "browser-end"
          ? { ...reply, browserWide: false }
          : reply,
    },
    {
      prepare: (reply) =>
        reply.scope === "browser-end" ? { ...reply, session: {} } : reply,
    },
    {
      prepare: (reply) =>
        reply.scope === "browser-end"
          ? { ...reply, remaining: { ...emptyWork, producers: 1 } }
          : reply,
    },
    {
      authenticate: (reply) =>
        reply.claim.ticket.scope === "browser-end"
          ? { ...reply, checkpointAncestryVerified: false }
          : reply,
    },
  ]) {
    const { lane } = control(overrides);
    await lane.enrollContext(contextA);
    await lane.closeContext(contextA, noOp);
    await assert.rejects(
      lane.prepareBrowserEnd(),
      /exact inventory|browser session changed|unrepresented producers|not authenticated/,
    );
    await assert.rejects(lane.enrollContext(contextB), /not available/);
  }
});

test("context retirement refuses a surviving context's global journal row or its filtered prefix", async () => {
  const surviving = journalRows(
    ["surviving-context"],
    journalCheckpoint(knownJournal.at(-1)),
    { originId: "source-p2", generationId: "p2" },
  );
  const actual = [...knownJournal, ...surviving];
  for (const filtered of [false, true]) {
    const { lane } = control({
      prepare: knownFence,
      verify(proof) {
        const full = withTeardownReceipts(proof);
        if (filtered) return full; // dishonest prefix and finalized head
        const head = journalCheckpoint(actual.at(-1));
        return {
          ...full,
          finalizedHead: head,
          extension: {
            base: knownBase,
            checkpoint: head,
            receipts: actual.slice(knownBase.sequence),
          },
        };
      },
      authenticate: retainedJournal(actual),
    });
    await enrollPrimary(lane);
    await lane.enrollContext(contextB);
    await lane.enroll(otherContextPage);
    await assert.rejects(
      lane.closeContext(contextA, noOp),
      filtered ? /not authenticated/ : /unbound origin/,
    );
    await assert.rejects(
      lane.closeContext(contextB, noOp),
      /previously refused/,
    );
  }
});
