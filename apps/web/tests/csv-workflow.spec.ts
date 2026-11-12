import "@angular/compiler";
import { expect, test } from "@playwright/test";
import { of } from "rxjs";
import {
  csvMetadata,
  csvPreviewProfile,
} from "../src/catalog/previews/csv-contract";
import { previewCaseDefinition } from "../src/catalog/previews/preview-case";
import { canonicalDefinition } from "../src/catalog/regression/regression-definition";
import { PreviewState } from "../src/catalog/previews/preview-state";
import type { PreviewApi } from "../src/catalog/previews/preview-api";
import {
  csvBytes,
  csvCapabilities,
  csvDialect,
  csvRun,
  csvScope,
  CsvWorkflowFixture,
} from "./csv-workflow.fixture";

test("CSV metadata requires an exact versioned dialect consistent with media", () => {
  expect(csvMetadata("text/tab-separated-values", csvDialect)).toBe(true);
  expect(csvMetadata("text/csv", csvDialect)).toBe(false);
  expect(
    csvMetadata("text/tab-separated-values", {
      ...csvDialect,
      schema: "unknown",
    }),
  ).toBe(false);
  expect(
    csvMetadata("text/tab-separated-values", { ...csvDialect, guessed: true }),
  ).toBe(false);
  expect(csvMetadata("application/json", csvDialect)).toBe(false);
  expect(csvMetadata("text/csv", undefined)).toBe(false);
});

test("only explicitly selected and advertised CSV admission permits an empty input", () => {
  const sent: unknown[] = [];
  const state = new PreviewState(
    {
      submit: (
        _scope: unknown,
        _token: unknown,
        _key: unknown,
        body: unknown,
      ) => {
        sent.push(body);
        return of({ outcome: "created", run: csvRun() });
      },
    } as unknown as PreviewApi,
    null,
  );
  state.bind({ scope: csvScope, admit: () => of("token"), ready: () => true });
  state.submit();
  expect(sent).toHaveLength(0);
  state.csvProfile.set(true);
  state.submit();
  expect(sent).toHaveLength(0);
  state.bind({
    scope: csvScope,
    admit: () => of("token"),
    ready: () => true,
    csvAvailable: () => true,
  });
  state.submit();
  expect(sent).toEqual([
    {
      target: { kind: "all", name: null },
      parameters: [],
      inputs: [{ name: null, content: "" }],
      profile: csvPreviewProfile,
    },
  ]);
  state.destroy();
});

test("saved preview baseline uses verified output metadata and exact bytes", () => {
  const submission = {
    profile: csvPreviewProfile,
    target: { kind: "all", name: null },
    parameters: [],
    inputs: [{ name: null, content: "" }],
  } as const;
  const body = previewCaseDefinition(
    csvRun(),
    { ...submission, inputs: [...submission.inputs], parameters: [] },
    csvCapabilities,
    Buffer.from(csvBytes),
    "CSV baseline",
  );
  expect(body.runtime.profile).toBe(csvPreviewProfile);
  expect(body.primaryBase64Url).toBe("");
  expect(body.comparisonPolicy).toBe("weft.regression.exact-bytes.v1");
  expect(body.expected[0]).toEqual({
    name: null,
    bytesBase64Url: Buffer.from(csvBytes).toString("base64url"),
    mediaType: "text/tab-separated-values",
    csvDialect,
  });
  const unconfirmed = csvRun();
  unconfirmed.provenance!.confirmed = false;
  expect(() =>
    previewCaseDefinition(
      unconfirmed,
      { ...submission, inputs: [...submission.inputs], parameters: [] },
      csvCapabilities,
      Buffer.from(csvBytes),
      "CSV baseline",
    ),
  ).toThrow();
});

test("saved CSV parameters use server ordering without changing draft values or order", () => {
  const parameters = [
    { name: "z", kind: "string" as const, value: "last" },
    { name: "a", kind: "integer" as const, value: "0007" },
    { name: "A", kind: "float" as const, value: "1.00" },
  ];
  const original = structuredClone(parameters);
  const body = previewCaseDefinition(
    csvRun(),
    {
      profile: csvPreviewProfile,
      target: { kind: "all", name: null },
      parameters,
      inputs: [{ name: null, content: "" }],
    },
    csvCapabilities,
    Buffer.from(csvBytes),
    "Ordered baseline",
  );
  expect(body.parameters.map((parameter) => parameter.name)).toEqual([
    "A",
    "a",
    "z",
  ]);
  expect(body.parameters.map((parameter) => parameter.value)).toEqual([
    "1.00",
    "0007",
    "last",
  ]);
  expect(parameters).toEqual(original);
  expect(body.parameters[2]).not.toBe(parameters[0]);
  const reordered = { ...body, parameters: [...body.parameters].reverse() };
  expect(canonicalDefinition(reordered)).toBe(canonicalDefinition(body));
  expect(
    canonicalDefinition({
      ...body,
      parameters: [
        { ...body.parameters[0]!, value: "different" },
        ...body.parameters.slice(1),
      ],
    }),
  ).not.toBe(canonicalDefinition(body));
});

test("CSV preview keeps empty file bytes, renders native TSV, and saves only after baseline approval", async ({
  page,
}) => {
  const fixture = new CsvWorkflowFixture();
  await fixture.install(page);
  await page.locator("#preview-admission").selectOption("csv");
  await page
    .locator('weft-revision-preview input[type="file"]')
    .first()
    .setInputFiles({
      name: "deliberately-wrong.json",
      mimeType: "application/json",
      buffer: Buffer.alloc(0),
    });
  await page.getByRole("button", { name: "Run preview", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Show Primary output", exact: true }),
  ).toBeVisible();
  expect(fixture.submissions[0]!.body.profile).toBe(csvPreviewProfile);
  expect(fixture.submissions[0]!.body.inputs[0]!.content).toBe("");
  await page
    .getByRole("button", { name: "Show Primary output", exact: true })
    .click();
  const table = page.locator("weft-document-view table");
  await expect(table.getByRole("columnheader")).toHaveText([
    "code",
    "description",
  ]);
  await expect(table.getByRole("cell").nth(1)).toHaveText(
    'comma, quote "kept"\r\nsecond line',
  );
  await page.getByLabel("Test name", { exact: true }).fill("CSV baseline");
  const save = page.getByRole("button", {
    name: "Save preview as test case",
    exact: true,
  });
  await expect(save).toBeDisabled();
  await page
    .getByRole("checkbox", { name: "I reviewed this output", exact: false })
    .check();
  await save.click();
  await expect(
    page.getByText("The exact CSV baseline is saved as a test case.", {
      exact: false,
    }),
  ).toBeVisible();
  expect(fixture.publications).toHaveLength(1);
  expect(fixture.publications[0]!.body.expected[0]!.csvDialect).toEqual(
    csvDialect,
  );
  const downloadPromise = page.waitForEvent("download");
  await page
    .getByRole("button", { name: "Download Primary output", exact: true })
    .click();
  expect((await downloadPromise).suggestedFilename()).toMatch(/\.tsv$/);
});

test("CSV preview publication recovery keeps the original identity and never retries automatically", async ({
  page,
}) => {
  const fixture = new CsvWorkflowFixture();
  fixture.failPublication = true;
  await fixture.install(page);
  await page.locator("#preview-admission").selectOption("csv");
  const parameters = [
    ["z", "last"],
    ["a", "first"],
  ] as const;
  for (const [index, [name, value]] of parameters.entries()) {
    await page
      .getByRole("button", { name: "Add parameter", exact: true })
      .click();
    await expect(page.locator(".preview-parameter")).toHaveCount(index + 1);
    const row = page.locator(".preview-parameter").nth(index);
    await row.getByRole("textbox", { name: "Name", exact: true }).fill(name);
    await row.getByRole("textbox", { name: "Value", exact: true }).fill(value);
  }
  await page.getByRole("button", { name: "Run preview", exact: true }).click();
  await page.getByLabel("Test name", { exact: true }).fill("Kept CSV baseline");
  await page
    .getByRole("checkbox", { name: "I reviewed this output", exact: false })
    .check();
  await page
    .getByRole("button", { name: "Save preview as test case", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Check saved preview case" }),
  ).toBeVisible();
  expect(fixture.publications).toHaveLength(1);
  expect(
    fixture.submissions[0]!.body.parameters.map((parameter) => parameter.name),
  ).toEqual(["z", "a"]);
  expect(
    fixture.publications[0]!.body.parameters.map((parameter) => parameter.name),
  ).toEqual(["a", "z"]);
  await page.getByRole("button", { name: "Check saved preview case" }).click();
  await expect(
    page.getByText("The exact CSV baseline is saved as a test case.", {
      exact: false,
    }),
  ).toBeVisible();
  expect(fixture.publications).toHaveLength(1);
  const parameterRows = page.locator(".preview-parameter");
  await expect(parameterRows).toHaveCount(2);
  await expect(
    parameterRows.nth(0).getByRole("textbox", { name: "Name", exact: true }),
  ).toHaveValue("z");
  await expect(
    parameterRows.nth(1).getByRole("textbox", { name: "Name", exact: true }),
  ).toHaveValue("a");
});

test("CSV preview is absent without its own advertisement even when CSV regressions are available", async ({
  page,
}) => {
  const fixture = new CsvWorkflowFixture();
  fixture.csvPreviewEnabled = false;
  await fixture.install(page);
  await expect(
    page.locator('#preview-admission option[value="csv"]'),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "Run preview", exact: true }).click();
  expect(fixture.submissions).toHaveLength(0);
});

test("CSV preview advertisement is independent of CSV regression availability", async ({
  page,
}) => {
  const fixture = new CsvWorkflowFixture();
  delete fixture.capabilities.csvRuntime;
  delete fixture.capabilities.comparisonPolicies;
  await fixture.install(page);
  await expect(
    page.locator('#preview-admission option[value="csv"]'),
  ).toHaveCount(1);
  await page.locator("#preview-admission").selectOption("csv");
  await page.getByRole("button", { name: "Run preview", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Show Primary output", exact: true }),
  ).toBeVisible();
  expect(fixture.submissions).toHaveLength(1);
  expect(fixture.submissions[0]!.body.profile).toBe(csvPreviewProfile);
  await expect(
    page.getByRole("button", {
      name: "Save preview as test case",
      exact: true,
    }),
  ).toHaveCount(0);
});

test("a CSV response with malformed metadata or an oversized row count never invents a table", async ({
  page,
}) => {
  const fixture = new CsvWorkflowFixture();
  fixture.result.outputs[0]!.recordsWritten = "100001";
  await fixture.install(page);
  await page.locator("#preview-admission").selectOption("csv");
  await page.getByRole("button", { name: "Run preview", exact: true }).click();
  await expect(
    page.getByText("The server response could not be verified.", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(page.locator("weft-document-view table")).toHaveCount(0);
  expect(fixture.publications).toHaveLength(0);
});
