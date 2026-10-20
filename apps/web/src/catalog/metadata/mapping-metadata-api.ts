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
  map,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog-types";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  MappingMetadataAttempt,
  MappingMetadataBaseline,
  MappingMetadataError,
  type MappingMetadataAdmission,
  type MappingMetadataPatchResponse,
} from "./mapping-metadata-types";

const mediaType = "application/vnd.weft.mapping-metadata-edit.v1+json";
type Admitted = {
  readonly mode: CatalogContext["mode"];
  readonly scope: CatalogMappingScope;
  readonly actorPrincipalId: string;
  readonly editEnabled: boolean;
  readonly canEdit: boolean;
  readonly token: unknown;
};

@Injectable({ providedIn: "root" })
export class MappingMetadataApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  /** An unconditional view read; neither Accept-Patch nor edit capability authorizes this read. */
  read(
    admission: MappingMetadataAdmission,
  ): Observable<MappingMetadataBaseline> {
    try {
      const admitted = this.capture(admission);
      return this.http
        .get<unknown>(path(admitted.scope), { observe: "response" })
        .pipe(
          timeout(8000),
          map((response) => {
            if (!this.sessions.accepts(admission.context))
              throw new MappingMetadataError("unavailable");
            return baseline(response, admitted, false);
          }),
          catchError((error: unknown) =>
            throwError(() => metadataFailure(error, false)),
          ),
        );
    } catch (error: unknown) {
      return throwError(() => metadataFailure(error, false));
    }
  }

  /** One deliberate replacement. Caller owns fresh context/action admission and prior uncertainty. */
  patch(
    admission: MappingMetadataAdmission,
    attempt: MappingMetadataAttempt,
  ): Observable<MappingMetadataPatchResponse> {
    try {
      const admitted = this.capture(admission);
      if (!(attempt instanceof MappingMetadataAttempt))
        throw new MappingMetadataError("invalidInput");
      const original = attempt.baseline;
      if (
        admitted.mode !== original.mode ||
        admitted.actorPrincipalId !== original.actorPrincipalId ||
        !sameScope(admitted.scope, original.scope)
      )
        throw new MappingMetadataError("identityChanged");
      if (!admitted.editEnabled)
        throw new MappingMetadataError("featureDisabled");
      if (!admitted.canEdit) throw new MappingMetadataError("editUnavailable");
      const token = admitted.token;
      if (
        typeof token !== "string" ||
        token.length === 0 ||
        token.length > 4096 ||
        /[\0\r\n]/.test(token)
      )
        throw new MappingMetadataError("identityRejected");
      return this.http
        .patch<unknown>(path(admitted.scope), attempt.body, {
          observe: "response",
          headers: new HttpHeaders({
            "Content-Type": mediaType,
            "If-Match": original.etag,
            "X-XSRF-TOKEN": token,
          }),
        })
        .pipe(
          timeout(30000),
          map((response) => {
            if (!this.sessions.accepts(admission.context))
              throw new MappingMetadataError("unavailable", null, true);
            const updated = baseline(response, admitted, true);
            const changed =
              attempt.acceptedName !== original.metadata.name ||
              attempt.description !== original.metadata.description;
            const expectedVersion = changed
              ? (BigInt(original.metadata.version) + 1n).toString()
              : original.metadata.version;
            if (
              updated.metadata.name !== attempt.acceptedName ||
              updated.metadata.description !== attempt.description ||
              updated.metadata.version !== expectedVersion
            )
              throw new MappingMetadataError(
                "incompatible",
                response.status,
                true,
              );
            return Object.freeze({
              status: 200 as const,
              baseline: updated,
              changed,
            });
          }),
          catchError((error: unknown) =>
            throwError(() => metadataFailure(error, true)),
          ),
        );
    } catch (error: unknown) {
      return throwError(() => metadataFailure(error, false));
    }
  }

  private capture(admission: MappingMetadataAdmission): Admitted {
    if (!admission?.context || !this.sessions.canDispatch(admission.context))
      throw new MappingMetadataError("identityRejected");
    return capture(admission);
  }
}

function capture(admission: MappingMetadataAdmission): Admitted {
  const context = admission?.context,
    mapping = admission?.mapping;
  const scope = mapping?.scope;
  if (
    (context?.mode !== "development" && context?.mode !== "session") ||
    !isCatalogId(context.actorPrincipalId) ||
    !isCatalogId(context.allowedOrganizationId)
  )
    throw new MappingMetadataError("identityRejected");
  if (
    !scope ||
    !isCatalogId(scope.organizationId) ||
    !isCatalogId(scope.projectId) ||
    !isCatalogId(scope.mappingId)
  )
    throw new MappingMetadataError("invalidInput");
  if (
    scope.organizationId !== context.allowedOrganizationId ||
    context.features?.readCatalog !== true ||
    mapping.access?.actions?.view !== true
  )
    throw new MappingMetadataError("notFound");
  return {
    mode: context.mode,
    scope: {
      organizationId: scope.organizationId,
      projectId: scope.projectId,
      mappingId: scope.mappingId,
    },
    actorPrincipalId: context.actorPrincipalId,
    editEnabled: context.features.editMetadata === true,
    canEdit: mapping.access.actions.editMetadata === true,
    token: context.requestToken,
  };
}

function baseline(
  response: HttpResponse<unknown>,
  admitted: Admitted,
  writing: boolean,
): MappingMetadataBaseline {
  if (response.status !== 200)
    throw new MappingMetadataError("incompatible", response.status, writing);
  try {
    const received = MappingMetadataBaseline.fromResponse(
      admitted.actorPrincipalId,
      response.body,
      response.headers.get("ETag"),
      admitted.mode,
    );
    if (!sameScope(received.scope, admitted.scope))
      throw new MappingMetadataError("incompatible");
    return received;
  } catch {
    throw new MappingMetadataError("incompatible", response.status, writing);
  }
}

function metadataFailure(
  error: unknown,
  writing: boolean,
): MappingMetadataError {
  if (error instanceof MappingMetadataError) return error;
  if (error instanceof TimeoutError)
    return new MappingMetadataError("timedOut", null, writing);
  if (!(error instanceof HttpErrorResponse))
    return new MappingMetadataError("unavailable", null, writing);
  const status = error.status;
  if (status === 0)
    return new MappingMetadataError("unavailable", status, writing);
  if (!object(error.error) || !keys(error.error, ["code"]))
    return new MappingMetadataError("incompatible", status, writing);
  const code: unknown = error.error["code"];
  if (status === 400 && code === "invalidRequest")
    return new MappingMetadataError("invalidRequest", status);
  if ((status === 401 || status === 403) && code === "identityRejected")
    return new MappingMetadataError("identityRejected", status);
  if (status === 404 && (code === "notFound" || code === "unavailable"))
    return new MappingMetadataError("notFound", status);
  if (status === 412 && code === "preconditionFailed")
    return new MappingMetadataError("preconditionFailed", status);
  if (writing && status === 428 && code === "preconditionRequired")
    return new MappingMetadataError("preconditionRequired", status);
  if (writing && status === 409 && code === "versionExhausted")
    return new MappingMetadataError("versionExhausted", status);
  if (writing && status === 413 && code === "bodyLimit")
    return new MappingMetadataError("bodyLimit", status);
  if (writing && status === 415 && code === "unsupportedMediaType")
    return new MappingMetadataError("unsupportedMediaType", status);
  if (status === 500 && code === "persistenceFailure")
    return new MappingMetadataError("persistenceFailure", status, writing);
  if (status === 500 && code === "internalFailure")
    return new MappingMetadataError("internalFailure", status, writing);
  return new MappingMetadataError("incompatible", status, writing);
}

type JsonObject = Record<string, unknown>;
function object(value: unknown): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function keys(value: JsonObject, expected: readonly string[]): boolean {
  return (
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key))
  );
}
function sameScope(
  left: CatalogMappingScope,
  right: CatalogMappingScope,
): boolean {
  return (
    left.organizationId === right.organizationId &&
    left.projectId === right.projectId &&
    left.mappingId === right.mappingId
  );
}
function path(scope: CatalogMappingScope): string {
  return `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/metadata`;
}
