import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
  HttpParams,
  type HttpResponse,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  TimeoutError,
  catchError,
  defer,
  map,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import {
  isCatalogId,
  type CatalogRevision,
  type CatalogRevisionScope,
} from "../catalog-types";
import {
  RevisionReportError,
  type CatalogProjectionReport,
  type CatalogValidationReport,
  type RevisionReport,
  type RevisionReportOperation,
  type RevisionReportResponse,
} from "./revision-report-types";

const statuses = [
  "succeeded",
  "invalidMapping",
  "unsupportedArtifact",
  "resourceLimit",
  "protocolFailure",
  "backendUnavailable",
  "backendFailure",
  "timedOut",
  "canceled",
  "isolationFailure",
];
const failures = [
  "objectUnavailable",
  "storageIntegrity",
  "storageIo",
  "backendUnavailable",
  "backendFailure",
  "timedOut",
  "canceled",
  "isolationFailure",
  "protocolFailure",
  "profileUnavailable",
  "resourceLimit",
  "unsupportedArtifact",
];
const outcomes = [
  "observed",
  "objectUnavailable",
  "storageIntegrity",
  "unsupportedFormat",
  "resourceLimit",
];
const scopeKeys = [
  "organizationId",
  "projectId",
  "mappingId",
  "revisionId",
] as const;
const wrapperKeys = [
  "schemaVersion",
  "revision",
  "operation",
  "state",
  "readOutcome",
  "status",
  "result",
  "currentAttempt",
  "nextAttemptAtUtc",
  "failure",
  "report",
];
type JsonObject = Record<string, unknown>;
type Expected = {
  scope: CatalogRevisionScope;
  digest: string;
  backendId: string;
  logicalDocument: string;
};

@Injectable({ providedIn: "root" })
export class RevisionReportApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  projection(
    expected: CatalogRevision,
    retainedId: string | null = null,
  ): Observable<RevisionReportResponse<CatalogProjectionReport>> {
    return this.read<CatalogProjectionReport>(
      expected,
      "projection",
      retainedId,
      null,
    );
  }

  validation(
    expected: CatalogRevision,
    retainedId: string | null = null,
    projectionDerivationId: string | null = null,
  ): Observable<RevisionReportResponse<CatalogValidationReport>> {
    return this.read<CatalogValidationReport>(
      expected,
      "validation",
      retainedId,
      projectionDerivationId,
    );
  }

  ensureProjection(
    expected: CatalogRevision,
    requestToken: string,
    repairDerivationId: string | null = null,
  ): Observable<RevisionReportResponse<CatalogProjectionReport>> {
    return this.ensure<CatalogProjectionReport>(
      expected,
      "projection",
      requestToken,
      repairDerivationId,
    );
  }

  ensureValidation(
    expected: CatalogRevision,
    requestToken: string,
    repairDerivationId: string | null = null,
  ): Observable<RevisionReportResponse<CatalogValidationReport>> {
    return this.ensure<CatalogValidationReport>(
      expected,
      "validation",
      requestToken,
      repairDerivationId,
    );
  }

  private read<T extends RevisionReport>(
    revision: CatalogRevision,
    operation: RevisionReportOperation,
    retainedId: string | null,
    projectionId: string | null,
  ): Observable<RevisionReportResponse<T>> {
    return captured(revision, (expected) => {
      if (!this.sessions.visible()) throw new RevisionReportError("rejected");
      const generation = this.sessions.generation();
      const sessionMode = this.sessions.state().kind === "authenticated";
      selector(retainedId);
      selector(projectionId);
      let params = new HttpParams();
      if (projectionId !== null)
        params = params.set("projectionDerivationId", projectionId);
      const path =
        currentPath(expected.scope, operation) +
        (retainedId === null ? "" : `/${retainedId}`);
      return this.http.get<unknown>(path, { params, observe: "response" }).pipe(
        timeout(8000),
        map((response) => {
          if (generation !== this.sessions.generation())
            throw new RevisionReportError("unavailable");
          return admitted<T>(
            response,
            expected,
            operation,
            retainedId,
            projectionId,
            false,
          );
        }),
        catchError((error: unknown) =>
          this.failure(error, generation, sessionMode),
        ),
      );
    });
  }

  private ensure<T extends RevisionReport>(
    revision: CatalogRevision,
    operation: RevisionReportOperation,
    requestToken: string,
    repairId: string | null,
  ): Observable<RevisionReportResponse<T>> {
    return captured(revision, (expected) => {
      if (!this.sessions.visible()) throw new RevisionReportError("rejected");
      const generation = this.sessions.generation();
      const sessionMode = this.sessions.state().kind === "authenticated";
      selector(repairId);
      if (
        typeof requestToken !== "string" ||
        requestToken.length > 4096 ||
        requestToken.trim().length === 0 ||
        /[\0\r\n]/.test(requestToken)
      )
        throw new RevisionReportError("invalidLink");
      let params = new HttpParams();
      if (repairId !== null)
        params = params.set("repairDerivationId", repairId);
      // null is a genuinely empty entity: no JSON, media, conditional or idempotency headers.
      return this.http
        .post<unknown>(
          currentPath(expected.scope, operation) + "/ensure",
          null,
          {
            observe: "response",
            params,
            headers: new HttpHeaders({ "X-XSRF-TOKEN": requestToken }),
          },
        )
        .pipe(
          timeout(45000),
          map((response) => {
            if (generation !== this.sessions.generation())
              throw new RevisionReportError("unavailable");
            return admitted<T>(response, expected, operation, null, null, true);
          }),
          catchError((error: unknown) =>
            this.failure(error, generation, sessionMode),
          ),
        );
    });
  }
  private failure(
    error: unknown,
    generation: number,
    sessionMode: boolean,
  ): Observable<never> {
    if (
      generation === this.sessions.generation() &&
      sessionMode &&
      error instanceof HttpErrorResponse &&
      (error.status === 401 || error.status === 403)
    )
      this.sessions.rejectProtectedRequest(generation);
    return throwError(() => reportFailure(error));
  }
}

function captured<T>(
  revision: CatalogRevision,
  request: (expected: Expected) => Observable<T>,
): Observable<T> {
  try {
    const scope = revision?.scope;
    if (
      !scope ||
      !scopeKeys.every((key) => isCatalogId(scope[key])) ||
      revision.nativeDigest?.algorithm !== "sha256" ||
      !digest(revision.nativeDigest.value) ||
      !text(revision.backendId, true) ||
      revision.logicalDocument !== "entry.native.json"
    )
      throw new RevisionReportError("invalidLink");
    const expected: Expected = {
      scope: {
        organizationId: scope.organizationId,
        projectId: scope.projectId,
        mappingId: scope.mappingId,
        revisionId: scope.revisionId,
      },
      digest: revision.nativeDigest.value,
      backendId: revision.backendId,
      logicalDocument: revision.logicalDocument,
    };
    return defer(() => request(expected)).pipe(
      catchError((error: unknown) => throwError(() => reportFailure(error))),
    );
  } catch (error: unknown) {
    return throwError(() => reportFailure(error));
  }
}

function selector(value: string | null): void {
  if (value !== null && !isCatalogId(value))
    throw new RevisionReportError("invalidLink");
}

function currentPath(
  scope: CatalogRevisionScope,
  operation: RevisionReportOperation,
): string {
  return `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}/reports/${operation}`;
}

function admitted<T extends RevisionReport>(
  response: HttpResponse<unknown>,
  expected: Expected,
  operation: RevisionReportOperation,
  retainedId: string | null,
  projectionId: string | null,
  ensure: boolean,
): RevisionReportResponse<T> {
  const status = response.status;
  if (
    (status !== 200 && status !== 202) ||
    (!ensure && status !== 200) ||
    !wrapper(response.body, expected, operation, retainedId, projectionId)
  )
    throw new RevisionReportError("incompatible", status);
  const body = response.body as T;
  const location = response.headers.get("Location");
  const retry = response.headers.get("Retry-After");
  if (status === 202) {
    if (
      location !== currentPath(expected.scope, operation) ||
      retry === null ||
      !/^[1-9][0-9]{0,9}$/.test(retry) ||
      Number(retry) > 2147483647 ||
      (body.state !== "pending" && body.state !== "failed")
    )
      throw new RevisionReportError("incompatible", status);
    return { status, body, location, retryAfterSeconds: Number(retry) };
  }
  if (location !== null || retry !== null)
    throw new RevisionReportError("incompatible", status);
  return { status, body, location: null, retryAfterSeconds: null };
}

function wrapper(
  value: unknown,
  expected: Expected,
  operation: RevisionReportOperation,
  retainedId: string | null,
  projectionId: string | null,
): value is RevisionReport {
  if (
    !object(value) ||
    !keys(
      value,
      operation === "validation"
        ? [...wrapperKeys, "projectionDerivationId"]
        : wrapperKeys,
    ) ||
    value["schemaVersion"] !== "weft.catalog-revision-report.v1" ||
    value["operation"] !== operation ||
    !scope(value["revision"], expected.scope) ||
    !oneOf(value["readOutcome"], outcomes) ||
    !nullable(value["status"], (item) => oneOf(item, statuses)) ||
    !nullable(value["failure"], (item) => oneOf(item, failures)) ||
    !nullable(value["nextAttemptAtUtc"], utc) ||
    !nullable(value["result"], result) ||
    !nullable(value["currentAttempt"], attempt)
  )
    return false;
  const state = value["state"],
    selected = value["result"],
    active = value["currentAttempt"];
  const report = value["report"],
    hasReport = report !== null;
  if (
    retainedId !== null &&
    (!object(selected) ||
      selected["derivationId"] !== retainedId ||
      state !== "available")
  )
    return false;
  if (state === "notDerived") {
    if (
      selected !== null ||
      active !== null ||
      value["status"] !== null ||
      value["failure"] !== null ||
      value["nextAttemptAtUtc"] !== null ||
      hasReport
    )
      return false;
  } else if (state === "pending") {
    if (
      selected !== null ||
      !object(active) ||
      active["finishedAtUtc"] !== null ||
      active["status"] !== null ||
      active["failure"] !== null ||
      value["status"] !== null ||
      value["failure"] !== null ||
      value["nextAttemptAtUtc"] === null ||
      hasReport
    )
      return false;
  } else if (state === "failed") {
    if (
      selected !== null ||
      !object(active) ||
      active["finishedAtUtc"] === null ||
      active["failure"] === null ||
      value["failure"] !== active["failure"] ||
      value["status"] !== active["status"] ||
      value["nextAttemptAtUtc"] === null ||
      hasReport
    )
      return false;
  } else if (state === "available") {
    if (
      selected === null ||
      active !== null ||
      value["nextAttemptAtUtc"] !== null ||
      value["failure"] !== null ||
      (operation === "projection"
        ? value["status"] !== "succeeded"
        : !["succeeded", "invalidMapping"].includes(value["status"] as string))
    )
      return false;
  } else return false;
  if (
    object(active) &&
    value["nextAttemptAtUtc"] !== null &&
    compareUtc(
      value["nextAttemptAtUtc"] as string,
      (active["finishedAtUtc"] ?? active["startedAtUtc"]) as string,
    ) < 0
  )
    return false;
  if (value["readOutcome"] === "observed") {
    if ((state === "available") !== hasReport) return false;
  } else if (
    hasReport ||
    (value["readOutcome"] !== "resourceLimit" && state !== "available")
  )
    return false;
  if (operation === "validation") {
    const linked = value["projectionDerivationId"];
    if (
      !nullable(linked, isCatalogId) ||
      (linked !== null && linked !== projectionId) ||
      (linked !== null &&
        !hasReport &&
        (value["readOutcome"] !== "resourceLimit" || state !== "available")) ||
      (hasReport && linked !== projectionId)
    )
      return false;
  }
  return (
    !hasReport ||
    (object(report) &&
      (operation === "projection"
        ? projection(report, expected, value["status"])
        : validation(report, expected, value["status"], projectionId)))
  );
}

function result(value: unknown): boolean {
  return (
    object(value) &&
    keys(value, [
      "derivationId",
      "generation",
      "startedAtUtc",
      "finishedAtUtc",
    ]) &&
    isCatalogId(value["derivationId"]) &&
    positive(value["generation"]) &&
    utc(value["startedAtUtc"]) &&
    utc(value["finishedAtUtc"]) &&
    compareUtc(value["finishedAtUtc"], value["startedAtUtc"]) >= 0
  );
}

function attempt(value: unknown): boolean {
  if (
    !object(value) ||
    !keys(value, [
      "derivationId",
      "generation",
      "startedAtUtc",
      "finishedAtUtc",
      "status",
      "failure",
    ]) ||
    !isCatalogId(value["derivationId"]) ||
    !positive(value["generation"]) ||
    !utc(value["startedAtUtc"]) ||
    !nullable(value["finishedAtUtc"], utc) ||
    !nullable(value["status"], (item) => oneOf(item, statuses)) ||
    !nullable(value["failure"], (item) => oneOf(item, failures))
  )
    return false;
  return value["finishedAtUtc"] === null
    ? value["status"] === null && value["failure"] === null
    : compareUtc(value["finishedAtUtc"] as string, value["startedAtUtc"]) >= 0;
}

function projection(
  value: JsonObject,
  expected: Expected,
  status: unknown,
): boolean {
  if (
    !keys(value, ["status", "provenance", "document", "diagnostics"]) ||
    value["status"] !== status ||
    status !== "succeeded" ||
    !provenance(value["provenance"], expected, true) ||
    !diagnostics(value["diagnostics"])
  )
    return false;
  const document = value["document"];
  if (
    !object(document) ||
    document["schemaVersion"] !== "weft.mapping-projection.v1" ||
    !provenance(document["provenance"], expected, false) ||
    !object(value["provenance"]) ||
    !object(document["provenance"]) ||
    !text(document["provenance"]["projectionVersion"], true) ||
    !sameBackend(document["provenance"], value["provenance"]) ||
    !object(document["coverage"]) ||
    !oneOf(document["coverage"]["status"], [
      "complete",
      "partial",
      "unavailable",
    ]) ||
    !Array.isArray(document["coverage"]["gaps"]) ||
    !document["coverage"]["gaps"].every(
      (gap) =>
        object(gap) &&
        text(gap["code"], true) &&
        text(gap["message"]) &&
        locator(gap["nativeLocator"]) &&
        (gap["projectionLocator"] == null ||
          reference(gap["projectionLocator"])),
    )
  )
    return false;
  // The qualified server owns full graph/profile validation. Guard the renderer's
  // collection boundary here without parsing native data or rebuilding a cache key.
  return [
    "boundaries",
    "fields",
    "rules",
    "parameters",
    "controls",
    "relationships",
    "capabilities",
    "diagnostics",
  ].every((key) => Array.isArray(document[key]) && document[key].every(object));
}

function validation(
  value: JsonObject,
  expected: Expected,
  status: unknown,
  projectionId: string | null,
): boolean {
  if (
    !keys(value, [
      "status",
      "provenance",
      "isValid",
      "totalIssueCount",
      "emittedIssueCount",
      "truncated",
      "diagnostics",
    ]) ||
    value["status"] !== status ||
    typeof value["isValid"] !== "boolean" ||
    value["isValid"] !== (status === "succeeded") ||
    !provenance(value["provenance"], expected, true) ||
    !count(value["totalIssueCount"], 32768) ||
    !count(value["emittedIssueCount"], 128) ||
    value["emittedIssueCount"] > value["totalIssueCount"] ||
    typeof value["truncated"] !== "boolean" ||
    !diagnostics(value["diagnostics"]) ||
    value["diagnostics"].length !== value["emittedIssueCount"]
  )
    return false;
  return value["diagnostics"].every(
    (item) =>
      object(item) &&
      typeof item["locationTruncated"] === "boolean" &&
      (item["location"] === undefined ||
        item["location"] === null ||
        text(item["location"])) &&
      (item["projectionLocator"] === undefined ||
        item["projectionLocator"] === null ||
        (projectionId !== null && reference(item["projectionLocator"]))),
  );
}

function provenance(
  value: unknown,
  expected: Expected,
  operation: boolean,
): boolean {
  return (
    object(value) &&
    scope(value["revision"], expected.scope) &&
    object(value["nativeArtifactDigest"]) &&
    value["nativeArtifactDigest"]["algorithm"] === "sha256" &&
    value["nativeArtifactDigest"]["value"] === expected.digest &&
    object(value["backend"]) &&
    value["backend"]["id"] === expected.backendId &&
    text(value["backend"]["revision"], true) &&
    text(value["backend"]["buildId"], true) &&
    text(value["adapterVersion"], true) &&
    (!operation || value["confirmed"] === true)
  );
}

function sameBackend(left: JsonObject, right: JsonObject): boolean {
  return (
    object(left["backend"]) &&
    object(right["backend"]) &&
    left["adapterVersion"] === right["adapterVersion"] &&
    ["id", "revision", "buildId"].every(
      (key) =>
        (left["backend"] as JsonObject)[key] ===
        (right["backend"] as JsonObject)[key],
    )
  );
}

function diagnostics(value: unknown): value is JsonObject[] {
  return (
    Array.isArray(value) &&
    value.length <= 128 &&
    value.every(
      (item) =>
        object(item) &&
        text(item["code"], true) &&
        text(item["message"]) &&
        typeof item["messageTruncated"] === "boolean" &&
        oneOf(item["severity"], ["information", "warning", "error"]) &&
        (item["nativeLocator"] === undefined ||
          item["nativeLocator"] === null ||
          locator(item["nativeLocator"])),
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
  if (!object(value)) return false;
  switch (value["kind"]) {
    case "boundary":
      return text(value["boundaryId"], true);
    case "field":
      return text(value["fieldId"], true);
    case "rule":
      return text(value["ruleId"], true);
    case "parameter":
      return text(value["parameterId"], true);
    case "control":
      return text(value["controlId"], true);
    default:
      return false;
  }
}

function reportFailure(error: unknown): RevisionReportError {
  if (error instanceof RevisionReportError) return error;
  if (error instanceof TimeoutError) return new RevisionReportError("timedOut");
  if (!(error instanceof HttpErrorResponse))
    return new RevisionReportError("unavailable");
  const status = error.status,
    code: unknown = object(error.error) ? error.error["code"] : null;
  if (status === 0) return new RevisionReportError("unavailable", status);
  if (status === 404 && (code === "notFound" || code === "unavailable"))
    return new RevisionReportError("notFound", status);
  if ((status === 401 || status === 403) && code === "identityRejected")
    return new RevisionReportError("rejected", status);
  if (status === 400 && code === "invalidRequest")
    return new RevisionReportError("invalidLink", status);
  if (status === 409) {
    if (code === "revisionReportClaimLost")
      return new RevisionReportError("claimLost", status);
    if (code === "revisionReportSelectionChanged")
      return new RevisionReportError("selectionChanged", status);
    if (code === "revisionReportGenerationExhausted")
      return new RevisionReportError("generationExhausted", status);
  }
  if (status === 429 && code === "revisionReportAdmissionBusy")
    return new RevisionReportError("admissionBusy", status);
  if (status === 503 && code === "revisionReportUnavailable")
    return new RevisionReportError("dependencyUnavailable", status);
  if (status === 503 && code === "revisionReportResourceLimit")
    return new RevisionReportError("resourceLimit", status);
  if (
    status === 500 &&
    (code === "internalFailure" || code === "persistenceFailure")
  )
    return new RevisionReportError("unavailable", status);
  return new RevisionReportError("incompatible", status);
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
function nullable(value: unknown, check: (value: unknown) => boolean): boolean {
  return value === null || check(value);
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
function utc(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
      value,
    ) &&
    Number.isFinite(Date.parse(value))
  );
}
function compareUtc(left: string, right: string): number {
  // Preserve sub-millisecond ordering without changing exact bigint generation strings.
  const key = (value: string) =>
    value.slice(0, 19) +
    (value.slice(19).match(/^\.(\d+)/)?.[1] ?? "").padEnd(7, "0");
  const l = key(left),
    r = key(right);
  return l < r ? -1 : l > r ? 1 : 0;
}
