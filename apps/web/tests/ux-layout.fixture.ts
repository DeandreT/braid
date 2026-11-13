import type { Page } from "@playwright/test";
import { createHash } from "node:crypto";
import type { components } from "../src/api/generated";
import type { CreationProposal } from "../src/catalog/creation/creation-model";
import {
  catalogApi,
  mappingPath,
  projectPath,
  projectsPath,
} from "./catalog.fixture";
import { workspaceFixture } from "./workspace-chat.fixture";

export const uxHash = (text: string) =>
  createHash("sha256").update(text).digest("hex");
const PROPOSAL = "50000000-0000-4000-8000-000000000081";
const RUN = "80000000-0000-4000-8000-000000000081";
const OPERATION = "50000000-0000-4000-8000-000000000082";
export const uxSample = '{"code":"A42"}';
export const uxOutput = '{"item":"A42"}';

/** Actual Angular UI with synthetic API replies only; no provider, database or worker qualification. */
export async function uxFixture(page: Page, baseURL: string) {
  const origin = new URL(baseURL).origin;
  if (!/^http:\/\/(127\.0\.0\.1|localhost):\d+$/.test(origin))
    throw new Error(
      "UX qualification requires an explicit isolated loopback host",
    );
  const escaped: string[] = [];
  // Assets may reach only the supplied static host. Every API is intercepted below
  // or by workspaceFixture's terminal 404; an unhandled API may never reach a host.
  await page.context().route("**/*", async (route) => {
    const url = new URL(route.request().url());
    if (url.origin !== origin || url.pathname.startsWith("/api/")) {
      escaped.push(url.origin + url.pathname);
      return route.abort("blockedbyclient");
    }
    return route.fallback();
  });
  const workspace = await workspaceFixture(page);
  const data = workspace.data;
  data.context.features.deriveReports = false;
  data.context.features.preview = true;
  data.mapping.access.role = "publisher";
  data.mapping.access.actions.deriveReports = false;
  data.mapping.access.actions.preview = true;
  const projectApi = catalogApi(projectPath(data.project.scope));
  const mappingApi = catalogApi(mappingPath(data.mapping.scope));
  const planJson = JSON.stringify({
    schema: "weft.creation-plan.v1",
    format: "json-record",
    source: [{ Name: "code", Type: 1, Required: true }],
    target: [{ Name: "item", Type: 1, Required: true }],
    bindings: [{ Source: "code", Target: "item" }],
  });
  const proposal: CreationProposal = {
    projectId: data.project.scope.projectId,
    proposalId: PROPOSAL,
    fingerprint: uxHash("isolated-ux-proposal"),
    snapshotDigest: uxHash("retained-ux-source"),
    planDigest: uxHash(planJson),
    nativeDigest: uxHash("trusted-ux-native"),
    name: "Synthetic item-code mapping",
    description: "Retained synthetic requirements",
    planJson,
    approved: false,
    evidence: [
      {
        bundleId: OPERATION,
        sourceId: PROPOSAL,
        sourceVersion: 1,
        chunkOrdinal: 0,
      },
    ],
    assumptions: [
      {
        id: "A1",
        text: "Use the customer's supplied item code.",
        critical: true,
      },
    ],
    questions: [],
    receipt: null,
  };
  const state = {
    proposal,
    escaped,
    writes: [] as { path: string; body: Record<string, unknown> }[],
    loseApprovalAcknowledgement: false,
    regressionUnavailable: false,
  };
  const previewPath = `${data.apiPath}/previews`;
  const runPath = `/api/v1/organizations/${data.project.scope.organizationId}/previews/${RUN}`;
  type PreviewRun = components["schemas"]["CatalogPreviewRunV1"];
  const preview = (finished: boolean): PreviewRun => ({
    runId: RUN,
    revision: data.revision.scope,
    subjectKind: "revision",
    draft: null,
    requestFingerprint: uxHash("isolated-preview"),
    currentDateTime: "2026-11-12T12:00:00Z",
    state: finished ? "completed" : "queued",
    attempt: finished ? 1 : 0,
    maxAttempts: 3,
    cancelRequested: false,
    outcome: finished ? "succeeded" : null,
    failure: null,
    provenance: finished
      ? {
          confirmed: true,
          engineRevision: "synthetic-engine",
          backendBuild: "sha256:" + "c".repeat(64),
          adapterBuild: "weft.ferrule-preview-adapter.v1",
          profile: "weft.ferrule-json-preview.v1",
        }
      : null,
    recordsWritten: finished ? "1" : null,
    outputs: finished
      ? [
          {
            ordinal: 0,
            boundaryId: "primary-output",
            targetName: "",
            targetNameTruncated: false,
            mediaType: "application/json",
            recordsWritten: "1",
            sha256: uxHash(uxOutput),
            byteCount: Buffer.byteLength(uxOutput),
          },
        ]
      : [],
    createdAtUtc: "2026-11-12T12:00:00Z",
    startedAtUtc: finished ? "2026-11-12T12:00:01Z" : null,
    finishedAtUtc: finished ? "2026-11-12T12:00:02Z" : null,
    expiresAtUtc: "2026-11-13T12:00:00Z",
  });
  const runtime = {
    backendId: "ferrule",
    engineRevision: "synthetic-engine",
    backendBuild: "sha256:" + "c".repeat(64),
    adapterBuild: "weft.ferrule-preview-adapter.v1",
    profile: "weft.ferrule-json-preview.v1",
  };
  await page.route("**/api/**", async (route) => {
    const request = route.request(),
      url = new URL(request.url()),
      path = url.pathname;
    const method = request.method();
    if (method !== "GET")
      state.writes.push({
        path,
        body: request.postData() ? request.postDataJSON() : {},
      });
    if (
      path === `${projectApi}/creation-proposals/${PROPOSAL}` &&
      method === "GET"
    )
      return route.fulfill({ json: state.proposal });
    if (
      path.startsWith(`${projectApi}/creation-proposals/${PROPOSAL}/`) &&
      method === "POST"
    ) {
      const action = path.split("/").at(-1);
      const body = request.postDataJSON();
      if (action === "preview") {
        const sample = Buffer.from(body.sampleBase64, "base64").toString(
          "utf8",
        );
        return route.fulfill({
          json: {
            proposalId: PROPOSAL,
            fingerprint: proposal.fingerprint,
            nativeDigest: proposal.nativeDigest,
            sampleDigest: uxHash(sample),
            outcome: "Succeeded",
            format: "json",
            mediaType: "application/json",
            outputDigest: uxHash(uxOutput),
            outputBase64: Buffer.from(uxOutput).toString("base64"),
          },
        });
      }
      if (action === "approve") {
        state.proposal.approved = true;
        state.proposal.receipt = {
          operationId: OPERATION,
          phase: "Committed",
          mappingId: data.mapping.scope.mappingId,
          revisionId: data.revision.scope.revisionId,
        };
        if (state.loseApprovalAcknowledgement) return route.abort("failed");
      }
      if (action === "approve" || action === "recover")
        return route.fulfill({ json: state.proposal });
    }
    if (path === previewPath && method === "POST")
      return route.fulfill({
        status: 201,
        headers: { Location: runPath },
        json: { outcome: "created", run: preview(false) },
      });
    if (path === runPath && method === "GET")
      return route.fulfill({ json: preview(true) });
    if (path === runPath + "/outputs/0" && method === "GET")
      return route.fulfill({ contentType: "application/json", body: uxOutput });
    if (method !== "GET")
      return route.fulfill({ status: 403, json: { code: "notPermitted" } });
    if (path === catalogApi(projectsPath(data.project.scope.organizationId))) {
      const runner = structuredClone(data.project);
      runner.scope.projectId = "20000000-0000-4000-8000-000000000082";
      runner.name = "Synthetic carrier invoices";
      runner.access.role = "runner";
      return route.fulfill({
        json: { projects: [data.project, runner], nextCursor: null },
      });
    }
    if (path.startsWith(mappingApi + "/regression-")) {
      if (state.regressionUnavailable)
        return route.fulfill({ status: 503, json: { code: "unavailable" } });
      if (path.endsWith("/capabilities"))
        return route.fulfill({
          json: {
            runtime,
            limits: {
              maximumInputBytes: 8388608,
              maximumOutputBytes: 8388608,
              maximumArtifacts: 64,
              maximumTextBytes: 2048,
            },
            comparisonPolicy: "weft.regression.exact-bytes.v1",
            readSamples: true,
            publishCases: false,
          },
        });
      if (path.endsWith("/regression-cases"))
        return route.fulfill({ json: { cases: [], nextCaseId: null } });
      if (path.endsWith("/regression-runs"))
        return route.fulfill({ json: { runs: [], nextRunId: null } });
    }
    // Unavailable is deliberately not a successful build, qualified release or permission grant.
    if (path.includes("/build-"))
      return route.fulfill({ status: 503, json: { code: "unavailable" } });
    return route.fallback();
  });
  return {
    ...state,
    state,
    data,
    workspace,
    reviewPath: `${projectPath(data.project.scope)}/creation/${PROPOSAL}?fingerprint=${proposal.fingerprint}`,
  };
}
