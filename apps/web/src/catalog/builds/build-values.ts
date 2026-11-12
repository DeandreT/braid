import type { components } from "../../api/generated";
import { csvBuildProfile, csvMetadata } from "../previews/csv-contract";
import {
  isCatalogId,
  type CatalogMappingScope,
  type CatalogProjectScope,
} from "../catalog-types";

export type BuildCapabilities = components["schemas"]["BuildCapabilitiesV1"];
export type EnvironmentCapabilities =
  components["schemas"]["BuildEnvironmentCapabilitiesV1"];
export type BuildEnvironment = components["schemas"]["BuildEnvironmentV1"];
export type EnvironmentPage = components["schemas"]["BuildEnvironmentPageV1"];
export type EnvironmentReceipt =
  components["schemas"]["BuildEnvironmentReceiptV1"];
export type EnvironmentDefinition =
  components["schemas"]["BuildEnvironmentPutV1"];
export type BuildProfile = components["schemas"]["BuildRuntimeV1"];
export type BuildApproval = components["schemas"]["BuildApprovalV1"];
export type ApprovalPage = components["schemas"]["BuildApprovalPageV1"];
export type ApprovalDefinition = components["schemas"]["BuildApprovalPutV1"];
export type ApprovalReceipt = components["schemas"]["BuildApprovalReceiptV1"];
export type BuildRun = components["schemas"]["BuildRunV1"];
export type BuildRunPage = components["schemas"]["BuildRunPageV1"];
export type BuildRunReceipt = components["schemas"]["BuildRunReceiptV1"];
export type Obj = Record<string, unknown>;
export type BuildFailure =
  | "unavailable"
  | "notFound"
  | "rejected"
  | "conflict"
  | "invalid"
  | "precondition"
  | "busy"
  | "incompatible";

export class BuildError extends Error {
  constructor(readonly kind: BuildFailure) {
    super("The build request could not be completed.");
  }
}
export function invalid(): never {
  throw new BuildError("incompatible");
}
export function shape(
  value: unknown,
  fields: readonly string[],
  optional: readonly string[] = [],
): value is Obj {
  return (
    !!value &&
    typeof value === "object" &&
    !Array.isArray(value) &&
    Object.keys(value).length ===
      fields.length +
        optional.filter((key) => Object.hasOwn(value, key)).length &&
    fields.every((key) => Object.hasOwn(value, key))
  );
}
export function version(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[1-9][0-9]{0,18}$/.test(value) &&
    BigInt(value) <= 9223372036854775807n
  );
}
export function text(value: unknown, maximum: number): value is string {
  return (
    typeof value === "string" &&
    value.trim().length > 0 &&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value) &&
    new TextEncoder().encode(value).length <= maximum
  );
}
export function utc(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?(?:Z|\+00:00)$/.test(value) &&
    Number.isFinite(Date.parse(value)) &&
    new Date(value).toISOString().slice(0, 19) === value.slice(0, 19)
  );
}
export function oneOf(
  value: unknown,
  values: readonly string[],
): value is string {
  return typeof value === "string" && values.includes(value);
}
export function profile(value: unknown): value is BuildProfile {
  return (
    shape(value, [
      "id",
      "version",
      "runtime",
      "runtimeVersion",
      "artifactFormat",
      "maximumArtifactBytes",
    ]) &&
    text(value.id, 128) &&
    version(value.version) &&
    BigInt(value.version) <= 2147483647n &&
    text(value.runtime, 128) &&
    text(value.runtimeVersion, 128) &&
    text(value.artifactFormat, 128) &&
    integer(value.maximumArtifactBytes, 1, 33554432)
  );
}
export function csvProfile(value: unknown): value is BuildProfile {
  return (
    profile(value) &&
    value.id === csvBuildProfile &&
    value.version === "1" &&
    value.runtime === "dotnet" &&
    value.artifactFormat === "weft.dotnet-assembly.v1"
  );
}
export function project(
  value: unknown,
  scope: CatalogProjectScope,
): value is Obj {
  return (
    !!value &&
    typeof value === "object" &&
    !Array.isArray(value) &&
    (value as Obj).organizationId === scope.organizationId &&
    (value as Obj).projectId === scope.projectId
  );
}
export function revision(
  value: unknown,
  scope: CatalogMappingScope,
): value is Obj {
  return (
    shape(value, ["organizationId", "projectId", "mappingId", "revisionId"]) &&
    project(value, scope) &&
    value.mappingId === scope.mappingId &&
    isCatalogId(value.revisionId)
  );
}
export function environmentReference(
  value: unknown,
  scope: CatalogProjectScope,
): value is Obj {
  return (
    shape(value, ["organizationId", "projectId", "environmentId", "version"]) &&
    project(value, scope) &&
    isCatalogId(value.environmentId) &&
    version(value.version)
  );
}
export function environment(
  value: unknown,
  scope: CatalogProjectScope,
): BuildEnvironment {
  if (
    !shape(value, [
      "reference",
      "profile",
      "active",
      "creatorPrincipalId",
      "createdAtUtc",
    ]) ||
    !environmentReference(value.reference, scope) ||
    !profile(value.profile) ||
    typeof value.active !== "boolean" ||
    !isCatalogId(value.creatorPrincipalId) ||
    !utc(value.createdAtUtc)
  )
    invalid();
  return value as unknown as BuildEnvironment;
}
export function approval(
  value: unknown,
  scope: CatalogMappingScope,
): BuildApproval {
  if (
    !shape(value, [
      "approvalId",
      "buildId",
      "revision",
      "nativeDigest",
      "backend",
      "profile",
      "boundaries",
      "environment",
      "regressionEvidenceId",
      "revoked",
      "environmentActive",
      "createdAtUtc",
    ]) ||
    !isCatalogId(value.approvalId) ||
    !isCatalogId(value.buildId) ||
    !revision(value.revision, scope) ||
    !digest(value.nativeDigest) ||
    !backend(value.backend) ||
    !profile(value.profile) ||
    !environmentReference(value.environment, scope) ||
    !isCatalogId(value.regressionEvidenceId) ||
    typeof value.revoked !== "boolean" ||
    typeof value.environmentActive !== "boolean" ||
    !utc(value.createdAtUtc) ||
    !Array.isArray(value.boundaries) ||
    value.boundaries.length < 2 ||
    value.boundaries.length > 128
  )
    invalid();
  const keys = new Set<string>();
  let inputs = 0,
    outputs = 0;
  for (const item of value.boundaries) {
    if (
      !shape(item, ["kind", "name", "format"], ["csvDialect"]) ||
      !oneOf(item.kind, [
        "primaryInput",
        "namedInput",
        "primaryOutput",
        "namedOutput",
      ]) ||
      !text(item.format, 128)
    )
      invalid();
    if (item.format === "csv" || item.format === "tsv") {
      if (
        value.profile.id !== csvBuildProfile ||
        !csvMetadata(
          item.format === "csv" ? "text/csv" : "text/tab-separated-values",
          item.csvDialect,
        )
      )
        invalid();
    } else if (item.csvDialect !== undefined) invalid();
    const primary =
      item.kind === "primaryInput" || item.kind === "primaryOutput";
    if (primary ? item.name !== null : !text(item.name, 256)) invalid();
    const key = JSON.stringify([item.kind, item.name]);
    if (keys.has(key)) invalid();
    keys.add(key);
    if (item.kind === "primaryInput") inputs++;
    if (item.kind === "primaryOutput") outputs++;
  }
  if (inputs !== 1 || outputs !== 1) invalid();
  if (
    value.profile.id === csvBuildProfile &&
    (value.boundaries.length !== 2 ||
      value.boundaries.some(
        (item) => !["csv", "tsv"].includes(item.format as string),
      ))
  )
    invalid();
  return value as unknown as BuildApproval;
}
export function run(value: unknown, scope: CatalogMappingScope): BuildRun {
  if (
    !shape(value, [
      "buildId",
      "approvalId",
      "revision",
      "state",
      "attempt",
      "createdAtUtc",
      "outcome",
      "artifact",
    ]) ||
    !isCatalogId(value.buildId) ||
    !isCatalogId(value.approvalId) ||
    !revision(value.revision, scope) ||
    !oneOf(value.state, [
      "queued",
      "running",
      "succeeded",
      "failed",
      "canceled",
      "exhausted",
      "revoked",
      "uncertain",
    ]) ||
    !integer(value.attempt, 0, 3) ||
    !utc(value.createdAtUtc)
  )
    invalid();
  if (
    ["running", "succeeded", "failed"].includes(value.state) &&
    value.attempt === 0
  )
    invalid();
  if (["exhausted", "uncertain"].includes(value.state) && value.attempt !== 3)
    invalid();
  if (
    value.state === "succeeded"
      ? !oneOf(value.outcome, ["created", "replayed"])
      : value.state === "failed"
        ? !oneOf(value.outcome, [
            "refused",
            "unsupported",
            "conflict",
            "integrityFailure",
          ])
        : value.outcome !== null
  )
    invalid();
  if (["succeeded", "uncertain"].includes(value.state)) {
    if (
      !shape(value.artifact, ["sha256", "byteCount", "format"]) ||
      !digest(value.artifact.sha256) ||
      !integer(value.artifact.byteCount, 1, 33554432) ||
      !text(value.artifact.format, 128)
    )
      invalid();
  } else if (value.artifact !== null) invalid();
  return value as unknown as BuildRun;
}
export function approvalBody(body: string): ApprovalDefinition {
  const value = bodyValue(body);
  if (
    !shape(value, [
      "buildId",
      "revisionId",
      "environmentId",
      "environmentVersion",
      "regressionEvidenceId",
    ]) ||
    ![
      value.buildId,
      value.revisionId,
      value.environmentId,
      value.regressionEvidenceId,
    ].every(isCatalogId) ||
    !version(value.environmentVersion)
  )
    throw new BuildError("invalid");
  return value as unknown as ApprovalDefinition;
}
export function environmentBody(body: string): EnvironmentDefinition {
  const value = bodyValue(body);
  if (
    !shape(value, ["profileId", "profileVersion"]) ||
    !text(value.profileId, 128) ||
    !version(value.profileVersion)
  )
    throw new BuildError("invalid");
  return value as unknown as EnvironmentDefinition;
}
export function matchesApproval(
  value: BuildApproval,
  body: ApprovalDefinition,
): boolean {
  return (
    value.buildId === body.buildId &&
    value.revision.revisionId === body.revisionId &&
    value.environment.environmentId === body.environmentId &&
    value.environment.version === body.environmentVersion &&
    value.regressionEvidenceId === body.regressionEvidenceId
  );
}
export function active(value: BuildRun): boolean {
  return value.state === "queued" || value.state === "running";
}
export function backend(value: unknown): boolean {
  return (
    shape(value, [
      "backendId",
      "engineRevision",
      "engineBuild",
      "adapterVersion",
    ]) &&
    text(value.backendId, 128) &&
    text(value.engineRevision, 256) &&
    text(value.engineBuild, 256) &&
    text(value.adapterVersion, 128)
  );
}
export function nullableId(value: unknown): boolean {
  return value === null || isCatalogId(value);
}
function digest(value: unknown): boolean {
  return typeof value === "string" && /^[0-9a-f]{64}$/.test(value);
}
function integer(
  value: unknown,
  minimum: number,
  maximum: number,
): value is number {
  return (
    typeof value === "number" &&
    Number.isSafeInteger(value) &&
    value >= minimum &&
    value <= maximum
  );
}
function bodyValue(body: string): unknown {
  if (new TextEncoder().encode(body).length > 2048)
    throw new BuildError("invalid");
  try {
    const value: unknown = JSON.parse(body);
    if (JSON.stringify(value) !== body) throw new BuildError("invalid");
    return value;
  } catch {
    throw new BuildError("invalid");
  }
}
