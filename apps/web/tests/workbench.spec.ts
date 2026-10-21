import { expect, test, type Page } from "@playwright/test";
import { controlledData } from "./revision-reports-controlled.fixture";
import {
  catalogApi,
  projectPath,
  mappingPath,
  syntheticCatalog,
} from "./catalog.fixture";
import type { components } from "../src/api/generated";

const CASE = "50000000-0000-4000-8000-000000000091";
const RUN = "60000000-0000-4000-8000-000000000091";
const base64 = (text: string) => Buffer.from(text).toString("base64url");

async function workbench(page: Page, fieldCount = 1) {
  const data = controlledData();
  data.projection.report!.document!.coverage = { status: "complete", gaps: [] };
  if (fieldCount > 1) {
    const document = data.projection.report!.document!;
    const field = structuredClone(document.fields[0]!);
    const rule = structuredClone(document.rules[0]!);
    document.fields = Array.from({ length: fieldCount }, (_, index) => ({
      ...structuredClone(field),
      id: `synthetic-field-${index}`,
      name: `value${index}`,
      path: [
        { kind: "field" as const, name: `group${Math.floor(index / 20)}` },
        { kind: "field" as const, name: `value${index}` },
      ],
    }));
    document.rules = document.fields.map((field, index) => ({
      ...structuredClone(rule),
      id: `synthetic-rule-${index}`,
      outputFieldId: field.id,
      summary: `Declared rule ${String(index).padStart(3, "0")}`,
    }));
  }
  const mappingApi = catalogApi(mappingPath(data.mapping.scope));
  const runtime = {
    backendId: "ferrule",
    engineRevision: "engine",
    backendBuild: "sha256:" + "c".repeat(64),
    adapterBuild: "adapter",
    profile: "weft.ferrule-json-preview.v1",
  };
  const definition: components["schemas"]["RegressionCasePutV1"] = {
    name: "Exact number sample",
    revisionId: data.revision.scope.revisionId,
    runtime,
    limits: {
      maximumInputBytes: 8388608,
      maximumOutputBytes: 8388608,
      maximumArtifacts: 64,
      maximumTextBytes: 2048,
    },
    comparisonPolicy: "weft.regression.exact-bytes.v1",
    currentDateTime: "2026-10-20T12:00:00Z",
    target: { kind: "all", name: null },
    primaryBase64Url: base64('{"privateSample":9007199254740993}'),
    namedInputs: [],
    parameters: [],
    expected: [
      { name: null, bytesBase64Url: base64('{"savedValue":9007199254740993}') },
    ],
  };
  let readSamples = true;
  const writes: string[] = [];
  const run = {
    run: {
      runId: RUN,
      caseId: CASE,
      version: "1",
      revision: data.revision.scope,
      state: "completed",
      attempt: 1,
      createdAtUtc: "2026-10-20T12:00:00Z",
    },
    executionOutcome: "completed",
    previewStatus: "succeeded",
    outputs: [
      { name: null, bytesBase64Url: base64('{"savedValue":9007199254740994}') },
    ],
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
          expectedBytes: 31,
          actualBytes: 31,
          expectedDigest: "a".repeat(64),
          actualDigest: "b".repeat(64),
          firstDifferentByte: 29,
        },
      ],
    },
  };
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": data.context,
    [catalogApi(projectPath(data.project.scope))]: data.project,
    [mappingApi]: data.mapping,
    [data.apiPath]: data.revision,
  });
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
  await page.route(new RegExp(mappingApi + "/regression-"), async (route) => {
    const request = route.request(),
      path = new URL(request.url()).pathname;
    if (request.method() !== "GET") writes.push(path);
    if (path.endsWith("/capabilities"))
      return route.fulfill({
        json: {
          runtime,
          limits: definition.limits,
          comparisonPolicy: definition.comparisonPolicy,
          readSamples,
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
              createdAtUtc: run.run.createdAtUtc,
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
  await page.goto(data.browserPath);
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toBeVisible();
  return {
    data,
    writes,
    revoke: () => {
      readSamples = false;
    },
  };
}

test("compact mapping is keyboard accessible, grouped, filterable and adjustable", async ({
  page,
}) => {
  await workbench(page);
  const row = page.getByRole("button", {
    name: 'Inspect field ["savedValue"]',
    exact: true,
  });
  await row.focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toContainText('Controlled output ["savedValue"]');
  await page.getByRole("button", { name: "Lineage", exact: true }).click();
  await expect(page.locator(".wb-inline-lineage")).toContainText(
    "Controlled saved explanation",
  );
  await page.getByLabel("Filter mapping fields").fill("no such field");
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toContainText("No fields match");
  await page.getByRole("button", { name: "Clear filters" }).click();
  const resize = page.getByRole("separator", {
    name: "Resize Source column",
    exact: true,
  });
  const old = Number(await resize.getAttribute("aria-valuenow"));
  await resize.focus();
  await page.keyboard.press("ArrowRight");
  await expect(resize).toHaveAttribute("aria-valuenow", String(old + 10));
  await page.getByRole("tab", { name: "Mapping", exact: true }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(
    page.getByRole("tab", { name: "Preview", exact: true }),
  ).toBeFocused();
  await page.keyboard.press("Home");
  await expect(
    page.getByRole("tab", { name: "Mapping", exact: true }),
  ).toBeFocused();
  await expect(page.locator(".app-shell")).toHaveClass(/app-workbench/);
});

test("selected saved case and run survive workflow changes without writes or numeric rounding", async ({
  page,
}) => {
  const fixture = await workbench(page);
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  await page
    .getByRole("button", { name: "Load regression cases", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Open Exact number sample", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await page
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await page.getByRole("tab", { name: "Diff", exact: true }).click();
  await expect(page.locator(".wb-diff")).toContainText("9007199254740993");
  await expect(page.locator(".wb-diff")).toContainText("9007199254740994");
  await page
    .getByRole("navigation", { name: "Affected mapping fields" })
    .getByRole("button", {
      name: 'Controlled output ["savedValue"]',
      exact: true,
    })
    .click();
  await expect(
    page.getByRole("tab", { name: "Mapping", exact: true }),
  ).toHaveAttribute("aria-selected", "true");
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toContainText("Controlled saved explanation");
  await page.getByRole("tab", { name: "Mapping", exact: true }).click();
  await page
    .getByRole("button", { name: "Sample values", exact: true })
    .click();
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toContainText("9007199254740994");
  await expect(page.locator(".wb-sample-label")).toContainText(
    "Exact number sample",
  );
  await page.getByRole("tab", { name: "Builds", exact: true }).click();
  await expect(
    page.getByRole("tab", { name: "Readiness", exact: true }),
  ).toBeVisible();
  await page.getByRole("tab", { name: "Mapping", exact: true }).click();
  await expect(page.locator(".wb-sample-label")).toBeVisible();
  expect(fixture.writes).toEqual([]);
  const persisted = await page.evaluate(() =>
    JSON.stringify({ ...localStorage, ...sessionStorage }),
  );
  expect(persisted).not.toContain("privateSample");
  expect(persisted).not.toContain("9007199254740993");
});

test("revoked sample access clears dock and table values", async ({ page }) => {
  const fixture = await workbench(page);
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  await page
    .getByRole("button", { name: "Load regression cases", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Open Exact number sample", exact: true })
    .click();
  await page.getByRole("tab", { name: "Input", exact: true }).click();
  await expect(page.locator(".wb-dock-body")).toContainText("privateSample");
  fixture.revoke();
  await page
    .getByRole("button", { name: "Refresh test access", exact: true })
    .click();
  await expect(page.locator(".wb-dock-body")).not.toContainText(
    "privateSample",
  );
  await page.getByRole("tab", { name: "Mapping", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Sample values", exact: true }),
  ).toBeDisabled();
});

test("palette, responsive inspector and unavailable release state do not invent capabilities", async ({
  page,
}) => {
  const fixture = await workbench(page);
  await page.keyboard.press("Control+k");
  await expect(
    page.getByRole("dialog", { name: "Go to…", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("combobox", { name: "Search commands and current context" })
    .fill("Releases");
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("table").filter({ hasText: "Active release" }),
  ).toContainText("Not exposed by the connected browser API");
  expect(fixture.writes).toEqual([]);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("tab", { name: "Mapping", exact: true }).click();
  await page.getByRole("button", { name: "Inspector", exact: true }).click();
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).not.toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});

test("large field tables page and collapse without losing keyboard selection", async ({
  page,
}) => {
  await workbench(page, 140);
  const table = page.getByRole("table", { name: "Mapping field connections" });
  const rows = table.locator("[data-row-key]");
  await expect(rows).toHaveCount(100);
  const group = table.locator(".wb-group-row button").first();
  await group.click();
  await expect(rows).toHaveCount(80);
  await group.click();
  await expect(rows).toHaveCount(100);
  await rows.first().focus();
  await page.keyboard.press("ArrowDown");
  await expect(rows.nth(1)).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(table.locator("tr.wb-selected")).toHaveCount(1);
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toContainText("value1");
  await page.keyboard.press("Escape");
  await expect(table.locator("tr.wb-selected")).toHaveCount(0);
  await page
    .locator(".wb-table-footer")
    .getByRole("button", { name: "Next", exact: true })
    .click();
  await expect(rows).toHaveCount(40);
  await page.getByLabel("Filter mapping fields").fill("value139");
  await expect(rows).toHaveCount(1);
  await rows.first().click();
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toContainText("value139");
  await expect(page).toHaveURL(/\/revisions\/[a-f0-9-]+$/);
});

test("columns, density and both resize handles work together on the dense layout", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await workbench(page, 60);
  const table = page.getByRole("table", { name: "Mapping field connections" });
  await page.locator(".wb-columns > summary").click();
  await page.getByLabel("Transformation", { exact: true }).uncheck();
  await expect(table.getByRole("columnheader")).toHaveCount(3);
  await page.getByLabel("Transformation", { exact: true }).check();
  await expect(table.getByRole("columnheader")).toHaveCount(4);
  await page.keyboard.press("Escape");
  await expect(page.locator(".wb-columns")).not.toHaveAttribute("open", "");
  for (const [name, key] of [
    ["Resize inspector", "ArrowLeft"],
    ["Resize investigation dock", "ArrowUp"],
  ]) {
    const separator = page.getByRole("separator", { name: name!, exact: true });
    const value = Number(await separator.getAttribute("aria-valuenow"));
    await separator.focus();
    await page.keyboard.press(key!);
    await expect(separator).toHaveAttribute(
      "aria-valuenow",
      String(value + 10),
    );
  }
  await page
    .getByRole("button", { name: "compact · 28px rows", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "comfortable · 32px rows", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "comfortable · 32px rows", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "dense · 24px rows", exact: true }),
  ).toBeVisible();
  await table.locator("[data-row-key]").first().click();
  await page.getByRole("button", { name: "Lineage", exact: true }).click();
  await page.screenshot({
    path: testInfo.outputPath("synthetic-workbench-desktop.png"),
  });
});

test("narrow scaled text keeps role, controls and overflow inside the workbench", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await workbench(page, 60);
  await page.addStyleTag({ content: ":root {font-size:24px !important}" });
  const catalog = await page.locator(".catalog-context").boundingBox();
  const context = await page.locator(".wb-context-path").boundingBox();
  expect(catalog).not.toBeNull();
  expect(context).not.toBeNull();
  expect(context!.y).toBeGreaterThanOrEqual(catalog!.y + catalog!.height);
  await expect(page.locator(".wb-role")).toBeVisible();
  await page.getByRole("button", { name: "Inspector", exact: true }).click();
  const inspector = page.getByRole("complementary", {
    name: "Context inspector",
  });
  await expect(inspector).toBeVisible();
  await inspector.getByRole("tab", { name: "Rule", exact: true }).focus();
  await page.keyboard.press("Escape");
  await expect(
    page.getByRole("button", { name: "Inspector", exact: true }),
  ).toBeFocused();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: testInfo.outputPath("synthetic-workbench-scaled-mobile.png"),
  });
});
