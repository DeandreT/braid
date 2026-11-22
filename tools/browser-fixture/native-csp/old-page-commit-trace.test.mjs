import assert from "node:assert/strict";
import { test } from "node:test";
import {
  proveNavigation,
  synchronousSlices,
  verifyNativeSource,
} from "./old-page-commit-trace.mjs";

const commit = "WebViewImpl::SetPageLifecycleStateFromNewPageCommit";
const unload = "Document::DispatchUnloadEvents";
const ordinary = "WebViewImpl::SetPageLifecycleState";
const marker = "braid-test-marker";
const slice = (name, ts, dur, extra = {}) => ({
  ph: "X",
  name,
  pid: 20,
  tid: 21,
  ts,
  dur,
  ...extra,
});
const mark = (name, ts, extra = {}) => ({
  ...slice(`${marker}:${name}`, ts, 0, extra),
  ph: "R",
  cat: "blink.user_timing",
});

function fixture(source = commit) {
  return [
    mark("original", 1),
    slice("EventDispatch", 1, 2, {
      args: { data: { type: "visibilitychange" } },
    }),
    mark("visibility:synthetic:visible", 2),
    slice(source, 3, 10),
    slice("EventDispatch", 4, 4, {
      args: { data: { type: "visibilitychange" } },
    }),
    mark("visibility:trusted:hidden", 5),
    mark("replacement", 20),
  ];
}

test("nested native dispatch certifies reuse of the old renderer and thread", () => {
  assert.equal(proveNavigation(fixture(), marker, "commit").sameRenderer, true);
  assert.equal(
    proveNavigation(fixture(unload), marker, "unload").source,
    unload,
  );
});

test("a commit slice elsewhere cannot certify direct unload or ordinary delivery", () => {
  for (const source of [unload, ordinary]) {
    assert.throws(
      () =>
        proveNavigation(
          [...fixture(source), slice(commit, 21, 10)],
          marker,
          "commit",
        ),
      /one expected source slice/,
    );
  }
  assert.throws(
    () =>
      proveNavigation([...fixture(), slice(unload, 4, 2)], marker, "commit"),
    /one expected source slice/,
  );
});

test("duplicate, spoofed or unmatched marker and dispatch do not count", () => {
  const original = fixture();
  assert.throws(() =>
    proveNavigation([...original, original[5]], marker, "commit"),
  );
  assert.throws(() =>
    proveNavigation(
      original.toSpliced(5, 1, { ...original[5], cat: "navigation" }),
      marker,
      "commit",
    ),
  );
  assert.throws(() =>
    proveNavigation(original.toSpliced(4, 1), marker, "commit"),
  );
  assert.throws(
    () =>
      proveNavigation(
        original.toSpliced(6, 1, { ...original[6], pid: 30 }),
        marker,
        "commit",
      ),
    /old renderer was not reused/,
  );
});

test("the innermost B/E trace scope closes first", () => {
  const slices = synchronousSlices([
    { ph: "B", pid: 1, tid: 1, name: "a", ts: 10 },
    { ph: "B", pid: 1, tid: 1, name: "b", ts: 11 },
    { ph: "E", pid: 1, tid: 1, ts: 12 },
    { ph: "E", pid: 1, tid: 1, ts: 13 },
  ]);
  assert.deepEqual(
    slices.map(({ name, end }) => [name, end]),
    [
      ["b", 12],
      ["a", 13],
    ],
  );
});

test("owner hook must identify the same original, not a retired predecessor or replacement", () => {
  const id = "0123456789ABCDEF0123456789ABCDEF";
  const old = "FEDCBA9876543210FEDCBA9876543210";
  const record = (source, token) =>
    `BRAID_FIXTURE_VIS source=${source} original=${token}`;
  const commitOnly = record(2, id);
  verifyNativeSource(`${record(3, old)}\n${commitOnly}`, id, "commit");
  assert.throws(() => verifyNativeSource(record(2, old), id, "commit"));
  assert.throws(() => verifyNativeSource(record(3, id), id, "commit"));
  assert.throws(() =>
    verifyNativeSource(`${commitOnly}\n${record(3, id)}`, id, "commit"),
  );
});
