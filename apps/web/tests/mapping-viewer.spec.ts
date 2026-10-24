import {
  expect,
  test,
  type APIRequestContext,
  type Locator,
  type Page,
} from "@playwright/test";
import type { components } from "../src/api/generated";

type ExampleResult = components["schemas"]["NativeExampleResultV1"];
type Descriptor = components["schemas"]["NativeExampleDescriptorV1"];
type Projection = components["schemas"]["ProjectionDocumentV1"];
const pageErrors = new WeakMap<Page, string[]>();
test.setTimeout(90000);
test.beforeEach(({ page }) => {
  const errors: string[] = [];
  pageErrors.set(page, errors);
  page.on("pageerror", (error) => errors.push(error.message));
});
test.afterEach(({ page }) => expect(pageErrors.get(page)).toEqual([]));

async function tabTo(
  page: Page,
  target: Locator,
  maximumStops = 40,
): Promise<void> {
  for (let stop = 0; stop < maximumStops; stop++) {
    if (await target.evaluate((element) => element === document.activeElement))
      return;
    await page.keyboard.press("Tab");
  }
  await expect(target).toBeFocused();
}

test("large display fixtures page all rows without hiding coverage and instantiate details on demand", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request, "partial");
  const document = report.projection.document!;
  const templateRule = document.rules[0];
  const templateField = document.fields.find(
    (field) => field.id === templateRule.outputFieldId,
  )!;
  for (let index = 0; index < 220; index++) {
    const name = `paged${String(index).padStart(3, "0")}`;
    const field = {
      ...templateField,
      id: `display-field-${index}`,
      name,
      path: [{ kind: "field" as const, name }],
    };
    if (index === 150)
      field.dataType = { kind: "array", scalarTypes: [], nativeName: null };
    document.fields.push(field);
    document.rules.push({
      ...templateRule,
      id: `display-rule-${index}`,
      outputFieldId: field.id,
      summary: `Populate ${name}`,
      details: [
        {
          kind: "group",
          label: "Paged rule explanation",
          children: [
            {
              kind: "literal",
              value: { kind: "string", value: `value${index}` },
            },
          ],
        },
      ],
    });
  }
  const alternateBoundary = {
    ...document.boundaries.find(
      (boundary) => boundary.role === "primaryOutput",
    )!,
    id: "display-alternate-output",
    name: "Alternate output",
    role: "namedOutput" as const,
    rootFieldId: null,
  };
  document.boundaries.push(alternateBoundary);
  const alternateField = {
    ...templateField,
    id: "display-alternate-field",
    name: "alternate",
    boundaryId: alternateBoundary.id,
    path: [{ kind: "field" as const, name: "alternate" }],
    dataType: {
      kind: "array" as const,
      scalarTypes: ["string" as const],
      nativeName: null,
    },
  };
  document.fields.push(alternateField);
  document.rules.push({
    ...templateRule,
    id: "display-alternate-rule",
    outputFieldId: alternateField.id,
  });
  await routeFixture(page, report);
  await page.goto("/");
  const mapping = page.getByRole("region", {
    name: "Source → rule → target",
    exact: true,
  });
  const coverage = page.getByRole("region", {
    name: "Explanation incomplete",
    exact: true,
  });
  await expect(mapping.getByRole("article")).toHaveCount(100);
  await expect(page.locator(".mapping-rows weft-mapping-details")).toHaveCount(
    0,
  );
  const first = mapping.getByRole("article").first();
  await first.getByText("Expand rule explanation", { exact: true }).click();
  await expect(first.locator("weft-mapping-details").first()).toBeVisible();
  await first.getByText("Expand rule explanation", { exact: true }).click();
  await expect(page.locator(".mapping-rows weft-mapping-details")).toHaveCount(
    0,
  );
  const pages = page.getByRole("navigation", {
    name: "Mapping row pages",
    exact: true,
  });
  await pages.getByRole("button", { name: "Next rows", exact: true }).click();
  await expect(pages).toContainText("Rows 101–200 of 222");
  await expect(mapping.getByRole("article")).toHaveCount(100);
  await expect(coverage).toBeVisible();
  await page.getByLabel("Find a field or rule").fill("paged150");
  await expect(mapping.getByRole("article")).toHaveCount(1);
  await mapping
    .getByRole("button", { name: '["paged150"]', exact: true })
    .click();
  const details = page.getByRole("region", {
    name: "Mapping details",
    exact: true,
  });
  await expect(details).toBeFocused();
  await expect(details).toContainText("Repeated values");
  await expect(details).not.toContainText("Repeated object");
  await expect(pages).toContainText("Rows 101–200 of 222");
  await page
    .getByLabel("Output", { exact: true })
    .selectOption(alternateBoundary.id);
  await expect(mapping.getByRole("article")).toHaveCount(1);
  await expect(details).toHaveCount(0);
  await mapping
    .getByRole("button", { name: '["alternate"]', exact: true })
    .click();
  await expect(details).toContainText("Repeated text");
  await page
    .getByLabel("Output", { exact: true })
    .selectOption(templateField.boundaryId);
  await expect(pages).toContainText("Rows 1–100 of 222");
  await expect(coverage).toBeVisible();
});

async function selectNative(page: Page, id: string): Promise<ExampleResult> {
  const pending = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/v1/examples/mappings/${id}`) &&
      response.request().method() === "GET",
  );
  await page.getByLabel("Example mapping", { exact: true }).selectOption(id);
  const response = await pending;
  expect(response.status()).toBe(200);
  return (await response.json()) as ExampleResult;
}

async function openNative(page: Page, id = "basic"): Promise<ExampleResult> {
  const pending = page.waitForResponse((response) =>
    response.url().endsWith("/api/v1/examples/mappings/basic"),
  );
  await page.goto("/");
  const response = await pending;
  expect(response.status()).toBe(200);
  const basic = (await response.json()) as ExampleResult;
  expect(basic.projection.status).toBe("succeeded");
  expect(basic.projection.provenance?.backend.id).toBe("ferrule");
  expect(basic.projection.provenance?.confirmed).toBe(true);
  await expect(
    page.getByRole("heading", { name: basic.example.title, exact: true }),
  ).toBeVisible();
  return id === "basic" ? basic : selectNative(page, id);
}

function documentOf(result: ExampleResult): Projection {
  expect(result.projection.status).toBe("succeeded");
  if (!result.projection.document)
    throw new Error("The actual worker did not return a projection.");
  return result.projection.document;
}

async function displayFixture(
  request: APIRequestContext,
  id: "direct" | "rich" | "partial" | "unavailable" = "direct",
): Promise<ExampleResult> {
  const response = await request.get(`/api/v1/fixtures/projections/${id}`);
  expect(response.status()).toBe(200);
  const document = (await response.json()) as Projection;
  expect(document.provenance.backend.id).toBe("weft.synthetic");
  return {
    example: {
      id: "display",
      title: "Typed display fixture",
      source: "Browser display fixture (weft.synthetic)",
      revision: document.provenance.revision,
      digest: document.provenance.nativeArtifactDigest,
      byteCount: 1,
    },
    projection: {
      status: "succeeded",
      provenance: null,
      document,
      diagnostics: [],
    },
    validation: {
      status: "succeeded",
      provenance: null,
      isValid: true,
      totalIssueCount: 0,
      emittedIssueCount: 0,
      truncated: false,
      diagnostics: [],
    },
  };
}

async function routeFixture(
  page: Page,
  report: ExampleResult,
  descriptors: Descriptor[] = [report.example],
): Promise<void> {
  await page.route("**/api/v1/examples/mappings", (route) =>
    route.fulfill({ json: descriptors }),
  );
  await page.route(
    `**/api/v1/examples/mappings/${report.example.id}`,
    (route) => route.fulfill({ json: report }),
  );
}

test("the browser follows actual Ferrule fields with exact revision evidence and lineage", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const result = await openNative(page);
  const document = documentOf(result);
  expect(document.provenance.backend.revision).toBe(
    "0169809123b8caebd141b26a625be4c07e750536",
  );
  const mapping = page.getByRole("region", {
    name: "Source → rule → target",
    exact: true,
  });
  await expect(
    mapping.getByRole("article", {
      name: 'Mapping to ["dotted"]',
      exact: true,
    }),
  ).toContainText('Source ["a.b"]');
  await expect(
    mapping.getByRole("article", {
      name: 'Mapping to ["nestedOutput"]["b"]',
      exact: true,
    }),
  ).toContainText('Source ["nested"]["b"]');
  await page.getByLabel("Find a field or rule").fill("a.b");
  await expect(mapping.getByRole("article")).toHaveCount(1);
  await mapping
    .getByRole("button", { name: 'Source ["a.b"]', exact: true })
    .click();
  const details = page.getByRole("region", {
    name: "Mapping details",
    exact: true,
  });
  await expect(details).toBeFocused();
  await expect(
    details.getByRole("heading", { name: 'Source ["a.b"]', exact: true }),
  ).toBeVisible();
  await expect(
    details.getByRole("heading", { name: "Used by", exact: true }),
  ).toBeVisible();
  await details
    .getByRole("button", { name: "Close details", exact: true })
    .click();
  await expect(
    mapping.getByRole("button", { name: 'Source ["a.b"]', exact: true }),
  ).toBeFocused();
  await page
    .getByText("Revision and backend evidence", { exact: true })
    .click();
  const evidence = page
    .locator(".provenance-card")
    .filter({ hasText: "Revision and backend evidence" });
  await expect(evidence).toContainText(document.provenance.revision.revisionId);
  await expect(evidence).toContainText(
    document.provenance.nativeArtifactDigest.value,
  );
  await expect(evidence).toContainText(
    result.projection.provenance!.backend.buildId,
  );
  await evidence
    .getByText("Revision and backend evidence", { exact: true })
    .click();
  await expect(
    page.getByRole("region", { name: "Inputs and outputs", exact: true }),
  ).toContainText("Format unknown");
  await page.evaluate(() => {
    if (globalThis.document.activeElement instanceof HTMLElement)
      globalThis.document.activeElement.blur();
    window.scrollTo(0, 0);
  });
  const firstRow = await mapping.getByRole("article").first().boundingBox();
  expect(firstRow).not.toBeNull();
  expect(firstRow!.y + firstRow!.height).toBeLessThanOrEqual(900);
  await page.screenshot({
    path: testInfo.outputPath("native-mapping.png"),
    fullPage: true,
  });
  await page.screenshot({
    path: testInfo.outputPath("native-mapping-first-screen.png"),
    fullPage: false,
  });
});

test("actual integer and IEEE-754 values preserve large integers and negative zero", async ({
  page,
}) => {
  const result = await openNative(page, "exact");
  documentOf(result);
  const mapping = page.getByRole("region", {
    name: "Source → rule → target",
    exact: true,
  });
  const integer = mapping.getByRole("article", {
    name: 'Mapping to ["exactInteger"]',
    exact: true,
  });
  await expect(integer).toContainText("9007199254740993");
  const zero = mapping.getByRole("article", {
    name: 'Mapping to ["negativeZero"]',
    exact: true,
  });
  await zero.getByText("Expand rule explanation", { exact: true }).click();
  await expect(zero.locator(".literal-value")).toHaveText("-0");
  await expect(zero.locator(".literal-bits")).toContainText("8000000000000000");
  const integralFloat = mapping.getByRole("article", {
    name: 'Mapping to ["integralFloat"]',
    exact: true,
  });
  await integralFloat
    .getByText("Expand rule explanation", { exact: true })
    .click();
  await expect(integralFloat.locator(".literal-kind")).toContainText(
    "Binary floating-point value",
  );
  await expect(integralFloat.locator(".literal-bits")).toContainText(
    "3ff0000000000000",
  );
});

test("actual conditions defaults parameters and null forms remain distinct", async ({
  page,
}) => {
  const result = await openNative(page, "conditions");
  documentOf(result);
  await expect(
    page
      .locator(".mapping-rows .dependency-role")
      .filter({ hasText: /^condition$/ })
      .first(),
  ).toBeVisible();
  await expect(
    page
      .locator(".mapping-rows .dependency-role")
      .filter({ hasText: /^default$/ })
      .first(),
  ).toBeVisible();
  const controls = page.getByRole("region", {
    name: "Controls and parameters",
    exact: true,
  });
  await controls
    .getByRole("button", { name: "Parameter: fallbackCode", exact: true })
    .click();
  await expect(
    page.getByRole("region", { name: "Mapping details", exact: true }),
  ).toContainText("Default value");
  for (const [field, label] of [
    ["absent", "Absent value"],
    ["explicitNull", "Explicit null"],
    ["xmlNil", "XML nil"],
  ]) {
    const row = page.getByRole("article", {
      name: `Mapping to ["${field}"]`,
      exact: true,
    });
    await row.getByText("Expand rule explanation", { exact: true }).click();
    await expect(row.locator(".literal-value")).toContainText(label);
  }
});

test("actual named outputs preserve order and group controls appear on descendant rows", async ({
  page,
}) => {
  const result = await openNative(page, "controls");
  const document = documentOf(result);
  const outputs = document.boundaries.filter(
    (boundary) =>
      boundary.role === "primaryOutput" || boundary.role === "namedOutput",
  );
  expect(outputs.map((boundary) => boundary.name)).toEqual([
    "Output",
    "audit",
    "summary",
  ]);
  const outputSelector = page.getByLabel("Output", { exact: true });
  await expect(outputSelector.locator("option")).toHaveText([
    "Output",
    "audit (named output)",
    "summary (named output)",
  ]);
  const amount = page.getByRole("article", {
    name: 'Mapping to ["rows"][*]["amount"]',
    exact: true,
  });
  await amount.getByText("Expand rule explanation", { exact: true }).click();
  await expect(
    amount.getByRole("button", { name: /^grouping:/ }).first(),
  ).toBeVisible();
  const globalControls = page.getByRole("region", {
    name: "Controls and parameters",
    exact: true,
  });
  await expect(
    globalControls.getByRole("button", { name: /join/ }).first(),
  ).toBeVisible();
  await outputSelector.selectOption(outputs[1].id);
  await expect(
    page.getByRole("article", { name: 'Mapping to ["rowCount"]', exact: true }),
  ).toBeVisible();
  await outputSelector.selectOption(outputs[2].id);
  await expect(
    page.getByRole("article", { name: 'Mapping to ["status"]', exact: true }),
  ).toBeVisible();
  await page.getByLabel("Find a field or rule").fill("there-is-no-such-field");
  await expect(
    page.getByRole("heading", { name: "No matching rows", exact: true }),
  ).toBeVisible();
  await expect(globalControls).toBeVisible();
});

test("actual reusable function definitions stay inspectable without becoming host parameters", async ({
  page,
}) => {
  const result = await openNative(page, "functions");
  const document = documentOf(result);
  expect(document.parameters).toEqual([]);
  const functionControl = document.controls.find(
    (control) => control.kind === "construction",
  );
  expect(functionControl).toBeDefined();
  const globalControls = page.getByRole("region", {
    name: "Controls and parameters",
    exact: true,
  });
  await globalControls
    .getByRole("button", {
      name: `${functionControl!.kind} ${functionControl!.summary}`,
      exact: true,
    })
    .click();
  const details = page.getByRole("region", {
    name: "Mapping details",
    exact: true,
  });
  await expect(details).toContainText("Private parameters");
  await details.getByText("Native evidence", { exact: true }).last().click();
  await expect(details).toContainText("18446744073709551615");
});

test("actual partial and native-only ambiguous gaps survive empty searches", async ({
  page,
}) => {
  const partial = await openNative(page, "partial");
  expect(documentOf(partial).coverage.status).toBe("partial");
  const coverage = page.getByRole("region", {
    name: "Explanation incomplete",
    exact: true,
  });
  await expect(coverage).toContainText("Explanation gaps");
  await page.getByLabel("Find a field or rule").fill("there-is-no-such-field");
  await expect(
    page.getByRole("heading", { name: "No matching rows", exact: true }),
  ).toBeVisible();
  await expect(coverage).toBeVisible();
  const ambiguous = await selectNative(page, "ambiguous");
  const document = documentOf(ambiguous);
  expect(document.coverage.status).toBe("partial");
  expect(
    document.coverage.gaps.some((gap) => gap.projectionLocator == null),
  ).toBe(true);
  await expect(coverage).toContainText(
    "Location is available in native evidence.",
  );
  await expect(
    page.locator(".mapping-row .incomplete-label").first(),
  ).toContainText("reads may be possible");
  await page.getByLabel("Find a field or rule").fill("there-is-no-such-field");
  await expect(coverage).toBeVisible();
});

test("actual generated source context uses the qualified inner field", async ({
  page,
}) => {
  const result = await openNative(page, "generated");
  const document = documentOf(result);
  const rule = document.rules[0];
  const dependencies = rule.dependencies.filter(
    (dependency) => dependency.kind === "field",
  );
  expect(dependencies).toHaveLength(1);
  const field = document.fields.find(
    (field) => field.id === dependencies[0].fieldId,
  )!;
  expect(field.path).toEqual([
    { kind: "field", name: "numbers", namespaceUri: null },
  ]);
  await expect(page.locator(".mapping-row .source-cell")).toContainText(
    'Source ["numbers"]',
  );
});

test("actual invalid mappings show usable validation findings and navigate unique native evidence", async ({
  page,
}) => {
  const result = await openNative(page, "invalid");
  const document = documentOf(result);
  expect(result.validation.status).toBe("invalidMapping");
  expect(result.validation.isValid).toBe(false);
  await expect(
    page.getByRole("heading", {
      name: "Native mapping has validation issues",
      exact: true,
    }),
  ).toBeVisible();
  const locations = page.getByRole("region", {
    name: "Validation locations",
    exact: true,
  });
  const linked = result.validation.diagnostics.find(
    (diagnostic) => diagnostic.projectionLocator?.kind === "rule",
  );
  expect(linked).toBeDefined();
  expect(linked!.owner).toEqual({
    kind: "graphNode",
    functionId: null,
    nodeId: "1",
  });
  const ruleId =
    linked!.projectionLocator!.kind === "rule"
      ? linked!.projectionLocator!.ruleId
      : "";
  const rule = document.rules.find((rule) => rule.id === ruleId)!;
  expect(rule).toBeDefined();
  await locations
    .getByRole("button", { name: "Show validation rule", exact: true })
    .first()
    .click();
  const details = page.getByRole("region", {
    name: "Mapping details",
    exact: true,
  });
  await expect(details).toBeFocused();
  await expect(details).toContainText(rule.summary);
  await locations.getByText("Native evidence", { exact: true }).first().click();
  await expect(locations).toContainText("Validator owner");
  await expect(locations).toContainText('"nodeId":"1"');
});

test("generic typed rendering preserves decimal namespaces indexes and operation qualification", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request, "rich");
  await routeFixture(page, report);
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "Typed display fixture", exact: true }),
  ).toBeVisible();
  await expect(page.locator(".mapping-rows")).toContainText(
    'customers {"urn:weft:customers"}["customer"]{"urn:weft:customers"}["tier"]',
  );
  const join = report.projection.document!.controls.find(
    (control) => control.kind === "join",
  )!;
  await page
    .getByRole("region", { name: "Controls and parameters", exact: true })
    .getByRole("button", { name: `${join.kind} ${join.summary}`, exact: true })
    .click();
  await expect(
    page.getByRole("region", { name: "Mapping details", exact: true }),
  ).toContainText(
    'customers {"urn:weft:customers"}["customer"]@{"urn:weft:identity"}["id"]',
  );
  await expect(page.locator(".mapping-rows")).toContainText(
    'rates ["rates"][0]["value"]',
  );
  await expect(page.locator(".mapping-rows")).toContainText(
    "0.1000000000000000000000000001",
  );
  await expect(page.locator(".mapping-rows")).toContainText('[*]["total"]');
  await page.getByText("Available mapping operations", { exact: true }).click();
  const capabilities = page.getByRole("region", {
    name: "Available mapping operations",
    exact: true,
  });
  await expect(capabilities).toContainText("preview");
  await expect(capabilities).toContainText("Not yet verified");
  await page
    .getByText("Revision and backend evidence", { exact: true })
    .click();
  await expect(
    page
      .locator(".provenance-card")
      .filter({ hasText: "Revision and backend evidence" }),
  ).toContainText("weft.synthetic");
});

test("unavailable coverage never presents an apparently complete graph", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request, "unavailable");
  await routeFixture(page, report);
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "Explanation unavailable", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("heading", { name: "Native mapping is valid", exact: true }),
  ).toBeVisible();
});

test("literal slashes Unicode and explicit empty namespaces remain distinct from nested fields", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request);
  const document = report.projection.document!;
  const rule = document.rules[0];
  const output = document.fields.find(
    (field) => field.id === rule.outputFieldId,
  )!;
  output.path = [{ kind: "field", name: "a~b/c.𝛼", namespaceUri: "" }];
  const dependency = rule.dependencies.find(
    (dependency) => dependency.kind === "field",
  )!;
  const source = document.fields.find(
    (field) => field.id === dependency.fieldId,
  )!;
  source.path = [{ kind: "field", name: "a~b/c.𝛼", namespaceUri: "" }];
  await routeFixture(page, report);
  await page.goto("/");
  const row = page.getByRole("article", {
    name: 'Mapping to {""}["a~b/c.𝛼"]',
    exact: true,
  });
  await expect(row).toContainText('source {""}["a~b/c.𝛼"]');
  await expect(
    page.getByRole("article", {
      name: 'Mapping to ["display"]["name"]',
      exact: true,
    }),
  ).toBeVisible();
  await row
    .getByRole("button", { name: '{""}["a~b/c.𝛼"]', exact: true })
    .click();
  await expect(
    page.getByRole("region", { name: "Mapping details", exact: true }),
  ).toContainText('{""}["a~b/c.𝛼"]');
});

test("operation failures cancellation and unknown validity stay explicit without stale data", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request);
  await routeFixture(page, report);
  await page.goto("/");
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toBeVisible();
  const failure = structuredClone(report);
  failure.projection = {
    status: "canceled",
    provenance: null,
    document: null,
    diagnostics: [],
  };
  failure.validation = {
    ...failure.validation,
    status: "backendUnavailable",
    isValid: null,
  };
  await page.unroute(`**/api/v1/examples/mappings/${report.example.id}`);
  await page.route(
    `**/api/v1/examples/mappings/${report.example.id}`,
    (route) => route.fulfill({ json: failure }),
  );
  await page
    .getByLabel("Example mapping", { exact: true })
    .selectOption(report.example.id);
  await expect(
    page.getByRole("heading", {
      name: "Mapping explanation unavailable",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByText("The mapping operation was canceled.", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", {
      name: "The mapping engine is unavailable",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toHaveCount(0);
  await page.unroute(`**/api/v1/examples/mappings/${report.example.id}`);
  await page.route(
    `**/api/v1/examples/mappings/${report.example.id}`,
    (route) => route.fulfill({ json: report }),
  );
  await page
    .getByRole("button", { name: "Retry mapping", exact: true })
    .click();
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toBeVisible();
  await page.unroute(`**/api/v1/examples/mappings/${report.example.id}`);
  await page.route(
    `**/api/v1/examples/mappings/${report.example.id}`,
    (route) =>
      route.fulfill({
        json: {
          ...report,
          validation: { ...report.validation, isValid: null },
        },
      }),
  );
  await page
    .getByLabel("Example mapping", { exact: true })
    .selectOption(report.example.id);
  await expect(
    page.getByRole("heading", {
      name: "Validation result is unknown",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Validation did not provide a validity decision for this attempt. Explanation coverage is reported separately.",
      { exact: true },
    ),
  ).toBeVisible();
});

test("unknown examples and transport failures clear the view and can be retried", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request);
  const missing = {
    ...report.example,
    id: "missing",
    title: "Missing example",
  };
  const failed = { ...report.example, id: "failed", title: "Failed example" };
  await routeFixture(page, report, [report.example, missing, failed]);
  await page.route("**/api/v1/examples/mappings/missing", (route) =>
    route.fulfill({ status: 404 }),
  );
  await page.route("**/api/v1/examples/mappings/failed", (route) =>
    route.fulfill({ status: 503 }),
  );
  await page.goto("/");
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Example mapping", { exact: true })
    .selectOption("missing");
  await expect(
    page.getByRole("heading", { name: "Example not found", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toHaveCount(0);
  await page
    .getByLabel("Example mapping", { exact: true })
    .selectOption("failed");
  await expect(
    page.getByRole("heading", {
      name: "Mapping could not be loaded",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Example not found", exact: true }),
  ).toHaveCount(0);
  await page.unroute("**/api/v1/examples/mappings/failed");
  await page.route("**/api/v1/examples/mappings/failed", (route) =>
    route.fulfill({ json: { ...report, example: failed } }),
  );
  await page
    .getByRole("button", { name: "Retry mapping", exact: true })
    .click();
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toBeVisible();
});

test("a newer selection aborts the old request and never shows its late result", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request);
  const slow = { ...report.example, id: "slow", title: "Old slow selection" };
  const latest = { ...report.example, id: "latest", title: "Latest selection" };
  await routeFixture(page, report, [report.example, slow, latest]);
  let release: () => void = () => {};
  const released = new Promise<void>((resolve) => {
    release = resolve;
  });
  let started: () => void = () => {};
  const slowStarted = new Promise<void>((resolve) => {
    started = resolve;
  });
  let finished: () => void = () => {};
  const slowFinished = new Promise<void>((resolve) => {
    finished = resolve;
  });
  await page.route("**/api/v1/examples/mappings/slow", async (route) => {
    started();
    await released;
    try {
      await route.fulfill({ json: { ...report, example: slow } });
    } catch {
      /* The old browser request is intentionally canceled. */
    } finally {
      finished();
    }
  });
  await page.route("**/api/v1/examples/mappings/latest", (route) =>
    route.fulfill({ json: { ...report, example: latest } }),
  );
  await page.goto("/");
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toBeVisible();
  const canceled = page.waitForEvent("requestfailed", (request) =>
    request.url().endsWith("/slow"),
  );
  await page
    .getByLabel("Example mapping", { exact: true })
    .selectOption("slow");
  await slowStarted;
  await expect(
    page
      .getByRole("status", { name: "" })
      .filter({ hasText: "Reading the selected mapping…" }),
  ).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toHaveCount(0);
  await page
    .getByLabel("Example mapping", { exact: true })
    .selectOption("latest");
  await canceled;
  await expect(
    page.getByRole("heading", { name: latest.title, exact: true }),
  ).toBeVisible();
  release();
  await slowFinished;
  await expect(
    page.getByRole("heading", { name: slow.title, exact: true }),
  ).toHaveCount(0);
  await expect(page.getByLabel("Example mapping", { exact: true })).toHaveValue(
    "latest",
  );
});

test("the example list can be retried after a service failure", async ({
  page,
  request,
}) => {
  const report = await displayFixture(request);
  await page.route("**/api/v1/examples/mappings", (route) =>
    route.fulfill({ status: 503 }),
  );
  await page.goto("/");
  await expect(
    page.getByRole("heading", {
      name: "Example mappings are unavailable",
      exact: true,
    }),
  ).toBeVisible();
  await page.unroute("**/api/v1/examples/mappings");
  await routeFixture(page, report);
  await page
    .getByRole("button", { name: "Retry example list", exact: true })
    .click();
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toBeVisible();
});

test("keyboard navigation and mobile details stay readable without page overflow", async ({
  page,
  request,
}, testInfo) => {
  const report = await displayFixture(request, "rich");
  await routeFixture(page, report);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await expect(
    page.getByRole("region", { name: "Source → rule → target", exact: true }),
  ).toBeVisible();
  await page.keyboard.press("Tab");
  await expect(
    page.getByRole("link", { name: "Skip to content", exact: true }),
  ).toBeFocused();
  const search = page.getByLabel("Find a field or rule");
  await tabTo(page, search);
  await page.keyboard.type("total");
  await expect(page.locator(".mapping-rows .mapping-row")).toHaveCount(1);
  const sourceReference = page
    .getByRole("region", { name: "Source → rule → target", exact: true })
    .getByRole("button", { name: 'orders [*]["amount"]', exact: true })
    .first();
  await tabTo(page, sourceReference);
  await expect(sourceReference).toBeFocused();
  await page.keyboard.press("Enter");
  const details = page.getByRole("region", {
    name: "Mapping details",
    exact: true,
  });
  await expect(details).toBeFocused();
  const close = details.getByRole("button", {
    name: "Close details",
    exact: true,
  });
  await tabTo(page, close, 8);
  await expect(close).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(sourceReference).toBeFocused();
  await tabTo(
    page,
    page.getByText("Revision and backend evidence", { exact: true }),
    80,
  );
  await page.keyboard.press("Enter");
  const evidence = page
    .locator(".provenance-card")
    .filter({ hasText: "Revision and backend evidence" });
  await expect(evidence.locator("details")).toHaveAttribute("open", "");
  await page.keyboard.press("Enter");
  await page.evaluate(() => {
    if (globalThis.document.activeElement instanceof HTMLElement)
      globalThis.document.activeElement.blur();
    window.scrollTo(0, 0);
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    ),
  ).toBe(false);
  await page.screenshot({
    path: testInfo.outputPath("mapping-mobile.png"),
    fullPage: true,
  });
});
