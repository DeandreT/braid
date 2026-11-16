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
import { isCatalogId, type CatalogContext } from "../catalog-types";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  ProjectCreationError,
  ProjectCreationIntent,
  type CatalogProjectCreationReceipt,
  type CatalogProjectCreationResult,
  type ProjectCreationResponse,
} from "./project-creation-types";

const mediaType = "application/vnd.braid.project-create.v1+json";

@Injectable({ providedIn: "root" })
export class ProjectCreationApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  /** One deliberate send or original-request recovery. Fresh admission is the caller's responsibility. */
  create(
    context: CatalogContext,
    intent: ProjectCreationIntent,
  ): Observable<ProjectCreationResponse> {
    try {
      if (!(intent instanceof ProjectCreationIntent))
        throw new ProjectCreationError("invalidInput");
      if (
        !context ||
        !this.sessions.canDispatch(context) ||
        !isCatalogId(context.actorPrincipalId) ||
        !isCatalogId(context.allowedOrganizationId)
      )
        throw new ProjectCreationError("identityRejected");
      if (
        context.mode !== intent.mode ||
        context.actorPrincipalId !== intent.actorPrincipalId ||
        context.allowedOrganizationId !== intent.organizationId
      )
        throw new ProjectCreationError("identityChanged");
      if (context.features?.createProjects !== true)
        throw new ProjectCreationError("featureDisabled");
      if (context.organizationActions?.createProjects !== true)
        throw new ProjectCreationError("permissionDenied");
      const token = context.requestToken;
      if (
        typeof token !== "string" ||
        token.length === 0 ||
        token.length > 4096 ||
        /[\0\r\n]/.test(token)
      )
        throw new ProjectCreationError("identityRejected");
      // A frozen string is sent exactly once by each explicit subscription. The
      // token may refresh; the original body and key never do. No GET receipt API exists.
      return this.http
        .post<unknown>(
          `/api/v1/organizations/${intent.organizationId}/projects`,
          intent.body,
          {
            observe: "response",
            headers: new HttpHeaders({
              "Content-Type": mediaType,
              "Idempotency-Key": intent.key,
              "X-XSRF-TOKEN": token,
            }),
          },
        )
        .pipe(
          timeout(30000),
          map((response) => {
            if (!this.sessions.accepts(context))
              throw new ProjectCreationError("unavailable");
            return admitted(response, intent);
          }),
          catchError((error: unknown) =>
            throwError(() => creationFailure(error)),
          ),
        );
    } catch (error: unknown) {
      return throwError(() => creationFailure(error));
    }
  }
}

type ObjectValue = Record<string, unknown>;
function admitted(
  response: HttpResponse<unknown>,
  intent: ProjectCreationIntent,
): ProjectCreationResponse {
  const value = response.body,
    status = response.status;
  if (
    (status !== 201 && status !== 200) ||
    !object(value) ||
    !keys(value, ["outcome", "receipt", "projectUrl"]) ||
    value["outcome"] !== (status === 201 ? "created" : "replayed") ||
    !receipt(value["receipt"], intent)
  )
    throw new ProjectCreationError("incompatible", status);
  const facts = value["receipt"];
  const location = `/api/v1/organizations/${intent.organizationId}/projects/${facts.project.projectId}`;
  if (
    value["projectUrl"] !== location ||
    response.headers.get("Location") !== location
  )
    throw new ProjectCreationError("incompatible", status);
  // Own the acknowledged history; an interceptor/caller cannot mutate the raw
  // response object after validation. None of these initial facts grants current access.
  const ownedReceipt: CatalogProjectCreationReceipt = Object.freeze({
    project: Object.freeze({
      organizationId: facts.project.organizationId,
      projectId: facts.project.projectId,
    }),
    name: facts.name,
    description: facts.description,
    creatorPrincipalId: facts.creatorPrincipalId,
    createdAtUtc: facts.createdAtUtc,
    initialProjectVersion: facts.initialProjectVersion,
    initialMembershipRole: facts.initialMembershipRole,
    initialMembershipActive: facts.initialMembershipActive,
    initialMembershipVersion: facts.initialMembershipVersion,
  });
  const body: CatalogProjectCreationResult = Object.freeze({
    outcome: status === 201 ? "created" : "replayed",
    receipt: ownedReceipt,
    projectUrl: location,
  });
  return Object.freeze({ status, body, location });
}

function receipt(
  value: unknown,
  intent: ProjectCreationIntent,
): value is CatalogProjectCreationReceipt {
  return (
    object(value) &&
    keys(value, [
      "project",
      "name",
      "description",
      "creatorPrincipalId",
      "createdAtUtc",
      "initialProjectVersion",
      "initialMembershipRole",
      "initialMembershipActive",
      "initialMembershipVersion",
    ]) &&
    object(value["project"]) &&
    keys(value["project"], ["organizationId", "projectId"]) &&
    value["project"]["organizationId"] === intent.organizationId &&
    isCatalogId(value["project"]["projectId"]) &&
    value["creatorPrincipalId"] === intent.actorPrincipalId &&
    value["name"] === intent.acceptedName &&
    value["description"] === intent.description &&
    utc(value["createdAtUtc"]) &&
    value["initialProjectVersion"] === "1" &&
    value["initialMembershipRole"] === "administrator" &&
    value["initialMembershipActive"] === true &&
    value["initialMembershipVersion"] === "1"
  );
}

function creationFailure(error: unknown): ProjectCreationError {
  if (error instanceof ProjectCreationError) return error;
  if (error instanceof TimeoutError)
    return new ProjectCreationError("timedOut");
  if (!(error instanceof HttpErrorResponse))
    return new ProjectCreationError("unavailable");
  const status = error.status;
  if (status === 0) return new ProjectCreationError("unavailable", status);
  if (!object(error.error) || !keys(error.error, ["code"]))
    return new ProjectCreationError("incompatible", status);
  const code: unknown = error.error["code"];
  if (status === 400 && code === "invalidRequest")
    return new ProjectCreationError("invalidRequest", status);
  if ((status === 401 || status === 403) && code === "identityRejected")
    return new ProjectCreationError("identityRejected", status);
  if (status === 403 && code === "permissionDenied")
    return new ProjectCreationError("permissionDenied", status);
  if (status === 404 && (code === "notFound" || code === "unavailable"))
    return new ProjectCreationError("notFound", status);
  if (status === 409 && code === "idempotencyConflict")
    return new ProjectCreationError("idempotencyConflict", status);
  if (status === 413 && code === "bodyLimit")
    return new ProjectCreationError("bodyLimit", status);
  if (status === 415 && code === "unsupportedMediaType")
    return new ProjectCreationError("unsupportedMediaType", status);
  if (status === 503 && code === "projectCreationAdmissionBusy")
    return new ProjectCreationError("admissionBusy", status);
  if (status === 500 && code === "persistenceFailure")
    return new ProjectCreationError("persistenceFailure", status);
  if (status === 500 && code === "internalFailure")
    return new ProjectCreationError("internalFailure", status);
  return new ProjectCreationError("incompatible", status);
}

function object(value: unknown): value is ObjectValue {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function keys(value: ObjectValue, expected: readonly string[]): boolean {
  return (
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key))
  );
}
function utc(value: unknown): value is string {
  if (
    typeof value !== "string" ||
    !/^(?!0000)\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
      value,
    )
  )
    return false;
  const parsed = Date.parse(value);
  return (
    Number.isFinite(parsed) &&
    new Date(parsed).toISOString().slice(0, 19) === value.slice(0, 19)
  );
}
