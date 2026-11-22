import assert from "node:assert/strict";

export const cases = Object.freeze({
  html: { document: "html", mutation: "none" },
  xhtml: { document: "xhtml", mutation: "none" },
  svg: { document: "svg", mutation: "none" },
  removed: { document: "html", mutation: "remove" },
  "xhtml-removed": { document: "xhtml", mutation: "remove" },
  "svg-removed": { document: "svg", mutation: "remove" },
  adopted: { document: "html", mutation: "adopt" },
  "shadow-open": { document: "html", mutation: "open" },
  "shadow-closed": { document: "html", mutation: "closed" },
});

const htmlNamespace = "http://www.w3.org/1999/xhtml";
const svgNamespace = "http://www.w3.org/2000/svg";
const types = {
  html: {
    mime: "text/html",
    html: true,
    xml: false,
    root: "html",
    namespace: htmlNamespace,
  },
  xhtml: {
    mime: "application/xhtml+xml",
    html: false,
    xml: true,
    root: "html",
    namespace: htmlNamespace,
  },
  svg: {
    mime: "image/svg+xml",
    html: false,
    xml: true,
    root: "svg",
    namespace: svgNamespace,
  },
};

// phase: capture=1, at target=2, bubble=3. All listeners use the exact
// object references from the same fixture callback; no selector re-resolution.
const plain = [
  ["original", "style", ["style"], 1],
  ["style", "style", ["style"], 2],
];
const documentFallback = [["original", "original", ["original"], 2]];
const shadow = (closed) => [
  ["original", "host", closed ? ["host"] : ["style", "shadow", "host"], 1],
  ["style", "style", ["style", "shadow", "host"], 2],
  ["shadow", "style", ["style", "shadow", "host"], 3],
  ["host", "host", closed ? ["host"] : ["style", "shadow", "host"], 2],
];
const views = {
  html: plain,
  xhtml: plain,
  svg: plain,
  removed: documentFallback,
  "xhtml-removed": documentFallback,
  "svg-removed": documentFallback,
  adopted: documentFallback,
  "shadow-open": shadow(false),
  "shadow-closed": shadow(true),
};

export function assertTargetObservation(mode, originalUrl, observation) {
  assert(Object.hasOwn(cases, mode));
  const { document, mutation } = cases[mode];
  assert.deepEqual(observation.documentType, types[document]);
  assert.equal(observation.callsBeforeReturn, 0);
  assert.equal(observation.checkedConnectedOriginal, true);
  assert.equal(observation.originalStyleUnchanged, true);
  assert.equal(observation.connected, mutation !== "remove");
  const owner = mutation === "adopt" ? "other" : "original";
  assert.equal(observation.owner, owner);
  assert.equal(
    observation.styleAppliedInOther,
    mutation === "adopt" ? true : null,
  );
  assert.equal(
    observation.eventCount,
    1,
    "expected one event object, not one per view",
  );
  assert.deepEqual(
    observation.events.map(({ listener, serializedTarget, path, phase }) => [
      listener,
      serializedTarget,
      path[0],
      path.filter((entry) => entry !== "unlabeled"),
      phase,
    ]),
    views[mode].map(([listener, target, prefix, phase]) => [
      listener,
      target,
      prefix[0],
      prefix[0] === "original" ? prefix : [...prefix, "original"],
      phase,
    ]),
  );
  for (const view of observation.events) {
    assert.equal(view.id, 1);
    assert.equal(view.current, view.listener);
    assert.equal(view.currentDocumentIsOriginal, true);
    assert.equal(view.owner, owner);
    assert.equal(view.elementConnected, mutation !== "remove");
    assert.equal(view.trusted, true);
    assert.equal(view.genuineType, true);
    assert.equal(view.bubbles, true);
    assert.equal(view.composed, true);
    assert.equal(view.cancelable, false);
    assert.equal(view.blocked, "inline");
    assert.equal(view.effective, "style-src-elem");
    assert.equal(view.violated, "style-src-elem");
    assert.equal(view.disposition, "enforce");
    assert.equal(view.policy, "style-src 'none'");
    assert.equal(view.uri, originalUrl);
  }
}
