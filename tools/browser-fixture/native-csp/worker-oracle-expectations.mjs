import assert from "node:assert/strict";

export const origin = "https://worker-fixture.invalid";
export const ownerUrl = `${origin}/owner`;
export const workletModule = `${origin}/paint-module.js`;
export const moduleWorker = `${origin}/module-worker.js`;
export const dependency = `${origin}/module-dependency.js`;

const onSameLane = (a, b) => a.pid === b.pid && a.tid === b.tid;
const initialization = "WorkerThread::InitializeWorkerContext";
const moduleStart = "DedicatedWorkerGlobalScope::FetchAndRunModuleScript";

export function checkWorkerObservation(observation) {
  const { mode, allowed, hits, logs, execution, events, trace } = observation;
  const find = (name, predicate = () => true) =>
    trace.filter((event) => event.name === name && predicate(event));
  const only = (matches, name) => {
    assert.equal(matches.length, 1, `missing or ambiguous ${name}`);
    return matches[0];
  };
  const mark = (name) =>
    only(
      find(
        name,
        (e) => ["I", "i", "R"].includes(e.ph) && e.cat === "blink.user_timing",
      ),
      name,
    );
  const parent = mark("braid-original-owner");
  const end = mark("braid-owner-snapshot");
  const image = mark("braid-owner-csp:img-src");
  assert(onSameLane(parent, image) && onSameLane(parent, end));
  assert(parent.ts < image.ts && image.ts < end.ts);
  const navigationId = parent.args?.data?.navigationId;
  assert.match(navigationId ?? "", /^[A-F0-9]{32}$/);
  assert.equal(image.args?.data?.navigationId, navigationId);
  assert.equal(end.args?.data?.navigationId, navigationId);
  assert.deepEqual(
    events,
    [
      {
        trusted: true,
        directive: "img-src",
        disposition: "enforce",
        blocked: `${origin}/owner-pixel`,
        document: ownerUrl,
        original: true,
      },
    ],
    "expected only the owner image event, not a forwarded violation",
  );
  const imageSlice = only(
    find(
      "EventDispatch",
      (e) =>
        e.ph === "X" &&
        onSameLane(e, image) &&
        e.args?.data?.type === "securitypolicyviolation" &&
        e.ts <= image.ts &&
        image.ts < e.ts + e.dur,
    ),
    "owner native EventDispatch containing the image listener",
  );
  const initialized = find(
    initialization,
    (e) =>
      e.ph === "X" &&
      !onSameLane(e, parent) &&
      parent.ts < e.ts &&
      e.ts + e.dur < image.ts,
  );
  const summary = {
    mode,
    allowed,
    ownerCspEvents: events.map((e) => e.directive),
  };
  if (mode === "classic") {
    assert.deepEqual(hits, [
      ownerUrl,
      `${origin}/classic-old.js`,
      `${origin}/classic-new.js`,
    ]);
    assert.deepEqual(
      execution.map(({ fixtureId }) => fixtureId),
      ["old", "new"],
    );
    assert.notEqual(execution[0].targetId, execution[1].targetId);
    let priorViolation = parent.ts;
    for (const {
      fixtureId,
      targetId,
      messages,
      targetDestroyed,
    } of execution) {
      assert.equal(targetDestroyed, true);
      assert.match(targetId, /^[A-F0-9]{32}$/);
      assert.deepEqual(messages, [
        { kind: "ready", fixtureId },
        {
          kind: "violation",
          fixtureId,
          trusted: true,
          directive: "connect-src",
          disposition: "enforce",
          blocked: `${origin}/blocked-${fixtureId}`,
          document: `${origin}/classic-${fixtureId}.js`,
          originalWorker: true,
        },
      ]);
      const started = mark(`braid-worker-ready:${fixtureId}`);
      const violation = mark(`braid-worker-csp:${fixtureId}`);
      assert(!onSameLane(parent, started) && onSameLane(started, violation));
      assert(
        priorViolation < started.ts &&
          started.ts < violation.ts &&
          violation.ts < image.ts,
      );
      only(
        initialized.filter(
          (e) =>
            onSameLane(e, started) &&
            priorViolation < e.ts &&
            e.ts + e.dur <= started.ts,
        ),
        `${fixtureId} worker initialization`,
      );
      priorViolation = violation.ts;
    }
    return {
      ...summary,
      workers: execution.map(({ fixtureId, targetId }) => ({
        fixtureId,
        targetId,
      })),
    };
  }
  if (mode === "module") {
    assert.equal(execution, allowed ? "module-ran" : "error");
    assert.deepEqual(hits, [
      ownerUrl,
      moduleWorker,
      ...(allowed ? [dependency] : []),
    ]);
    const fetch = only(
      find(
        moduleStart,
        (e) =>
          e.ph === "X" &&
          e.args?.module_url_record === moduleWorker &&
          parent.ts < e.ts &&
          e.ts + e.dur < image.ts,
      ),
      "dedicated worker's initial module graph",
    );
    only(
      initialized.filter(
        (e) => onSameLane(e, fetch) && e.ts + e.dur <= fetch.ts,
      ),
      "module worker initialization",
    );
    return { ...summary, initialModuleGraph: true, result: execution };
  }
  assert.equal(mode, "worklet");
  assert.equal(execution, allowed ? "loaded" : "AbortError");
  assert.deepEqual(hits, [ownerUrl, ...(allowed ? [workletModule] : [])]);
  const begin = mark("braid-worklet-add-module");
  const returned = mark("braid-worklet-return");
  assert(onSameLane(parent, begin) && onSameLane(parent, returned));
  assert(
    parent.ts < begin.ts &&
      begin.ts < returned.ts &&
      returned.ts <= imageSlice.ts,
  );
  assert.equal(find(moduleStart).length, 0, "not a dedicated worker case");
  const policyMessages = logs.filter(
    (text) =>
      text.includes(workletModule) &&
      text.includes("Content Security Policy") &&
      text.includes("script-src 'none'"),
  );
  assert.equal(policyMessages.length > 0, !allowed);
  return {
    ...summary,
    result: execution,
    threadedWorkletInitializations: initialized.filter(
      (e) => begin.ts < e.ts && e.ts + e.dur < returned.ts,
    ).length,
    forwarding: "not observed; refuse worklet DOM credit",
  };
}
