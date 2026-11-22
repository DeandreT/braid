import assert from "node:assert/strict";
import { test } from "node:test";
import {
  checkWorkerObservation,
  origin,
  ownerUrl,
  workletModule,
  moduleWorker,
} from "./worker-oracle-expectations.mjs";

const lane = 12;
const mark = (name, ts, tid = 1) => ({
  name,
  ts,
  pid: 100,
  tid,
  ph: "I",
  cat: "blink.user_timing",
  args: { data: { navigationId: "A".repeat(32) } },
});
const slice = (name, ts, tid, args = {}) => ({
  name,
  ts,
  dur: 3,
  tid,
  args,
  pid: 100,
  ph: "X",
});
const init = (ts) => slice("WorkerThread::InitializeWorkerContext", ts, lane);
const moduleStart = "DedicatedWorkerGlobalScope::FetchAndRunModuleScript";

function input(mode) {
  return {
    mode,
    allowed: false,
    hits: mode === "module" ? [ownerUrl, moduleWorker] : [ownerUrl],
    execution: mode === "module" ? "error" : "AbortError",
    logs:
      mode === "module"
        ? []
        : [
            `${workletModule} violates Content Security Policy: script-src 'none'`,
          ],
    events: [
      {
        trusted: true,
        directive: "img-src",
        disposition: "enforce",
        blocked: `${origin}/owner-pixel`,
        document: ownerUrl,
        original: true,
      },
    ],
    trace: [
      mark("braid-original-owner", 1),
      init(4),
      ...(mode === "module"
        ? [slice(moduleStart, 8, lane, { module_url_record: moduleWorker })]
        : [
            mark("braid-worklet-add-module", 2),
            mark("braid-worklet-return", 12),
          ]),
      slice("EventDispatch", 13, 1, {
        data: { type: "securitypolicyviolation" },
      }),
      mark("braid-owner-csp:img-src", 14),
      mark("braid-owner-snapshot", 18),
    ],
  };
}

test("startup error alone is not evidence of a module graph policy check", () => {
  const evidence = input("module");
  assert.equal(checkWorkerObservation(evidence).initialModuleGraph, true);
  evidence.hits.pop();
  assert.throws(() => checkWorkerObservation(evidence));
  evidence.hits.push(moduleWorker);
  evidence.trace = evidence.trace.filter((event) => event.name !== moduleStart);
  assert.throws(() => checkWorkerObservation(evidence), /initial module graph/);
});

test("worklet rejection and diagnostic do not earn forwarded DOM credit", () => {
  const evidence = input("worklet");
  assert.match(
    checkWorkerObservation(evidence).forwarding,
    /refuse worklet DOM credit/,
  );
  evidence.events.push({ directive: "script-src-elem" });
  assert.throws(
    () => checkWorkerObservation(evidence),
    /not a forwarded violation/,
  );
  evidence.events.pop();
  evidence.logs = [];
  assert.throws(() => checkWorkerObservation(evidence));
});

test("a successor cannot substitute for the original owner control", () => {
  const evidence = input("worklet");
  const end = evidence.trace.at(-1);
  end.args.data.navigationId = "B".repeat(32);
  assert.throws(() => checkWorkerObservation(evidence));
});
