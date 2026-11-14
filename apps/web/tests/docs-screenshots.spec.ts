import { expect, test, type Page, type TestInfo } from "@playwright/test";
import { createHash } from "node:crypto";
import { readdir, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import type { components } from "../src/api/generated";
import {
  catalogApi,
  mappingPath,
  projectPath,
  projectsPath,
  revisionPath,
  synthetic,
  syntheticCatalog,
} from "./catalog.fixture";
import { controlledData } from "./revision-reports-controlled.fixture";
import { buildFixture, type BuildFixture } from "./builds.fixture";
import { confirmAction } from "./confirmation.fixture";
import { ComparisonFixture, REV } from "./revision-comparison.fixture";
import { workspaceFixture } from "./workspace-chat.fixture";
import { captureProvenance } from "../../../tools/docs-images/capture-source.mjs";

// Documentation screenshots, written to docs/images and listed in
// docs/images/manifest.json. Every reply is self-authored demo data; nothing
// here qualifies a workflow. The demo names below replace the shared synthetic
// fixture labels for this worker only.
type Schema = components["schemas"];

Object.assign(synthetic.context, { organizationName: "Demo organization" });
Object.assign(synthetic.project, {
  name: "Order fulfilment",
  description: "Partner purchase orders converted for the warehouse.",
  createdAtUtc: "2026-09-02T09:00:00Z",
  updatedAtUtc: "2026-10-22T14:30:00Z",
});
Object.assign(synthetic.mapping, {
  name: "Purchase order to warehouse order",
  description:
    "Converts partner purchase orders to the warehouse order format.",
  createdAtUtc: "2026-09-02T09:15:00Z",
  updatedAtUtc: "2026-10-22T14:30:00Z",
});
Object.assign(synthetic.revision, {
  sequence: "4",
  originalFilename: "purchase-order-to-warehouse-order.json",
  committedAtUtc: "2026-10-22T14:30:00Z",
  nativeByteCount: 18432,
});
synthetic.mapping.head.sequence = "4";

const now = new Date("2026-10-24T15:00:00Z");
const base64 = (text: string) => Buffer.from(text).toString("base64url");
const id = (prefix: string, n: number) =>
  `${prefix}-0000-4000-8000-${String(n).padStart(12, "0")}`;

async function prepare(page: Page): Promise<void> {
  await page.clock.setFixedTime(now);
}

/** Register after a fixture's catch-all route, which would otherwise refuse the health check. */
async function healthy(page: Page): Promise<void> {
  await page.route("**/api/v1/health", (route) =>
    route.fulfill({
      json: { status: "ok", service: "weft-api", apiVersion: "v1" },
    }),
  );
}

async function capture(page: Page, testInfo: TestInfo, name: string) {
  await page.evaluate(() => document.fonts.ready);
  await expect(page.locator(".app-shell")).toBeVisible();
  // Focus rings and hover states otherwise vary with the last interaction.
  await page.evaluate(() =>
    (document.activeElement as HTMLElement | null)?.blur(),
  );
  await page.mouse.move(0, 0);
  const image = await page.screenshot({
    path: resolve(testInfo.config.rootDir, "../../../docs/images", name),
    animations: "disabled",
    caret: "hide",
  });
  const root = resolve(testInfo.config.rootDir, "../../..");
  const path = resolve(root, "docs/images/manifest.json");
  const manifest = JSON.parse(await readFile(path, "utf8"));
  const entry = manifest.images.find(
    (image: { file: string }) => image.file === name,
  );
  if (!entry) throw new Error(`No documentation manifest entry for ${name}.`);
  entry.provenance = {
    // Includes untracked capture helpers.
    ...(await captureProvenance(root)),
    imageSha256: createHash("sha256").update(image).digest("hex"),
    viewport: page.viewportSize(),
    deviceScaleFactor: testInfo.project.use.deviceScaleFactor,
    colorScheme: testInfo.project.use.colorScheme,
    locale: testInfo.project.use.locale,
    timezoneId: testInfo.project.use.timezoneId,
    fixedTime: now.toISOString(),
    browser: page.context().browser()!.version(),
    node: process.version,
    npm:
      /npm\/([^ ]+)/.exec(process.env.npm_config_user_agent ?? "")?.[1] ?? null,
    platform: process.platform,
  };
  await writeFile(path, JSON.stringify(manifest, null, 2) + "\n");
}

function demoProjects(): Schema["CatalogProjectV1"][] {
  const names: [string, string, Schema["CatalogRoleV1"]][] = [
    ["Carrier invoices", "Freight invoices from carriers.", "runner"],
    ["Inventory sync", "Daily stock positions for sales channels.", "viewer"],
    ["Returns processing", "Return authorizations and receipts.", "publisher"],
  ];
  return [
    synthetic.project,
    ...names.map(([name, description, role], index) => ({
      ...structuredClone(synthetic.project),
      scope: {
        organizationId: synthetic.org,
        projectId: id("20000000", 11 + index),
      },
      name,
      description,
      access: { ...structuredClone(synthetic.project.access), role },
    })),
  ];
}

function demoMappings(): Schema["CatalogMappingV1"][] {
  const names = [
    [
      "Advance ship notice to partner",
      "Warehouse shipments sent to the partner.",
    ],
    ["Inventory snapshot export", "Stock positions for the partner portal."],
  ];
  return [
    synthetic.mapping,
    ...names.map(([name, description], index) => {
      const mapping = structuredClone(synthetic.mapping);
      mapping.scope = {
        ...mapping.scope,
        mappingId: id("30000000", 11 + index),
      };
      mapping.head = {
        scope: { ...mapping.scope, revisionId: id("40000000", 21 + index) },
        sequence: String(2 + index),
        admission: "valid",
      };
      return { ...mapping, name, description };
    }),
  ];
}

function demoRevisions(): Schema["CatalogRevisionV1"][] {
  return [4, 3, 2, 1].map((sequence) => ({
    ...structuredClone(synthetic.revision),
    scope:
      sequence === 4
        ? synthetic.revision.scope
        : {
            ...synthetic.revision.scope,
            revisionId: id("40000000", 10 + sequence),
          },
    sequence: String(sequence),
    committedAtUtc: [
      "",
      "2026-09-02T09:15:00Z",
      "2026-09-18T11:05:00Z",
      "2026-10-06T16:40:00Z",
      "2026-10-22T14:30:00Z",
    ][sequence]!,
    nativeByteCount: 16000 + sequence * 608,
    nativeDigest: {
      algorithm: "sha256",
      value: "0a1b2c3d"[sequence]!.repeat(64),
    },
  }));
}

async function demoCatalog(page: Page): Promise<void> {
  await prepare(page);
  await syntheticCatalog(page, {
    [catalogApi(projectsPath(synthetic.org))]: {
      projects: demoProjects(),
      nextCursor: null,
    },
    [catalogApi(projectPath(synthetic.project.scope)) + "/mappings"]: {
      mappings: demoMappings(),
      nextCursor: null,
    },
    [catalogApi(mappingPath(synthetic.mapping.scope)) + "/revisions"]: {
      revisions: demoRevisions(),
      nextCursor: null,
    },
  });
  await healthy(page);
}

/** A small purchase-order mapping projected as Ferrule would describe it. */
function demoData() {
  const data = controlledData();
  const document = data.projection.report!.document!;
  const template = document.fields[0]!;
  const locator = (pointer: string): Schema["NativeLocator"] => ({
    kind: "jsonPointer",
    documentId: "entry.native.json",
    pointer,
  });
  document.boundaries = [
    {
      id: "purchase-order",
      name: "purchase-order",
      format: "json",
      role: "primaryInput",
      shape: "object",
      rootFieldId: null,
      nativeLocator: locator("/inputs/0"),
    },
    {
      id: "warehouse-order",
      name: "warehouse-order",
      format: "json",
      role: "primaryOutput",
      shape: "object",
      rootFieldId: null,
      nativeLocator: locator("/outputs/0"),
    },
  ];
  const lines = [
    { kind: "field" as const, name: "lines" },
    { kind: "anyItem" as const },
  ];
  const field = (
    fieldId: string,
    boundaryId: string,
    path: Schema["PathSegment"][],
    scalar: "string" | "integer" | "decimal" = "string",
  ): Schema["ProjectionField"] => ({
    ...structuredClone(template),
    id: fieldId,
    name: (path.at(-1) as { name?: string }).name ?? fieldId,
    boundaryId,
    path,
    dataType: { kind: "scalar", scalarTypes: [scalar] },
    nativeLocator: locator(`/fields/${fieldId}`),
  });
  const input = (
    name: string,
    path: Schema["PathSegment"][],
    scalar?: "string" | "integer" | "decimal",
  ) => field(`po-${name}`, "purchase-order", path, scalar);
  const output = (
    name: string,
    path: Schema["PathSegment"][],
    scalar?: "string" | "integer" | "decimal",
  ) => field(`wo-${name}`, "warehouse-order", path, scalar);
  const f = (name: string) => ({ kind: "field" as const, name });
  document.fields = [
    input("number", [f("order"), f("number")]),
    input("date", [f("order"), f("date")]),
    input("buyer", [f("buyer"), f("name")]),
    input("postal", [f("shipTo"), f("postalCode")]),
    input("sku", [...lines, f("sku")]),
    input("quantity", [...lines, f("quantity")], "integer"),
    input("price", [...lines, f("unitPrice")], "decimal"),
    output("number", [f("orderNumber")]),
    output("date", [f("orderDate")]),
    output("customer", [f("customerName")]),
    output("postal", [f("shipping"), f("postalCode")]),
    output("item", [...lines, f("item")]),
    output("qty", [...lines, f("qty")], "integer"),
    output("amount", [...lines, f("amount")], "decimal"),
  ];
  const rule = (
    target: string,
    summary: string,
    sources: string[],
  ): Schema["ProjectionRule"] => ({
    id: `rule-${target}`,
    outputFieldId: `wo-${target}`,
    summary,
    nativeLocator: locator(`/rules/${target}`),
    dependencies: sources.map((source) => ({
      kind: "field" as const,
      fieldId: `po-${source}`,
      role: "value" as const,
    })),
    details: [],
  });
  document.rules = [
    rule("number", "Copy the purchase order number", ["number"]),
    rule("date", "Format the order date as YYYY-MM-DD", ["date"]),
    rule("customer", "Copy the buyer name", ["buyer"]),
    rule("postal", "Copy the ship-to postal code", ["postal"]),
    rule("item", "Look up the warehouse item for the SKU", ["sku"]),
    rule("qty", "Copy the ordered quantity", ["quantity"]),
    rule("amount", "Multiply quantity by unit price", ["quantity", "price"]),
  ];
  document.coverage = { status: "complete", gaps: [] };
  data.context.features.preview = true;
  data.mapping.access.role = "publisher";
  data.mapping.access.actions.preview = true;
  data.validation.status = "succeeded";
  data.validation.report = {
    ...data.validation.report!,
    status: "succeeded",
    isValid: true,
    totalIssueCount: 0,
    emittedIssueCount: 0,
    truncated: false,
    diagnostics: [],
  };
  return data;
}

async function demoReports(page: Page, data = demoData()) {
  await page.route("**/reports/**", (route) => {
    const url = new URL(route.request().url());
    const response = structuredClone(
      url.pathname.includes("/projection") ? data.projection : data.validation,
    );
    if (response.operation === "validation")
      response.projectionDerivationId = url.searchParams.get(
        "projectionDerivationId",
      );
    return route.fulfill({ json: response });
  });
}

const CASE = id("50000000", 191);
const RUN = id("60000000", 191);
const sampleInput = JSON.stringify(
  {
    order: { number: "PO-10482", date: "10/21/2026" },
    buyer: { name: "Example Outdoor Supply" },
    shipTo: { postalCode: "68102" },
    lines: [{ sku: "TENT-2P", quantity: 4, unitPrice: 129.5 }],
  },
  null,
  2,
);
const expected = JSON.stringify(
  {
    orderNumber: "PO-10482",
    orderDate: "2026-10-21",
    customerName: "Example Outdoor Supply",
    shipping: { postalCode: "68102" },
    lines: [{ item: "100-2041", qty: 4, amount: 518.0 }],
  },
  null,
  2,
);
const actual = expected.replace("2026-10-21", "10/21/2026");

/** The revision workbench with the demo projection, a saved test and its last run. */
async function workbench(page: Page) {
  await prepare(page);
  const data = demoData();
  const mappingApi = catalogApi(mappingPath(data.mapping.scope));
  const runtime = {
    backendId: "ferrule",
    engineRevision: "engine",
    backendBuild: "sha256:" + "c".repeat(64),
    adapterBuild: "adapter",
    profile: "weft.ferrule-json-preview.v1",
  };
  const definition: Schema["RegressionCasePutV1"] = {
    name: "Single-line purchase order",
    revisionId: data.revision.scope.revisionId,
    runtime,
    limits: {
      maximumInputBytes: 8388608,
      maximumOutputBytes: 8388608,
      maximumArtifacts: 64,
      maximumTextBytes: 2048,
    },
    comparisonPolicy: "weft.regression.exact-bytes.v1",
    currentDateTime: "2026-10-22T15:00:00Z",
    target: { kind: "all", name: null },
    primaryBase64Url: base64(sampleInput),
    namedInputs: [],
    parameters: [],
    expected: [{ name: null, bytesBase64Url: base64(expected) }],
  };
  const firstDifferentByte = [...expected].findIndex((c, i) => c !== actual[i]);
  const run = {
    run: {
      runId: RUN,
      caseId: CASE,
      version: "1",
      revision: data.revision.scope,
      state: "completed",
      attempt: 1,
      createdAtUtc: "2026-10-23T08:12:00Z",
    },
    executionOutcome: "completed",
    previewStatus: "succeeded",
    outputs: [{ name: null, bytesBase64Url: base64(actual) }],
    comparison: {
      policy: definition.comparisonPolicy,
      outcome: "failed",
      notEvaluatedReason: "none",
      expectedOutputs: 1,
      actualOutputs: 1,
      totalDifferences: 1,
      truncated: false,
      differences: [
        {
          name: null,
          kind: "bytesDiffer",
          expectedBytes: expected.length,
          actualBytes: actual.length,
          expectedDigest: "a".repeat(64),
          actualDigest: "b".repeat(64),
          firstDifferentByte,
        },
      ],
    },
  };
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": data.context,
    [catalogApi(projectPath(data.project.scope))]: data.project,
    [mappingApi]: data.mapping,
    [mappingApi + "/revisions"]: {
      revisions: demoRevisions(),
      nextCursor: null,
    },
    [data.apiPath]: data.revision,
  });
  await demoReports(page, data);
  await page.route(new RegExp(mappingApi + "/regression-"), async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/capabilities"))
      return route.fulfill({
        json: {
          runtime,
          limits: definition.limits,
          comparisonPolicy: definition.comparisonPolicy,
          readSamples: true,
          publishCases: false,
        },
      });
    if (path.endsWith("/regression-cases"))
      return route.fulfill({
        json: {
          cases: [
            {
              caseId: CASE,
              version: "1",
              revision: data.revision.scope,
              name: definition.name,
              createdAtUtc: "2026-10-22T15:00:00Z",
            },
          ],
          nextCaseId: null,
        },
      });
    if (path.endsWith("/versions/1"))
      return route.fulfill({
        json: {
          caseId: CASE,
          version: "1",
          revision: data.revision.scope,
          definition,
        },
      });
    if (path.endsWith("/regression-runs"))
      return route.fulfill({ json: { runs: [run.run], nextRunId: null } });
    if (path.endsWith(RUN)) return route.fulfill({ json: run });
    return route.fulfill({ status: 404, json: { code: "notFound" } });
  });
  await healthy(page);
  await page.goto(data.browserPath);
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toBeVisible();
  return data;
}

test("catalog projects", async ({ page }, testInfo) => {
  await demoCatalog(page);
  await page.goto(projectsPath(synthetic.org));
  await expect(page.getByText("Returns processing")).toBeVisible();
  await capture(page, testInfo, "catalog-projects.png");
});

test("mapping revisions", async ({ page }, testInfo) => {
  await demoCatalog(page);
  await page.goto(mappingPath(synthetic.mapping.scope));
  await expect(
    page.getByText("Revision 1", { exact: false }).first(),
  ).toBeVisible();
  await capture(page, testInfo, "catalog-mapping.png");
});

test("catalog search", async ({ page }, testInfo) => {
  await demoCatalog(page);
  const { org } = synthetic;
  const { projectId, mappingId, revisionId } = synthetic.revision.scope;
  const text = (value: string) => {
    const start = value.toLowerCase().indexOf("order");
    return {
      text: value,
      highlights: start < 0 ? [] : [{ start, length: 5 }],
    };
  };
  const hit = (
    kind: Schema["CatalogSearchKindV1"],
    title: string,
    description: string,
    resourceId: string | null = null,
  ): Schema["CatalogSearchHitV1"] => ({
    kind,
    organizationId: org,
    projectId,
    mappingId: kind === "project" ? null : mappingId,
    revisionId: kind === "project" ? null : revisionId,
    projectionDerivationId: resourceId ? id("50000000", 1) : null,
    resourceId,
    title: text(title),
    description: text(description),
  });
  await page.route("**/api/v1/organizations/*/search*", (route) =>
    route.fulfill({
      json: {
        query: "order",
        hits: [
          hit("project", "Order fulfilment", synthetic.project.description),
          hit("mapping", synthetic.mapping.name, synthetic.mapping.description),
          hit(
            "field",
            "orderNumber",
            'warehouse-order ["orderNumber"]',
            "wo-number",
          ),
          hit("field", "orderDate", 'warehouse-order ["orderDate"]', "wo-date"),
          hit(
            "rule",
            "Copy the purchase order number",
            'Fills ["orderNumber"]',
            "rule-number",
          ),
        ],
        nextCursor: null,
      },
    }),
  );
  await page.goto("/catalog/search?q=order");
  await expect(page.locator(".catalog-search-results li")).toHaveCount(5);
  await capture(page, testInfo, "catalog-search.png");
});

test("workbench data mapping", async ({ page }, testInfo) => {
  await workbench(page);
  await page
    .getByRole("button", { name: 'Inspect field ["lines"][*]["amount"]' })
    .click();
  await page.getByRole("button", { name: "Data flow", exact: true }).click();
  await page
    .getByRole("button", { name: "Hide inspector", exact: true })
    .click();
  await capture(page, testInfo, "workbench-mapping.png");
});

test("workbench sample preview", async ({ page }, testInfo) => {
  await workbench(page);
  const scope = synthetic.revision.scope;
  const runId = id("80000000", 1);
  const runPath = `/api/v1/organizations/${scope.organizationId}/previews/${runId}`;
  const output = actual.replace("10/21/2026", "2026-10-21");
  const run = (
    state: "queued" | "completed",
  ): Schema["CatalogPreviewRunV1"] => ({
    runId,
    revision: scope,
    subjectKind: "revision",
    draft: null,
    requestFingerprint: "b".repeat(64),
    currentDateTime: "2026-10-24T14:58:00Z",
    state,
    attempt: state === "queued" ? 0 : 1,
    maxAttempts: 3,
    cancelRequested: false,
    outcome: state === "queued" ? null : "succeeded",
    failure: null,
    provenance:
      state === "queued"
        ? null
        : {
            confirmed: true,
            engineRevision: "d".repeat(40),
            backendBuild: "sha256:" + "c".repeat(64),
            adapterBuild: "weft.ferrule-preview-adapter.v1",
            profile: "weft.ferrule-json-preview.v1",
          },
    recordsWritten: state === "queued" ? null : "1",
    outputs:
      state === "queued"
        ? []
        : [
            {
              ordinal: 0,
              boundaryId: "warehouse-order",
              targetName: "",
              targetNameTruncated: false,
              mediaType: "application/json",
              recordsWritten: "1",
              sha256: createHash("sha256").update(output).digest("hex"),
              byteCount: Buffer.byteLength(output),
            },
          ],
    createdAtUtc: "2026-10-24T14:58:00Z",
    startedAtUtc: state === "queued" ? null : "2026-10-24T14:58:01Z",
    finishedAtUtc: state === "queued" ? null : "2026-10-24T14:58:02Z",
    expiresAtUtc: "2026-10-25T14:58:00Z",
  });
  await page.route(`**${catalogApi(revisionPath(scope))}/previews`, (route) =>
    route.fulfill({
      status: 201,
      headers: { Location: runPath },
      json: { outcome: "created", run: run("queued") },
    }),
  );
  await page.route(`**${runPath}**`, (route) =>
    new URL(route.request().url()).pathname.endsWith("/outputs/0")
      ? route.fulfill({
          headers: { "Content-Type": "application/json" },
          body: output,
        })
      : route.fulfill({ json: run("completed") }),
  );
  await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  const region = page.getByRole("region", {
    name: "Sample preview",
    exact: true,
  });
  await region.getByLabel("Primary input (JSON)").fill(sampleInput);
  await region
    .getByRole("button", { name: "Run preview", exact: true })
    .click();
  await region.getByRole("button", { name: "Show Primary output" }).click();
  await expect(
    page.getByRole("region", { name: "Document bytes", exact: true }),
  ).toContainText("2026-10-21");
  await region.evaluate((element) => {
    // Scroll only the workbench pane; the shell itself must stay at the top.
    element.scrollIntoView({ block: "start" });
    window.scrollTo(0, 0);
    element
      .querySelectorAll("textarea")
      .forEach((input) => (input.scrollTop = 0));
  });
  await capture(page, testInfo, "workbench-preview.png");
});

test("workbench regression test", async ({ page }, testInfo) => {
  await workbench(page);
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  await page
    .getByRole("button", {
      name: "Open Single-line purchase order",
      exact: true,
    })
    .click();
  await page
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await page.getByRole("tab", { name: "Diff", exact: true }).click();
  await expect(page.locator(".wb-diff")).toContainText("10/21/2026");
  await page
    .getByRole("heading", { name: "Your run history", exact: true })
    .evaluate((element) => {
      element.scrollIntoView({ block: "start" });
      window.scrollTo(0, 0);
    });
  await capture(page, testInfo, "workbench-regression.png");
});

test("workbench build preparation", async ({ page }, testInfo) => {
  await prepare(page);
  const state: BuildFixture = await buildFixture(page);
  await demoReports(page);
  await healthy(page);
  await page.reload();
  await page.getByRole("tab", { name: "Prepare", exact: true }).click();
  const panel = page.getByRole("region", {
    name: "Prepare this version",
    exact: true,
  });
  await panel
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  await panel
    .getByRole("button", { name: "Approve preparation", exact: true })
    .click();
  await confirmAction(page);
  await expect.poll(() => state.approval?.approvalId).toBeTruthy();
  await panel
    .getByRole("button", { name: "Prepare approved version", exact: true })
    .click();
  await confirmAction(page);
  await expect.poll(() => state.run?.buildId).toBeTruthy();
  state.run!.state = "succeeded";
  Object.assign(state.run!, {
    attempt: 1,
    outcome: "created",
    artifact: {
      sha256: "b".repeat(64),
      byteCount: 48128,
      format: "dotnet-library",
    },
  });
  await panel
    .getByRole("button", { name: /Refresh/ })
    .first()
    .click();
  await expect(panel).toContainText(/succeeded|Succeeded|Ready/);
  await capture(page, testInfo, "workbench-builds.png");
});

test("revision comparison", async ({ page }, testInfo) => {
  await prepare(page);
  const fixture = new ComparisonFixture(page);
  await fixture.install();
  await healthy(page);
  await page.goto(fixture.comparisonPage(`left=${REV[0]}&right=${REV[2]}`));
  const results = page.getByRole("region", { name: "Comparison", exact: true });
  await expect(results.locator(".comparison-headline")).toBeVisible();
  await results.evaluate((element) =>
    element.scrollIntoView({ block: "start" }),
  );
  await capture(page, testInfo, "revision-comparison.png");
});

test("workspace chat", async ({ page }, testInfo) => {
  await prepare(page);
  const { data } = await workspaceFixture(page);
  const org = data.revision.scope.organizationId;
  await page.route(`**/api/v1/organizations/${org}/chat`, (route) =>
    route.fulfill({
      json: {
        schemaVersion: "weft.chat.v1",
        organizationId: org,
        limits: { maxTextBytes: 32768, maxSelectors: 32, turnSeconds: 120 },
        profiles: [
          {
            profile: {
              profileId: "fixture",
              version: 1,
              displayName: "Standard",
              providerId: "fixture",
              modelId: "demo-model",
              supportsTools: true,
            },
            payloadsAllowed: false,
          },
        ],
      },
    }),
  );
  const panel = page.getByRole("complementary", { name: "Workspace chat" });
  await page.goto("/chat");
  await expect(panel).toBeVisible();
  await panel
    .getByRole("button", { name: "New conversation", exact: true })
    .click();
  await panel
    .getByLabel("Question", { exact: true })
    .fill("Which mappings fill the warehouse order number?");
  await panel
    .getByRole("button", { name: "Send question", exact: true })
    .click();
  await expect(panel).toContainText("Answer complete.");
  const resize = page.getByRole("separator", { name: "Resize chat" });
  await resize.focus();
  await resize.press("End");
  await panel
    .getByRole("region", { name: "Selected answer" })
    .evaluate((element) => element.scrollIntoView({ block: "start" }));
  await page.locator("body").focus();
  await capture(page, testInfo, "chat.png");
});

test("user guide", async ({ page }, testInfo) => {
  await prepare(page);
  // The contents come from the guide's own front matter, so the image follows its sections.
  const guide = resolve(testInfo.config.rootDir, "../../../docs/user-guide");
  const sections = [];
  for (const name of await readdir(guide)) {
    if (!name.endsWith(".md")) continue;
    const head = (await readFile(resolve(guide, name), "utf8")).split(
      /\r?\n---/,
    )[0]!;
    const field = (key: string) =>
      new RegExp(`^${key}: (.*)$`, "m").exec(head)?.[1]?.trim() ?? "";
    sections.push({
      id: field("id"),
      title: field("title"),
      summary: field("summary"),
      order: Number(field("order")),
    });
  }
  sections.sort((a, b) => a.order - b.order);
  await syntheticCatalog(page, {
    "/api/v1/help/sections": {
      sections: sections.map(({ id, title, summary }) => ({
        id,
        title,
        summary,
      })),
    },
  });
  await healthy(page);
  await page.goto("/help");
  await expect(
    page.getByRole("list", { name: "Guide sections" }).getByRole("listitem"),
  ).toHaveCount(sections.length);
  await capture(page, testInfo, "help.png");
});

test("organization administration", async ({ page }, testInfo) => {
  await prepare(page);
  const org = synthetic.org;
  const members = [
    [synthetic.actor, "administrator"],
    [id("10000000", 21), "member"],
    [id("10000000", 22), "member"],
  ];
  await syntheticCatalog(page);
  await page.route(`**/api/v1/organizations/${org}/administration`, (route) =>
    route.fulfill({
      headers: { ETag: `"admin-${org}-7"` },
      json: {
        version: 7,
        members: members.map(([principalId, role]) => ({
          principalId,
          active: true,
          role,
          version: 1,
          principalActive: true,
          bindings: [],
        })),
        invitations: [],
        audit: [],
      },
    }),
  );
  await healthy(page);
  await page.goto("/administration");
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  await capture(page, testInfo, "administration.png");
});

test("mapping operations", async ({ page }, testInfo) => {
  await demoCatalog(page);
  await page.route(
    `**${catalogApi(projectPath(synthetic.project.scope))}`,
    (route) =>
      route.fulfill({
        json: {
          ...synthetic.project,
          access: { ...synthetic.project.access, role: "administrator" },
        },
      }),
  );
  const base = catalogApi(mappingPath(synthetic.mapping.scope)) + "/operations";
  const target = (n: number, enabled: boolean, release: string | null) => ({
    kind: "target",
    target_id: id("70000000", n),
    environment_id: id("70000000", 100 + n),
    environment_version: "2",
    enabled,
    active_release_id: release,
    runtime_image: "sha256:" + "f".repeat(64),
    profile: {
      id: "weft.ferrule-csharp-document.v1",
      version: 1,
      runtime: "dotnet",
      runtimeVersion: "10.0.12",
      artifactFormat: "dotnet-library",
      maximumArtifactBytes: 1048576,
    },
    version: "3",
    active: null,
  });
  await page.route(`**${base}**`, (route) =>
    route.fulfill({
      json: new URL(route.request().url()).pathname.endsWith("/summary")
        ? {
            count: 1284,
            failed: 3,
            running: 0,
            retryable: 1,
            failuresByCategory: { invalidInput: 3 },
            meanLatencyMs: 41,
            p95LatencyMs: 87,
          }
        : {
            items: [target(1, true, null)],
            nextOffset: null,
          },
    }),
  );
  await page.goto(mappingPath(synthetic.mapping.scope) + "/operations");
  await expect(
    page.getByRole("button", { name: "Operate this target" }).first(),
  ).toBeVisible();
  await capture(page, testInfo, "operations.png");
});
