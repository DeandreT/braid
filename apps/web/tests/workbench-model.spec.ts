import { expect, test } from "@playwright/test";
import {
  emptyWorkbenchSelection,
  inspectorLabels,
  normalizeWorkbenchLayout,
  reduceWorkbenchSelection,
} from "../src/workbench/workbench-model";
import { ObservedJson } from "../src/workbench/observed-json";
import { sessionTone } from "../src/auth/browser-auth-presentation";
import { buildConfirmation } from "../src/catalog/builds/build-confirmation";
import {
  previewOutcomeStatus,
  previewRunStatus,
} from "../src/catalog/previews/preview-presentation";
import type { PendingBuildMutation } from "../src/catalog/builds/build-recovery";
import {
  formatByteCount,
  formatUtcTimestamp,
} from "../src/workbench/metadata-format";
import {
  normalizeSavedLayout,
  readSavedLayout,
  writeSavedLayout,
  layoutStorageKey,
} from "../src/workbench/layout-preferences";
import {
  workbenchLabels,
  dockLabels,
  comparisonStatus,
  buildStatus,
  operationStatus,
  fileStatusLabel,
} from "../src/workbench/presentation";

const caseId = "11111111-1111-4111-8111-111111111111";
const runId = "22222222-2222-4222-8222-222222222222";
const field = (name: string) => ({
  kind: "field" as const,
  name,
  namespaceUri: null,
});
const bytes = (value: string) => new TextEncoder().encode(value);

test("sign-in colors never promote local recovery or a pending action to authenticated success", () => {
  expect(sessionTone("authenticated", "idle")).toBe("success");
  expect(sessionTone("authenticated", "pending")).toBe("running");
  expect(sessionTone("unavailable", "revocationUnconfirmed")).toBe("uncertain");
  expect(sessionTone("unavailable", "unconfirmed")).toBe("uncertain");
  expect(sessionTone("development", "idle")).toBe("neutral");
  expect(sessionTone("anonymous", "idle")).toBe("neutral");
  expect(sessionTone("future", "idle")).toBe("unavailable");
});

test("sample status never confuses execution success with a comparison or an unknown state", () => {
  expect(
    previewRunStatus({ state: "completed", outcome: "succeeded" }),
  ).toMatchObject({ label: "Sample ran successfully", tone: "success" });
  expect(previewRunStatus({ state: "future", outcome: "succeeded" }).tone).toBe(
    "unavailable",
  );
  expect(previewOutcomeStatus("future").tone).toBe("unavailable");
  expect(previewOutcomeStatus(null).tone).toBe("unavailable");
  expect(previewOutcomeStatus("missingInput").tone).toBe("attention");
  expect(previewOutcomeStatus("protocolFailure").tone).toBe("failure");
  expect(previewOutcomeStatus("backendUnavailable").tone).toBe("unavailable");
});

test("confirmation copy preserves exact operation facts without implying deployment", () => {
  const operation: PendingBuildMutation = Object.freeze({
    kind: "approval",
    id: runId,
    owner: {
      identity: "current-owner",
      nativeDigest: "a".repeat(64),
      scope: {
        organizationId: caseId,
        projectId: caseId,
        mappingId: caseId,
        revisionId: caseId,
      },
    },
    body: JSON.stringify({
      environmentId: caseId,
      environmentVersion: "27",
      regressionEvidenceId: runId,
    }),
  });
  const original = JSON.stringify(operation);
  const copy = buildConfirmation(operation, { sequence: "19" });
  expect(copy.action).toBe("Approve version 19");
  expect(copy.effect).toContain(
    "does not start preparation or change the version in use",
  );
  expect(copy.facts).toContainEqual({
    label: "Environment reference",
    value: caseId,
  });
  expect(copy.facts).toContainEqual({
    label: "Environment version",
    value: "27",
  });
  expect(copy.facts).toContainEqual({
    label: "Request reference",
    value: runId,
  });
  expect(JSON.stringify(operation)).toBe(original);
  const cancel = buildConfirmation(
    { kind: "cancel", id: runId, owner: operation.owner },
    { sequence: "19", previousState: "Running" },
  );
  expect(cancel.facts).toContainEqual({
    label: "Environment reference",
    value: "Not supplied with this operation",
  });
  expect(cancel.effect).toContain("may already have finished");
});

test("metadata formatting is readable, timezone-explicit and never guesses missing values", () => {
  expect(formatByteCount(0)).toBe("0 bytes");
  expect(formatByteCount(1)).toBe("1 byte");
  expect(formatByteCount(1536)).toBe("1.5 KiB");
  expect(formatByteCount(1048576)).toBe("1 MiB");
  for (const value of [
    null,
    undefined,
    -1,
    1.5,
    Infinity,
    "1536",
    Number.MAX_SAFE_INTEGER + 1,
  ])
    expect(formatByteCount(value)).toBe("Size unavailable");
  expect(formatUtcTimestamp("2026-10-21T01:30:00.123456789+02:00")).toBe(
    "Oct 20, 2026, 23:30 UTC",
  );
  for (const value of [
    null,
    undefined,
    "",
    "not a date",
    "2026-10-21T01:30:00",
    "2026-13-99T01:30:00Z",
    "2026-02-30T01:30:00Z",
    "2026-10-21T24:30:00Z",
  ])
    expect(formatUtcTimestamp(value)).toBe("Time unavailable");
});

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
    inspectorCollapsed: true,
    dockHeight: 140,
    dockCollapsed: true,
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
  expect(inspectorLabels("mapping")).toEqual(["Rule", "Data flow", "Details"]);
  expect(inspectorLabels("tests")).toEqual(["Result", "Test setup", "Outputs"]);
  expect(inspectorLabels("builds")).toEqual([
    "Readiness",
    "Approval",
    "Prepared version",
  ]);
  expect(inspectorLabels("releases")).toEqual([
    "Required checks",
    "In use",
    "Change history",
  ]);
});

test("saved layout uses a bounded allowlist and never stores private context", () => {
  const privateFields = {
    search: "private-search",
    runId,
    caseId,
    sample: "private-sample",
    token: "private-token",
  };
  let saved = "";
  writeSavedLayout(
    {
      setItem: (key, value) => {
        expect(key).toBe(layoutStorageKey);
        saved = value;
      },
    },
    {
      ...privateFields,
      frame: { density: "dense", ...privateFields },
      table: {
        ...privateFields,
        widths: { source: Infinity, target: 9999, ...privateFields },
        hiddenColumns: ["source", "rule", "private"],
        sort: "private-search",
        pinKeyColumn: false,
      },
    },
  );
  expect(saved).not.toMatch(/private|11111111|22222222/);
  const parsed = readSavedLayout({ getItem: () => saved });
  expect(parsed.frame.density).toBe("dense");
  expect(parsed.table.widths.source).toBe(190);
  expect(parsed.table.widths.target).toBe(720);
  expect(parsed.table.hiddenColumns).toEqual(["rule"]);
  expect(parsed.table.sort).toBeNull();
  expect(parsed.table.pinKeyColumn).toBe(false);
});

test("blocked, corrupt, oversized and future layout storage falls back without blocking access", () => {
  const defaults = normalizeSavedLayout(null);
  for (const value of [
    "{",
    "x".repeat(8193),
    '{"version":2,"frame":{"density":"dense"}}',
  ])
    expect(readSavedLayout({ getItem: () => value })).toEqual(defaults);
  expect(
    readSavedLayout({
      getItem: () => {
        throw Error("blocked");
      },
    }),
  ).toEqual(defaults);
  expect(() =>
    writeSavedLayout(
      {
        setItem: () => {
          throw Error("quota");
        },
      },
      defaults,
    ),
  ).not.toThrow();
});

test("presentation distinguishes completion, a comparison pass, uncertainty and unknown state codes", () => {
  expect(workbenchLabels.tests).toBe("Tests");
  expect(dockLabels.diff).toBe("Diff");
  expect(comparisonStatus("notEvaluated").tone).toBe("attention");
  expect(comparisonStatus("notEvaluated", "missingBaseline").label).toBe(
    "Expected result needed",
  );
  expect(comparisonStatus("notEvaluated", "unsupportedPolicy").tone).toBe(
    "unavailable",
  );
  expect(fileStatusLabel("valid")).toBe("File accepted");
  expect(fileStatusLabel("future")).toBe("File status not confirmed");
  expect(comparisonStatus("passed").tone).toBe("success");
  expect(comparisonStatus("failed").tone).toBe("failure");
  expect(operationStatus("completed").tone).toBe("neutral");
  expect(buildStatus("uncertain").tone).toBe("uncertain");
  expect(buildStatus("succeeded").label).not.toMatch(
    /in use|verified|approved/i,
  );
  for (const code of ["future", "Passed", "", null, undefined]) {
    expect(comparisonStatus(code).tone).toBe("unavailable");
    expect(operationStatus(code).tone).toBe("unavailable");
  }
});

test("investigation follows only the selected run and clears on case, run or access changes", () => {
  const run = { runId, caseId, version: "1" };
  let state = reduceWorkbenchSelection(emptyWorkbenchSelection("owner"), {
    kind: "run",
    binding: "owner",
    epoch: 0,
    selection: run,
  });
  const entity = { kind: "field" as const, fieldId: "actual-output-field" };
  state = reduceWorkbenchSelection(state, {
    kind: "investigate",
    binding: "owner",
    epoch: 0,
    run,
    entity,
  });
  state = reduceWorkbenchSelection(state, {
    kind: "entity",
    binding: "owner",
    epoch: 0,
    entity: { kind: "rule", ruleId: "declared-rule" },
  });
  expect(state.investigation?.path).toEqual([
    entity,
    { kind: "rule", ruleId: "declared-rule" },
  ]);
  const linked = state;
  for (const action of [
    {
      kind: "case" as const,
      binding: "owner",
      epoch: 0,
      selection: { caseId, version: "2" },
    },
    { kind: "run" as const, binding: "owner", epoch: 0, selection: null },
    { kind: "bind" as const, binding: "another-user" },
    { kind: "clear" as const },
  ])
    expect(reduceWorkbenchSelection(linked, action).investigation).toBeNull();
  expect(
    reduceWorkbenchSelection(linked, {
      kind: "investigate",
      binding: "owner",
      epoch: 0,
      run: { ...run, version: "2" },
      entity,
    }),
  ).toBe(linked);
  expect(
    reduceWorkbenchSelection(linked, {
      kind: "investigate",
      binding: "old-user",
      epoch: 0,
      run,
      entity,
    }),
  ).toBe(linked);
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
