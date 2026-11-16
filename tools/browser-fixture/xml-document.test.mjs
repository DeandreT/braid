import assert from "node:assert/strict";
import test from "node:test";
import { xmlOutline } from "../../apps/web/src/workbench/xml-outline.ts";
import {
  xmlMetadata,
  xmlMetadataBytes,
} from "../../apps/web/src/catalog/previews/xml-contract.ts";
import { hasUnsentPreview } from "../../apps/web/src/catalog/previews/preview-unsent.ts";
import {
  xmlPreflightProblem,
  xmlPreflightText,
} from "../../apps/web/src/catalog/previews/xml-problem.ts";

const metadata = {
  schema: "braid.xml-boundary.v1",
  safetyProfile: "braid.xml-safe.v1",
  schemaHints: null,
};
test("XML metadata is closed and schema locations stay lexical", () => {
  assert.equal(xmlMetadata(metadata), true);
  assert.equal(xmlMetadata({ ...metadata, extra: true }), false);
  assert.equal(xmlMetadata({ ...metadata, schemaHints: {} }), false);
  assert.equal(
    xmlMetadata({
      ...metadata,
      schemaHints: { noNamespaceLocation: "file:///never-open", locations: [] },
    }),
    true,
  );
  const pair = {
    namespace: "urn:test",
    location: "https://never-fetch.invalid/schema",
  };
  assert.equal(
    xmlMetadata({
      ...metadata,
      schemaHints: { noNamespaceLocation: null, locations: [pair, pair] },
    }),
    false,
  );
  assert.equal(xmlMetadata({ ...metadata, safetyProfile: null }), false);
  assert.equal(xmlMetadataBytes(metadata), 102);
});
test("lexical XML outline retains escaped markup without a DOM or entity resolver", () => {
  const source =
    '<Order url="https://never-fetch.invalid"><code>&lt;script&gt;</code><![CDATA[<img src=x>]]><!-- text --></Order>';
  const lines = xmlOutline(source);
  assert.ok(lines);
  assert.equal(lines.map((line) => line.text).join(""), source);
  assert.equal(lines[1].depth, 1);
  for (const input of [
    '<!DOCTYPE x SYSTEM "https://never-fetch.invalid"><x/>',
    "<x>",
    "<x/><y/>",
    "<x>".repeat(65) + "</x>".repeat(65),
    "<x>" + "a".repeat(65536) + "</x>",
    "<x>" + "<y/>".repeat(1001) + "</x>",
  ])
    assert.equal(xmlOutline(input), null);
});
test("an explicit XML choice is private unsent work even with no input", () => {
  assert.equal(
    !!hasUnsentPreview({
      inputs: [{ name: "", text: "", file: null }],
      parameters: [],
      targetKind: "all",
      targetName: "",
      documentProfile: false,
      xmlProfile: true,
      busy: null,
      pendingKey: false,
    }),
    true,
  );
});

test("XML preflight diagnostics retain only closed reasons and exact safe counts", () => {
  for (const [reason, kind, observed, limit, phrase] of [
    ["invalidXml", "invalidInput", null, null, "safe syntax policy"],
    ["depthLimit", "resourceLimit", "65", "64", "depth limit"],
    ["nodeLimit", "resourceLimit", "1000001", "1000000", "node limit"],
    [
      "metadataTooLarge",
      "resourceLimit",
      "9223372036854775807",
      "32768",
      "metadata exceeds",
    ],
  ]) {
    const value = {
      code: "xmlPreflight",
      kind,
      reason,
      location: { line: 2, column: 3 },
      statistics: {
        originalBytes: 100,
        physicalNodes: 4,
        maximumDepth: 2,
        maximumAttributes: 0,
      },
      observed,
      limit,
    };
    const accepted = xmlPreflightProblem(value);
    assert.ok(accepted);
    assert.notEqual(accepted, value);
    const text = xmlPreflightText(accepted);
    assert.ok(text.includes(phrase));
    assert.ok(text.includes("Line 2, column 3"));
    assert.ok(text.includes("No preview run was created"));
    if (observed) assert.ok(text.includes(observed));
    for (const bad of [
      { ...value, message: "private-sentinel parser details" },
      { ...value, reason: "private-sentinel" },
      { ...value, reason: "__proto__" },
      { ...value, kind: "private-sentinel" },
      { ...value, observed: "9223372036854775808" },
      { ...value, observed: "01" },
      { ...value, limit: 32768 },
      { ...value, location: { line: 0, column: 3 } },
      { ...value, statistics: { ...value.statistics, physicalNodes: -1 } },
      { ...value, location: { line: 2, column: 3, name: "private-sentinel" } },
    ])
      assert.equal(xmlPreflightProblem(bad), null);
  }
});
