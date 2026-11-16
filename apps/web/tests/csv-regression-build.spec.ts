import "@angular/compiler";
import { expect, test } from "@playwright/test";
import { buildFixture, evidenceId } from "./builds.fixture";
import { confirmAction } from "./confirmation.fixture";
import { synthetic } from "./catalog.fixture";
import {
  csvBytes,
  csvCapabilities,
  csvCaseId,
  csvDialect,
  csvRun,
  csvScope,
  CsvWorkflowFixture,
} from "./csv-workflow.fixture";
import { previewCaseDefinition } from "../src/catalog/previews/preview-case";
import {
  csvBuildProfile,
  csvPreviewProfile,
  csvRowsPolicy,
} from "../src/catalog/previews/csv-contract";

const mappingRoot = `/api/v1/organizations/${csvScope.organizationId}/projects/${csvScope.projectId}/mappings/${csvScope.mappingId}`;
const projectRoot = `/api/v1/organizations/${csvScope.organizationId}/projects/${csvScope.projectId}`;

test("saved CSV regression retains its native dialect and offers row comparison only in the CSV runtime", async ({
  page,
}) => {
  const fixture = new CsvWorkflowFixture();
  await fixture.install(page);
  const definition = previewCaseDefinition(
    csvRun(),
    {
      profile: csvPreviewProfile,
      target: { kind: "all", name: null },
      parameters: [],
      inputs: [{ name: null, content: "" }],
    },
    csvCapabilities,
    [Buffer.from(csvBytes)],
    "Native CSV case",
  );
  definition.comparisonPolicy = csvRowsPolicy;
  await page.route(
    new RegExp(`${mappingRoot}/regression-cases(?:\\?|$)`),
    (route) =>
      route.fulfill({
        json: {
          cases: [
            {
              caseId: csvCaseId,
              version: "1",
              revision: csvScope,
              name: definition.name,
              createdAtUtc: "2026-10-20T12:00:00Z",
            },
          ],
          nextCaseId: null,
        },
      }),
  );
  await page.route(
    `**${mappingRoot}/regression-cases/${csvCaseId}/versions/1`,
    (route) =>
      route.fulfill({
        json: {
          caseId: csvCaseId,
          version: "1",
          revision: csvScope,
          definition,
        },
      }),
  );
  await page.route(
    new RegExp(`${mappingRoot}/regression-runs/[^/]+$`),
    (route) => {
      const runId = new URL(route.request().url()).pathname.split("/").at(-1)!;
      return route.request().method() === "PUT"
        ? route.fulfill({ status: 201, json: { runId, outcome: "created" } })
        : route.fulfill({
            json: {
              run: {
                runId,
                caseId: csvCaseId,
                version: "1",
                revision: csvScope,
                state: "completed",
                attempt: 1,
                createdAtUtc: "2026-10-20T12:00:00Z",
              },
              executionOutcome: "completed",
              previewStatus: "succeeded",
              outputs: [
                {
                  name: null,
                  bytesBase64Url: Buffer.from(
                    csvBytes.replace("A\t", "B\t"),
                  ).toString("base64url"),
                  mediaType: "text/tab-separated-values",
                  csvDialect,
                },
              ],
              comparison: {
                policy: csvRowsPolicy,
                outcome: "failed",
                notEvaluatedReason: "none",
                expectedOutputs: 1,
                actualOutputs: 1,
                totalDifferences: 1,
                truncated: false,
                differences: [
                  {
                    name: null,
                    kind: "csvRowsDiffer",
                    expectedBytes: Buffer.byteLength(csvBytes),
                    actualBytes: Buffer.byteLength(csvBytes),
                    expectedDigest: "a".repeat(64),
                    actualDigest: "b".repeat(64),
                    firstDifferentByte: null,
                    csvLocation: { row: 2, column: 1 },
                  },
                ],
              },
            },
          });
    },
  );
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  await page
    .getByRole("button", { name: "Open Native CSV case", exact: true })
    .click();
  await page
    .getByRole("button", { name: "View expected primary", exact: true })
    .click();
  await expect(
    page.locator("braid-document-view table").getByRole("columnheader"),
  ).toHaveText(["code", "description"]);
  await page
    .getByRole("button", { name: "Run saved version", exact: true })
    .click();
  await expect(
    page.getByRole("cell", { name: "Row 2 · column 1", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Edit as next version", exact: true })
    .click();
  await expect(
    page.getByLabel("Execution profile", { exact: true }),
  ).toHaveValue("csv");
  await expect(
    page.getByLabel("Comparison policy", { exact: true }),
  ).toHaveValue(csvRowsPolicy);
  await expect(
    page.getByText("Only exact-byte comparison can qualify a build.", {
      exact: false,
    }),
  ).toBeVisible();
  await page
    .getByLabel("Execution profile", { exact: true })
    .selectOption("json");
  await expect(
    page.getByLabel("Comparison policy", { exact: true }),
  ).toHaveCount(0);
});

test("CSV compiler registration submits only the explicitly advertised profile", async ({
  page,
}) => {
  const state = await buildFixture(page);
  const profile = {
    id: "braid.synthetic-build",
    version: "1",
    runtime: "dotnet",
    runtimeVersion: "10.0.12",
    artifactFormat: "dotnet-library",
    maximumArtifactBytes: 1048576,
  };
  const csvProfile = {
    ...profile,
    id: csvBuildProfile,
    artifactFormat: "braid.dotnet-assembly.v1",
  };
  await page.route(`**${projectRoot}/build-environment-capabilities`, (route) =>
    route.fulfill({ json: { profile, csvProfile, canCreateVersions: true } }),
  );
  let submitted: unknown;
  await page.route(
    new RegExp(`${projectRoot}/build-environments/[^/]+/versions/1$`),
    (route) => {
      const id = new URL(route.request().url()).pathname.split("/").at(-3)!;
      submitted = route.request().postDataJSON();
      return route.fulfill({
        status: 201,
        json: {
          outcome: "created",
          environment: {
            reference: {
              organizationId: csvScope.organizationId,
              projectId: csvScope.projectId,
              environmentId: id,
              version: "1",
            },
            profile: csvProfile,
            active: true,
            creatorPrincipalId: synthetic.actor,
            createdAtUtc: "2026-10-20T12:00:00Z",
          },
        },
      });
    },
  );
  const panel = page.getByRole("region", {
    name: "Prepare this version",
    exact: true,
  });
  await panel
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  await panel
    .getByRole("combobox", { name: "Compiler profile", exact: true })
    .selectOption({ label: "CSV / TSV with native dialect" });
  await panel
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await expect
    .poll(() => submitted)
    .toEqual({ profileId: csvBuildProfile, profileVersion: "1" });
  expect(state.approval).toBeNull();
});

test("a passing CSV row comparison is refused as build evidence", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await page.route(`**${mappingRoot}/regression-runs/${evidenceId}`, (route) =>
    route.fulfill({
      json: {
        run: {
          runId: evidenceId,
          caseId: csvCaseId,
          version: "1",
          revision: csvScope,
          state: "completed",
          attempt: 1,
          createdAtUtc: "2026-10-20T12:00:00Z",
        },
        executionOutcome: "completed",
        previewStatus: "succeeded",
        outputs: [
          {
            name: null,
            bytesBase64Url: Buffer.from(csvBytes).toString("base64url"),
            mediaType: "text/tab-separated-values",
            csvDialect,
          },
        ],
        comparison: {
          policy: csvRowsPolicy,
          outcome: "passed",
          notEvaluatedReason: "none",
          expectedOutputs: 1,
          actualOutputs: 1,
          totalDifferences: 0,
          truncated: false,
          differences: [],
        },
      },
    }),
  );
  const panel = page.getByRole("region", {
    name: "Prepare this version",
    exact: true,
  });
  await panel
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  await expect(
    panel.getByText(
      "This run did not pass exact-byte comparison for the displayed revision.",
      { exact: true },
    ),
  ).toBeVisible();
  await expect(
    panel.getByRole("button", { name: "Approve preparation", exact: true }),
  ).toBeDisabled();
  expect(state.writes).toHaveLength(0);
});
