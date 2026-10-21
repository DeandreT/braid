import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  defer,
  map,
  takeUntil,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import type { components } from "../../api/generated";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog-types";

export type CaseCapabilities =
  components["schemas"]["RegressionCaseCapabilitiesV1"];
export type CaseDefinition = components["schemas"]["RegressionCasePutV1"];
export type CaseDetail = components["schemas"]["RegressionCaseDetailV1"];
export type CasePage = components["schemas"]["RegressionCasePageV1"];
export type CaseSaved = components["schemas"]["RegressionCaseSavedV1"];
export type RunPage = components["schemas"]["RegressionRunPageV1"];
export type RunSummary = components["schemas"]["RegressionRunSummaryV1"];
export type RegressionRun = components["schemas"]["RegressionRunV1"];
export type RunReceipt = components["schemas"]["RegressionRunReceiptV1"];
export type RegressionDocument = components["schemas"]["RegressionDocumentV1"];

export class RegressionError extends Error {
  constructor(
    readonly kind:
      | "unavailable"
      | "notFound"
      | "rejected"
      | "conflict"
      | "invalid"
      | "runtime"
      | "busy"
      | "incompatible",
  ) {
    super("The regression request could not be completed.");
  }
}
export const exactPolicy = "weft.regression.exact-bytes.v1";
const maxBytes = 8 * 1024 * 1024;
type Obj = Record<string, unknown>;
const scopeKeys = ["organizationId", "projectId", "mappingId"] as const;
const states = [
  "queued",
  "running",
  "completed",
  "canceled",
  "attemptsExhausted",
  "authorityRevoked",
];

/** No private response is accepted after a session boundary or under a different mapping. */
@Injectable({ providedIn: "root" })
export class RegressionApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  capabilities(
    context: CatalogContext,
    scope: CatalogMappingScope,
  ): Observable<CaseCapabilities> {
    return this.request(
      context,
      scope,
      "GET",
      "/regression-cases/capabilities",
      undefined,
      (v) => {
        if (
          !shape(v, [
            "runtime",
            "limits",
            "comparisonPolicy",
            "readSamples",
            "publishCases",
          ]) ||
          !runtime(v.runtime) ||
          !limits(v.limits) ||
          v.comparisonPolicy !== exactPolicy ||
          typeof v.readSamples !== "boolean" ||
          typeof v.publishCases !== "boolean"
        )
          invalid();
        return v as unknown as CaseCapabilities;
      },
    );
  }
  cases(
    context: CatalogContext,
    scope: CatalogMappingScope,
    after?: string,
  ): Observable<CasePage> {
    if (after && !isCatalogId(after)) invalid();
    return this.request(
      context,
      scope,
      "GET",
      "/regression-cases?limit=20" + (after ? "&afterCaseId=" + after : ""),
      undefined,
      (v) => {
        if (
          !shape(v, ["cases", "nextCaseId"]) ||
          !Array.isArray(v.cases) ||
          v.cases.length > 20 ||
          !nullableId(v.nextCaseId) ||
          !v.cases.every(
            (x) =>
              shape(x, [
                "caseId",
                "version",
                "revision",
                "name",
                "createdAtUtc",
              ]) &&
              isCatalogId(x.caseId) &&
              version(x.version) &&
              revision(x.revision, scope) &&
              text(x.name, 256) &&
              date(x.createdAtUtc),
          )
        )
          invalid();
        return v as unknown as CasePage;
      },
    );
  }
  case(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    number: string,
  ): Observable<CaseDetail> {
    ids(id, number);
    return this.request(
      context,
      scope,
      "GET",
      `/regression-cases/${id}/versions/${number}`,
      undefined,
      (v) => {
        if (
          !shape(v, ["caseId", "version", "revision", "definition"]) ||
          v.caseId !== id ||
          v.version !== number ||
          !revision(v.revision, scope) ||
          !definition(v.definition) ||
          v.definition.revisionId !== v.revision.revisionId
        )
          invalid();
        return v as unknown as CaseDetail;
      },
    );
  }
  publish(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    number: string,
    body: CaseDefinition,
  ): Observable<CaseSaved> {
    ids(id, number);
    return this.request(
      context,
      scope,
      "PUT",
      `/regression-cases/${id}/versions/${number}`,
      body,
      (v, status) => {
        if (
          !shape(v, ["caseId", "version", "revision", "outcome"]) ||
          v.caseId !== id ||
          v.version !== number ||
          !revision(v.revision, scope) ||
          v.revision.revisionId !== body.revisionId ||
          (status === 200
            ? v.outcome !== "replayed"
            : status !== 201 || !oneOf(v.outcome, ["created", "appended"]))
        )
          invalid();
        return v as unknown as CaseSaved;
      },
    );
  }
  runs(
    context: CatalogContext,
    scope: CatalogMappingScope,
    after?: string,
  ): Observable<RunPage> {
    if (after && !isCatalogId(after)) invalid();
    return this.request(
      context,
      scope,
      "GET",
      "/regression-runs?limit=20" + (after ? "&afterRunId=" + after : ""),
      undefined,
      (v) => {
        if (
          !shape(v, ["runs", "nextRunId"]) ||
          !Array.isArray(v.runs) ||
          v.runs.length > 20 ||
          !nullableId(v.nextRunId) ||
          !v.runs.every((x) => summary(x, scope))
        )
          invalid();
        return v as unknown as RunPage;
      },
    );
  }
  submit(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    caseId: string,
    number: string,
  ): Observable<RunReceipt> {
    ids(caseId, number);
    if (!isCatalogId(id)) invalid();
    return this.request(
      context,
      scope,
      "PUT",
      `/regression-runs/${id}`,
      { caseId, version: number },
      (v, status) => {
        if (
          !shape(v, ["runId", "outcome"]) ||
          v.runId !== id ||
          (status === 201
            ? v.outcome !== "created"
            : status !== 200 || v.outcome !== "replayed")
        )
          invalid();
        return v as unknown as RunReceipt;
      },
    );
  }
  run(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    cancel = false,
  ): Observable<RegressionRun> {
    if (!isCatalogId(id)) invalid();
    return this.request(
      context,
      scope,
      cancel ? "POST" : "GET",
      `/regression-runs/${id}` + (cancel ? "/cancel" : ""),
      undefined,
      (v) => {
        if (
          !shape(v, [
            "run",
            "executionOutcome",
            "previewStatus",
            "comparison",
            "outputs",
          ]) ||
          !summary(v.run, scope) ||
          v.run.runId !== id
        )
          invalid();
        if (v.run.state !== "completed") {
          if (
            [v.executionOutcome, v.previewStatus, v.comparison, v.outputs].some(
              (x) => x !== null,
            )
          )
            invalid();
        } else {
          if (
            !oneOf(v.executionOutcome, [
              "completed",
              "notAuthorizedOrNotFound",
              "caseChanged",
              "runtimeMismatch",
              "sourceMismatch",
              "resultRejected",
              "infrastructureFailure",
            ])
          )
            invalid();
          if (v.executionOutcome === "completed") {
            if (
              !comparison(v.comparison) ||
              !documents(v.outputs, 64) ||
              !oneOf(v.previewStatus, [
                "succeeded",
                "invalidRequest",
                "invalidMapping",
                "unsupportedArtifact",
                "unsupportedCapability",
                "missingInput",
                "invalidInput",
                "invalidParameter",
                "executionFailed",
                "resourceLimit",
                "protocolFailure",
                "backendUnavailable",
                "backendFailure",
                "timedOut",
                "canceled",
                "isolationFailure",
              ])
            )
              invalid();
            if (v.comparison.actualOutputs !== v.outputs.length) invalid();
            if (
              v.comparison.outcome === "passed" &&
              (v.previewStatus !== "succeeded" ||
                v.comparison.totalDifferences !== 0 ||
                v.comparison.expectedOutputs === 0 ||
                v.comparison.notEvaluatedReason !== "none" ||
                v.comparison.expectedOutputs !== v.comparison.actualOutputs)
            )
              invalid();
          } else if (
            v.previewStatus !== null ||
            v.comparison !== null ||
            v.outputs !== null
          )
            invalid();
        }
        return v as unknown as RegressionRun;
      },
    );
  }
  private request<T>(
    context: CatalogContext,
    scope: CatalogMappingScope,
    method: string,
    suffix: string,
    body: unknown,
    decode: (value: unknown, status: number) => T,
  ): Observable<T> {
    const selected = { ...scope };
    return defer(() => {
      if (
        !scopeKeys.every((key) => isCatalogId(selected[key])) ||
        context.allowedOrganizationId !== selected.organizationId ||
        !this.sessions.canDispatch(context)
      )
        throw new RegressionError("rejected");
      const generation = this.sessions.generation();
      const headers =
        method === "GET"
          ? new HttpHeaders()
          : new HttpHeaders({
              "X-XSRF-TOKEN": context.requestToken,
              ...(method === "PUT"
                ? {
                    "Content-Type": suffix.startsWith("/regression-cases")
                      ? "application/vnd.weft.regression-case.v1+json"
                      : "application/vnd.weft.regression-run.v1+json",
                  }
                : {}),
            });
      if (
        method !== "GET" &&
        (!context.requestToken || /[\r\n\0]/.test(context.requestToken))
      )
        throw new RegressionError("rejected");
      return this.http
        .request(
          method,
          `/api/v1/organizations/${selected.organizationId}/projects/${selected.projectId}/mappings/${selected.mappingId}${suffix}`,
          { body, headers, observe: "response", responseType: "text" },
        )
        .pipe(
          timeout(45000),
          takeUntil(this.sessions.invalidated),
          map((response) => {
            if (
              generation !== this.sessions.generation() ||
              !this.sessions.canDispatch(context)
            )
              throw new RegressionError("rejected");
            if ((response.body?.length ?? 0) > 24 * 1024 * 1024) invalid();
            return decode(JSON.parse(response.body || "null"), response.status);
          }),
          catchError((error: unknown) => {
            if (
              error instanceof HttpErrorResponse &&
              [401, 403].includes(error.status)
            )
              this.sessions.rejectProtectedRequest(generation, context);
            return throwError(() => failure(error));
          }),
        );
    }).pipe(catchError((error: unknown) => throwError(() => failure(error))));
  }
}

export function encodeBytes(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 16384)
    binary += String.fromCharCode(...bytes.subarray(i, i + 16384));
  return btoa(binary)
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}
export function decodeBytes(value: string): Uint8Array {
  if (
    value.length > Math.ceil(maxBytes / 3) * 4 ||
    !/^[A-Za-z0-9_-]*$/.test(value) ||
    value.length % 4 === 1
  )
    invalid();
  const bytes = Uint8Array.from(
    atob(value.replace(/-/g, "+").replace(/_/g, "/")),
    (c) => c.charCodeAt(0),
  );
  if (bytes.length > maxBytes || encodeBytes(bytes) !== value) invalid();
  return bytes;
}
export function version(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[1-9][0-9]{0,18}$/.test(value) &&
    BigInt(value) <= 9223372036854775807n
  );
}
function ids(id: string, number: string): void {
  if (!isCatalogId(id) || !version(number)) invalid();
}
function nullableId(value: unknown): boolean {
  return value === null || isCatalogId(value);
}
function invalid(): never {
  throw new RegressionError("incompatible");
}
function text(v: unknown, max: number): v is string {
  return typeof v === "string" && v.length <= max && !v.includes("\0");
}
function date(v: unknown): boolean {
  return text(v, 40) && Number.isFinite(Date.parse(v));
}
function shape(v: unknown, keys: string[]): v is Obj {
  return (
    !!v &&
    typeof v === "object" &&
    !Array.isArray(v) &&
    Object.keys(v).sort().join("|") === [...keys].sort().join("|")
  );
}
function integer(v: unknown, max: number): v is number {
  return typeof v === "number" && Number.isSafeInteger(v) && v >= 0 && v <= max;
}
function oneOf(value: unknown, values: readonly string[]): value is string {
  return typeof value === "string" && values.includes(value);
}
function revision(v: unknown, scope: CatalogMappingScope): v is Obj {
  return (
    shape(v, [...scopeKeys, "revisionId"]) &&
    scopeKeys.every((key) => v[key] === scope[key]) &&
    isCatalogId(v.revisionId)
  );
}
function runtime(v: unknown): boolean {
  return (
    shape(v, [
      "backendId",
      "engineRevision",
      "backendBuild",
      "adapterBuild",
      "profile",
    ]) && Object.values(v).every((x) => text(x, 256) && x.length > 0)
  );
}
function limits(v: unknown): boolean {
  return (
    shape(v, [
      "maximumInputBytes",
      "maximumOutputBytes",
      "maximumArtifacts",
      "maximumTextBytes",
    ]) &&
    integer(v.maximumInputBytes, maxBytes) &&
    Number(v.maximumInputBytes) > 0 &&
    integer(v.maximumOutputBytes, maxBytes) &&
    Number(v.maximumOutputBytes) > 0 &&
    integer(v.maximumArtifacts, 64) &&
    Number(v.maximumArtifacts) > 0 &&
    integer(v.maximumTextBytes, 2048)
  );
}
function documents(v: unknown, count: number): v is RegressionDocument[] {
  if (!Array.isArray(v) || v.length > count) return false;
  const names = new Set();
  let total = 0;
  for (const item of v) {
    if (
      !shape(item, ["name", "bytesBase64Url"]) ||
      !(item.name === null || (text(item.name, 256) && item.name.length > 0)) ||
      typeof item.bytesBase64Url !== "string" ||
      names.has(item.name)
    )
      return false;
    names.add(item.name);
    total += decodeBytes(item.bytesBase64Url).length;
    if (total > maxBytes) return false;
  }
  return true;
}
function definition(v: unknown): v is CaseDefinition {
  return (
    shape(v, [
      "name",
      "revisionId",
      "runtime",
      "currentDateTime",
      "target",
      "limits",
      "primaryBase64Url",
      "namedInputs",
      "parameters",
      "comparisonPolicy",
      "expected",
    ]) &&
    text(v.name, 256) &&
    isCatalogId(v.revisionId) &&
    runtime(v.runtime) &&
    date(v.currentDateTime) &&
    limits(v.limits) &&
    v.comparisonPolicy === exactPolicy &&
    shape(v.target, ["kind", "name"]) &&
    ((oneOf(v.target.kind, ["all", "primary"]) && v.target.name === null) ||
      (v.target.kind === "named" && text(v.target.name, 256))) &&
    typeof v.primaryBase64Url === "string" &&
    decodeBytes(v.primaryBase64Url).length <= maxBytes &&
    documents(v.namedInputs, 15) &&
    v.namedInputs.every((x) => x.name !== null) &&
    documents(v.expected, 64) &&
    Array.isArray(v.parameters) &&
    v.parameters.length <= 32 &&
    v.parameters.every(
      (x) =>
        shape(x, ["name", "kind", "value"]) &&
        text(x.name, 256) &&
        text(x.value, 4096) &&
        oneOf(x.kind, ["string", "integer", "float", "boolean"]),
    )
  );
}
function summary(v: unknown, scope: CatalogMappingScope): v is RunSummary {
  return (
    shape(v, [
      "runId",
      "caseId",
      "version",
      "revision",
      "state",
      "attempt",
      "createdAtUtc",
    ]) &&
    isCatalogId(v.runId) &&
    isCatalogId(v.caseId) &&
    version(v.version) &&
    revision(v.revision, scope) &&
    oneOf(v.state, states) &&
    integer(v.attempt, 3) &&
    date(v.createdAtUtc)
  );
}
function comparison(
  v: unknown,
): v is components["schemas"]["RegressionComparisonV1"] {
  return (
    shape(v, [
      "policy",
      "outcome",
      "notEvaluatedReason",
      "expectedOutputs",
      "actualOutputs",
      "totalDifferences",
      "truncated",
      "differences",
    ]) &&
    v.policy === exactPolicy &&
    oneOf(v.outcome, ["passed", "failed", "notEvaluated"]) &&
    oneOf(v.notEvaluatedReason, [
      "none",
      "unsupportedPolicy",
      "executionUnsuccessful",
      "missingBaseline",
    ]) &&
    integer(v.expectedOutputs, 128) &&
    integer(v.actualOutputs, 128) &&
    integer(v.totalDifferences, 256) &&
    typeof v.truncated === "boolean" &&
    Array.isArray(v.differences) &&
    v.differences.length <= 256 &&
    v.differences.length <= v.totalDifferences &&
    (v.truncated || v.differences.length === v.totalDifferences) &&
    v.differences.every(
      (x) =>
        shape(x, [
          "name",
          "kind",
          "expectedBytes",
          "actualBytes",
          "expectedDigest",
          "actualDigest",
          "firstDifferentByte",
        ]) &&
        (x.name === null || text(x.name, 256)) &&
        oneOf(x.kind, ["missingOutput", "unexpectedOutput", "bytesDiffer"]) &&
        [x.expectedBytes, x.actualBytes, x.firstDifferentByte].every(
          (n) => n === null || integer(n, maxBytes),
        ) &&
        [x.expectedDigest, x.actualDigest].every(
          (d) =>
            d === null || (typeof d === "string" && /^[a-f0-9]{64}$/.test(d)),
        ),
    )
  );
}
function failure(error: unknown): RegressionError {
  if (error instanceof RegressionError) return error;
  if (error instanceof SyntaxError || error instanceof DOMException)
    return new RegressionError("incompatible");
  if (error instanceof HttpErrorResponse)
    return new RegressionError(
      (
        {
          400: "invalid",
          401: "rejected",
          403: "rejected",
          404: "notFound",
          409: "conflict",
          412: "runtime",
          413: "invalid",
          415: "invalid",
          429: "busy",
        } as const
      )[error.status as 400] || "unavailable",
    );
  return new RegressionError("unavailable");
}
