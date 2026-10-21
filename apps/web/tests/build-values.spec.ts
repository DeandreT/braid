import { test, expect } from "@playwright/test";
import {
  BuildError,
  approval,
  approvalBody,
  run,
  version,
  type BuildApproval,
  type BuildEnvironment,
  type BuildProfile,
  type BuildRun,
} from "../src/catalog/builds/build-values";
import {
  buildOwner,
  captureApproval,
  captureCancellation,
  captureEnvironment,
  captureSubmission,
  recoveredApproval,
  recoveredEnvironment,
  recoveredRun,
  sameOwner,
} from "../src/catalog/builds/build-recovery";
import { synthetic } from "./catalog.fixture";

const ENV = "70000000-0000-4000-8000-000000000001";
const APPROVAL = "70000000-0000-4000-8000-000000000002";
const BUILD = "70000000-0000-4000-8000-000000000003";
const EVIDENCE = "70000000-0000-4000-8000-000000000004";
const OTHER = "70000000-0000-4000-8000-000000000005";
const scope = synthetic.revision.scope;
const profile: BuildProfile = {
  id: "synthetic-build-profile",
  version: "1",
  runtime: "dotnet",
  runtimeVersion: "10.0.12",
  artifactFormat: "dotnet-library",
  maximumArtifactBytes: 1048576,
};
const owner = buildOwner(synthetic.context, synthetic.revision);
function environment(): BuildEnvironment {
  return {
    reference: {
      organizationId: scope.organizationId,
      projectId: scope.projectId,
      environmentId: ENV,
      version: "1",
    },
    profile: { ...profile },
    active: true,
    creatorPrincipalId: synthetic.actor,
    createdAtUtc: "2026-10-20T12:00:00Z",
  };
}
function approved(): BuildApproval {
  return {
    approvalId: APPROVAL,
    buildId: BUILD,
    revision: { ...scope },
    nativeDigest: synthetic.revision.nativeDigest.value,
    backend: {
      backendId: "ferrule",
      engineRevision: "engine",
      engineBuild: "build",
      adapterVersion: "adapter",
    },
    profile: { ...profile },
    boundaries: [
      { kind: "primaryInput", name: null, format: "json" },
      { kind: "primaryOutput", name: null, format: "json" },
    ],
    environment: environment().reference,
    regressionEvidenceId: EVIDENCE,
    revoked: false,
    environmentActive: true,
    createdAtUtc: "2026-10-20T12:00:00Z",
  };
}
function queued(): BuildRun {
  return {
    buildId: BUILD,
    approvalId: APPROVAL,
    revision: { ...scope },
    state: "queued",
    attempt: 0,
    createdAtUtc: "2026-10-20T12:00:00Z",
    outcome: null,
    artifact: null,
  };
}
function capturedApproval() {
  const ids = [BUILD, APPROVAL];
  return captureApproval(owner, environment(), EVIDENCE, () => ids.shift()!);
}

test("exact retained approval metadata is accepted", () => {
  expect(approval(approved(), scope).approvalId).toBe(APPROVAL);
});
test("foreign mapping metadata and private artifact-body fields are refused", () => {
  const foreign = approved();
  foreign.revision.mappingId = OTHER;
  expect(() => approval(foreign, scope)).toThrow(BuildError);
  expect(() => run({ ...queued(), artifactBody: "private" }, scope)).toThrow(
    BuildError,
  );
});
test("unknown states and incomplete primary boundary inventories are refused", () => {
  expect(() => run({ ...queued(), state: "publishedMaybe" }, scope)).toThrow(
    BuildError,
  );
  const value = approved();
  value.boundaries.pop();
  expect(() => approval(value, scope)).toThrow(BuildError);
});
test("uncertain retains artifact metadata but cannot claim a passing outcome", () => {
  const value = {
    ...queued(),
    state: "uncertain",
    attempt: 3,
    artifact: {
      sha256: "b".repeat(64),
      byteCount: 1024,
      format: profile.artifactFormat,
    },
  };
  expect(run(value, scope).state).toBe("uncertain");
  expect(() => run({ ...value, outcome: "created" }, scope)).toThrow(
    BuildError,
  );
  expect(() => run({ ...value, attempt: 2 }, scope)).toThrow(BuildError);
});
for (const value of ["0", "01", "+1", "9223372036854775808"]) {
  test(`noncanonical or overflowing version ${value} is refused`, () =>
    expect(version(value)).toBe(false));
}
test("duplicate original request fields are refused", () => {
  const pending = capturedApproval();
  if (pending.kind !== "approval") throw new Error("Wrong test frame.");
  const duplicate = pending.body.slice(0, -1) + `,"buildId":"${OTHER}"}`;
  expect(() => approvalBody(duplicate)).toThrow(BuildError);
});
test("captured selectors and owner scope cannot follow later view edits", () => {
  const selected = environment();
  const ids = [BUILD, APPROVAL];
  const pending = captureApproval(owner, selected, EVIDENCE, () =>
    ids.shift()!,
  );
  selected.reference.version = "2";
  expect(pending.kind).toBe("approval");
  expect(Object.isFrozen(pending)).toBe(true);
  expect(Object.isFrozen(pending.owner.scope)).toBe(true);
  if (pending.kind !== "approval") throw new Error("Wrong test frame.");
  expect(approvalBody(pending.body).environmentVersion).toBe("1");
  expect(pending.owner.identity).not.toContain(synthetic.context.requestToken);
});
test("revoked and superseded approval recovery preserves the original request", () => {
  const pending = capturedApproval();
  const retained = { ...approved(), revoked: true, environmentActive: false };
  expect(recoveredApproval(pending, retained)).toBe(true);
  expect(
    recoveredApproval(pending, { ...retained, regressionEvidenceId: OTHER }),
  ).toBe(false);
});
test("submission uses the approved build ID and remains recoverable after revocation", () => {
  const original = approved();
  const pending = captureSubmission(owner, original);
  original.revoked = true;
  expect(pending.id).toBe(BUILD);
  expect(recoveredRun(pending, queued())).toBe(true);
  expect(recoveredRun(pending, { ...queued(), approvalId: OTHER })).toBe(false);
});
test("a terminal result wins a cancellation race without being relabeled canceled", () => {
  const pending = captureCancellation(owner, queued());
  const succeeded = {
    ...queued(),
    state: "succeeded",
    attempt: 1,
    outcome: "created",
    artifact: {
      sha256: "b".repeat(64),
      byteCount: 1024,
      format: profile.artifactFormat,
    },
  };
  expect(recoveredRun(pending, run(succeeded, scope))).toBe(true);
  expect(succeeded.state).toBe("succeeded");
  expect(recoveredRun(pending, queued())).toBe(false);
});
test("retained environment recovery does not require current runtime facts or reactivate it", () => {
  const pending = captureEnvironment(owner, profile, null, () => ENV);
  const retained = environment();
  retained.active = false;
  retained.profile.runtimeVersion = "earlier-runtime";
  expect(recoveredEnvironment(pending, retained)).toBe(true);
  expect(retained.active).toBe(false);
});
test("the maximum environment version cannot overflow into a replacement request", () => {
  const previous = environment();
  previous.reference.version = "9223372036854775807";
  expect(() =>
    captureEnvironment(owner, profile, previous, () => OTHER),
  ).toThrow(BuildError);
});
test("an actor or revision change invalidates the recovery owner", () => {
  expect(
    sameOwner(
      owner,
      buildOwner(
        { ...synthetic.context, actorPrincipalId: OTHER },
        synthetic.revision,
      ),
    ),
  ).toBe(false);
  const changed = structuredClone(synthetic.revision);
  changed.scope.revisionId = OTHER;
  expect(sameOwner(owner, buildOwner(synthetic.context, changed))).toBe(false);
});
