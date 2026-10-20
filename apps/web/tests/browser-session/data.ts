import type { components } from "../../src/api/generated";

export type Context = components["schemas"]["CatalogContextV1"];
export type Mapping = components["schemas"]["CatalogMappingV1"];
export type Revision = components["schemas"]["CatalogRevisionV1"];
export type Feature = "creation" | "metadata" | "upload" | "append";
export const ids = Object.freeze({
  organization: "10000000-0000-4000-8000-000000000001",
  actor: "10000000-0000-4000-8000-000000000002",
  foreignActor: "10000000-0000-4000-8000-000000000088",
  foreignOrganization: "10000000-0000-4000-8000-000000000089",
  project: "20000000-0000-4000-8000-000000000001",
  foreignProject: "20000000-0000-4000-8000-000000000088",
  createdProject: "20000000-0000-4000-8000-000000000081",
  mapping: "30000000-0000-4000-8000-000000000001",
  futureMapping: "30000000-0000-4000-8000-000000000081",
  foreignMapping: "30000000-0000-4000-8000-000000000088",
  revision: "40000000-0000-4000-8000-000000000001",
  futureRevision: "40000000-0000-4000-8000-000000000081",
  operation: "50000000-0000-4000-8000-000000000081",
  projection: "60000000-0000-4000-8000-000000000081",
});
export const scope = {
  organizationId: ids.organization,
  projectId: ids.project,
  mappingId: ids.mapping,
};
export const projectPath = `/catalog/organizations/${ids.organization}/projects/${ids.project}`;
export const mappingPath = `${projectPath}/mappings/${ids.mapping}`;
export const revisionPath = `${mappingPath}/revisions/${ids.revision}`;
export const api = (path: string): string =>
  path.replace(/^\/catalog/, "/api/v1");
export const browserPaths: Record<Feature, string> = {
  creation: `/catalog/organizations/${ids.organization}/projects/new`,
  metadata: `${mappingPath}/edit`,
  upload: `${projectPath}/mappings/new`,
  append: `${mappingPath}/revisions/new`,
};
export const inputIds: Record<Feature, string> = {
  creation: "project-name",
  metadata: "metadata-name",
  upload: "upload-name",
  append: "append-filename",
};
export const clone = <T>(value: T): T => structuredClone(value);
export const actions = {
  view: true,
  createProjects: true,
  createMappings: true,
  editMetadata: true,
  uploadRevisions: true,
  readReports: true,
  deriveReports: false,
  chat: false,
  preview: false,
  manageMembership: false,
} satisfies components["schemas"]["CatalogActionsV1"];
const { view: _view, ...featureActions } = actions;
export function context(): Context {
  return {
    mode: "development",
    actorPrincipalId: ids.actor,
    allowedOrganizationId: ids.organization,
    organizationName: "Owned synthetic session workspace",
    features: { ...featureActions, readCatalog: true },
    organizationActions: { createProjects: true },
    requestToken: "fixture-before-admission",
  };
}
export const project: components["schemas"]["CatalogProjectV1"] = {
  scope: { organizationId: ids.organization, projectId: ids.project },
  access: { role: "administrator", actions: { ...actions } },
  name: "Owned synthetic project",
  description: "Controlled browser evidence only",
  creatorPrincipalId: ids.actor,
  version: "1",
  createdAtUtc: "2026-01-01T00:00:00Z",
  updatedAtUtc: "2026-01-01T00:00:00Z",
};
export const revision: Revision = {
  scope: { ...scope, revisionId: ids.revision },
  sequence: "9007199254740993",
  admission: "valid",
  backendId: "ferrule",
  committedAtUtc: "2026-01-01T00:00:00Z",
  creatorPrincipalId: ids.actor,
  originalFilename: "owned.native.json",
  logicalDocument: "entry.native.json",
  nativeByteCount: 8,
  nativeDigest: { algorithm: "sha256", value: "a".repeat(64) },
};
export const mapping: Mapping = {
  scope,
  access: { role: "administrator", actions: { ...actions } },
  name: "Owned synthetic mapping",
  description: "Controlled current mapping",
  backendId: "ferrule",
  ownerPrincipalId: ids.actor,
  creatorPrincipalId: ids.actor,
  version: "1",
  createdAtUtc: project.createdAtUtc,
  updatedAtUtc: project.updatedAtUtc,
  head: {
    scope: revision.scope,
    sequence: revision.sequence,
    admission: "valid",
  },
};

export function savedReports() {
  const nativeLocator = {
    kind: "jsonPointer" as const,
    documentId: "entry.native.json",
    pointer: "/owned",
  };
  const provenance = {
    revision: revision.scope,
    nativeArtifactDigest: revision.nativeDigest,
    backend: {
      id: "ferrule",
      revision: "owned-fixture",
      buildId: "owned-fixture",
    },
    adapterVersion: "owned-fixture",
    confirmed: true,
  };
  const projection: components["schemas"]["CatalogProjectionReportV1"] = {
    schemaVersion: "weft.catalog-revision-report.v1",
    operation: "projection",
    revision: revision.scope,
    state: "available",
    readOutcome: "observed",
    status: "succeeded",
    failure: null,
    nextAttemptAtUtc: null,
    currentAttempt: null,
    result: {
      derivationId: ids.projection,
      generation: "1",
      startedAtUtc: "2026-01-01T00:00:00Z",
      finishedAtUtc: "2026-01-01T00:00:01Z",
    },
    report: {
      status: "succeeded",
      diagnostics: [],
      provenance,
      document: {
        schemaVersion: "weft.mapping-projection.v1",
        provenance: {
          revision: revision.scope,
          nativeArtifactDigest: revision.nativeDigest,
          backend: provenance.backend,
          adapterVersion: "owned-fixture",
          projectionVersion: "weft.ferrule-projection.v1",
        },
        boundaries: [
          {
            id: "owned-output",
            name: "Owned private saved explanation",
            format: "json",
            role: "primaryOutput",
            shape: "object",
            rootFieldId: null,
            nativeLocator,
          },
        ],
        fields: [],
        rules: [],
        controls: [],
        parameters: [],
        relationships: [],
        capabilities: [],
        diagnostics: [],
        coverage: {
          status: "partial",
          gaps: [
            {
              code: "ownedGap",
              message: "Owned controlled gap",
              nativeLocator,
            },
          ],
        },
      },
    },
  };
  const validation: components["schemas"]["CatalogValidationReportV1"] = {
    schemaVersion: "weft.catalog-revision-report.v1",
    operation: "validation",
    revision: revision.scope,
    state: "available",
    readOutcome: "observed",
    status: "succeeded",
    failure: null,
    nextAttemptAtUtc: null,
    currentAttempt: null,
    result: {
      derivationId: "70000000-0000-4000-8000-000000000081",
      generation: "1",
      startedAtUtc: "2026-01-01T00:00:00Z",
      finishedAtUtc: "2026-01-01T00:00:01Z",
    },
    projectionDerivationId: null,
    report: {
      status: "succeeded",
      isValid: true,
      totalIssueCount: 0,
      emittedIssueCount: 0,
      truncated: false,
      diagnostics: [],
      provenance,
    },
  };
  return { projection, validation };
}
