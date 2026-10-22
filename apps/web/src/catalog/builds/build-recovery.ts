import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
  type CatalogRevision,
  type CatalogRevisionScope,
} from "../catalog-types";
import {
  BuildError,
  active,
  approvalBody,
  environmentBody,
  matchesApproval,
  version as validVersion,
  type BuildApproval,
  type BuildEnvironment,
  type BuildProfile,
  type BuildRun,
} from "./build-values";

export interface BuildOwner {
  readonly identity: string;
  readonly scope: Readonly<CatalogRevisionScope>;
  readonly nativeDigest: string;
}
export type PendingBuildMutation = Readonly<
  | {
      kind: "environment";
      owner: BuildOwner;
      id: string;
      version: string;
      body: string;
    }
  | { kind: "approval"; owner: BuildOwner; id: string; body: string }
  | {
      kind: "submit";
      owner: BuildOwner;
      id: string;
      body: string;
      approvalId: string;
    }
  | { kind: "cancel"; owner: BuildOwner; id: string }
  | { kind: "revoke"; owner: BuildOwner; id: string }
>;

/** Recovery frames contain only immutable metadata in view memory, never a reusable credential or payload. */
export function buildOwner(
  context: CatalogContext,
  revision: CatalogRevision,
): BuildOwner {
  const scope = Object.freeze({ ...revision.scope });
  if (
    ![
      scope.organizationId,
      scope.projectId,
      scope.mappingId,
      scope.revisionId,
      context.actorPrincipalId,
    ].every(isCatalogId) ||
    context.allowedOrganizationId !== scope.organizationId ||
    revision.nativeDigest.algorithm !== "sha256" ||
    !/^[0-9a-f]{64}$/.test(revision.nativeDigest.value)
  )
    throw new BuildError("invalid");
  return Object.freeze({
    identity: catalogOwner(context),
    scope,
    nativeDigest: revision.nativeDigest.value,
  });
}
export function sameOwner(left: BuildOwner, right: BuildOwner): boolean {
  return (
    left.identity === right.identity &&
    left.nativeDigest === right.nativeDigest &&
    sameRevision(left.scope, right.scope)
  );
}
export function sameRevision(
  left: CatalogRevisionScope,
  right: CatalogRevisionScope,
): boolean {
  return (
    left.organizationId === right.organizationId &&
    left.projectId === right.projectId &&
    left.mappingId === right.mappingId &&
    left.revisionId === right.revisionId
  );
}
export function captureEnvironment(
  owner: BuildOwner,
  profile: BuildProfile,
  previous: BuildEnvironment | null,
  nextId: () => string = () => crypto.randomUUID(),
): PendingBuildMutation {
  if (
    previous &&
    (!previous.active ||
      previous.reference.organizationId !== owner.scope.organizationId ||
      previous.reference.projectId !== owner.scope.projectId ||
      !validVersion(previous.reference.version) ||
      BigInt(previous.reference.version) >= 9223372036854775807n)
  )
    throw new BuildError("precondition");
  const id = previous?.reference.environmentId ?? checkedId(nextId());
  const version = previous
    ? String(BigInt(previous.reference.version) + 1n)
    : "1";
  const body = JSON.stringify({
    profileId: profile.id,
    profileVersion: profile.version,
  });
  environmentBody(body);
  return Object.freeze({ kind: "environment", owner, id, version, body });
}
export function captureApproval(
  owner: BuildOwner,
  environment: BuildEnvironment,
  evidenceId: string,
  nextId: () => string = () => crypto.randomUUID(),
): PendingBuildMutation {
  if (
    !environment.active ||
    environment.reference.organizationId !== owner.scope.organizationId ||
    environment.reference.projectId !== owner.scope.projectId
  )
    throw new BuildError("precondition");
  const body = JSON.stringify({
    buildId: checkedId(nextId()),
    revisionId: owner.scope.revisionId,
    environmentId: environment.reference.environmentId,
    environmentVersion: environment.reference.version,
    regressionEvidenceId: checkedId(evidenceId),
  });
  approvalBody(body);
  return Object.freeze({
    kind: "approval",
    owner,
    id: checkedId(nextId()),
    body,
  });
}
export function captureSubmission(
  owner: BuildOwner,
  approval: BuildApproval,
): PendingBuildMutation {
  if (
    !approvalBelongs(owner, approval) ||
    approval.revoked ||
    !approval.environmentActive
  )
    throw new BuildError("precondition");
  checkedId(approval.approvalId);
  return Object.freeze({
    kind: "submit",
    owner,
    id: checkedId(approval.buildId),
    approvalId: approval.approvalId,
    body: JSON.stringify({ approvalId: approval.approvalId }),
  });
}
export function captureCancellation(
  owner: BuildOwner,
  run: BuildRun,
): PendingBuildMutation {
  if (!sameRevision(owner.scope, run.revision) || !active(run))
    throw new BuildError("precondition");
  return Object.freeze({ kind: "cancel", owner, id: checkedId(run.buildId) });
}
export function captureRevocation(
  owner: BuildOwner,
  approval: BuildApproval,
): PendingBuildMutation {
  if (!approvalBelongs(owner, approval) || approval.revoked)
    throw new BuildError("precondition");
  return Object.freeze({
    kind: "revoke",
    owner,
    id: checkedId(approval.approvalId),
  });
}
export function recoveredEnvironment(
  pending: PendingBuildMutation,
  value: BuildEnvironment,
): boolean {
  if (pending.kind !== "environment") return false;
  const selector = environmentBody(pending.body);
  return (
    value.reference.organizationId === pending.owner.scope.organizationId &&
    value.reference.projectId === pending.owner.scope.projectId &&
    value.reference.environmentId === pending.id &&
    value.reference.version === pending.version &&
    value.profile.id === selector.profileId &&
    value.profile.version === selector.profileVersion
  );
}
export function recoveredApproval(
  pending: PendingBuildMutation,
  value: BuildApproval,
): boolean {
  if (!approvalBelongs(pending.owner, value) || value.approvalId !== pending.id)
    return false;
  return pending.kind === "approval"
    ? matchesApproval(value, approvalBody(pending.body))
    : pending.kind === "revoke" && value.revoked;
}
export function recoveredRun(
  pending: PendingBuildMutation,
  value: BuildRun,
): boolean {
  if (
    !sameRevision(pending.owner.scope, value.revision) ||
    value.buildId !== pending.id
  )
    return false;
  return pending.kind === "submit"
    ? value.approvalId === pending.approvalId
    : pending.kind === "cancel" && !active(value);
}
function approvalBelongs(owner: BuildOwner, value: BuildApproval): boolean {
  return (
    sameRevision(owner.scope, value.revision) &&
    value.nativeDigest === owner.nativeDigest
  );
}
function checkedId(value: string): string {
  if (!isCatalogId(value)) throw new BuildError("invalid");
  return value;
}
