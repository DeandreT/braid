import assert from "node:assert/strict";
import { test } from "node:test";
import { assertTargetObservation } from "./dom-target-expectations.mjs";

const url = "http://braid.invalid/removed";

function removed() {
  return {
    documentType: {
      mime: "text/html",
      html: true,
      xml: false,
      root: "html",
      namespace: "http://www.w3.org/1999/xhtml",
    },
    callsBeforeReturn: 0,
    checkedConnectedOriginal: true,
    originalStyleUnchanged: true,
    connected: false,
    owner: "original",
    styleAppliedInOther: null,
    eventCount: 1,
    events: [
      {
        listener: "original",
        serializedTarget: "original",
        current: "original",
        path: ["original", "unlabeled"],
        phase: 2,
        id: 1,
        currentDocumentIsOriginal: true,
        owner: "original",
        elementConnected: false,
        trusted: true,
        genuineType: true,
        bubbles: true,
        composed: true,
        cancelable: false,
        blocked: "inline",
        effective: "style-src-elem",
        violated: "style-src-elem",
        disposition: "enforce",
        policy: "style-src 'none'",
        uri: url,
      },
    ],
  };
}

test("accepts a fallback only on the exact original", () => {
  assertTargetObservation("removed", url, removed());
  for (const extra of [
    { serializedTarget: "style" },
    { serializedTarget: "other" },
    { currentDocumentIsOriginal: false },
    { uri: "http://braid.invalid/replacement" },
    { trusted: false },
    { path: ["unlabeled", "original"] },
  ]) {
    const result = removed();
    Object.assign(result.events[0], extra);
    assert.throws(() => assertTargetObservation("removed", url, result));
  }
});

test("a listener view does not stand in for a separate native event", () => {
  const result = removed();
  result.eventCount = 2;
  assert.throws(
    () => assertTargetObservation("removed", url, result),
    /one event object/,
  );
  result.eventCount = 1;
  result.events[0].id = 2;
  assert.throws(() => assertTargetObservation("removed", url, result));
});

test("HTML evidence cannot prove a non-HTML original", () => {
  for (const mode of ["xhtml-removed", "svg-removed"]) {
    assert.throws(() => assertTargetObservation(mode, url, removed()));
  }
});
