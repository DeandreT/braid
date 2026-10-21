import {
  HttpClient,
  HttpErrorResponse,
  HttpParams,
  type HttpResponse,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  TimeoutError,
  catchError,
  defer,
  map,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  isCatalogId,
  type CatalogRevision,
  type CatalogRevisionScope,
} from "../catalog-types";
import {
  ComparisonError,
  comparisonAspects,
  comparisonEntities,
  comparisonMatches,
  comparisonReasons,
  type ComparisonRequest,
  type ComparisonSideRequest,
  type RevisionComparison,
} from "./revision-comparison-types";

const scopeKeys = [
  "organizationId",
  "projectId",
  "mappingId",
  "revisionId",
] as const;
const comparisonKeys = [
  "schemaVersion",
  "policyVersion",
  "left",
  "right",
  "leftCoverage",
  "rightCoverage",
  "validationIncluded",
  "leftValidation",
  "rightValidation",
  "hasObservedDifferences",
  "hasUncertainty",
  "totalItemCount",
  "emittedItemCount",
  "truncated",
  "items",
];
const maximumItems = 1024;
const maximumCandidates = 8;
type JsonObject = Record<string, unknown>;
type Expected = {
  readonly revision: CatalogRevisionScope;
  readonly sequence: string;
  readonly digest: string;
  readonly byteCount: string;
  readonly backendId: string;
  readonly projection: string;
  readonly validation: string | null;
};

/** The comparison read names exact retained derivations; it never derives or selects current reports. */
@Injectable({ providedIn: "root" })
export class RevisionComparisonApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  compare(request: ComparisonRequest): Observable<RevisionComparison> {
    let left: Expected, right: Expected;
    try {
      left = expected(request.left);
      right = expected(request.right);
      if (
        left.revision.organizationId !== right.revision.organizationId ||
        left.revision.projectId !== right.revision.projectId ||
        left.revision.mappingId !== right.revision.mappingId
      )
        throw new ComparisonError("invalidLink");
    } catch (error: unknown) {
      return throwError(() => comparisonFailure(error));
    }
    return defer(() => {
      if (!this.sessions.visible()) throw new ComparisonError("rejected");
      const generation = this.sessions.generation();
      const sessionMode = this.sessions.state().kind === "authenticated";
      let params = new HttpParams()
        .set("leftRevision", left.revision.revisionId)
        .set("leftProjection", left.projection);
      if (left.validation !== null)
        params = params.set("leftValidation", left.validation);
      params = params
        .set("rightRevision", right.revision.revisionId)
        .set("rightProjection", right.projection);
      if (right.validation !== null)
        params = params.set("rightValidation", right.validation);
      const scope = left.revision;
      const path = `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/comparisons`;
      return this.http.get<unknown>(path, { params, observe: "response" }).pipe(
        timeout(20000),
        map((response) => {
          if (generation !== this.sessions.generation())
            throw new ComparisonError("unavailable");
          return admitted(response, left, right);
        }),
        catchError((error: unknown) => {
          if (
            generation === this.sessions.generation() &&
            sessionMode &&
            error instanceof HttpErrorResponse &&
            (error.status === 401 || error.status === 403)
          )
            this.sessions.rejectProtectedRequest(generation);
          return throwError(() => comparisonFailure(error));
        }),
      );
    }).pipe(
      catchError((error: unknown) =>
        throwError(() => comparisonFailure(error)),
      ),
    );
  }
}

/** Capture every immutable fact the response must cite before the request can outlive its caller. */
function expected(side: ComparisonSideRequest): Expected {
  const revision: CatalogRevision | undefined = side?.revision;
  const scope = revision?.scope;
  if (
    !scope ||
    !scopeKeys.every((key) => isCatalogId(scope[key])) ||
    revision.nativeDigest?.algorithm !== "sha256" ||
    !digest(revision.nativeDigest.value) ||
    !text(revision.backendId, true) ||
    !positive(revision.sequence) ||
    !Number.isInteger(revision.nativeByteCount) ||
    revision.nativeByteCount < 1 ||
    !isCatalogId(side.projectionDerivationId) ||
    (side.validationDerivationId !== null &&
      !isCatalogId(side.validationDerivationId))
  )
    throw new ComparisonError("invalidLink");
  return {
    revision: {
      organizationId: scope.organizationId,
      projectId: scope.projectId,
      mappingId: scope.mappingId,
      revisionId: scope.revisionId,
    },
    sequence: revision.sequence,
    digest: revision.nativeDigest.value,
    byteCount: String(revision.nativeByteCount),
    backendId: revision.backendId,
    projection: side.projectionDerivationId,
    validation: side.validationDerivationId,
  };
}

function admitted(
  response: HttpResponse<unknown>,
  left: Expected,
  right: Expected,
): RevisionComparison {
  if (
    response.status !== 200 ||
    response.headers.get("Location") !== null ||
    !comparison(response.body, left, right)
  )
    throw new ComparisonError("incompatible", response.status);
  return response.body;
}

function comparison(
  value: unknown,
  left: Expected,
  right: Expected,
): value is RevisionComparison {
  if (
    !object(value) ||
    !keys(value, comparisonKeys) ||
    value["schemaVersion"] !== "weft.revision-comparison.v1" ||
    !text(value["policyVersion"], true) ||
    !citation(value["left"], left) ||
    !citation(value["right"], right) ||
    !coverage(value["leftCoverage"]) ||
    !coverage(value["rightCoverage"]) ||
    typeof value["validationIncluded"] !== "boolean" ||
    value["validationIncluded"] !==
      (left.validation !== null || right.validation !== null) ||
    !summaryFor(value["leftValidation"], left) ||
    !summaryFor(value["rightValidation"], right) ||
    typeof value["hasObservedDifferences"] !== "boolean" ||
    typeof value["hasUncertainty"] !== "boolean" ||
    typeof value["truncated"] !== "boolean" ||
    !count(value["totalItemCount"], Number.MAX_SAFE_INTEGER) ||
    !count(value["emittedItemCount"], maximumItems) ||
    value["emittedItemCount"] > value["totalItemCount"] ||
    !Array.isArray(value["items"]) ||
    value["items"].length !== value["emittedItemCount"]
  )
    return false;
  const items = value["items"] as unknown[];
  let validationItems = 0,
    different = false,
    uncertain = false,
    clipped = false;
  for (const item of items) {
    if (!comparisonItem(item, value)) return false;
    if (item["entity"] === "validation") ++validationItems;
    clipped ||= item["candidatesTruncated"] === true;
    for (const facet of item["facets"] as JsonObject[]) {
      different ||= facet["hasObservedDifference"] === true;
      uncertain ||= facet["hasUncertainty"] === true;
    }
  }
  const complete = value["emittedItemCount"] === value["totalItemCount"];
  const coverageIncomplete =
    (value["leftCoverage"] as JsonObject)["status"] !== "complete" ||
    (value["rightCoverage"] as JsonObject)["status"] !== "complete";
  uncertain ||= coverageIncomplete || value["truncated"] === true;
  // Omitted items can still carry differences, uncertainty or clipped candidates.
  return (
    validationItems <= 1 &&
    (validationItems === 0 || value["validationIncluded"] === true) &&
    (complete
      ? value["truncated"] === clipped &&
        value["hasObservedDifferences"] === different &&
        value["hasUncertainty"] === uncertain
      : value["truncated"] === true &&
        (!different || value["hasObservedDifferences"] === true) &&
        value["hasUncertainty"] === true)
  );
}

function citation(value: unknown, expected: Expected): boolean {
  if (
    !object(value) ||
    !keys(value, [
      "revision",
      "sequence",
      "nativeDigest",
      "nativeByteCount",
      "projectionProvenance",
      "projection",
      "validation",
    ]) ||
    !scope(value["revision"], expected.revision) ||
    value["sequence"] !== expected.sequence ||
    !nativeDigest(value["nativeDigest"], expected.digest) ||
    value["nativeByteCount"] !== expected.byteCount ||
    !report(value["projection"], expected.projection) ||
    (expected.validation === null
      ? value["validation"] !== null
      : !report(value["validation"], expected.validation))
  )
    return false;
  const provenance = value["projectionProvenance"];
  return (
    object(provenance) &&
    keys(provenance, [
      "revision",
      "nativeArtifactDigest",
      "backend",
      "adapterVersion",
      "projectionVersion",
    ]) &&
    scope(provenance["revision"], expected.revision) &&
    nativeDigest(provenance["nativeArtifactDigest"], expected.digest) &&
    object(provenance["backend"]) &&
    keys(provenance["backend"], ["id", "revision", "buildId"]) &&
    provenance["backend"]["id"] === expected.backendId &&
    text(provenance["backend"]["revision"], true) &&
    text(provenance["backend"]["buildId"], true) &&
    text(provenance["adapterVersion"], true) &&
    text(provenance["projectionVersion"], true)
  );
}

function report(value: unknown, derivationId: string): boolean {
  return (
    object(value) &&
    keys(value, ["derivationId", "generation", "keyFingerprint"]) &&
    value["derivationId"] === derivationId &&
    positive(value["generation"]) &&
    digest(value["keyFingerprint"])
  );
}

function summaryFor(value: unknown, expected: Expected): boolean {
  if (expected.validation === null) return value === null;
  return (
    object(value) &&
    keys(value, [
      "isValid",
      "totalIssueCount",
      "emittedIssueCount",
      "truncated",
      "hasTruncatedDiagnosticText",
    ]) &&
    typeof value["isValid"] === "boolean" &&
    count(value["totalIssueCount"], 32768) &&
    count(value["emittedIssueCount"], 128) &&
    value["emittedIssueCount"] <= value["totalIssueCount"] &&
    typeof value["truncated"] === "boolean" &&
    typeof value["hasTruncatedDiagnosticText"] === "boolean"
  );
}

function comparisonItem(
  value: unknown,
  comparison: JsonObject,
): value is JsonObject {
  if (
    !object(value) ||
    !keys(value, [
      "entity",
      "match",
      "leftCandidateCount",
      "rightCandidateCount",
      "candidatesTruncated",
      "left",
      "right",
      "facets",
    ]) ||
    !oneOf(value["entity"], comparisonEntities) ||
    !oneOf(value["match"], comparisonMatches) ||
    !count(value["leftCandidateCount"], Number.MAX_SAFE_INTEGER) ||
    !count(value["rightCandidateCount"], Number.MAX_SAFE_INTEGER) ||
    typeof value["candidatesTruncated"] !== "boolean" ||
    !candidates(value["left"], value["entity"], value["leftCandidateCount"]) ||
    !candidates(
      value["right"],
      value["entity"],
      value["rightCandidateCount"],
    ) ||
    !Array.isArray(value["facets"]) ||
    !value["facets"].every(facet)
  )
    return false;
  const entity = value["entity"],
    match = value["match"];
  const leftCount = value["leftCandidateCount"],
    rightCount = value["rightCandidateCount"];
  const left = value["left"] as unknown[],
    right = value["right"] as unknown[];
  const aspects = (value["facets"] as JsonObject[]).map(
    (item) => item["aspect"],
  );
  if (
    new Set(aspects).size !== aspects.length ||
    value["candidatesTruncated"] !==
      (left.length < leftCount || right.length < rightCount)
  )
    return false;
  if (entity === "validation") {
    const l = comparison["leftValidation"] !== null,
      r = comparison["rightValidation"] !== null;
    return (
      leftCount === 0 &&
      rightCount === 0 &&
      match === (l && r ? "matched" : l ? "onlyLeft" : "onlyRight") &&
      (l || r)
    );
  }
  switch (match) {
    case "matched":
      return leftCount === 1 && rightCount === 1;
    case "onlyLeft":
      return leftCount === 1 && rightCount === 0;
    case "onlyRight":
      return leftCount === 0 && rightCount === 1;
    default:
      return leftCount + rightCount >= 1;
  }
}

function candidates(
  value: unknown,
  entity: string,
  total: number,
): value is JsonObject[] {
  return (
    Array.isArray(value) &&
    value.length <= maximumCandidates &&
    value.length <= total &&
    value.every(
      (item) =>
        object(item) &&
        keys(item, ["node", "relationshipId"]) &&
        (entity === "relationship"
          ? item["node"] === null && text(item["relationshipId"], true)
          : item["relationshipId"] === null &&
            reference(item["node"]) &&
            (item["node"] as JsonObject)["kind"] === entity),
    )
  );
}

function facet(value: unknown): boolean {
  return (
    object(value) &&
    keys(value, [
      "aspect",
      "hasObservedDifference",
      "hasUncertainty",
      "reasons",
    ]) &&
    oneOf(value["aspect"], comparisonAspects) &&
    typeof value["hasObservedDifference"] === "boolean" &&
    typeof value["hasUncertainty"] === "boolean" &&
    Array.isArray(value["reasons"]) &&
    value["reasons"].every((reason) => oneOf(reason, comparisonReasons)) &&
    new Set(value["reasons"]).size === value["reasons"].length &&
    (value["reasons"].length === 0 || value["hasUncertainty"] === true)
  );
}

function coverage(value: unknown): boolean {
  return (
    object(value) &&
    keys(value, ["status", "gaps"]) &&
    oneOf(value["status"], ["complete", "partial", "unavailable"]) &&
    Array.isArray(value["gaps"]) &&
    value["gaps"].every(
      (gap) =>
        object(gap) &&
        text(gap["code"], true) &&
        text(gap["message"]) &&
        locator(gap["nativeLocator"]) &&
        (gap["projectionLocator"] == null ||
          reference(gap["projectionLocator"])),
    )
  );
}

function locator(value: unknown): boolean {
  if (!object(value) || !text(value["documentId"], true)) return false;
  switch (value["kind"]) {
    case "document":
      return true;
    case "jsonPointer":
      return typeof value["pointer"] === "string";
    case "graphNode":
      return (
        text(value["nodeId"], true) &&
        (value["functionId"] == null || text(value["functionId"], true))
      );
    case "opaque":
      return text(value["scheme"], true) && text(value["value"]);
    case "textSpan":
      return (
        unsigned(value["startOffset"]) &&
        unsigned(value["length"]) &&
        (value["unit"] === undefined ||
          oneOf(value["unit"], [
            "utf8Bytes",
            "utf16CodeUnits",
            "unicodeScalars",
          ]))
      );
    default:
      return false;
  }
}

function reference(value: unknown): boolean {
  if (!object(value) || !keys(value, ["kind", `${value["kind"]}Id`]))
    return false;
  switch (value["kind"]) {
    case "boundary":
    case "field":
    case "rule":
    case "parameter":
    case "control":
      return text(value[`${value["kind"]}Id`], true);
    default:
      return false;
  }
}

function nativeDigest(value: unknown, expected: string): boolean {
  return (
    object(value) &&
    keys(value, ["algorithm", "value"]) &&
    value["algorithm"] === "sha256" &&
    value["value"] === expected
  );
}

export function comparisonFailure(error: unknown): ComparisonError {
  if (error instanceof ComparisonError) return error;
  if (error instanceof TimeoutError) return new ComparisonError("timedOut");
  if (!(error instanceof HttpErrorResponse))
    return new ComparisonError("unavailable");
  const status = error.status,
    code: unknown = object(error.error) ? error.error["code"] : null;
  if (status === 0) return new ComparisonError("unavailable", status);
  if (status === 404 && (code === "notFound" || code === "unavailable"))
    return new ComparisonError("notFound", status);
  if ((status === 401 || status === 403) && code === "identityRejected")
    return new ComparisonError("rejected", status);
  if (status === 400 && code === "invalidRequest")
    return new ComparisonError("invalidLink", status);
  if (status === 429 && code === "revisionReportAdmissionBusy")
    return new ComparisonError("admissionBusy", status);
  if (status === 503 && code === "revisionReportUnavailable")
    return new ComparisonError("dependencyUnavailable", status);
  if (status === 503 && code === "revisionReportResourceLimit")
    return new ComparisonError("resourceLimit", status);
  if (
    status === 500 &&
    (code === "internalFailure" || code === "persistenceFailure")
  )
    return new ComparisonError("unavailable", status);
  return new ComparisonError("incompatible", status);
}

function object(value: unknown): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function keys(value: JsonObject, expected: readonly string[]): boolean {
  return (
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key))
  );
}
function scope(value: unknown, expected: CatalogRevisionScope): boolean {
  return (
    object(value) &&
    keys(value, scopeKeys) &&
    scopeKeys.every((key) => value[key] === expected[key])
  );
}
function oneOf(value: unknown, values: readonly string[]): value is string {
  return typeof value === "string" && values.includes(value);
}
function text(value: unknown, nonblank = false): value is string {
  return (
    typeof value === "string" &&
    value.length <= 2048 &&
    (!nonblank || value.length > 0)
  );
}
function digest(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{64}$/.test(value);
}
function positive(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[1-9][0-9]{0,18}$/.test(value) &&
    (value.length < 19 || value <= "9223372036854775807")
  );
}
function unsigned(value: unknown): value is string {
  return typeof value === "string" && /^(?:0|[1-9][0-9]*)$/.test(value);
}
function count(value: unknown, maximum: number): value is number {
  return (
    typeof value === "number" &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= maximum
  );
}
