import { strict as assert } from "node:assert";
import { test } from "node:test";
import { cspMethodHash, proveCspOrigin } from "./browser-origin-csp-trace.mjs";

const n = "fixture-marker";
const prove = (trace, scenario) => proveCspOrigin(trace, n, scenario, 42);
const document = "A".repeat(32);
const mark = (suffix, ts, pid = 101, navigationId = document) => ({
  ts,
  pid,
  name: `${n}:${suffix}`,
  tid: 1,
  ph: "I",
  cat: "blink.user_timing",
  args: { data: { navigationId } },
});
const slice = (name, ts, dur, cat, args = {}, pid = 101) => ({
  name,
  ts,
  dur,
  cat,
  args,
  pid,
  tid: 1,
  ph: "X",
});
const dispatch = (ts) =>
  slice("EventDispatch", ts, 2, "devtools.timeline", {
    data: { type: "securitypolicyviolation" },
  });
const receive = (pid = 101, ipc_hash = cspMethodHash) =>
  slice(
    "Receive mojo message",
    5,
    1,
    "toplevel,mojom",
    {
      chrome_mojo_event_info: {
        mojo_interface_tag: "blink.mojom.LocalFrame",
        ipc_hash,
      },
    },
    pid,
  );
const navigate = (name) =>
  slice(`NavigationRequest::${name}`, 4, 7, "navigation", {}, 42);
const base = () => [
  mark("original", 0),
  mark("peer", 2, 102, "B".repeat(32)),
  dispatch(12),
  mark("event:img-src:true", 13),
  mark("end", 16),
];
const redirect = () => [
  ...base(),
  receive(),
  navigate("OnRequestRedirected"),
  dispatch(7),
  mark("event:frame-src:true", 8),
];

test("recognizes the pinned IPC hash and the browser, local and audit-only paths", () => {
  assert.equal(cspMethodHash, 0x4593d00c);
  assert.equal(prove(base(), "blink").cspMojoReceives, 0);
  assert.equal(prove(redirect(), "redirect").browserCspDispatch, 7);
  const auditOnly = [...base(), receive(), navigate("OnRequestFailedInternal")];
  assert.equal(prove(auditOnly, "ancestors").cspDomDispatches, 1);
  assert.throws(
    () => proveCspOrigin(redirect(), n, "redirect", 999),
    /browser navigation slice/,
  );
});

test("a blocked navigation or generic Mojo message cannot substitute for CSP IPC", () => {
  const withoutMojo = redirect().filter(
    (event) => event.name !== "Receive mojo message",
  );
  assert.throws(() => prove(withoutMojo, "redirect"), /CSP Mojo receive/);
  assert.throws(
    () => prove([...withoutMojo, receive(101, 335)], "redirect"),
    /CSP Mojo receive/,
  );
  assert.throws(
    () => prove([...withoutMojo, receive(102)], "redirect"),
    /different renderer/,
  );
});

test("requires a distinct child process and trusted dispatch in the same original navigation", () => {
  const withoutChild = redirect().filter((event) => event.name !== `${n}:peer`);
  assert.throws(
    () =>
      prove(
        [...withoutChild, mark("peer", 2, 101, "B".repeat(32))],
        "redirect",
      ),
    /not out of process/,
  );
  const withoutEvent = redirect().filter(
    (event) => event.name !== `${n}:event:frame-src:true`,
  );
  assert.throws(
    () =>
      prove([...withoutEvent, mark("event:frame-src:false", 8)], "redirect"),
    /frame-src:true/,
  );
  assert.throws(() =>
    prove(
      [...withoutEvent, mark("event:frame-src:true", 8, 101, "B".repeat(32))],
      "redirect",
    ),
  );
});

test("form navigation credits the original initiator, not its destination", () => {
  const evidence = redirect().map((entry) =>
    entry.name === `${n}:event:frame-src:true`
      ? mark("event:form-action:true", 8)
      : entry,
  );
  assert.equal(prove(evidence, "form").browserCspDispatch, 7);
  const other = evidence.map((entry) =>
    entry.name === "Receive mojo message" ? receive(102) : entry,
  );
  assert.throws(() => prove(other, "form"), /different renderer/);
});

test("audit-only rejects an actual or mislabeled frame-ancestors event", () => {
  const evidence = [...base(), receive(), navigate("OnRequestFailedInternal")];
  assert.throws(() => prove([...evidence, dispatch(7)], "ancestors"));
  assert.throws(() =>
    prove([...evidence, mark("event:frame-ancestors:true", 8)], "ancestors"),
  );
});
