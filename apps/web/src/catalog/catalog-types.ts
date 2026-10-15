import type { components } from "../api/generated";

export type CatalogContext = components["schemas"]["CatalogContextV1"];
export type CatalogProject = components["schemas"]["CatalogProjectV1"];
export type CatalogMapping = components["schemas"]["CatalogMappingV1"];
export type CatalogRevision = components["schemas"]["CatalogRevisionV1"];
export type CatalogProjectPage = components["schemas"]["CatalogProjectPageV1"];
export type CatalogMappingPage = components["schemas"]["CatalogMappingPageV1"];
export type CatalogRevisionPage =
  components["schemas"]["CatalogRevisionPageV1"];
export type CatalogProjectScope =
  components["schemas"]["CatalogProjectScopeV1"];
export type CatalogMappingScope =
  components["schemas"]["CatalogMappingScopeV1"];
export type CatalogRevisionScope =
  components["schemas"]["CatalogRevisionScopeV1"];
export type CatalogScope =
  | { organizationId: string }
  | CatalogProjectScope
  | CatalogMappingScope
  | CatalogRevisionScope;

export type CatalogFailureKind =
  "notFound" | "rejected" | "invalidLink" | "unavailable";

/** Closed display categories; response bodies and transport diagnostics stay out of UI state. */
export class CatalogReadError extends Error {
  constructor(readonly kind: CatalogFailureKind) {
    super("The catalog request could not be completed.");
    this.name = "CatalogReadError";
  }
}

export type CatalogReadState<T> =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "ready"; data: T; context: CatalogContext }
  | { kind: CatalogFailureKind };

export type CatalogContextState =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "ready"; context: CatalogContext }
  | { kind: CatalogFailureKind };

export function isCatalogId(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
      value,
    ) &&
    value !== "00000000-0000-0000-0000-000000000000"
  );
}

/** Check the bounded wire alphabet without decoding or interpreting the opaque cursor. */
export function isCatalogCursor(value: unknown): value is string {
  if (
    typeof value !== "string" ||
    value.length < 2 ||
    value.length > 683 ||
    !/^[A-Za-z0-9_-]+$/.test(value)
  )
    return false;
  const remainder = value.length % 4;
  return (
    remainder === 0 ||
    (remainder === 2 && /[AQgw]$/.test(value)) ||
    (remainder === 3 && /[AEIMQUYcgkosw048]$/.test(value))
  );
}

/** Copy every route component before deferred requests can outlive a caller's object. */
export function snapshotCatalogScope(scope: CatalogScope): CatalogScope {
  if (!scope || !isCatalogId(scope.organizationId))
    throw new CatalogReadError("invalidLink");
  const copy: CatalogScope = { organizationId: scope.organizationId };
  if ("projectId" in scope) {
    if (!isCatalogId(scope.projectId))
      throw new CatalogReadError("invalidLink");
    Object.assign(copy, { projectId: scope.projectId });
  }
  if ("mappingId" in scope) {
    if (!("projectId" in copy) || !isCatalogId(scope.mappingId))
      throw new CatalogReadError("invalidLink");
    Object.assign(copy, { mappingId: scope.mappingId });
  }
  if ("revisionId" in scope) {
    if (!("mappingId" in copy) || !isCatalogId(scope.revisionId))
      throw new CatalogReadError("invalidLink");
    Object.assign(copy, { revisionId: scope.revisionId });
  }
  return copy;
}

function projectRoute(scope: CatalogProjectScope): string {
  const copy = snapshotCatalogScope(scope);
  if (!("projectId" in copy)) throw new CatalogReadError("invalidLink");
  return `${catalogRoutes.projects(copy.organizationId)}/${copy.projectId}`;
}

function mappingRoute(scope: CatalogMappingScope): string {
  const copy = snapshotCatalogScope(scope);
  if (!("mappingId" in copy)) throw new CatalogReadError("invalidLink");
  return `${projectRoute(copy as CatalogMappingScope)}/mappings/${copy.mappingId}`;
}

export const catalogRoutes = {
  projects(organizationId: string): string {
    if (!isCatalogId(organizationId)) throw new CatalogReadError("invalidLink");
    return `/catalog/organizations/${organizationId}/projects`;
  },
  createProject(organizationId: string): string {
    return `${catalogRoutes.projects(organizationId)}/new`;
  },
  project: projectRoute,
  mapping: mappingRoute,
  editMapping(scope: CatalogMappingScope): string {
    return `${mappingRoute(scope)}/edit`;
  },
  revision(scope: CatalogRevisionScope): string {
    const copy = snapshotCatalogScope(scope);
    if (!("revisionId" in copy)) throw new CatalogReadError("invalidLink");
    return `${mappingRoute(copy as CatalogRevisionScope)}/revisions/${copy.revisionId}`;
  },
};
