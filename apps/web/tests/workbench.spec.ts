import { expect, test, type Page } from "@playwright/test";
import { writeFile } from "node:fs/promises";
import { controlledData } from "./revision-reports-controlled.fixture";
import {
  catalogApi,
  projectPath,
  mappingPath,
  syntheticCatalog,
} from "./catalog.fixture";
import type { components } from "../src/api/generated";
import { workbenchLabels, dockLabels } from "../src/workbench/presentation";
import { layoutStorageKey } from "../src/workbench/layout-preferences";

// Exercise native scrollport geometry in headless runs too; Playwright's
// default --hide-scrollbars would otherwise conceal lost usable viewport space.
test.use({
  launchOptions: {
    executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE,
    ignoreDefaultArgs: ["--hide-scrollbars"],
  },
});

const CASE = "50000000-0000-4000-8000-000000000091";
const RUN = "60000000-0000-4000-8000-000000000091";
const base64 = (text: string) => Buffer.from(text).toString("base64url");

test("the offline notice states only browser connectivity and never retries a mutation", async ({
  page,
  context,
}) => {
  const fixture = await workbench(page);
  await context.setOffline(true);
  await expect(
    page.getByRole("status", { name: "Browser offline", exact: true }),
  ).toContainText("may still finish");
  expect(fixture.writes).toEqual([]);
  await context.setOffline(false);
  await expect(
    page.getByRole("status", { name: "Browser offline", exact: true }),
  ).not.toBeVisible();
  expect(fixture.writes).toEqual([]);
});

test("readable saved timestamps retain their exact value through a keyboard disclosure", async ({
  page,
}) => {
  await workbench(page);
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  const metadata = page.locator(".wb-case-table weft-metadata-value");
  const summary = metadata.locator("summary");
  await expect(summary).toContainText("UTC");
  await summary.focus();
  await page.keyboard.press("Enter");
  await expect(metadata.locator("code")).toBeVisible();
  await expect(metadata.locator("code")).toHaveText("2026-10-20T12:00:00Z");
});

test("layout preferences survive reload and Reset layout without persisting private selection", async ({
  page,
}) => {
  await workbench(page, 20);
  await page.locator(".wb-columns > summary").click();
  await page.getByRole("checkbox", { name: "Rule", exact: true }).uncheck();
  await page.keyboard.press("Escape");
  const resize = page.getByRole("separator", {
    name: "Resize Incoming field column",
    exact: true,
  });
  await resize.focus();
  await page.keyboard.press("ArrowRight");
  await page.getByRole("button", { name: "comfortable · 32px rows" }).click();
  await page
    .getByRole("button", {
      name: 'Inspect field ["group0"]["value0"]',
      exact: true,
    })
    .click();
  await page.getByLabel("Filter mapping fields").fill("value0");
  const stored = await page.evaluate(
    (key) => localStorage.getItem(key),
    layoutStorageKey,
  );
  expect(stored).toBeTruthy();
  expect(stored).not.toMatch(
    /value0|synthetic-field|50000000|60000000|privateSample|binding|epoch|token/,
  );
  await page.reload();
  await expect(
    page
      .getByRole("table", { name: "Mapping field connections" })
      .getByRole("columnheader"),
  ).toHaveCount(3);
  await expect(resize).toHaveAttribute("aria-valuenow", "200");
  await expect(
    page.getByRole("button", { name: "dense · 24px rows" }),
  ).toBeVisible();
  await expect(page.getByLabel("Filter mapping fields")).toHaveValue("");
  await expect(page.locator("tr.wb-selected")).toHaveCount(0);
  await page.getByRole("button", { name: "Reset layout", exact: true }).click();
  await expect(
    page
      .getByRole("table", { name: "Mapping field connections" })
      .getByRole("columnheader"),
  ).toHaveCount(4);
  await expect(resize).toHaveAttribute("aria-valuenow", "190");
  await expect(
    page.getByRole("button", { name: "comfortable · 32px rows" }),
  ).toBeVisible();
});

test("semantic tokens have readable contrast and selected rows retain their own treatment", async ({
  page,
}) => {
  await workbench(page);
  const tokens = await page.evaluate(() => {
    const style = getComputedStyle(document.documentElement);
    const roles = [
      "source",
      "rule",
      "output",
      "evidence",
      "selection",
      "success",
      "running",
      "attention",
      "failure",
      "unavailable",
    ];
    return roles.map((role) => ({
      role,
      text: style.getPropertyValue(`--wb-${role}-text`).trim(),
      tint: style.getPropertyValue(`--wb-${role}-tint`).trim(),
    }));
  });
  const luminance = (hex: string) => {
    const linear = [1, 3, 5]
      .map((offset) => parseInt(hex.slice(offset, offset + 2), 16) / 255)
      .map((v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
    return linear[0]! * 0.2126 + linear[1]! * 0.7152 + linear[2]! * 0.0722;
  };
  for (const token of tokens) {
    const a = luminance(token.text),
      b = luminance(token.tint);
    expect(
      (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05),
      token.role,
    ).toBeGreaterThanOrEqual(4.5);
  }
  await page.locator("[data-row-key]").first().click();
  const cell = page.locator("tr.wb-selected td").first();
  const background = await cell.evaluate(
    (element) => getComputedStyle(element).backgroundColor,
  );
  await cell.hover();
  expect(
    await cell.evaluate((element) => getComputedStyle(element).backgroundColor),
  ).toBe(background);
  await page.evaluate(() => (document.documentElement.style.fontSize = "200%"));
  await expect(
    page.getByRole("tab", { name: "Data mapping", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});

test("rendered workbench text keeps readable contrast including neutral metadata", async ({
  page,
}) => {
  await workbench(page);
  await page.locator("[data-row-key]").first().click();
  const audit = () =>
    page.locator(".wb-frame").evaluate((frame) => {
      const rgba = (value: string): number[] => {
        const numbers = value.match(/[\d.]+/g)?.map(Number) ?? [];
        return [
          numbers[0] ?? 0,
          numbers[1] ?? 0,
          numbers[2] ?? 0,
          numbers[3] ?? 1,
        ];
      };
      const over = (front: number[], back: number[]) =>
        front
          .slice(0, 3)
          .map((v, i) => v * front[3]! + back[i]! * (1 - front[3]!));
      const light = (color: number[]) =>
        color
          .map((v) => v / 255)
          .map((v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4))
          .reduce((n, v, i) => n + v * [0.2126, 0.7152, 0.0722][i]!, 0);
      const bad: {
        text: string;
        element: string;
        ratio: number;
        foreground: string;
      }[] = [];
      for (const element of frame.querySelectorAll<HTMLElement>("*")) {
        if (
          !element.getClientRects().length ||
          element.closest("[hidden], [disabled], [aria-disabled='true']")
        )
          continue;
        const text = Array.from(element.childNodes)
          .filter((n) => n.nodeType === Node.TEXT_NODE)
          .map((n) => n.textContent)
          .join("")
          .trim();
        if (!text) continue;
        const style = getComputedStyle(element);
        const backgrounds: number[][] = [];
        let opacity = 1;
        for (
          let parent: HTMLElement | null = element;
          parent;
          parent = parent.parentElement
        ) {
          const parentStyle = getComputedStyle(parent);
          backgrounds.push(rgba(parentStyle.backgroundColor));
          opacity *= Number(parentStyle.opacity);
        }
        let background = [255, 255, 255];
        for (const layer of backgrounds.reverse())
          background = over(layer, background);
        const foreground = rgba(style.color);
        foreground[3] = foreground[3]! * opacity;
        const a = light(over(foreground, background)),
          b = light(background);
        const ratio = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
        const large =
          parseFloat(style.fontSize) >= 24 ||
          (parseFloat(style.fontSize) >= 18.66 &&
            parseInt(style.fontWeight, 10) >= 700);
        if (ratio < (large ? 3 : 4.5))
          bad.push({
            text: text.slice(0, 50),
            element: element.className,
            ratio,
            foreground: style.color,
          });
      }
      return bad;
    });
  expect(await audit(), "Data mapping").toEqual([]);
  for (const name of [
    "Try sample",
    "Versions",
    "Compare",
    "Tests",
    "Prepare",
    "In use",
    "Change history",
    "Data mapping",
  ]) {
    await page
      .getByRole("tablist", { name: "Version workflows", exact: true })
      .getByRole("tab", { name, exact: true })
      .click();
    expect(await audit(), name).toEqual([]);
  }
  const control = page.getByLabel("Filter mapping fields");
  await control.focus();
  const ratios = await control.evaluate((element) => {
    const light = (color: string) => {
      const values = color
        .match(/[\d.]+/g)!
        .slice(0, 3)
        .map(Number)
        .map((v) => v / 255)
        .map((v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
      return values.reduce(
        (sum, value, i) => sum + value * [0.2126, 0.7152, 0.0722][i]!,
        0,
      );
    };
    const style = getComputedStyle(element),
      background = light(style.backgroundColor);
    return [style.borderTopColor, style.outlineColor].map(
      (color) =>
        (Math.max(light(color), background) + 0.05) /
        (Math.min(light(color), background) + 0.05),
    );
  });
  expect(ratios.every((ratio) => ratio >= 3)).toBe(true);
});

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
  await page.getByRole("button", { name: "Data flow", exact: true }).click();
  await expect(page.locator(".wb-inline-lineage")).toContainText(
    "Controlled saved explanation",
  );
  await page.getByLabel("Filter mapping fields").fill("no such field");
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toContainText("No fields match");
  await page.getByRole("button", { name: "Clear filters" }).click();
  const resize = page.getByRole("separator", {
    name: "Resize Incoming field column",
    exact: true,
  });
  const old = Number(await resize.getAttribute("aria-valuenow"));
  await resize.focus();
  await page.keyboard.press("ArrowRight");
  await expect(resize).toHaveAttribute("aria-valuenow", String(old + 10));
  await page
    .getByRole("tab", { name: workbenchLabels.mapping, exact: true })
    .focus();
  await page.keyboard.press("ArrowRight");
  await expect(
    page.getByRole("tab", { name: workbenchLabels.preview, exact: true }),
  ).toBeFocused();
  await page.keyboard.press("Home");
  await expect(
    page.getByRole("tab", { name: workbenchLabels.mapping, exact: true }),
  ).toBeFocused();
  await expect(page.locator(".app-shell")).toHaveClass(/app-workbench/);
});

test("selected saved case and run survive workflow changes without writes or numeric rounding", async ({
  page,
}) => {
  const fixture = await workbench(page);
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  await page
    .getByRole("button", { name: "Open Exact number sample", exact: true })
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
    page.getByRole("tab", { name: workbenchLabels.mapping, exact: true }),
  ).toHaveAttribute("aria-selected", "true");
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toContainText("Controlled saved explanation");
  await expect(
    page.getByRole("navigation", { name: "Investigation trail" }),
  ).toContainText("Exact number sample");
  await page
    .getByRole("button", { name: "Return to Exact number sample", exact: true })
    .click();
  await expect(page.locator("#regression-result-title")).toBeFocused();
  await expect(
    page.getByRole("tab", { name: "Tests", exact: true }),
  ).toHaveAttribute("aria-selected", "true");
  await page
    .getByRole("tab", { name: workbenchLabels.mapping, exact: true })
    .click();
  await page
    .getByRole("button", { name: "Sample values", exact: true })
    .click();
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toContainText("9007199254740994");
  await expect(page.locator(".wb-sample-label")).toContainText(
    "Exact number sample",
  );
  await page
    .getByRole("tab", { name: workbenchLabels.builds, exact: true })
    .click();
  await expect(
    page.getByRole("tab", { name: "Readiness", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("tab", { name: workbenchLabels.mapping, exact: true })
    .click();
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
    .getByRole("button", { name: "Open Exact number sample", exact: true })
    .click();
  await page.getByRole("tab", { name: dockLabels.input, exact: true }).click();
  await expect(page.locator(".wb-dock-body")).toContainText("privateSample");
  fixture.revoke();
  await page
    .getByRole("button", { name: "Refresh test access", exact: true })
    .click();
  await expect(page.locator(".wb-dock-body")).not.toContainText(
    "privateSample",
  );
  await page
    .getByRole("tab", { name: workbenchLabels.mapping, exact: true })
    .click();
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
    page.getByRole("dialog", { name: "Search / Go to", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("searchbox", { name: "Search catalog" })
    .fill(workbenchLabels.releases);
  await page
    .getByRole("dialog")
    .getByRole("button", { name: /^In use/ })
    .click();
  await expect(
    page
      .getByRole("list", { name: "Release status", exact: true })
      .filter({ hasText: "Version currently in use" }),
  ).toContainText("This workspace can't show which version is in use.");
  expect(fixture.writes).toEqual([]);
  await page.setViewportSize({ width: 390, height: 844 });
  await page
    .getByRole("tab", { name: workbenchLabels.mapping, exact: true })
    .click();
  await page
    .getByRole("button", { name: "Show inspector", exact: true })
    .click();
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
  await page.getByRole("checkbox", { name: "Rule", exact: true }).uncheck();
  await expect(table.getByRole("columnheader")).toHaveCount(3);
  await page.getByRole("checkbox", { name: "Rule", exact: true }).check();
  await expect(table.getByRole("columnheader")).toHaveCount(4);
  await page.keyboard.press("Escape");
  await expect(page.locator(".wb-columns")).not.toHaveAttribute("open", "");
  await page.locator(".wb-columns > summary").click();
  await expect(page.locator(".wb-columns")).toHaveAttribute("open", "");
  await page.locator("#main-content").dispatchEvent("pointerdown");
  await expect(page.locator(".wb-columns")).not.toHaveAttribute("open", "");
  await page
    .getByRole("button", { name: "Show inspector", exact: true })
    .click();
  for (const [name, key] of [
    ["Resize Incoming field column", "ArrowRight"],
    ["Resize investigation dock", "ArrowUp"],
  ]) {
    const separator = page.getByRole("separator", { name: name!, exact: true });
    await separator.focus();
    await page.keyboard.press("Home");
    const minimum = Number(await separator.getAttribute("aria-valuemin"));
    const maximum = Number(await separator.getAttribute("aria-valuemax"));
    expect(maximum).toBeGreaterThan(minimum);
    await expect(separator).toHaveAttribute("aria-valuenow", String(minimum));
    await page.keyboard.press(key!);
    await expect(separator).toHaveAttribute(
      "aria-valuenow",
      String(Math.min(minimum + 10, maximum)),
    );
    await page.keyboard.press("End");
    await expect(separator).toHaveAttribute("aria-valuenow", String(maximum));
    await page.keyboard.press(key === "ArrowRight" ? "ArrowLeft" : "ArrowDown");
    await expect(separator).toHaveAttribute(
      "aria-valuenow",
      String(Math.max(minimum, maximum - 10)),
    );
  }
  await page
    .getByRole("button", { name: "comfortable · 32px rows", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "dense · 24px rows", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "dense · 24px rows", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "compact · 28px rows", exact: true }),
  ).toBeVisible();
  await table.locator("[data-row-key]").first().click();
  await page.getByRole("button", { name: "Data flow", exact: true }).click();
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
  const catalog = await page.locator(".app-header").boundingBox();
  const context = await page.locator(".wb-context-path").boundingBox();
  expect(catalog).not.toBeNull();
  expect(context).not.toBeNull();
  expect(context!.y).toBeGreaterThanOrEqual(catalog!.y + catalog!.height);
  await expect(page.locator(".wb-workspace-role")).toBeVisible();
  await page
    .getByRole("button", { name: "Show inspector", exact: true })
    .click();
  const inspector = page.getByRole("complementary", {
    name: "Context inspector",
  });
  await expect(inspector).toBeVisible();
  await inspector.getByRole("tab", { name: "Rule", exact: true }).focus();
  await page.keyboard.press("Escape");
  await expect(
    page.getByRole("button", { name: "Show inspector", exact: true }),
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

test("narrow enlarged text leaves a usable table viewport and keyboard-reachable rows", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await workbench(page, 60);
  await page.addStyleTag({ content: ":root {font-size:24px !important}" });
  expect(
    await page
      .locator(".wb-table-scroll")
      .evaluate((element) => element.clientHeight),
  ).toBeGreaterThanOrEqual(160);
  const rows = page.locator("[data-row-key]");
  await rows.first().focus();
  await expect(rows.first()).toBeInViewport();
  await page.keyboard.press("ArrowDown");
  await expect(rows.nth(1)).toBeFocused();
  await expect(rows.nth(1)).toBeInViewport();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toContainText("value1");
});

test.describe("native scrollbar viewport", () => {
  test("narrow enlarged text reserves usable height with non-overlay scrollbars", async ({
    page,
  }, testInfo) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.setViewportSize({ width: 390, height: 844 });
    await workbench(page, 60);
    // Explicit native scrollbar geometry reproduces the headed-browser gutter
    // even when a headless browser would normally use overlay scrollbars.
    await page.addStyleTag({
      content:
        ":root {font-size:24px !important} .wb-table-scroll::-webkit-scrollbar {width:32px;height:32px}",
    });
    const viewport = page.locator(".wb-table-scroll");
    const geometry = () =>
      viewport.evaluate((element) => ({
        outerHeight: (element as HTMLElement).offsetHeight,
        clientHeight: element.clientHeight,
        horizontalGutter:
          (element as HTMLElement).offsetHeight - element.clientHeight,
        clientWidth: element.clientWidth,
        scrollWidth: element.scrollWidth,
        minimumHeight: getComputedStyle(element).minHeight,
      }));
    await expect.poll(async () => (await geometry()).horizontalGutter).toBe(32);
    await testInfo.attach("scrollbar-geometry", {
      body: JSON.stringify(await geometry()),
      contentType: "application/json",
    });
    await expect
      .poll(async () => (await geometry()).clientHeight)
      .toBeGreaterThanOrEqual(160);
    await writeFile(
      testInfo.outputPath("scrollbar-geometry.json"),
      JSON.stringify(await geometry(), null, 2),
    );
    await testInfo.attach("settled-scrollbar-geometry", {
      body: JSON.stringify(await geometry()),
      contentType: "application/json",
    });
    await page.screenshot({
      path: testInfo.outputPath("usable-table-viewport.png"),
    });
    const rows = page.locator("[data-row-key]");
    await rows.first().focus();
    await expect(rows.first()).toBeInViewport();
    await page.keyboard.press("ArrowDown");
    await expect(rows.nth(1)).toBeFocused();
    await expect(rows.nth(1)).toBeInViewport();
    await page.keyboard.press("Enter");
    await expect(
      page.getByRole("complementary", { name: "Context inspector" }),
    ).toContainText("value1");
    expect(errors).toEqual([]);
  });
});
