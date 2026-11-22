// Test-only proof on complete traces of a single controlled navigation.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";

const method = "blink::mojom::LocalFrame::ReportContentSecurityPolicyViolation";
// Pinned mojom_cpp_generator.py: the first eight hex digits of SHA-256.
export const cspMethodHash = createHash("sha256")
  .update(method)
  .digest()
  .readUInt32BE();
const iface = "blink.mojom.LocalFrame";
const sameLane = (a, b) => a.pid === b.pid && a.tid === b.tid;
const category = (event, expected) => event.cat?.split(",").includes(expected);

const only = (values, message) => {
  assert.equal(values.length, 1, message);
  return values[0];
};

export function proveCspOrigin(trace, nonce, scenario, browserPid) {
  assert(["blink", "redirect", "ancestors", "form"].includes(scenario));
  const mark = (suffix) =>
    only(
      trace.filter(
        (e) =>
          e.name === `${nonce}:${suffix}` &&
          ["I", "i", "R"].includes(e.ph) &&
          category(e, "blink.user_timing"),
      ),
      `missing/ambiguous ${suffix} mark`,
    );
  const original = mark("original");
  const peer = mark("peer");
  const end = mark("end");
  assert(Number.isSafeInteger(browserPid) && browserPid > 0);
  assert(browserPid !== original.pid && browserPid !== peer.pid);
  assert.notEqual(peer.pid, original.pid, "peer was not out of process");
  assert.notEqual(peer.args.data.navigationId, original.args.data.navigationId);
  assert.match(original.args.data.navigationId, /^[A-F0-9]{32}$/);
  assert(sameLane(original, end));
  assert.equal(end.args.data.navigationId, original.args.data.navigationId);
  assert(original.ts < peer.ts && peer.ts < end.ts);

  // The pinned Chromium DevTools JSON export emits completed `X` slices for
  // these native scopes. An absent/partial slice is not positive evidence.
  const slices = trace
    .filter(
      (e) =>
        e.ph === "X" &&
        Number.isFinite(e.ts) &&
        Number.isFinite(e.dur) &&
        e.dur >= 0,
    )
    .map((e) => ({ ...e, end: e.ts + e.dur }));
  const window = (e) => e.ts > peer.ts && e.end <= end.ts;
  const dispatches = slices.filter(
    (e) =>
      e.name === "EventDispatch" &&
      category(e, "devtools.timeline") &&
      e.args?.data?.type === "securitypolicyviolation" &&
      window(e),
  );
  function dispatchFor(directive) {
    const evidence = mark(`event:${directive}:true`);
    assert(sameLane(original, evidence));
    assert.equal(
      evidence.args.data.navigationId,
      original.args.data.navigationId,
    );
    return only(
      dispatches.filter(
        (e) =>
          sameLane(e, evidence) && e.ts <= evidence.ts && evidence.ts < e.end,
      ),
      `missing/ambiguous native ${directive} dispatch`,
    );
  }
  const image = dispatchFor("img-src");
  const inbound = slices.filter(
    (e) =>
      category(e, "mojom") &&
      window(e) &&
      (e.name === `Receive ${method}` ||
        (e.name === "Receive mojo message" &&
          e.args?.chrome_mojo_event_info?.mojo_interface_tag === iface &&
          e.args.chrome_mojo_event_info.ipc_hash === cspMethodHash)),
  );
  const result = {
    originalRenderer: original.pid,
    peerRenderer: peer.pid,
    originalNavigationId: original.args.data.navigationId,
    blinkLocalDispatch: image.ts,
    cspMojoReceives: inbound.length,
    cspDomDispatches: dispatches.length,
  };
  if (scenario === "blink") {
    assert.equal(inbound.length, 0, "Blink-only control received browser CSP");
    assert.deepEqual(dispatches, [image]);
    return result;
  }

  const receive = only(inbound, "missing/ambiguous CSP Mojo receive");
  assert(
    sameLane(receive, original),
    "CSP arrived on a different renderer lane",
  );
  assert(receive.end <= image.ts, "Blink barrier did not follow CSP Mojo");
  const navigation = only(
    slices.filter(
      (e) =>
        window(e) &&
        e.pid === browserPid &&
        category(e, "navigation") &&
        e.name ===
          (scenario === "ancestors"
            ? "NavigationRequest::OnRequestFailedInternal"
            : "NavigationRequest::OnRequestRedirected"),
    ),
    "missing/ambiguous browser navigation slice",
  );
  if (scenario !== "ancestors") {
    const report = dispatchFor(
      scenario === "form" ? "form-action" : "frame-src",
    );
    assert(navigation.ts <= receive.ts);
    assert(receive.end <= report.ts && report.end <= image.ts);
    assert.equal(dispatches.length, 2);
    result.browserCspDispatch = report.ts;
  } else {
    // Audit is asserted in the runner after matched request failure. In this
    // trace the only native CSP dispatch is the subsequent Blink barrier.
    assert.deepEqual(dispatches, [image]);
    assert(
      !trace.some((e) => e.name?.startsWith(`${nonce}:event:frame-ancestors:`)),
    );
  }
  return {
    ...result,
    browserProcess: navigation.pid,
    navigationSlice: navigation.name,
    navigationAt: navigation.ts,
    originalMojoReceive: receive.ts,
    originalMojoReturn: receive.end,
  };
}
