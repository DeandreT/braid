import {
  HttpClient,
  HttpErrorResponse,
  HttpParams,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  defer,
  map,
  switchMap,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import {
  CatalogReadError,
  isCatalogCursor,
  isCatalogId,
  snapshotCatalogScope,
  type CatalogContext,
  type CatalogMapping,
  type CatalogMappingPage,
  type CatalogMappingScope,
  type CatalogProject,
  type CatalogProjectPage,
  type CatalogProjectScope,
  type CatalogRevision,
  type CatalogRevisionPage,
  type CatalogRevisionScope,
  type CatalogScope,
} from "./catalog-types";
import { trimCatalogName } from "./catalog-text";
import { BrowserSessionState } from "../auth/browser-session-state";
import type { CatalogSearchPage } from "./search/catalog-search";
import {
  isIdentityContext,
  isSessionRequestToken,
} from "../auth/browser-session-types";

const pageSize = 10;
const actionKeys = [
  "view",
  "createProjects",
  "createMappings",
  "editMetadata",
  "uploadRevisions",
  "readReports",
  "chat",
  "preview",
  "manageMembership",
  "deriveReports",
] as const;
const featureKeys = [
  "readCatalog",
  ...actionKeys.filter((key) => key !== "view"),
] as const;

@Injectable({ providedIn: "root" })
export class CatalogApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  context(): Observable<CatalogContext> {
    return this.sessions.observe().pipe(
      switchMap(() => this.get("/api/v1/catalog/context", isContext)),
      map((context) => {
        if (!this.sessions.accepts(context)) {
          this.sessions.invalidate();
          throw new CatalogReadError("unavailable");
        }
        return context;
      }),
      catchError((error: unknown) => throwError(() => catalogFailure(error))),
    );
  }

  projects(
    organizationId: string,
    cursor: string | null = null,
  ): Observable<CatalogProjectPage> {
    return defer(() => {
      requireId(organizationId);
      return this.get(
        `/api/v1/organizations/${organizationId}/projects`,
        (value): value is CatalogProjectPage =>
          isPage(value, "projects", (item) =>
            isProject(item, { organizationId }),
          ),
        pageParameters(cursor),
      );
    });
  }

  project(scope: CatalogProjectScope): Observable<CatalogProject> {
    return captured(
      () => projectScope(scope),
      (copy) =>
        this.get(projectPath(copy), (value): value is CatalogProject =>
          isProject(value, copy),
        ),
    );
  }

  mappings(
    scope: CatalogProjectScope,
    cursor: string | null = null,
  ): Observable<CatalogMappingPage> {
    return captured(
      () => projectScope(scope),
      (copy) =>
        this.get(
          `${projectPath(copy)}/mappings`,
          (value): value is CatalogMappingPage =>
            isPage(value, "mappings", (item) => isMapping(item, copy)),
          pageParameters(cursor),
        ),
    );
  }

  mapping(scope: CatalogMappingScope): Observable<CatalogMapping> {
    return captured(
      () => mappingScope(scope),
      (copy) =>
        this.get(mappingPath(copy), (value): value is CatalogMapping =>
          isMapping(value, copy),
        ),
    );
  }

  revisions(
    scope: CatalogMappingScope,
    cursor: string | null = null,
  ): Observable<CatalogRevisionPage> {
    return captured(
      () => mappingScope(scope),
      (copy) =>
        this.get(
          `${mappingPath(copy)}/revisions`,
          (value): value is CatalogRevisionPage =>
            isPage(value, "revisions", (item) => isRevision(item, copy)),
          pageParameters(cursor),
        ),
    );
  }

  revision(scope: CatalogRevisionScope): Observable<CatalogRevision> {
    return captured(
      () => {
        const copy = snapshotCatalogScope(scope);
        if (!("revisionId" in copy)) throw new CatalogReadError("invalidLink");
        return copy as CatalogRevisionScope;
      },
      (copy) =>
        this.get(
          `${mappingPath(copy)}/revisions/${copy.revisionId}`,
          (value): value is CatalogRevision => isRevision(value, copy),
        ),
    );
  }

  private get<T>(
    url: string,
    guard: (value: unknown) => value is T,
    params?: HttpParams,
    timeoutMs = 8000,
  ): Observable<T> {
    // A read that meets another action's routine check waits for it; without a presented
    // identity it is refused unsent.
    return this.sessions.whenSettled().pipe(
      catchError(() => throwError(() => new CatalogReadError("rejected"))),
      switchMap(() => {
        const generation = this.sessions.generation();
        const sessionMode = this.sessions.state().kind === "authenticated";
        return this.http.get<unknown>(url, { params }).pipe(
          timeout(timeoutMs),
          map((value) => {
            if (generation !== this.sessions.generation() || !guard(value))
              throw new CatalogReadError("unavailable");
            return value;
          }),
          catchError((error: unknown) => {
            if (
              generation === this.sessions.generation() &&
              sessionMode &&
              error instanceof HttpErrorResponse &&
              (error.status === 401 || error.status === 403)
            )
              this.sessions.rejectProtectedRequest(generation);
            return throwError(() => catalogFailure(error));
          }),
        );
      }),
    );
  }

  search(
    organizationId: string,
    query: string,
    cursor: string | null = null,
  ): Observable<CatalogSearchPage> {
    return defer(() => import("./search/catalog-search")).pipe(
      switchMap(({ isSearchPage, isSearchCursor }) => {
        requireId(organizationId);
        if (cursor !== null && !isSearchCursor(cursor))
          throw new CatalogReadError("invalidLink");
        let params = new HttpParams().set("q", query).set("limit", "25");
        if (cursor !== null) params = params.set("cursor", cursor);
        return this.get(
          `/api/v1/organizations/${organizationId}/search`,
          (value): value is CatalogSearchPage =>
            isSearchPage(value, organizationId, query),
          params,
          32000,
        );
      }),
    );
  }
}

function captured<TScope, TResult>(
  snapshot: () => TScope,
  request: (scope: TScope) => Observable<TResult>,
): Observable<TResult> {
  try {
    const scope = snapshot();
    return defer(() => request(scope));
  } catch (error: unknown) {
    return throwError(() => catalogFailure(error));
  }
}

export function catalogFailure(error: unknown): CatalogReadError {
  if (error instanceof CatalogReadError) return error;
  if (error instanceof HttpErrorResponse) {
    if (error.status === 404) return new CatalogReadError("notFound");
    if (error.status === 401 || error.status === 403)
      return new CatalogReadError("rejected");
    if (error.status === 400) return new CatalogReadError("invalidLink");
  }
  return new CatalogReadError("unavailable");
}

function requireId(id: unknown): asserts id is string {
  if (!isCatalogId(id)) throw new CatalogReadError("invalidLink");
}

function projectScope(scope: CatalogProjectScope): CatalogProjectScope {
  const copy = snapshotCatalogScope(scope);
  if (!("projectId" in copy)) throw new CatalogReadError("invalidLink");
  return { organizationId: copy.organizationId, projectId: copy.projectId };
}

function mappingScope(scope: CatalogMappingScope): CatalogMappingScope {
  const copy = snapshotCatalogScope(scope);
  if (!("mappingId" in copy)) throw new CatalogReadError("invalidLink");
  const mapping = copy as CatalogMappingScope;
  return {
    organizationId: mapping.organizationId,
    projectId: mapping.projectId,
    mappingId: mapping.mappingId,
  };
}

function projectPath(scope: CatalogProjectScope): string {
  return `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}`;
}

function mappingPath(scope: CatalogMappingScope): string {
  return `${projectPath(scope)}/mappings/${scope.mappingId}`;
}

function pageParameters(cursor: string | null): HttpParams {
  let params = new HttpParams().set("limit", pageSize);
  if (cursor !== null) {
    if (!isCatalogCursor(cursor)) throw new CatalogReadError("invalidLink");
    params = params.set("cursor", cursor);
  }
  return params;
}

type ObjectValue = Record<string, unknown>;
function object(value: unknown): value is ObjectValue {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function booleanFields(
  value: unknown,
  keys: readonly string[],
): value is ObjectValue {
  return object(value) && keys.every((key) => typeof value[key] === "boolean");
}

function isContext(value: unknown): value is CatalogContext {
  return (
    object(value) &&
    ((value["mode"] === "development" && value["identityContext"] == null) ||
      (value["mode"] === "session" &&
        isIdentityContext(value["identityContext"]) &&
        isSessionRequestToken(value["requestToken"]))) &&
    isCatalogId(value["actorPrincipalId"]) &&
    isCatalogId(value["allowedOrganizationId"]) &&
    text(value["organizationName"], 200, true) &&
    booleanFields(value["features"], featureKeys) &&
    value["features"]["readCatalog"] === true &&
    (!value["features"]["deriveReports"] ||
      value["features"]["readReports"] === true) &&
    booleanFields(value["organizationActions"], ["createProjects"]) &&
    (!value["organizationActions"]["createProjects"] ||
      value["features"]["createProjects"] === true) &&
    typeof value["requestToken"] === "string" &&
    value["requestToken"].length > 0 &&
    value["requestToken"].length <= 4096
  );
}

function access(value: unknown): boolean {
  return (
    object(value) &&
    ["viewer", "runner", "publisher", "administrator"].includes(
      value["role"] as string,
    ) &&
    booleanFields(value["actions"], actionKeys) &&
    value["actions"]["view"] === true &&
    (!value["actions"]["deriveReports"] ||
      value["actions"]["readReports"] === true)
  );
}

function matchesScope(
  value: unknown,
  expected: CatalogScope,
  keys: readonly string[],
): boolean {
  return (
    object(value) &&
    keys.every((key) => isCatalogId(value[key])) &&
    Object.entries(expected).every(([key, id]) => value[key] === id)
  );
}

const projectKeys = ["organizationId", "projectId"];
const mappingKeys = [...projectKeys, "mappingId"];
const revisionKeys = [...mappingKeys, "revisionId"];

function isProject(
  value: unknown,
  expected: CatalogScope,
): value is CatalogProject {
  return (
    object(value) &&
    matchesScope(value["scope"], expected, projectKeys) &&
    metadata(value) &&
    access(value["access"])
  );
}

function isMapping(
  value: unknown,
  expected: CatalogScope,
): value is CatalogMapping {
  if (
    !object(value) ||
    !matchesScope(value["scope"], expected, mappingKeys) ||
    !metadata(value) ||
    !access(value["access"]) ||
    !text(value["backendId"], 128, true) ||
    !isCatalogId(value["ownerPrincipalId"]) ||
    !object(value["head"])
  )
    return false;
  return (
    matchesScope(
      value["head"]["scope"],
      value["scope"] as CatalogMappingScope,
      revisionKeys,
    ) &&
    positiveDecimal(value["head"]["sequence"]) &&
    admission(value["head"]["admission"])
  );
}

function isRevision(
  value: unknown,
  expected: CatalogScope,
): value is CatalogRevision {
  return (
    object(value) &&
    matchesScope(value["scope"], expected, revisionKeys) &&
    positiveDecimal(value["sequence"]) &&
    admission(value["admission"]) &&
    isCatalogId(value["creatorPrincipalId"]) &&
    date(value["committedAtUtc"]) &&
    text(value["backendId"], 128, true) &&
    text(value["originalFilename"], 255, true) &&
    value["logicalDocument"] === "entry.native.json" &&
    object(value["nativeDigest"]) &&
    value["nativeDigest"]["algorithm"] === "sha256" &&
    typeof value["nativeDigest"]["value"] === "string" &&
    /^[0-9a-f]{64}$/.test(value["nativeDigest"]["value"]) &&
    typeof value["nativeByteCount"] === "number" &&
    Number.isInteger(value["nativeByteCount"]) &&
    value["nativeByteCount"] >= 1 &&
    value["nativeByteCount"] <= 4194304
  );
}

function isPage(
  value: unknown,
  key: string,
  item: (value: unknown) => boolean,
): boolean {
  return (
    object(value) &&
    Array.isArray(value[key]) &&
    value[key].length <= pageSize &&
    value[key].every(item) &&
    (value["nextCursor"] === undefined ||
      value["nextCursor"] === null ||
      isCatalogCursor(value["nextCursor"]))
  );
}

function metadata(value: ObjectValue): boolean {
  return (
    text(value["name"], 200, true) &&
    text(value["description"], 4000) &&
    isCatalogId(value["creatorPrincipalId"]) &&
    positiveDecimal(value["version"]) &&
    date(value["createdAtUtc"]) &&
    date(value["updatedAtUtc"]) &&
    Date.parse(value["updatedAtUtc"]) >= Date.parse(value["createdAtUtc"])
  );
}

function positiveDecimal(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[1-9][0-9]{0,18}$/.test(value) &&
    (value.length < 19 || value <= "9223372036854775807")
  );
}

function admission(value: unknown): boolean {
  return value === "valid" || value === "decodedInvalid";
}

function date(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
      value,
    ) &&
    Number.isFinite(Date.parse(value))
  );
}

function text(
  value: unknown,
  maximumScalars: number,
  nonblank = false,
): value is string {
  if (
    typeof value !== "string" ||
    value.length > maximumScalars * 2 ||
    value.includes("\0") ||
    (nonblank && trimCatalogName(value).length === 0)
  )
    return false;
  let count = 0;
  for (const scalar of value) {
    const code = scalar.codePointAt(0)!;
    if (++count > maximumScalars || (code >= 0xd800 && code <= 0xdfff))
      return false;
  }
  return true;
}
