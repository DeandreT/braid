import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import {
  reportSampleArtifacts,
  reportSampleFixtureIndex,
  reportSampleFixtureIndices,
  reportSampleNames,
} from "./samples.mjs";

const root = new URL("../../", import.meta.url);
test("reports owner admits exactly the typed native sample labels", () => {
  assert.deepEqual(reportSampleNames, [
    "basic",
    "exact",
    "partial",
    "ambiguous",
    "invalid",
    "uncached",
    "pending",
    "conditions",
    "controls",
    "functions",
    "generated",
    "namedInputs",
  ]);
  const declaration = readFileSync(
    new URL("./samples.d.mts", import.meta.url),
    "utf8",
  );
  const tuple = declaration.match(
    /reportSampleNames: readonly \[([\s\S]*?)\]/,
  )?.[1];
  assert.ok(tuple);
  assert.deepEqual(
    [...tuple.matchAll(/"([A-Za-z]+)"/g)].map((match) => match[1]),
    reportSampleNames,
  );
  assert.ok(Object.isFrozen(reportSampleNames));
  assert.ok(Object.isFrozen(reportSampleFixtureIndices));
});
test("every reports artifact is an immutable exact digest and length pin in maintained source", () => {
  assert.equal(reportSampleArtifacts.length, 9);
  assert.equal(new Set(reportSampleArtifacts.map(([path]) => path)).size, 9);
  assert.ok(Object.isFrozen(reportSampleArtifacts));
  for (const artifact of reportSampleArtifacts) {
    assert.ok(Object.isFrozen(artifact));
    const [path, digest, length] = artifact;
    assert.match(
      path,
      /^(?:engines\/ferrule-host\/tests\/fixtures|dotnet\/tests\/Weft.Tests\/Fixtures)\/[a-z-]+\.json$/,
    );
    const bytes = readFileSync(new URL(path, root));
    assert.equal(bytes.length, length, path);
    assert.equal(
      createHash("sha256").update(bytes).digest("hex"),
      digest,
      path,
    );
  }
});
test("closed sample selection preserves native parity and independent uncached/pending uploads", () => {
  const expected = {
    basic: 0,
    exact: 1,
    partial: 2,
    ambiguous: 3,
    invalid: 4,
    uncached: 0,
    pending: 0,
    conditions: 5,
    controls: 6,
    functions: 7,
    generated: 8,
    namedInputs: 6,
  };
  assert.deepEqual(reportSampleFixtureIndices, expected);
  for (const sample of reportSampleNames) {
    const index = reportSampleFixtureIndex(sample);
    assert.equal(index, expected[sample]);
    assert.ok(reportSampleArtifacts[index]);
  }
  for (const invalid of [
    "",
    "unknown",
    "__proto__",
    "constructor",
    "toString",
    "../projection-basic.json",
    null,
    0,
    {},
    ["basic"],
  ])
    assert.throws(
      () => reportSampleFixtureIndex(invalid),
      /Unknown pinned reports sample/,
    );
});
test("owner binds every manifest sample to a separately HTTP-created source-admitted revision", () => {
  const source = readFileSync(
    new URL("./operator.mjs", import.meta.url),
    "utf8",
  );
  assert.match(source, /fixtures: reportSampleArtifacts/);
  assert.match(
    source,
    /fixtures\[reportSampleFixtureIndex\(sampleNames\[index\]\)\]/,
  );
  for (const name of [
    "mappings",
    "mapping_revisions",
    "private_object_publications",
  ])
    assert.ok(source.includes(`initial.${name}.length === sampleNames.length`));
  const runtime = readFileSync(
    new URL("../browser-fixture/runtime.mjs", import.meta.url),
    "utf8",
  );
  assert.match(runtime, /sourceHashes\[path\] === digest/);
  assert.match(runtime, /sha\(bytes\) === digest/);
  assert.match(runtime, /bytes\.length === length/);
});
