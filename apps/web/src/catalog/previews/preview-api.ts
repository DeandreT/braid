import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
  type HttpResponse,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  TimeoutError,
  catchError,
  defer,
  from,
  map,
  switchMap,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  nativeFormats,
  nativeProfile,
  nativeMedia,
  nativeSettings,
} from "./native-document-contract";
import { xmlPreflightProblem } from "./xml-problem";
import { isCatalogId, type CatalogRevisionScope } from "../catalog-types";
import {
  csvMaximumRecords,
  csvMedia,
  csvMetadata,
  csvPreviewProfile,
} from "./csv-contract";
import {
  xmlMetadata,
  xmlMetadataBytes,
  xmlPreviewProfile,
  type XmlMetadata,
} from "./xml-contract";
import {
  PreviewError,
  previewLimits,
  previewOutcomes,
  type PreviewCanceled,
  type PreviewOutput,
  type PreviewRun,
  type PreviewSubmission,
  type PreviewSubmitted,
} from "./preview-types";

const scopeKeys = [
  "organizationId",
  "projectId",
  "mappingId",
  "revisionId",
] as const;
const runKeys = [
  "attempt",
  "cancelRequested",
  "createdAtUtc",
  "currentDateTime",
  "expiresAtUtc",
  "failure",
  "finishedAtUtc",
  "maxAttempts",
  "outcome",
  "outputs",
  "provenance",
  "recordsWritten",
  "requestFingerprint",
  "revision",
  "draft",
  "subjectKind",
  "runId",
  "startedAtUtc",
  "state",
];
const outputKeys = [
  "boundaryId",
  "byteCount",
  "mediaType",
  "ordinal",
  "recordsWritten",
  "sha256",
  "targetName",
  "targetNameTruncated",
];
type JsonObject = Record<string, unknown>;

/** Exact preview routes; every response is checked against the requested revision before use. */
@Injectable({ providedIn: "root" })
export class PreviewApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  submit(
    scope: CatalogRevisionScope,
    requestToken: string,
    key: string,
    body: PreviewSubmission,
  ): Observable<PreviewSubmitted> {
    return this.request(scope, (generation) => {
      token(requestToken);
      if (!isCatalogId(key)) throw new PreviewError("invalidLink");
      return this.http
        .post<unknown>(revisionPath(scope) + "/previews", body, {
          observe: "response",
          headers: new HttpHeaders({
            "X-XSRF-TOKEN": requestToken,
            "Idempotency-Key": key,
            "Content-Type": "application/json",
          }),
        })
        .pipe(
          timeout(45000),
          map((response) => {
            this.current(generation);
            const value = object(response.body);
            const outcome = value?.["outcome"];
            const expected = response.status === 201 ? "created" : "replayed";
            if (
              (response.status !== 201 && response.status !== 200) ||
              !exactKeys(value, ["outcome", "run"]) ||
              outcome !== expected ||
              !run(value["run"], scope)
            )
              throw new PreviewError("incompatible", response.status);
            const created = value["run"] as PreviewRun;
            if (
              response.status === 201 &&
              response.headers.get("Location") !== runPath(scope, created.runId)
            )
              throw new PreviewError("incompatible", response.status);
            return { outcome: expected, run: created };
          }),
        );
    });
  }

  read(scope: CatalogRevisionScope, runId: string): Observable<PreviewRun> {
    return this.request(scope, (generation) => {
      if (!isCatalogId(runId)) throw new PreviewError("invalidLink");
      return this.http
        .get<unknown>(runPath(scope, runId), { observe: "response" })
        .pipe(
          timeout(15000),
          map((response) => {
            this.current(generation);
            if (
              response.status !== 200 ||
              !run(response.body, scope) ||
              (response.body as PreviewRun).runId !== runId
            )
              throw new PreviewError("incompatible", response.status);
            return response.body as PreviewRun;
          }),
        );
    });
  }

  /** Exact output bytes, verified against the run's declared length and SHA-256. */
  output(
    scope: CatalogRevisionScope,
    runId: string,
    output: PreviewOutput,
  ): Observable<Uint8Array> {
    return this.request(scope, (generation) => {
      if (!isCatalogId(runId) || !outputDescriptor(output))
        throw new PreviewError("invalidLink");
      return this.http
        .get(`${runPath(scope, runId)}/outputs/${output.ordinal}`, {
          observe: "response",
          responseType: "arraybuffer",
        })
        .pipe(
          timeout(30000),
          switchMap((response: HttpResponse<ArrayBuffer>) => {
            this.current(generation);
            const bytes = new Uint8Array(response.body ?? new ArrayBuffer(0));
            if (
              response.status !== 200 ||
              bytes.length !== output.byteCount ||
              response.headers.get("Content-Type")?.split(";")[0]?.trim() !==
                output.mediaType
            )
              throw new PreviewError("integrity", response.status);
            return from(crypto.subtle.digest("SHA-256", bytes)).pipe(
              map((digest) => {
                this.current(generation);
                if (hex(digest) !== output.sha256)
                  throw new PreviewError("integrity", response.status);
                return bytes;
              }),
            );
          }),
        );
    });
  }

  cancel(
    scope: CatalogRevisionScope,
    runId: string,
    requestToken: string,
  ): Observable<PreviewCanceled> {
    return this.request(scope, (generation) => {
      token(requestToken);
      if (!isCatalogId(runId)) throw new PreviewError("invalidLink");
      return this.http
        .post<unknown>(`${runPath(scope, runId)}/cancel`, null, {
          observe: "response",
          headers: new HttpHeaders({ "X-XSRF-TOKEN": requestToken }),
        })
        .pipe(
          timeout(15000),
          map((response) => {
            this.current(generation);
            const value = object(response.body);
            const outcome = value?.["outcome"];
            if (
              response.status !== 200 ||
              !exactKeys(value, ["outcome", "run"]) ||
              (outcome !== "canceled" &&
                outcome !== "requested" &&
                outcome !== "finished") ||
              !run(value["run"], scope) ||
              (value["run"] as PreviewRun).runId !== runId
            )
              throw new PreviewError("incompatible", response.status);
            return {
              outcome,
              run: value["run"] as PreviewRun,
            } as PreviewCanceled;
          }),
        );
    });
  }

  private request<T>(
    scope: CatalogRevisionScope,
    send: (generation: number) => Observable<T>,
  ): Observable<T> {
    return defer(() => {
      if (!scopeKeys.every((key) => isCatalogId(scope?.[key])))
        throw new PreviewError("invalidLink");
      if (!this.sessions.settled()) throw new PreviewError("rejected");
      const generation = this.sessions.generation();
      const sessionMode = this.sessions.state().kind === "authenticated";
      return send(generation).pipe(
        catchError((error: unknown) => {
          if (
            generation === this.sessions.generation() &&
            sessionMode &&
            error instanceof HttpErrorResponse &&
            (error.status === 401 || error.status === 403)
          )
            this.sessions.rejectProtectedRequest(generation);
          return throwError(() => failure(error));
        }),
      );
    }).pipe(catchError((error: unknown) => throwError(() => failure(error))));
  }

  private current(generation: number): void {
    if (generation !== this.sessions.generation())
      throw new PreviewError("unavailable");
  }
}

export function revisionPath(scope: CatalogRevisionScope): string {
  return `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}`;
}

export function runPath(scope: CatalogRevisionScope, runId: string): string {
  return `/api/v1/organizations/${scope.organizationId}/previews/${runId}`;
}

function token(value: string): void {
  if (
    typeof value !== "string" ||
    value.length > 4096 ||
    value.trim().length === 0 ||
    /[\0\r\n]/.test(value)
  )
    throw new PreviewError("invalidLink");
}

function failure(error: unknown): PreviewError {
  if (error instanceof PreviewError) return error;
  if (error instanceof TimeoutError) return new PreviewError("timedOut");
  if (!(error instanceof HttpErrorResponse))
    return new PreviewError("unavailable");
  const code = object(error.error)?.["code"];
  switch (error.status) {
    case 422:
      return new PreviewError(
        "invalidRequest",
        422,
        xmlPreflightProblem(error.error),
      );
    case 400:
      return new PreviewError("invalidRequest", 400);
    case 401:
    case 403:
      return new PreviewError("rejected", error.status);
    case 404:
      return new PreviewError(
        code === "unavailable" ? "unavailable" : "notFound",
        404,
      );
    case 409:
      return new PreviewError("conflict", 409);
    case 413:
      return new PreviewError("bodyLimit", 413);
    case 415:
      return new PreviewError("unsupportedMediaType", 415);
    default:
      return new PreviewError("unavailable", error.status || null);
  }
}

function run(value: unknown, scope: CatalogRevisionScope): boolean {
  const body = object(value);
  if (
    !exactKeys(body, [
      ...runKeys,
      ...(body && Object.hasOwn(body, "primaryDocumentSettings")
        ? ["primaryDocumentSettings"]
        : []),
    ])
  )
    return false;
  const revision = object(body["revision"]);
  const state = body["state"];
  const outcome = body["outcome"];
  const terminal = state === "completed" || state === "canceled";
  const outputs = body["outputs"];
  return (
    body["draft"] === null &&
    body["subjectKind"] === "revision" &&
    exactKeys(revision, [...scopeKeys]) &&
    scopeKeys.every((key) => revision[key] === scope[key]) &&
    isCatalogId(body["runId"]) &&
    (state === "queued" ||
      state === "running" ||
      state === "completed" ||
      state === "canceled") &&
    (terminal
      ? typeof outcome === "string" &&
        (previewOutcomes as readonly string[]).includes(outcome) &&
        (state === "canceled") === (outcome === "canceled")
      : outcome === null) &&
    integer(body["attempt"], 0, 8) &&
    integer(body["maxAttempts"], 1, 8) &&
    typeof body["cancelRequested"] === "boolean" &&
    hexDigest(body["requestFingerprint"]) &&
    text(body["currentDateTime"], 64) &&
    date(body["createdAtUtc"]) &&
    date(body["expiresAtUtc"]) &&
    (body["startedAtUtc"] === null || date(body["startedAtUtc"])) &&
    (terminal ? date(body["finishedAtUtc"]) : body["finishedAtUtc"] === null) &&
    failureValue(body["failure"]) &&
    provenance(body["provenance"]) &&
    (body["recordsWritten"] === null || decimal(body["recordsWritten"])) &&
    Array.isArray(outputs) &&
    outputs.length <= previewLimits.outputs &&
    (nativeProfile(object(body["provenance"])?.["profile"])
      ? nativeSettings(
          nativeProfile(object(body["provenance"])?.["profile"])!.media,
          body["primaryDocumentSettings"],
        ) &&
        outputs.every(
          (o) =>
            object(o)?.["mediaType"] ===
              nativeProfile(object(body["provenance"])?.["profile"])!.media &&
            object(o)?.["boundaryId"] === "primary-output",
        ) &&
        outputs.length <= 1
      : body["primaryDocumentSettings"] === undefined &&
        outputs.every((o) => !nativeMedia(object(o)?.["mediaType"]))) &&
    (!outputs.some(
      (output) => object(output)?.["mediaType"] === "application/xml",
    ) ||
      (object(body["provenance"])?.["profile"] === xmlPreviewProfile &&
        outputs.every(
          (output) => object(output)?.["mediaType"] === "application/xml",
        ) &&
        outputs.reduce(
          (sum, output) =>
            sum +
            (xmlMetadata(object(output)?.["xmlMetadata"])
              ? xmlMetadataBytes(object(output)!["xmlMetadata"] as XmlMetadata)
              : 32769),
          0,
        ) <= 32768)) &&
    (outcome === "succeeded" || outputs.length === 0) &&
    (!outputs.some((output) => csvMedia(object(output)?.["mediaType"])) ||
      (outputs.length === 1 &&
        object(body["provenance"])?.["profile"] === csvPreviewProfile &&
        body["recordsWritten"] !== null &&
        decimal(body["recordsWritten"]) &&
        BigInt(body["recordsWritten"] as string) <=
          BigInt(csvMaximumRecords))) &&
    outputs.every(
      (output: unknown, index: number) =>
        outputDescriptor(output) && (output as PreviewOutput).ordinal === index,
    )
  );
}

function outputDescriptor(value: unknown): value is PreviewOutput {
  const output = object(value);
  return (
    exactKeys(output, [
      ...outputKeys,
      ...(output && Object.hasOwn(output, "documentSettings")
        ? ["documentSettings"]
        : []),
      ...(output && Object.hasOwn(output, "csvDialect") ? ["csvDialect"] : []),
      ...(output && Object.hasOwn(output, "xmlMetadata")
        ? ["xmlMetadata"]
        : []),
    ]) &&
    integer(output["ordinal"], 0, previewLimits.outputs - 1) &&
    text(output["boundaryId"], 2048, true) &&
    typeof output["targetName"] === "string" &&
    (output["targetName"] as string).length <= 2048 &&
    typeof output["targetNameTruncated"] === "boolean" &&
    [
      "application/json",
      "application/edi-x12",
      "application/edifact",
      "text/csv",
      "text/tab-separated-values",
      ...nativeFormats.map((f) => f.media),
      "application/xml",
    ].includes(output["mediaType"] as string) &&
    (nativeMedia(output["mediaType"])
      ? nativeSettings(output["mediaType"], output["documentSettings"]) &&
        output["xmlMetadata"] === undefined &&
        output["csvDialect"] === undefined
      : output["documentSettings"] !== undefined
        ? false
        : output["mediaType"] === "application/xml"
          ? output["csvDialect"] === undefined &&
            xmlMetadata(output["xmlMetadata"])
          : output["xmlMetadata"] === undefined &&
            csvMetadata(output["mediaType"], output["csvDialect"])) &&
    decimal(output["recordsWritten"]) &&
    (!csvMedia(output["mediaType"]) ||
      BigInt(output["recordsWritten"] as string) <=
        BigInt(csvMaximumRecords)) &&
    hexDigest(output["sha256"]) &&
    integer(output["byteCount"], 0, 8 * 1024 * 1024)
  );
}

function failureValue(value: unknown): boolean {
  if (value === null) return true;
  const failure = object(value);
  return (
    exactKeys(failure, [
      "code",
      "message",
      "messageTruncated",
      "subject",
      "subjectTruncated",
    ]) &&
    text(failure["code"], 128, true) &&
    text(failure["message"], 2048) &&
    typeof failure["messageTruncated"] === "boolean" &&
    (failure["subject"] === null || text(failure["subject"], 2048)) &&
    typeof failure["subjectTruncated"] === "boolean"
  );
}

function provenance(value: unknown): boolean {
  if (value === null) return true;
  const item = object(value);
  return (
    exactKeys(item, [
      "adapterBuild",
      "backendBuild",
      "confirmed",
      "engineRevision",
      "profile",
    ]) &&
    typeof item["confirmed"] === "boolean" &&
    text(item["adapterBuild"], 256, true) &&
    text(item["backendBuild"], 2048, true) &&
    text(item["engineRevision"], 256, true) &&
    text(item["profile"], 256, true)
  );
}

function object(value: unknown): JsonObject | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as JsonObject)
    : null;
}

function exactKeys(
  value: JsonObject | null,
  keys: string[],
): value is JsonObject {
  if (!value) return false;
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  return (
    actual.length === expected.length &&
    actual.every((key, index) => key === expected[index])
  );
}

function integer(value: unknown, minimum: number, maximum: number): boolean {
  return (
    typeof value === "number" &&
    Number.isInteger(value) &&
    value >= minimum &&
    value <= maximum
  );
}

function decimal(value: unknown): boolean {
  return (
    typeof value === "string" &&
    /^(0|[1-9][0-9]{0,19})$/.test(value) &&
    BigInt(value) <= 18446744073709551615n
  );
}

function hexDigest(value: unknown): boolean {
  return typeof value === "string" && /^[0-9a-f]{64}$/.test(value);
}

function text(value: unknown, maximum: number, required = false): boolean {
  return (
    typeof value === "string" &&
    value.length <= maximum &&
    (!required || value.length > 0)
  );
}

function date(value: unknown): boolean {
  return (
    typeof value === "string" &&
    value.length <= 40 &&
    !Number.isNaN(Date.parse(value))
  );
}

function hex(buffer: ArrayBuffer): string {
  return Array.from(new Uint8Array(buffer), (byte) =>
    byte.toString(16).padStart(2, "0"),
  ).join("");
}
