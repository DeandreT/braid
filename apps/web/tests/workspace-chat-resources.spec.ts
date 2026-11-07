import { expect, test } from "@playwright/test";
import { workspaceFixture, legacyId } from "./workspace-chat.fixture";
import {
  citationLink,
  type WorkspaceCitation,
} from "../src/chat/workspace-chat-types";

for (const kind of [
  "previewRun",
  "regressionCase",
  "regressionRun",
  "buildRun",
  "buildApproval",
  "buildEnvironment",
] as const) {
  test(`resource citation opens exact authorized ${kind} metadata without model work`, async ({
    page,
  }) => {
    const f = await workspaceFixture(page),
      scope = f.data.revision.scope;
    const profile = {
      id: "weft.synthetic-build",
      version: "1",
      runtime: "dotnet",
      runtimeVersion: "10.0.12",
      artifactFormat: "dotnet-library",
      maximumArtifactBytes: 1048576,
    };
    const environment = {
      organizationId: scope.organizationId,
      projectId: scope.projectId,
      environmentId: legacyId,
      version: "1",
    };
    const runtime = {
      backendId: "ferrule",
      engineRevision: "engine",
      backendBuild: "sha256:" + "c".repeat(64),
      adapterBuild: "adapter",
      profile: "weft.ferrule-json-preview.v1",
    };
    const reads: string[] = [];
    await page.route("**/api/v1/**", async (route) => {
      const path = new URL(route.request().url()).pathname;
      if (route.request().method() !== "GET") return route.fallback();
      const reply = (value: unknown) => {
        reads.push(path);
        return route.fulfill({ json: value });
      };
      if (path.endsWith(`/previews/${legacyId}`))
        return reply({
          runId: legacyId,
          revision: scope,
          requestFingerprint: "b".repeat(64),
          currentDateTime: "2026-10-20T08:30:00Z",
          state: "queued",
          attempt: 0,
          maxAttempts: 3,
          cancelRequested: false,
          outcome: null,
          failure: null,
          provenance: null,
          recordsWritten: null,
          outputs: [],
          createdAtUtc: "2026-10-20T08:30:00Z",
          startedAtUtc: null,
          finishedAtUtc: null,
          expiresAtUtc: "2026-10-21T08:30:00Z",
        });
      if (path.endsWith("/regression-cases/capabilities"))
        return reply({
          runtime,
          limits: {
            maximumInputBytes: 8388608,
            maximumOutputBytes: 8388608,
            maximumArtifacts: 64,
            maximumTextBytes: 2048,
          },
          comparisonPolicy: "weft.regression.exact-bytes.v1",
          readSamples: false,
          publishCases: false,
        });
      if (path.endsWith("/regression-cases"))
        return reply({
          cases: [
            {
              caseId: legacyId,
              version: "1",
              revision: scope,
              name: "Retained case",
              createdAtUtc: "2026-10-20T12:00:00Z",
            },
          ],
          nextCaseId: null,
        });
      if (path.endsWith(`/regression-runs/${legacyId}`))
        return reply({
          run: {
            runId: legacyId,
            caseId: legacyId,
            version: "1",
            revision: scope,
            state: "queued",
            attempt: 0,
            createdAtUtc: "2026-10-20T12:00:00Z",
          },
          executionOutcome: null,
          previewStatus: null,
          outputs: null,
          comparison: null,
        });
      if (path.endsWith(`/build-runs/${legacyId}`))
        return reply({
          buildId: legacyId,
          approvalId: legacyId,
          revision: scope,
          state: "queued",
          attempt: 0,
          createdAtUtc: "2026-10-20T12:00:00Z",
          outcome: null,
          artifact: null,
        });
      if (path.endsWith(`/build-approvals/${legacyId}`))
        return reply({
          approvalId: legacyId,
          buildId: legacyId,
          revision: scope,
          nativeDigest: "a".repeat(64),
          backend: {
            backendId: "ferrule",
            engineRevision: "engine",
            engineBuild: "sha256:" + "c".repeat(64),
            adapterVersion: "adapter",
          },
          profile,
          boundaries: [
            { kind: "primaryInput", name: null, format: "json" },
            { kind: "primaryOutput", name: null, format: "json" },
          ],
          environment,
          regressionEvidenceId: legacyId,
          revoked: false,
          environmentActive: true,
          createdAtUtc: "2026-10-20T12:00:00Z",
        });
      if (path.endsWith(`/build-environments/${legacyId}/versions/1`))
        return reply({
          reference: environment,
          profile,
          active: true,
          creatorPrincipalId: f.data.context.actorPrincipalId,
          createdAtUtc: "2026-10-20T12:00:00Z",
        });
      return route.fallback();
    });
    const c: WorkspaceCitation = {
      kind,
      organizationId: scope.organizationId,
      projectId: scope.projectId,
      mappingId: kind === "buildEnvironment" ? null : scope.mappingId,
      revisionId: kind === "previewRun" ? scope.revisionId : null,
      subjectId: legacyId,
      version:
        kind === "regressionCase" || kind === "buildEnvironment" ? "1" : null,
      target: null,
      reportDerivationId: null,
      sectionId: null,
    };
    await page.goto(`${citationLink(c)}&conversationId=${legacyId}`);
    await expect(
      page.getByRole("link", { name: "Return to conversation", exact: true }),
    ).toHaveAttribute("href", `/chat?conversationId=${legacyId}`);
    await expect(page.locator("dl")).toContainText(legacyId);
    await expect(
      page.getByRole("link", {
        name:
          kind === "buildEnvironment"
            ? "Open project"
            : kind === "previewRun" ||
                kind === "regressionCase" ||
                kind === "regressionRun" ||
                kind === "buildApproval" ||
                kind === "buildRun"
              ? "Open exact revision"
              : "Open mapping",
        exact: true,
      }),
    ).toBeVisible();
    expect(reads.length).toBeGreaterThan(0);
    expect(f.posts).toHaveLength(0);
    expect(
      reads.some(
        (path) =>
          /\/versions\/1$/.test(path) && path.includes("regression-cases"),
      ),
    ).toBe(false);
  });
}

test("resource links cannot retarget a different organization or silently load missing resources", async ({
  page,
}) => {
  const f = await workspaceFixture(page),
    scope = f.data.revision.scope;
  const c: WorkspaceCitation = {
    kind: "buildRun",
    ...scope,
    revisionId: null,
    subjectId: legacyId,
    version: null,
    target: null,
    reportDerivationId: null,
    sectionId: null,
  };
  for (const extra of [
    "conversationId=invalid",
    `conversationId=${legacyId}&conversationId=${legacyId}`,
  ]) {
    await page.goto(`${citationLink(c)}&${extra}`);
    await expect(page.locator("dl")).toHaveCount(0);
    await expect(
      page.getByRole("link", { name: "Return to conversation", exact: true }),
    ).toHaveCount(0);
    expect(f.reads.some((path) => path.includes("/build-runs/"))).toBe(false);
  }
  await page.goto(
    citationLink({
      ...c,
      organizationId: "10000000-0000-4000-8000-000000000099",
    }),
  );
  await expect(page.locator("dl")).toHaveCount(0);
  expect(f.reads.some((path) => path.includes("/build-runs/"))).toBe(false);
  await page.goto(citationLink(c));
  await expect(page.locator("dl")).toHaveCount(0);
  await expect
    .poll(() => f.reads.some((path) => path.includes("/build-runs/")))
    .toBe(true);
  expect(f.posts).toHaveLength(0);
});
