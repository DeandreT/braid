import type { Page, Route } from "@playwright/test";
import type { components } from "../src/api/generated";
import { synthetic, syntheticCatalog, revisionPath } from "./catalog.fixture";

export const environmentId = "70000000-0000-4000-8000-000000000001";
export const evidenceId = "70000000-0000-4000-8000-000000000004";
export const scope = synthetic.revision.scope;
const project = `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}`;
const mapping = project + `/mappings/${scope.mappingId}`;
const profile = {
  id: "braid.synthetic-build",
  version: "1",
  runtime: "dotnet",
  runtimeVersion: "10.0.12",
  artifactFormat: "dotnet-library",
  maximumArtifactBytes: 1048576,
};
const backend = {
  backendId: "ferrule",
  engineRevision: "engine",
  engineBuild: "sha256:" + "c".repeat(64),
  adapterVersion: "adapter",
};
const documentProfile = { ...profile, id: "braid.ferrule-csharp-document.v1" };
const interchangeProfile = {
  ...profile,
  id: "braid.ferrule-csharp-x12-interchanges.v1",
};
type Environment = components["schemas"]["BuildEnvironmentV1"];
type Approval = components["schemas"]["BuildApprovalV1"];
type Run = components["schemas"]["BuildRunV1"];
export interface BuildFixture {
  role: "administrator" | "publisher" | "viewer";
  nextWriteFailure: "abort" | "opaque404" | "badReceipt" | null;
  actorPrincipalId: string;
  readOwnMetadata: boolean;
  writes: {
    url: string;
    body: string | null;
    media: string | undefined;
    token: string | undefined;
  }[];
  approval: Approval | null;
  run: Run | null;
  cancelWinsAsSuccess: boolean;
  forgeRun: boolean;
  documentProfiles: boolean;
  interchangeProfiles: boolean;
}

/** Synthetic protocol/UI boundary only. This fixture does not qualify compilation or persistence. */
export async function buildFixture(
  page: Page,
  open = true,
): Promise<BuildFixture> {
  const state: BuildFixture = {
    role: "administrator",
    nextWriteFailure: null,
    actorPrincipalId: synthetic.actor,
    readOwnMetadata: true,
    writes: [],
    approval: null,
    run: null,
    cancelWinsAsSuccess: false,
    forgeRun: false,
    documentProfiles: false,
    interchangeProfiles: false,
  };
  const envs: Environment[] = [
    {
      reference: {
        organizationId: scope.organizationId,
        projectId: scope.projectId,
        environmentId,
        version: "1",
      },
      profile,
      active: true,
      creatorPrincipalId: synthetic.actor,
      createdAtUtc: "2026-10-20T12:00:00Z",
    },
  ];
  const access = () => ({
    role: state.role,
    actions: {
      ...synthetic.mapping.access.actions,
      uploadRevisions: false,
      manageMembership: state.role === "administrator",
    },
  });
  const context = () => ({
    ...synthetic.context,
    actorPrincipalId: state.actorPrincipalId,
    features: {
      ...synthetic.context.features,
      uploadRevisions: false,
    },
  });
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": context(),
    [project]: { ...synthetic.project, access: access() },
    [mapping]: { ...synthetic.mapping, access: access() },
  });
  const reply = (route: Route, value: unknown, status = 200) =>
    route.fulfill({
      status,
      json: value,
      headers: { "Cache-Control": "no-store" },
    });
  const missing = (route: Route) => reply(route, { code: "notFound" }, 404);
  async function written(route: Route, value: unknown, status = 200) {
    const failure = state.nextWriteFailure;
    state.nextWriteFailure = null;
    if (failure === "abort") await route.abort("connectionreset");
    else if (failure === "opaque404") await missing(route);
    else if (failure === "badReceipt" && value && typeof value === "object")
      await reply(
        route,
        { ...value, outcome: status === 201 ? "replayed" : "created" },
        status,
      );
    else await reply(route, value, status);
  }
  const runValue = () =>
    state.forgeRun && state.run
      ? {
          ...state.run,
          revision: {
            ...state.run.revision,
            mappingId: "70000000-0000-4000-8000-000000000099",
          },
        }
      : state.run;
  const succeeded = () => {
    if (state.run)
      Object.assign(state.run, {
        state: "succeeded",
        attempt: 1,
        outcome: "created",
        artifact: {
          sha256: "b".repeat(64),
          byteCount: 2048,
          format: profile.artifactFormat,
        },
      });
  };
  const evidence = {
    run: {
      runId: evidenceId,
      caseId: "70000000-0000-4000-8000-000000000005",
      version: "1",
      revision: scope,
      state: "completed",
      attempt: 1,
      createdAtUtc: "2026-10-20T12:00:00Z",
    },
    executionOutcome: "completed",
    previewStatus: "succeeded",
    outputs: [{ name: null, bytesBase64Url: "e30" }],
    comparison: {
      policy: "braid.regression.exact-bytes.v1",
      outcome: "passed",
      notEvaluatedReason: "none",
      expectedOutputs: 1,
      actualOutputs: 1,
      totalDifferences: 0,
      truncated: false,
      differences: [],
    },
  };
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request(),
      url = new URL(request.url()),
      path = url.pathname,
      method = request.method();
    if (method === "GET" && path === "/api/v1/catalog/context") {
      await reply(route, context());
      return;
    }
    if (method === "GET" && path === mapping) {
      await reply(route, { ...synthetic.mapping, access: access() });
      return;
    }
    if (method === "GET" && path === project) {
      await reply(route, { ...synthetic.project, access: access() });
      return;
    }
    if (method === "GET" && path === mapping + "/regression-runs") {
      await reply(route, { runs: [evidence.run], nextRunId: null });
      return;
    }
    if (
      method === "GET" &&
      path === mapping + "/regression-runs/" + evidenceId
    ) {
      await reply(route, evidence);
      return;
    }
    if (!path.includes("/build-")) {
      await route.fallback();
      return;
    }
    if (method !== "GET")
      state.writes.push({
        url: path,
        body: request.postData(),
        media: request.headers()["content-type"],
        token: request.headers()["x-xsrf-token"],
      });
    if (state.role === "viewer") {
      await missing(route);
      return;
    }
    if (method === "GET" && path === mapping + "/build-capabilities") {
      await reply(route, {
        backend,
        profile,
        readOwnMetadata: state.readOwnMetadata,
        approveBuilds: state.readOwnMetadata,
        submitBuilds: state.readOwnMetadata,
        ...(state.documentProfiles
          ? {
              documentProfile,
              documentBackend: {
                ...backend,
                adapterVersion: "document-adapter",
              },
            }
          : {}),
        ...(state.interchangeProfiles ? { interchangeProfile } : {}),
      });
      return;
    }
    if (
      method === "GET" &&
      path === project + "/build-environment-capabilities"
    ) {
      await reply(route, {
        profile,
        canCreateVersions: state.role === "administrator",
        ...(state.documentProfiles ? { documentProfile } : {}),
        ...(state.interchangeProfiles ? { interchangeProfile } : {}),
      });
      return;
    }
    if (method === "GET" && path === project + "/build-environments") {
      await reply(route, {
        environments: envs,
        nextEnvironmentId: null,
        nextVersion: null,
      });
      return;
    }
    const environment = path.match(
      /\/build-environments\/([^/]+)\/versions\/([^/]+)$/,
    );
    if (environment) {
      const [, id, version] = environment;
      const retained = envs.find(
        (value) =>
          value.reference.environmentId === id &&
          value.reference.version === version,
      );
      if (method === "GET") {
        if (retained) await reply(route, retained);
        else await missing(route);
        return;
      }
      if (method === "PUT" && state.role === "administrator") {
        const body = request.postDataJSON();
        const selectedProfile =
          state.interchangeProfiles && body.profileId === interchangeProfile.id
            ? interchangeProfile
            : state.documentProfiles && body.profileId === documentProfile.id
              ? documentProfile
              : profile;
        if (
          body.profileId !== selectedProfile.id ||
          body.profileVersion !== selectedProfile.version
        ) {
          await reply(route, { code: "preconditionFailed" }, 412);
          return;
        }
        let value = retained;
        if (!value) {
          envs
            .filter((item) => item.reference.environmentId === id)
            .forEach((item) => (item.active = false));
          value = {
            reference: {
              organizationId: scope.organizationId,
              projectId: scope.projectId,
              environmentId: id,
              version,
            },
            profile: { ...selectedProfile },
            active: true,
            creatorPrincipalId: synthetic.actor,
            createdAtUtc: "2026-10-20T12:00:00Z",
          };
          envs.unshift(value);
        }
        await written(
          route,
          { outcome: retained ? "replayed" : "created", environment: value },
          retained ? 200 : 201,
        );
        return;
      }
      await missing(route);
      return;
    }
    if (method === "GET" && path === mapping + "/build-approvals") {
      await reply(route, {
        approvals: state.approval ? [state.approval] : [],
        nextApprovalId: null,
      });
      return;
    }
    const approval = path.match(/\/build-approvals\/([^/]+)(\/revoke)?$/);
    if (approval) {
      const [, id, revoke] = approval;
      if (method === "GET" || (method === "POST" && revoke)) {
        if (!state.approval || state.approval.approvalId !== id) {
          await missing(route);
          return;
        }
        if (revoke) {
          state.approval.revoked = true;
          await written(route, state.approval);
        } else await reply(route, state.approval);
        return;
      }
      if (method === "PUT") {
        const body = request.postDataJSON(),
          retained = state.approval;
        if (!retained)
          state.approval = {
            approvalId: id,
            buildId: body.buildId,
            revision: { ...scope, revisionId: body.revisionId },
            nativeDigest: synthetic.revision.nativeDigest.value,
            backend,
            profile,
            boundaries: [
              { kind: "primaryInput", name: null, format: "json" },
              { kind: "primaryOutput", name: null, format: "json" },
            ],
            environment: {
              organizationId: scope.organizationId,
              projectId: scope.projectId,
              environmentId: body.environmentId,
              version: body.environmentVersion,
            },
            regressionEvidenceId: body.regressionEvidenceId,
            revoked: false,
            environmentActive: true,
            createdAtUtc: "2026-10-20T12:00:00Z",
          };
        await written(
          route,
          {
            outcome: retained ? "replayed" : "created",
            approval: state.approval,
          },
          retained ? 200 : 201,
        );
        return;
      }
    }
    if (method === "GET" && path === mapping + "/build-runs") {
      await reply(route, {
        runs: state.run ? [runValue()] : [],
        nextBuildId: null,
      });
      return;
    }
    const build = path.match(/\/build-runs\/([^/]+)(\/cancel)?$/);
    if (build) {
      const [, id, cancel] = build;
      if (method === "PUT") {
        const body = request.postDataJSON(),
          retained = state.run;
        if (
          !state.approval ||
          state.approval.buildId !== id ||
          state.approval.approvalId !== body.approvalId
        ) {
          await missing(route);
          return;
        }
        if (!retained)
          state.run = {
            buildId: id,
            approvalId: body.approvalId,
            revision: { ...scope },
            state: "queued",
            attempt: 0,
            createdAtUtc: "2026-10-20T12:00:00Z",
            outcome: null,
            artifact: null,
          };
        await written(
          route,
          { outcome: retained ? "replayed" : "created", run: runValue() },
          retained ? 200 : 201,
        );
        return;
      }
      if (!state.run || state.run.buildId !== id) {
        await missing(route);
        return;
      }
      if (method === "POST" && cancel) {
        if (state.cancelWinsAsSuccess) succeeded();
        else
          Object.assign(state.run, {
            state: "canceled",
            outcome: null,
            artifact: null,
          });
        await written(route, runValue());
        return;
      }
      if (method === "GET") {
        await reply(route, runValue());
        return;
      }
    }
    await missing(route);
  });
  await page.goto(revisionPath(synthetic.revision.scope));
  // The shown panel loads itself, so tests that change the fixture first open it later.
  if (open) await openPrepare(page);
  return state;
}

export async function openPrepare(page: Page): Promise<void> {
  await page.getByRole("tab", { name: "Prepare", exact: true }).click();
}
