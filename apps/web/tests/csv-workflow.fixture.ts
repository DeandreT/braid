import { createHash } from "node:crypto";
import type { Page } from "@playwright/test";
import {
  catalogApi,
  mappingPath,
  revisionPath,
  synthetic,
  syntheticCatalog,
} from "./catalog.fixture";
import type {
  CaseCapabilities,
  CaseDefinition,
} from "../src/catalog/regression/regression-api";
import type {
  PreviewRun,
  PreviewSubmission,
} from "../src/catalog/previews/preview-types";
import {
  csvPreviewProfile,
  csvRowsPolicy,
  type CsvDocumentDialect,
} from "../src/catalog/previews/csv-contract";

export const csvScope = synthetic.revision.scope;
export const csvRunId = "80000000-0000-4000-8000-000000000330";
export const csvCaseId = "50000000-0000-4000-8000-000000000330";
export const csvDialect: CsvDocumentDialect = {
  schema: "weft.csv-dialect.v1",
  delimiter: "\t",
  quote: '"',
  quoteDisabled: false,
  hasHeaderRow: true,
  utf8Bom: false,
  preserveEmptyStrings: true,
};
export const csvBytes =
  'code\tdescription\r\nA\t"comma, quote ""kept""\r\nsecond line"\r\n';
export const csvCapabilities: CaseCapabilities = {
  runtime: {
    backendId: "ferrule",
    engineRevision: "engine",
    backendBuild: "build",
    adapterBuild: "json-adapter",
    profile: "weft.ferrule-json-preview.v1",
  },
  csvRuntime: {
    backendId: "ferrule",
    engineRevision: "engine",
    backendBuild: "build",
    adapterBuild: "csv-adapter",
    profile: csvPreviewProfile,
  },
  comparisonPolicies: ["weft.regression.exact-bytes.v1", csvRowsPolicy],
  comparisonPolicy: "weft.regression.exact-bytes.v1",
  limits: {
    maximumInputBytes: 8388608,
    maximumOutputBytes: 8388608,
    maximumArtifacts: 64,
    maximumTextBytes: 2048,
  },
  readSamples: true,
  publishCases: true,
};

export function csvRun(): PreviewRun {
  return {
    runId: csvRunId,
    revision: csvScope,
    requestFingerprint: "b".repeat(64),
    currentDateTime: "2026-10-20T08:30:00Z",
    state: "completed",
    attempt: 1,
    maxAttempts: 3,
    cancelRequested: false,
    outcome: "succeeded",
    failure: null,
    provenance: {
      confirmed: true,
      engineRevision: "engine",
      backendBuild: "build",
      adapterBuild: "csv-adapter",
      profile: csvPreviewProfile,
    },
    recordsWritten: "1",
    outputs: [
      {
        ordinal: 0,
        boundaryId: "output",
        targetName: "",
        targetNameTruncated: false,
        mediaType: "text/tab-separated-values",
        recordsWritten: "1",
        sha256: createHash("sha256").update(csvBytes).digest("hex"),
        byteCount: Buffer.byteLength(csvBytes),
        csvDialect,
      },
    ],
    createdAtUtc: "2026-10-20T08:30:00Z",
    startedAtUtc: "2026-10-20T08:30:01Z",
    finishedAtUtc: "2026-10-20T08:30:02Z",
    expiresAtUtc: "2026-10-21T08:30:00Z",
  };
}

export class CsvWorkflowFixture {
  capabilities = structuredClone(csvCapabilities);
  result = csvRun();
  submissions: { key: string; body: PreviewSubmission }[] = [];
  publications: { id: string; version: string; body: CaseDefinition }[] = [];
  failPublication = false;
  csvPreviewEnabled = true;

  async install(page: Page) {
    await syntheticCatalog(page, {
      "/api/v1/catalog/context": {
        ...synthetic.context,
        features: {
          ...synthetic.context.features,
          preview: true,
          ...(this.csvPreviewEnabled ? { csvPreview: true } : {}),
        },
      },
      [catalogApi(mappingPath(synthetic.mapping.scope))]: {
        ...synthetic.mapping,
        access: {
          role: "publisher",
          actions: { ...synthetic.mapping.access.actions, preview: true },
        },
      },
    });
    const mappingRoot = `/api/v1/organizations/${csvScope.organizationId}/projects/${csvScope.projectId}/mappings/${csvScope.mappingId}`;
    const submitPath = `${catalogApi(revisionPath(csvScope))}/previews`;
    const runPath = `/api/v1/organizations/${csvScope.organizationId}/previews/${csvRunId}`;
    await page.route(
      `**${mappingRoot}/regression-cases/capabilities`,
      (route) => route.fulfill({ json: this.capabilities }),
    );
    await page.route(`**${submitPath}`, async (route) => {
      this.submissions.push({
        key: route.request().headers()["idempotency-key"]!,
        body: route.request().postDataJSON(),
      });
      await route.fulfill({
        status: 201,
        headers: { Location: runPath },
        json: { outcome: "created", run: this.result },
      });
    });
    await page.route(`**${runPath}/outputs/0`, (route) =>
      route.fulfill({
        headers: { "Content-Type": "text/tab-separated-values" },
        body: csvBytes,
      }),
    );
    await page.route(
      new RegExp(`${mappingRoot}/regression-cases/[^/]+/versions/1$`),
      async (route) => {
        const id = new URL(route.request().url()).pathname.split("/").at(-3)!;
        if (route.request().method() === "PUT") {
          this.publications.push({
            id,
            version: "1",
            body: route.request().postDataJSON(),
          });
          if (this.failPublication) {
            this.failPublication = false;
            await route.abort("failed");
            return;
          }
          await route.fulfill({
            status: this.publications.length === 1 ? 201 : 200,
            json: {
              caseId: id,
              version: "1",
              revision: csvScope,
              outcome: this.publications.length === 1 ? "created" : "replayed",
            },
          });
        } else {
          const saved = this.publications.at(-1);
          await route.fulfill(
            saved
              ? {
                  json: {
                    caseId: id,
                    version: "1",
                    revision: csvScope,
                    definition: {
                      ...saved.body,
                      parameters: [...saved.body.parameters].sort((a, b) =>
                        a.name === b.name ? 0 : a.name < b.name ? -1 : 1,
                      ),
                    },
                  },
                }
              : { status: 404, json: { code: "notFound" } },
          );
        }
      },
    );
    await page.goto(revisionPath(csvScope));
    await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  }
}
