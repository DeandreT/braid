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
  assert(["blink", "redirect", "ancestors"].includes(scenario));
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
  const parent = mark("original");
  const child = mark("oopif");
  const end = mark("end");
  assert(Number.isSafeInteger(browserPid) && browserPid > 0);
  assert(browserPid !== parent.pid && browserPid !== child.pid);
  assert.notEqual(child.pid, parent.pid, "child was not out of process");
  assert.notEqual(child.args.data.navigationId, parent.args.data.navigationId);
  assert.match(parent.args.data.navigationId, /^[A-F0-9]{32}$/);
  assert(sameLane(parent, end));
  assert.equal(end.args.data.navigationId, parent.args.data.navigationId);
  assert(parent.ts < child.ts && child.ts < end.ts);

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
  const window = (e) => e.ts > child.ts && e.end <= end.ts;
  const dispatches = slices.filter(
    (e) =>
      e.name === "EventDispatch" &&
      category(e, "devtools.timeline") &&
      e.args?.data?.type === "securitypolicyviolation" &&
      window(e),
  );
  function dispatchFor(directive) {
    const evidence = mark(`event:${directive}:true`);
    assert(sameLane(parent, evidence));
    assert.equal(
      evidence.args.data.navigationId,
      parent.args.data.navigationId,
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
    originalRenderer: parent.pid,
    childRenderer: child.pid,
    originalNavigationId: parent.args.data.navigationId,
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
  assert(sameLane(receive, parent), "CSP arrived on a different renderer lane");
  assert(receive.end <= image.ts, "Blink barrier did not follow CSP Mojo");
  const navigation = only(
    slices.filter(
      (e) =>
        window(e) &&
        e.pid === browserPid &&
        category(e, "navigation") &&
        e.name ===
          (scenario === "redirect"
            ? "NavigationRequest::OnRequestRedirected"
            : "NavigationRequest::OnRequestFailedInternal"),
    ),
    "missing/ambiguous browser navigation slice",
  );
  if (scenario === "redirect") {
    const report = dispatchFor("frame-src");
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
