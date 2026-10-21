import { expect, test } from "@playwright/test";
import {
  emptyWorkbenchSelection,
  inspectorLabels,
  normalizeWorkbenchLayout,
  reduceWorkbenchSelection,
} from "../src/workbench/workbench-model";
import { ObservedJson } from "../src/workbench/observed-json";

const caseId = "11111111-1111-4111-8111-111111111111";
const runId = "22222222-2222-4222-8222-222222222222";
const field = (name: string) => ({
  kind: "field" as const,
  name,
  namespaceUri: null,
});
const bytes = (value: string) => new TextEncoder().encode(value);

test("workflow and dock changes retain the same authorized selected case/run", () => {
  let state = emptyWorkbenchSelection("owner/revision");
  state = reduceWorkbenchSelection(state, {
    kind: "run",
    binding: state.binding!,
    epoch: state.epoch,
    selection: { runId, caseId, version: "2" },
  });
  state = reduceWorkbenchSelection(state, { kind: "view", view: "mapping" });
  state = reduceWorkbenchSelection(state, { kind: "dock", dock: "diff" });
  expect(state.run?.runId).toBe(runId);
  expect(state.case).toEqual({ caseId, version: "2" });
  expect(state.dock).toBe("diff");
});

test("changed identity or revision clears evidence selection and late completions cannot restore it", () => {
  let state = reduceWorkbenchSelection(emptyWorkbenchSelection("old"), {
    kind: "run",
    binding: "old",
    epoch: 0,
    selection: { runId, caseId, version: "1" },
  });
  state = reduceWorkbenchSelection(state, {
    kind: "sample",
    binding: "old",
    epoch: 0,
    enabled: true,
  });
  state = reduceWorkbenchSelection(state, { kind: "bind", binding: "new" });
  state = reduceWorkbenchSelection(state, {
    kind: "run",
    binding: "old",
    epoch: 0,
    selection: { runId, caseId, version: "1" },
  });
  expect(state).toEqual(emptyWorkbenchSelection("new", 1));
});

test("changing a case cannot leave a run from a different version selected", () => {
  const state = reduceWorkbenchSelection(emptyWorkbenchSelection("owner"), {
    kind: "run",
    binding: "owner",
    epoch: 0,
    selection: { runId, caseId, version: "1" },
  });
  expect(
    reduceWorkbenchSelection(state, {
      kind: "case",
      binding: "owner",
      epoch: 0,
      selection: { caseId, version: "2" },
    }).run,
  ).toBeNull();
});

test("A to B to A cannot revive a callback from the first A lifetime", () => {
  let state = reduceWorkbenchSelection(emptyWorkbenchSelection(), {
    kind: "bind",
    binding: "A",
  });
  const oldEpoch = state.epoch;
  state = reduceWorkbenchSelection(state, { kind: "bind", binding: "B" });
  state = reduceWorkbenchSelection(state, { kind: "bind", binding: "A" });
  const current = state;
  state = reduceWorkbenchSelection(state, {
    kind: "run",
    binding: "A",
    epoch: oldEpoch,
    selection: { runId, caseId, version: "1" },
  });
  expect(state).toBe(current);
  expect(state.run).toBeNull();
});

test("clear and rebind retires old work, while a same-lifetime metadata bind preserves selection", () => {
  let state = reduceWorkbenchSelection(emptyWorkbenchSelection(), {
    kind: "bind",
    binding: "A",
  });
  const oldEpoch = state.epoch;
  state = reduceWorkbenchSelection(state, {
    kind: "case",
    binding: "A",
    epoch: oldEpoch,
    selection: { caseId, version: "1" },
  });
  expect(reduceWorkbenchSelection(state, { kind: "bind", binding: "A" })).toBe(
    state,
  );
  state = reduceWorkbenchSelection(state, { kind: "clear" });
  state = reduceWorkbenchSelection(state, { kind: "bind", binding: "A" });
  expect(
    reduceWorkbenchSelection(state, {
      kind: "sample",
      binding: "A",
      epoch: oldEpoch,
      enabled: true,
    }).sampleMode,
  ).toBe(false);
  expect(state.case).toBeNull();
});

test("layout normalization cannot capture sample bytes or request tokens", () => {
  const value = normalizeWorkbenchLayout({
    density: "dense",
    inspectorWidth: 9000,
    dockHeight: -10,
    sample: "private",
    token: "private",
    runId,
    caseId,
  });
  expect(value).toEqual({
    density: "dense",
    inspectorWidth: 420,
    inspectorCollapsed: false,
    dockHeight: 90,
    dockCollapsed: false,
  });
  expect(JSON.stringify(value)).not.toContain("private");
});

test("an admission refresh keeps only nonprivate workflow navigation", () => {
  let state = emptyWorkbenchSelection("owner");
  state = reduceWorkbenchSelection(state, { kind: "view", view: "builds" });
  state = reduceWorkbenchSelection(state, { kind: "dock", dock: "diff" });
  state = reduceWorkbenchSelection(state, {
    kind: "run",
    binding: "owner",
    epoch: 0,
    selection: { runId, caseId, version: "1" },
  });
  state = reduceWorkbenchSelection(state, { kind: "clear" });
  state = reduceWorkbenchSelection(state, { kind: "bind", binding: "owner" });
  expect(state.view).toBe("builds");
  expect(state.dock).toBe("diff");
  expect(state.run).toBeNull();
  expect(state.case).toBeNull();
  expect(state.epoch).toBe(2);
});

test("inspector labels follow the workflow", () => {
  expect(inspectorLabels("mapping")).toEqual(["Rule", "Lineage", "Evidence"]);
  expect(inspectorLabels("tests")).toEqual([
    "Failure",
    "Reproduction",
    "Outputs",
  ]);
  expect(inspectorLabels("builds")).toEqual([
    "Readiness",
    "Approval",
    "Artifact",
  ]);
  expect(inspectorLabels("releases")).toEqual(["Gates", "Release", "Audit"]);
});

test("observed JSON preserves exact numeric and null lexemes", () => {
  const input = bytes(
    '{"large":9007199254740993,"zero":-0,"amount":2.000e+3,"absent":null}',
  );
  for (const [name, expected] of [
    ["large", "9007199254740993"],
    ["zero", "-0"],
    ["amount", "2.000e+3"],
    ["absent", "null"],
  ])
    expect(ObservedJson.read(input, [field(name!)])).toEqual({
      kind: "observed",
      more: false,
      values: [{ text: expected, truncated: false }],
    });
  expect(ObservedJson.read(input, [field("missing")])).toEqual({
    kind: "missing",
  });
});

test("dotted JSON keys stay literal and arrays follow declared paths", () => {
  const input = bytes('{"a.b":{"items":[{"id":"one"},{"id":"two"}]}}');
  expect(
    ObservedJson.read(input, [
      field("a.b"),
      field("items"),
      { kind: "index", index: "1" },
      field("id"),
    ]),
  ).toEqual({
    kind: "observed",
    more: false,
    values: [{ text: '"two"', truncated: false }],
  });
  expect(ObservedJson.read(input, [field("a"), field("b")])).toEqual({
    kind: "missing",
  });
});

test("wildcards are bounded only after path resolution, never fabricating missing data", () => {
  const input = bytes(
    JSON.stringify({
      items: [...Array.from({ length: 20 }, () => ({})), { value: 7 }],
    }),
  );
  expect(
    ObservedJson.read(input, [
      field("items"),
      { kind: "anyItem" },
      field("value"),
    ]),
  ).toEqual({
    kind: "observed",
    more: false,
    values: [{ text: "7", truncated: false }],
  });
  const many = ObservedJson.read(
    bytes(JSON.stringify(Array.from({ length: 20 }, (_, i) => i))),
    [{ kind: "anyItem" }],
  );
  expect(many.kind === "observed" && many.values.length).toBe(16);
  expect(many.kind === "observed" && many.more).toBe(true);
});

for (const input of ['{"x":1,"x":2}', '{"x":1,"\\u0078":2}'])
  test(`duplicate object identity is unavailable: ${input}`, () => {
    expect(ObservedJson.read(bytes(input), [field("x")])).toEqual({
      kind: "unavailable",
      reason: "ambiguousJson",
    });
  });

for (const input of [
  '{"x":01}',
  '{"x":}',
  "[1,]",
  '{"x":1} trailing',
  '{"x":"\\q"}',
])
  test(`invalid sample is not an observed value: ${input}`, () => {
    expect(ObservedJson.read(bytes(input), [field("x")])).toEqual({
      kind: "unavailable",
      reason: "invalidJson",
    });
  });

test("invalid UTF-8, oversized samples and namespaced paths fail closed", () => {
  expect(ObservedJson.read(new Uint8Array([0xff]), [])).toEqual({
    kind: "unavailable",
    reason: "invalidJson",
  });
  expect(
    ObservedJson.read(new Uint8Array(ObservedJson.maximumBytes + 1), []),
  ).toEqual({ kind: "unavailable", reason: "tooLarge" });
  expect(
    ObservedJson.read(bytes('{"x":1}'), [
      { ...field("x"), namespaceUri: "urn:example" },
    ]),
  ).toEqual({ kind: "unavailable", reason: "unsupportedPath" });
});

test("difference locations preserve literal keys and array indices without interpreting whitespace as field changes", () => {
  expect(
    ObservedJson.changedPaths(
      bytes('{"a.b":[1,2]}'),
      bytes('{ "a.b": [1,3] }'),
    ),
  ).toEqual([
    [
      { kind: "field", name: "a.b" },
      { kind: "index", index: "1" },
    ],
  ]);
  expect(
    ObservedJson.changedPaths(bytes('{"x":1}'), bytes('{ "x": 1 }')),
  ).toEqual([]);
  expect(
    ObservedJson.changedPaths(bytes('{"x":1,"x":2}'), bytes('{"x":3}')),
  ).toBeNull();
});
