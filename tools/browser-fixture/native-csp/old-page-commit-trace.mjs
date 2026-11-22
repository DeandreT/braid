// Trace evidence for one controlled navigation, not CSP receipt evidence.
import assert from "node:assert/strict";

const sourceNames = Object.freeze({
  commit: "WebViewImpl::SetPageLifecycleStateFromNewPageCommit",
  unload: "Document::DispatchUnloadEvents",
  ordinary: "WebViewImpl::SetPageLifecycleState",
});
const eventName = "EventDispatch";
const lane = ({ pid, tid }) => `${pid}:${tid}`;

// Chromium's JSON export can emit completed slices or synchronous B/E pairs.
// Keep the per-thread stack (including unlabeled/uninteresting slices): an
// unlabeled E closes the innermost B, not the nearest interesting B.
export function synchronousSlices(events) {
  const stacks = new Map();
  const slices = [];
  for (const event of events
    .filter(({ ph }) => ["B", "E", "X"].includes(ph))
    .toSorted((a, b) => a.ts - b.ts)) {
    assert(
      Number.isFinite(event.ts) &&
        Number.isSafeInteger(event.pid) &&
        Number.isSafeInteger(event.tid),
      "invalid synchronous trace event",
    );
    const key = lane(event);
    if (event.ph === "X") {
      assert(
        Number.isFinite(event.dur) && event.dur >= 0,
        "bad slice duration",
      );
      slices.push({ ...event, end: event.ts + event.dur });
    } else if (event.ph === "B") {
      if (!stacks.has(key)) stacks.set(key, []);
      stacks.get(key).push(event);
    } else {
      const begin = stacks.get(key)?.pop();
      // Unrelated scopes may straddle Tracing.start(). They're not evidence.
      if (!begin) continue;
      assert(!event.name || begin.name === event.name, "unmatched trace end");
      slices.push({ ...begin, end: event.ts });
    }
  }
  return slices;
}

export function proveNavigation(events, marker, source) {
  assert(source === "commit" || source === "unload");
  const oneMark = (suffix) => {
    const matches = events.filter(
      (e) =>
        e.name === `${marker}:${suffix}` &&
        e.cat?.split(",").includes("blink.user_timing") &&
        ["R", "I", "i"].includes(e.ph),
    );
    assert.equal(matches.length, 1, `expected exactly one ${suffix} mark`);
    const [mark] = matches;
    assert(
      Number.isFinite(mark.ts) &&
        Number.isSafeInteger(mark.pid) &&
        Number.isSafeInteger(mark.tid),
      "invalid renderer mark",
    );
    return mark;
  };
  const old = oneMark("original");
  const synthetic = oneMark("visibility:synthetic:visible");
  const hidden = oneMark("visibility:trusted:hidden");
  const replacement = oneMark("replacement");
  assert(old.ts <= synthetic.ts && synthetic.ts < hidden.ts);
  assert(hidden.ts < replacement.ts, "replacement did not follow original");
  assert.equal(lane(hidden), lane(old), "wrong original dispatch thread");
  assert.equal(lane(synthetic), lane(old), "wrong synthetic control thread");
  if (source === "commit") {
    assert.equal(lane(replacement), lane(old), "old renderer was not reused");
  }

  const slices = synchronousSlices(events);
  const contains = (outer, inner) =>
    lane(outer) === lane(inner) &&
    outer.ts <= inner.ts &&
    (inner.end === undefined ? inner.ts < outer.end : inner.end <= outer.end);
  const relevant = slices.filter(
    (s) => Object.values(sourceNames).includes(s.name) && contains(s, hidden),
  );
  assert.deepEqual(
    relevant.map(({ name }) => name),
    [sourceNames[source]],
    "trusted delivery must be inside the one expected source slice",
  );
  const [lifecycle] = relevant;
  assert(
    synthetic.ts < lifecycle.ts && lifecycle.end <= replacement.ts,
    "native lifecycle source must lie between original and replacement",
  );
  const dispatch = slices.filter(
    (s) =>
      s.name === eventName &&
      s.args?.data?.type === "visibilitychange" &&
      contains(lifecycle, s) &&
      contains(s, hidden),
  );
  assert.equal(dispatch.length, 1, "missing native visibility EventDispatch");

  const syntheticDispatch = slices.filter(
    (s) =>
      s.name === eventName &&
      s.args?.data?.type === "visibilitychange" &&
      contains(s, synthetic),
  );
  assert.equal(
    syntheticDispatch.length,
    1,
    "missing synthetic control dispatch",
  );
  assert(syntheticDispatch[0].end <= lifecycle.ts);

  return {
    source: lifecycle.name,
    sameRenderer: old.pid === replacement.pid,
    oldRenderer: old.pid,
    replacementRenderer: replacement.pid,
    oldThread: old.tid,
    originalMarkAt: old.ts,
    sourceStart: lifecycle.ts,
    dispatchStart: dispatch[0].ts,
    originalListenerAt: hidden.ts,
    dispatchEnd: dispatch[0].end,
    sourceEnd: lifecycle.end,
    replacementMarkAt: replacement.ts,
  };
}

export function verifyNativeSource(log, original, source) {
  assert.match(original, /^[A-F0-9]{32}$/);
  assert(source === "commit" || source === "unload");
  const hits = [
    ...log.matchAll(
      /BRAID_FIXTURE_VIS source=(\d+) original=([A-F0-9]{32})\b/g,
    ),
  ]
    .filter(([, , token]) => token === original)
    .map(([, kind]) => Number(kind));
  assert.deepEqual(
    hits,
    [source === "commit" ? 2 : 3],
    "wrong original native source",
  );
}
