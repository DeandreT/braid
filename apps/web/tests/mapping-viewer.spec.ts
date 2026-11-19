import type { Page } from "@playwright/test";
import type { components } from "../src/api/generated";
import {
  displayData,
  installDisplay,
  type Projection,
} from "./mapping-viewer.fixture";
import {
  ATTEMPT,
  controlledData,
  copy,
  expect,
  test,
  type ControlledReports,
  type Data,
} from "./revision-reports-controlled.fixture";
import {
  expectReport,
  pinnedPath,
  reportApi,
  revisionPath,
  type ProjectionReport,
  type ReportsOwner,
  type Sample,
  type ValidationReport,
} from "./revision-reports-real.fixture";

// These tests exercise the authenticated catalog workbench, not the retired example page.
// Real native evidence and controlled display/transport cases are named separately.
test.setTimeout(300_000);
const errors = new WeakMap<Page, string[]>();
test.beforeEach(({ page }) => {
  const observed: string[] = [];
  errors.set(page, observed);
  page.on("pageerror", (error) => observed.push(error.message));
});
test.afterEach(({ page }) => {
  // Worker-owner setup can fail before beforeEach installs the page observer.
  // Preserve that setup failure rather than masking it with an undefined census.
  const observed = errors.get(page);
  if (observed) expect(observed).toEqual([]);
});
const table = (page: Page) =>
  page.getByRole("table", { name: "Mapping field connections", exact: true });
const inspector = (page: Page) =>
  page.getByRole("complementary", { name: "Context inspector", exact: true });
const rowButton = (page: Page, path: string) =>
  table(page).getByRole("button", {
    name: `Inspect field ${path}`,
    exact: true,
  });
const row = (page: Page, path: string) =>
  rowButton(page, path).locator("xpath=ancestor::tr");
const search = (page: Page) =>
  page.getByRole("searchbox", { name: "Filter mapping fields", exact: true });
async function controls(page: Page): Promise<void> {
  const summary = page.getByText("Report controls and evidence", {
    exact: true,
  });
  if (
    !(await summary
      .locator("..")
      .evaluate((element) => element.hasAttribute("open")))
  )
    await summary.click();
}
async function inspectorTab(
  page: Page,
  name: "Rule" | "Data flow" | "Details",
) {
  await inspector(page).getByRole("tab", { name, exact: true }).click();
}
async function openDisplay(page: Page, reports: ControlledReports, data: Data) {
  installDisplay(reports, data);
  await page.goto(data.browserPath);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    `Version ${data.revision.sequence}`,
  );
}
function documentOf(report: ProjectionReport): Projection {
  expect(report.report?.status).toBe("succeeded");
  if (!report.report?.document)
    throw new Error("Expected an actual saved projection document");
  return report.report.document;
}
function outputRule(document: Projection, name: string) {
  const output = document.boundaries.find(
    (boundary) => boundary.role === "primaryOutput",
  )!;
  const field = document.fields.find(
    (field) => field.boundaryId === output.id && field.name === name,
  )!;
  expect(field).toBeDefined();
  const rule = document.rules.find((rule) => rule.outputFieldId === field.id)!;
  expect(rule).toBeDefined();
  return { field, rule };
}
function allDetails(
  details: components["schemas"]["DetailNode"][],
): components["schemas"]["DetailNode"][] {
  return details.flatMap((detail) => [
    detail,
    ...allDetails(
      detail.kind === "group"
        ? detail.children
        : detail.kind === "expression"
          ? detail.arguments
          : [],
    ),
  ]);
}
async function ruleFlow(
  page: Page,
  path: string,
  summary: string,
): Promise<void> {
  await rowButton(page, path).click();
  await inspectorTab(page, "Data flow");
  await inspector(page)
    .getByRole("button", { name: summary, exact: true })
    .click();
  await inspectorTab(page, "Data flow");
}
async function openNative(
  page: Page,
  owner: ReportsOwner,
  sample: Sample = "basic",
) {
  // The owner performs ordinary private-host HTTP derivation; browser reads remain viewer-only.
  for (const operation of ["projection", "validation"] as const)
    await owner.control({ action: "derive-by-http", sample, operation });
  const revision = owner.manifest.samples[sample].revision;
  const capture = <T>(operation: "projection" | "validation") =>
    page
      .waitForResponse((response) => {
        const url = new URL(response.url());
        return (
          url.origin === owner.manifest.readOrigin &&
          url.pathname === reportApi(revision, operation) &&
          !url.search &&
          response.request().method() === "GET"
        );
      })
      .then(async (response) => {
        expect(response.status()).toBe(200);
        return (await response.json()) as T;
      });
  const projectionReply = capture<ProjectionReport>("projection");
  const validationReply = capture<ValidationReport>("validation");
  await page.goto(revisionPath(revision));
  const [projection, validation] = await Promise.all([
    projectionReply,
    validationReply,
  ]);
  expectReport(projection, revision, "projection");
  expectReport(validation, revision, "validation");
  return { projection, validation, revision };
}

test("actual saved Ferrule fields retain exact revision evidence and lineage", async ({
  page,
  reportsOwner,
}, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const { projection } = await openNative(page, reportsOwner);
  const document = documentOf(projection);
  expect(document.provenance.backend.revision).toBe(
    "796eb8230561f200ca81c153c946a8fe08884e75",
  );
  for (const operation of ["preview", "build"] as const) {
    const capability = document.capabilities.find(
      (item) => item.operation === operation,
    )!;
    expect(capability).toBeDefined();
    expect(capability.status).toBe("notQualified");
    expect(capability.profile).not.toBe("");
  }
  await expect(row(page, '["dotted"]')).toContainText('Source ["a.b"]');
  await expect(row(page, '["nestedOutput"]["b"]')).toContainText(
    'Source ["nested"]["b"]',
  );
  await search(page).fill("a.b");
  await expect(table(page).locator(".wb-row-focus")).toHaveCount(1);
  await rowButton(page, '["dotted"]').click();
  await inspectorTab(page, "Data flow");
  await inspector(page)
    .getByRole("button", { name: 'Source ["a.b"]', exact: true })
    .click();
  await inspectorTab(page, "Data flow");
  await expect(inspector(page)).toContainText("Used by");
  await expect(
    inspector(page).getByRole("heading", {
      name: 'Source ["a.b"]',
      exact: true,
    }),
  ).toBeVisible();
  await inspectorTab(page, "Details");
  await inspector(page).getByText("Technical details", { exact: true }).click();
  await expect(inspector(page)).toContainText(
    document.provenance.revision.revisionId,
  );
  await expect(inspector(page)).toContainText(
    document.provenance.nativeArtifactDigest.value,
  );
  await expect(inspector(page)).toContainText(
    projection.report!.provenance!.backend.buildId,
  );
  await page.screenshot({
    path: testInfo.outputPath("saved-native-mapping.png"),
    fullPage: true,
  });
  await page.screenshot({
    path: testInfo.outputPath("saved-native-mapping-first-screen.png"),
    fullPage: false,
  });
});

test("actual saved integer and IEEE-754 details preserve large integers and negative zero", async ({
  page,
  reportsOwner,
}) => {
  documentOf((await openNative(page, reportsOwner, "exact")).projection);
  await rowButton(page, '["exactInteger"]').click();
  await expect(inspector(page)).toContainText("9007199254740993");
  await rowButton(page, '["negativeZero"]').click();
  await expect(inspector(page).locator(".literal-value")).toHaveText("-0");
  await expect(inspector(page).locator(".literal-bits")).toContainText(
    "8000000000000000",
  );
  await rowButton(page, '["integralFloat"]').click();
  await expect(inspector(page).locator(".literal-kind")).toContainText(
    "Binary floating-point value",
  );
  await expect(inspector(page).locator(".literal-bits")).toContainText(
    "3ff0000000000000",
  );
});

test("actual saved conditions defaults host parameters and null forms remain distinct", async ({
  page,
  reportsOwner,
}) => {
  const { projection, validation } = await openNative(
    page,
    reportsOwner,
    "conditions",
  );
  const document = documentOf(projection);
  expect(validation.report?.isValid).toBe(true);
  const { rule: conditional } = outputRule(document, "conditional");
  const enabled = document.fields.find((field) => field.name === "enabled")!;
  expect(conditional.dependencies).toContainEqual({
    kind: "field",
    fieldId: enabled.id,
    role: "condition",
  });
  await ruleFlow(page, '["conditional"]', conditional.summary);
  await expect(
    inspector(page)
      .locator(".dependency-role")
      .filter({ hasText: /^condition$/ }),
  ).toBeVisible();
  await expect(inspector(page)).toContainText('Source ["enabled"]');
  const { rule: parameterRule } = outputRule(document, "parameter");
  const parameter = document.parameters.find(
    (parameter) => parameter.name === "fallbackCode",
  )!;
  expect(parameter).toBeDefined();
  expect(parameter.defaultValue).toEqual({ kind: "string", value: "fallback" });
  expect(parameterRule.dependencies).toContainEqual({
    kind: "parameter",
    parameterId: parameter.id,
    role: "value",
  });
  expect(parameterRule.dependencies).toContainEqual({
    kind: "constant",
    value: { kind: "string", value: "fallback" },
    role: "default",
  });
  await ruleFlow(page, '["parameter"]', parameterRule.summary);
  await expect(
    inspector(page)
      .locator(".dependency-role")
      .filter({ hasText: /^default$/ }),
  ).toBeVisible();
  await inspector(page)
    .locator(".dependency")
    .getByRole("button", { name: "Parameter: fallbackCode", exact: true })
    .click();
  await expect(inspector(page)).toContainText("Default value");
  await expect(inspector(page)).toContainText("fallback");
  for (const [name, label] of [
    ["absent", "Absent value"],
    ["explicitNull", "Explicit null"],
    ["xmlNil", "XML nil"],
  ] as const) {
    const { rule } = outputRule(document, name);
    expect(allDetails(rule.details)).toContainEqual({
      kind: "literal",
      value: { kind: "null", nullKind: name },
    });
    await rowButton(page, `["${name}"]`).click();
    await expect(inspector(page).locator(".literal-value")).toContainText(
      label,
    );
  }
});

test("actual saved named outputs preserve order and parent grouping governs descendant rows", async ({
  page,
  reportsOwner,
}) => {
  const { projection } = await openNative(page, reportsOwner, "controls");
  const document = documentOf(projection);
  const outputs = document.boundaries.filter(
    (boundary) =>
      boundary.role === "primaryOutput" || boundary.role === "namedOutput",
  );
  expect(outputs.map((boundary) => boundary.name)).toEqual([
    "Output",
    "audit",
    "summary",
  ]);
  const selector = page.getByLabel("Result document", { exact: true });
  await expect(selector.locator("option")).toHaveText([
    "All result documents",
    "Output",
    "audit",
    "summary",
  ]);
  const grouping = document.controls.find(
    (control) => control.kind === "grouping",
  )!;
  expect(grouping).toBeDefined();
  const { field: amount } = outputRule(document, "amount");
  const parent = document.fields.find(
    (field) => field.boundaryId === amount.boundaryId && field.name === "rows",
  )!;
  expect(parent).toBeDefined();
  expect(grouping.appliesTo).toContainEqual({
    kind: "field",
    fieldId: parent.id,
  });
  expect(grouping.appliesTo).not.toContainEqual({
    kind: "field",
    fieldId: amount.id,
  });
  await rowButton(page, '["rows"][*]["amount"]').click();
  await expect(
    inspector(page).getByRole("button", {
      name: grouping.summary,
      exact: true,
    }),
  ).toBeVisible();
  await inspector(page)
    .getByRole("button", { name: grouping.summary, exact: true })
    .click();
  await expect(inspector(page)).toContainText(grouping.summary);
  // The retained grouping remains inspectable even when no mapping rows match.
  await search(page).fill("there-is-no-such-field");
  await expect(table(page)).toContainText("No fields match this selection.");
  await expect(inspector(page)).toContainText(grouping.summary);
  await search(page).fill("");
  for (const [index, name] of [
    [1, "rowCount"],
    [2, "status"],
  ] as const) {
    await selector.selectOption(outputs[index].id);
    await expect(rowButton(page, `["${name}"]`)).toBeVisible();
  }
});

test("actual saved reusable functions share inspectable private definitions without host parameters", async ({
  page,
  reportsOwner,
}) => {
  const { projection } = await openNative(page, reportsOwner, "functions");
  const document = documentOf(projection);
  expect(document.parameters).toEqual([]);
  const definition = document.controls.find(
    (control) => control.summary === "Reusable function prefixCode",
  )!;
  expect(definition).toBeDefined();
  expect(definition.kind).toBe("construction");
  expect(definition.nativeLocator).toMatchObject({
    kind: "jsonPointer",
    pointer: "/user_functions/18446744073709551615",
  });
  for (const name of ["formatted", "other"]) {
    const { rule } = outputRule(document, name);
    const details = allDetails(rule.details);
    expect(details).toContainEqual({
      kind: "reference",
      reference: { kind: "control", controlId: definition.id },
    });
    expect(
      details.some(
        (detail) =>
          detail.kind === "expression" &&
          detail.operation === "Reusable function call",
      ),
    ).toBe(true);
    await rowButton(page, `["${name}"]`).click();
    await inspector(page)
      .getByRole("button", {
        name: `${definition.kind}: ${definition.summary}`,
        exact: true,
      })
      .click();
    await expect(inspector(page)).toContainText("Private parameters");
    await expect(inspector(page)).toContainText("Body");
    await inspector(page)
      .getByText("Native evidence", { exact: true })
      .last()
      .click();
    await expect(inspector(page)).toContainText("18446744073709551615");
  }
});

test("actual saved generated source context uses the qualified inner field", async ({
  page,
  reportsOwner,
}) => {
  const document = documentOf(
    (await openNative(page, reportsOwner, "generated")).projection,
  );
  expect(document.rules).toHaveLength(1);
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
  const button = table(page).locator(".wb-row-focus");
  await expect(button).toHaveCount(1);
  await expect(button.locator("xpath=ancestor::tr")).toContainText(
    'Source ["numbers"]',
  );
  await button.click();
  await inspectorTab(page, "Data flow");
  await inspector(page)
    .getByRole("button", { name: 'Source ["numbers"]', exact: true })
    .click();
  await expect(inspector(page)).toContainText('["numbers"]');
});

test("actual saved named input lineage and composite joins retain source ownership", async ({
  page,
  reportsOwner,
}) => {
  const { projection } = await openNative(page, reportsOwner, "namedInputs");
  const document = documentOf(projection);
  expect(
    document.boundaries
      .filter(
        (boundary) =>
          boundary.role === "primaryInput" || boundary.role === "namedInput",
      )
      .map((boundary) => [boundary.role, boundary.name]),
  ).toEqual([
    ["primaryInput", "Orders"],
    ["namedInput", "catalog"],
  ]);
  const catalog = document.boundaries.find(
    (boundary) => boundary.role === "namedInput",
  )!;
  const label = document.fields.find(
    (field) => field.boundaryId === catalog.id && field.name === "label",
  )!;
  expect(label.path).toEqual([
    { kind: "anyItem" },
    { kind: "field", name: "label", namespaceUri: null },
  ]);
  const { rule: joinedLabel } = outputRule(document, "label");
  expect(joinedLabel.dependencies).toContainEqual({
    kind: "field",
    fieldId: label.id,
    role: "value",
  });
  await expect(row(page, '["joined"][*]["label"]')).toContainText(
    'catalog [*]["label"]',
  );
  const join = document.controls.find((control) => control.kind === "join")!;
  expect(join).toBeDefined();
  const keys = join.dependencies
    .filter((dependency) => dependency.kind === "field")
    .map((dependency) =>
      document.fields.find((field) => field.id === dependency.fieldId)!,
    );
  for (const name of ["orderId", "region"]) {
    expect(keys.filter((field) => field.name === name)).toHaveLength(2);
    expect(
      new Set(
        keys
          .filter((field) => field.name === name)
          .map((field) => field.boundaryId),
      ).size,
    ).toBe(2);
  }
  await rowButton(page, '["joined"][*]["label"]').click();
  await inspector(page)
    .getByRole("button", { name: join.summary, exact: true })
    .click();
  await expect(inspector(page)).toContainText(
    'Orders ["orders"][*]["orderId"]',
  );
  await expect(inspector(page)).toContainText('catalog [*]["orderId"]');
  await expect(inspector(page)).toContainText('Orders ["orders"][*]["region"]');
  await expect(inspector(page)).toContainText('catalog [*]["region"]');
});

test("actual saved partial and native-only ambiguous gaps survive empty searches", async ({
  page,
  reportsOwner,
}) => {
  for (const sample of ["partial", "ambiguous"] as const) {
    const document = documentOf(
      (await openNative(page, reportsOwner, sample)).projection,
    );
    expect(document.coverage.status).toBe("partial");
    if (sample === "ambiguous")
      expect(
        document.coverage.gaps.some((gap) => gap.projectionLocator == null),
      ).toBe(true);
    await inspector(page)
      .locator("summary")
      .filter({ hasText: "Missing mapping details" })
      .click();
    for (const gap of document.coverage.gaps)
      await expect(inspector(page)).toContainText(gap.message);
    await search(page).fill("there-is-no-such-field");
    await expect(table(page)).toContainText("No fields match this selection.");
    for (const gap of document.coverage.gaps)
      await expect(inspector(page)).toContainText(gap.message);
    await expect(page.locator(".wb-table-footer")).toContainText(
      "partial explanation",
    );
  }
});

test("actual invalid saved mappings navigate diagnostics linked to the exact explanation", async ({
  page,
  reportsOwner,
}) => {
  const { projection, validation, revision } = await openNative(
    page,
    reportsOwner,
    "invalid",
  );
  const document = documentOf(projection);
  expect(validation.report?.status).toBe("invalidMapping");
  expect(validation.report?.isValid).toBe(false);
  const projectionId = projection.result!.derivationId;
  const validationId = validation.result!.derivationId;
  const reply = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return (
      url.origin === reportsOwner.manifest.readOrigin &&
      url.pathname === reportApi(revision, "validation", validationId) &&
      url.searchParams.get("projectionDerivationId") === projectionId
    );
  });
  await page.goto(pinnedPath(revision, projectionId, validationId));
  const response = await reply;
  expect(response.status()).toBe(200);
  const linked = (await response.json()) as ValidationReport;
  expectReport(linked, revision, "validation", validationId);
  expect(linked.projectionDerivationId).toBe(projectionId);
  const finding = linked.report!.diagnostics.find(
    (item) => item.projectionLocator?.kind === "rule",
  )!;
  expect(finding).toBeDefined();
  expect(finding.owner).toEqual({
    kind: "graphNode",
    functionId: null,
    nodeId: "1",
  });
  const ruleId =
    finding.projectionLocator!.kind === "rule"
      ? finding.projectionLocator!.ruleId
      : "";
  const rule = document.rules.find((item) => item.id === ruleId)!;
  await page.locator('[data-dock="diagnostics"]').click();
  const dock = page.locator("#wb-dock-panel");
  await dock
    .getByRole("button", { name: "Inspect linked rule", exact: true })
    .first()
    .click();
  await expect(inspector(page)).toContainText(rule.summary);
  await expect(page.locator(".wb-report-bar")).toContainText(
    "Native mapping has validation issues",
  );
  const diagnostic = dock
    .locator("article")
    .filter({ hasText: finding.message })
    .first();
  await diagnostic.getByText("Native evidence", { exact: true }).click();
  await expect(diagnostic).toContainText(finding.nativeLocator!.documentId);
});

test("controlled large reports page all fields and create details only in the inspector", async ({
  page,
  reports,
}) => {
  const data = displayData();
  const document = data.projection.report!.document!;
  document.coverage = {
    status: "partial",
    gaps: [
      {
        code: "displayGap",
        message: "Display coverage remains incomplete",
        nativeLocator: document.fields[0].nativeLocator,
      },
    ],
  };
  const templateRule = document.rules[0];
  const templateField = document.fields[1];
  for (let index = 0; index < 220; index++) {
    const name = `paged${String(index).padStart(3, "0")}`;
    document.fields.push({
      ...templateField,
      id: name,
      path: [{ kind: "field", name }],
      dataType:
        index === 150
          ? { kind: "array", scalarTypes: [] }
          : templateField.dataType,
    });
    document.rules.push({
      ...templateRule,
      id: `${name}-rule`,
      outputFieldId: name,
    });
  }
  document.boundaries.push({
    ...document.boundaries[0],
    id: "alternate",
    name: "Alternate",
    role: "namedOutput",
  });
  document.fields.push({
    ...templateField,
    id: "alternate-field",
    boundaryId: "alternate",
    path: [{ kind: "field", name: "alternate" }],
    dataType: { kind: "array", scalarTypes: ["string"] },
  });
  document.rules.push({
    ...templateRule,
    id: "alternate-rule",
    outputFieldId: "alternate-field",
  });
  await openDisplay(page, reports, data);
  await expect(table(page).locator(".wb-row-focus")).toHaveCount(100);
  await expect(table(page).locator("braid-mapping-details")).toHaveCount(0);
  await expect(inspector(page).locator("braid-mapping-details")).toHaveCount(0);
  const pages = page.getByRole("navigation", {
    name: "Mapping table pages",
    exact: true,
  });
  await pages.getByRole("button", { name: "Next", exact: true }).click();
  await expect(pages).toContainText("2 / 3");
  await expect(table(page).locator(".wb-row-focus")).toHaveCount(100);
  await search(page).fill("paged150");
  await expect(table(page).locator(".wb-row-focus")).toHaveCount(1);
  await rowButton(page, '["paged150"]').click();
  await expect(inspector(page)).toContainText("Repeated values");
  await expect(inspector(page).locator("braid-mapping-details")).toHaveCount(1);
  await page
    .getByLabel("Result document", { exact: true })
    .selectOption("alternate");
  await search(page).fill("");
  await rowButton(page, '["alternate"]').click();
  await expect(inspector(page)).toContainText("Repeated text");
  await page
    .getByLabel("Result document", { exact: true })
    .selectOption("controlled-output");
  await expect(pages).toContainText("1 / 3");
  await inspector(page)
    .locator("summary")
    .filter({ hasText: "Missing mapping details" })
    .click();
  await expect(inspector(page)).toContainText(
    "Display coverage remains incomplete",
  );
});

test("controlled typed paths preserve decimal namespaces indexes slashes Unicode and empty namespaces", async ({
  page,
  reports,
}) => {
  const data = displayData(true);
  const document = data.projection.report!.document!;
  const literalPath = [
    { kind: "field" as const, name: "a~b/c.𝛼", namespaceUri: "" },
  ];
  document.fields.push(
    { ...document.fields[2], id: "unicode-output", path: literalPath },
    { ...document.fields[0], id: "unicode-source", path: literalPath },
  );
  document.rules.push({
    ...document.rules[1],
    id: "unicode-rule",
    outputFieldId: "unicode-output",
    dependencies: [{ kind: "field", fieldId: "unicode-source", role: "value" }],
  });
  document.rules[0].details.push({
    kind: "literal",
    value: {
      kind: "exactNumber",
      flavor: "decimal",
      value: "0.1000000000000000000000000001",
    },
  });
  await openDisplay(page, reports, data);
  await expect(rowButton(page, '{""}["a~b/c.𝛼"]')).toBeVisible();
  await expect(rowButton(page, '["display"]["name"]')).toBeVisible();
  await expect(row(page, '{""}["a~b/c.𝛼"]')).toContainText(
    'orders {""}["a~b/c.𝛼"]',
  );
  await expect(row(page, '[*]["total"]')).toContainText(
    'customers {"urn:braid:customers"}["customer"]{"urn:braid:customers"}["tier"]',
  );
  await expect(row(page, '[*]["total"]')).toContainText(
    'rates ["rates"][0]["value"]',
  );
  await rowButton(page, '[*]["total"]').click();
  await expect(inspector(page)).toContainText("0.1000000000000000000000000001");
  await inspector(page)
    .getByRole("button", { name: "Join customer identity", exact: true })
    .click();
  await expect(inspector(page)).toContainText(
    'customers {"urn:braid:customers"}["customer"]@{"urn:braid:identity"}["id"]',
  );
  await rowButton(page, '{""}["a~b/c.𝛼"]').click();
  await expect(inspector(page)).toContainText('{""}["a~b/c.𝛼"]');
});

test("controlled conditions defaults and null forms remain inspectable without conflating host parameters", async ({
  page,
  reports,
}) => {
  const data = displayData();
  const document = data.projection.report!.document!;
  document.parameters.push({
    id: "fallback",
    name: "fallbackCode",
    requirement: "optional",
    dataType: { kind: "scalar", scalarTypes: ["string"] },
    defaultValue: { kind: "string", value: "fallback" },
    nativeLocator: document.fields[0].nativeLocator,
  });
  document.rules[0].dependencies.push(
    { kind: "field", fieldId: "source-value", role: "condition" },
    { kind: "parameter", parameterId: "fallback", role: "default" },
  );
  for (const nullKind of ["absent", "explicitNull", "xmlNil"] as const) {
    document.fields.push({
      ...document.fields[1],
      id: nullKind,
      path: [{ kind: "field", name: nullKind }],
    });
    document.rules.push({
      ...document.rules[0],
      id: `${nullKind}-rule`,
      outputFieldId: nullKind,
      details: [{ kind: "literal", value: { kind: "null", nullKind } }],
    });
  }
  await openDisplay(page, reports, data);
  await rowButton(page, '["display.name"]').click();
  await inspectorTab(page, "Data flow");
  await inspector(page)
    .getByRole("button", { name: "Populate displayed value", exact: true })
    .click();
  await inspectorTab(page, "Data flow");
  for (const role of ["condition", "default"])
    await expect(
      inspector(page)
        .locator(".dependency-role")
        .filter({ hasText: new RegExp(`^${role}$`) })
        .first(),
    ).toBeVisible();
  await inspector(page)
    .locator(".dependency")
    .getByRole("button", { name: "Parameter: fallbackCode", exact: true })
    .click();
  await inspectorTab(page, "Rule");
  await expect(inspector(page)).toContainText("Default value");
  for (const [name, label] of [
    ["absent", "Absent value"],
    ["explicitNull", "Explicit null"],
    ["xmlNil", "XML nil"],
  ]) {
    await rowButton(page, `["${name}"]`).click();
    await expect(inspector(page).locator(".literal-value")).toContainText(
      label,
    );
  }
});

test("controlled named outputs and governing reusable controls retain inspectable private definitions", async ({
  page,
  reports,
}) => {
  const data = displayData();
  const document = data.projection.report!.document!;
  document.fields[1].path = [
    { kind: "field", name: "rows" },
    { kind: "anyItem" },
    { kind: "field", name: "amount" },
  ];
  document.fields.push({
    ...document.fields[1],
    id: "rows-parent",
    path: [{ kind: "field", name: "rows" }],
    dataType: { kind: "array", scalarTypes: [] },
  });
  document.controls.push({
    id: "group",
    kind: "grouping",
    summary: "Group parent rows",
    appliesTo: [{ kind: "field", fieldId: "rows-parent" }],
    dependencies: [],
    details: [],
    nativeLocator: document.fields[0].nativeLocator,
  });
  for (const [id, name] of [
    ["audit", "rowCount"],
    ["summary", "status"],
  ]) {
    document.boundaries.push({
      ...document.boundaries[0],
      id,
      name: id,
      role: "namedOutput",
    });
    document.fields.push({
      ...document.fields[1],
      id: `${id}-field`,
      boundaryId: id,
      path: [{ kind: "field", name }],
    });
    document.rules.push({
      ...document.rules[0],
      id: `${id}-rule`,
      outputFieldId: `${id}-field`,
    });
  }
  document.controls.push({
    id: "function",
    kind: "construction",
    summary: "Reusable function",
    appliesTo: [{ kind: "field", fieldId: "output-value" }],
    dependencies: [],
    nativeLocator: {
      kind: "opaque",
      documentId: "function",
      scheme: "display",
      value: "18446744073709551615",
    },
    details: [
      {
        kind: "group",
        label: "Private parameters",
        children: [
          {
            kind: "literal",
            value: { kind: "string", value: "private argument" },
          },
        ],
      },
    ],
  });
  expect(document.parameters).toEqual([]);
  await openDisplay(page, reports, data);
  const selector = page.getByLabel("Result document", { exact: true });
  await expect(selector.locator("option")).toHaveText([
    "All result documents",
    "target",
    "audit",
    "summary",
  ]);
  await rowButton(page, '["rows"][*]["amount"]').click();
  await expect(
    inspector(page).getByRole("button", {
      name: "Group parent rows",
      exact: true,
    }),
  ).toBeVisible();
  await inspector(page)
    .getByRole("button", { name: "Reusable function", exact: true })
    .click();
  await expect(inspector(page)).toContainText("Private parameters");
  await inspector(page)
    .getByText("Native evidence", { exact: true })
    .last()
    .click();
  await expect(inspector(page)).toContainText("18446744073709551615");
  for (const [id, name] of [
    ["audit", "rowCount"],
    ["summary", "status"],
  ]) {
    await selector.selectOption(id);
    await expect(rowButton(page, `["${name}"]`)).toBeVisible();
  }
});

test("controlled generated source context keeps the qualified inner field", async ({
  page,
  reports,
}) => {
  const data = displayData();
  data.projection.report!.document!.fields[0].path = [
    { kind: "field", name: "numbers", namespaceUri: null },
  ];
  await openDisplay(page, reports, data);
  await expect(row(page, '["display.name"]')).toContainText(
    'source ["numbers"]',
  );
  await rowButton(page, '["display.name"]').click();
  await page.getByRole("button", { name: "Data flow", exact: true }).click();
  await expect(
    page.locator(".wb-inline-lineage [data-role=source]"),
  ).toContainText('source ["numbers"]');
});

test("controlled unavailable coverage stays explicit independently of valid native validation", async ({
  page,
  reports,
}) => {
  const data = displayData();
  const document = data.projection.report!.document!;
  document.coverage = {
    status: "unavailable",
    gaps: [
      {
        code: "opaque",
        message: "No usable explanation is available",
        nativeLocator: document.fields[0].nativeLocator,
      },
    ],
  };
  document.fields = [];
  document.rules = [];
  await openDisplay(page, reports, data);
  await expect(table(page)).toContainText("No field details are available");
  await expect(page.locator(".wb-table-footer")).toContainText(
    "unavailable explanation",
  );
  await expect(page.locator(".wb-report-bar")).toContainText(
    "Native mapping is valid",
  );
  await inspector(page)
    .locator("summary")
    .filter({ hasText: "Missing mapping details" })
    .click();
  await expect(inspector(page)).toContainText(
    "No usable explanation is available",
  );
});

test("controlled canceled and unavailable operations clear stale fields and unknown validity is refused", async ({
  page,
  reports,
}) => {
  const data = displayData();
  await openDisplay(page, reports, data);
  await expect(table(page)).toBeVisible();
  await controls(page);
  for (const operation of ["projection", "validation"] as const) {
    const failed = copy(
      operation === "projection" ? data.projection : data.validation,
    );
    const failure =
      operation === "projection" ? "canceled" : "backendUnavailable";
    failed.state = "failed";
    failed.status = failure;
    failed.failure = failure;
    failed.report = null;
    failed.result = null;
    failed.nextAttemptAtUtc = "2026-01-01T00:00:03Z";
    failed.currentAttempt = {
      derivationId: ATTEMPT,
      generation: "2",
      startedAtUtc: "2026-01-01T00:00:01Z",
      finishedAtUtc: "2026-01-01T00:00:02Z",
      status: failure,
      failure,
    };
    reports.set("GET", data.reportPath(operation), { json: failed });
    await page
      .getByRole("button", {
        name: `Read ${operation === "projection" ? "explanation" : "validation"} again`,
        exact: true,
      })
      .click();
  }
  await expect(table(page)).toHaveCount(0);
  await expect(page.locator(".wb-reports")).toContainText(
    "The mapping operation was canceled.",
  );
  await expect(page.locator(".wb-reports")).toContainText(
    "The mapping engine is unavailable.",
  );
  installDisplay(reports, data);
  await page
    .getByRole("button", { name: "Read explanation again", exact: true })
    .click();
  await expect(table(page)).toBeVisible();
  // Saved successful validation requires a boolean decision; null is an incompatible response.
  data.validation.report!.isValid = null;
  installDisplay(reports, data);
  await page
    .getByRole("button", { name: "Read validation again", exact: true })
    .click();
  await expect(page.locator(".wb-reports").getByRole("alert")).toContainText(
    "could not be verified",
  );
  await expect(page.locator(".wb-report-bar")).not.toContainText(
    "Native mapping is valid",
  );
});

test("controlled missing and transport-failed report reads clear fields and recover on reread", async ({
  page,
  reports,
}) => {
  const data = displayData();
  await openDisplay(page, reports, data);
  await controls(page);
  for (const status of [404, 503]) {
    reports.set("GET", data.reportPath("projection"), {
      status,
      json: { code: status === 404 ? "notFound" : "revisionReportUnavailable" },
    });
    await page
      .getByRole("button", { name: "Read explanation again", exact: true })
      .click();
    await expect(page.locator(".wb-reports").getByRole("alert")).toBeVisible();
    await expect(table(page)).toHaveCount(0);
    installDisplay(reports, data);
    await page
      .getByRole("button", { name: "Read explanation again", exact: true })
      .click();
    await expect(table(page)).toBeVisible();
  }
});

test("controlled newer revision navigation cancels stale reads and ignores late reports", async ({
  page,
  reports,
}) => {
  const old = displayData();
  const latest = controlledData("44444444-4444-4444-4444-444444444449");
  installDisplay(reports, old);
  reports.add(latest);
  const slow = reports.hold("GET", old.reportPath("projection"), {
    json: old.projection,
  });
  await page.goto(old.browserPath);
  await expect.poll(slow.entered).toBe(true);
  await page.evaluate((path) => {
    history.pushState({}, "", path);
    window.dispatchEvent(new PopStateEvent("popstate"));
  }, latest.browserPath);
  await expect(table(page)).toContainText("Controlled saved explanation");
  await expect.poll(slow.cancelled).toBe(true);
  slow.release();
  await expect(table(page)).not.toContainText("Populate displayed value");
  await expect(page).toHaveURL(new RegExp(latest.revision.scope.revisionId));
});

test("controlled catalog revision metadata failures can be retried", async ({
  page,
  reports,
}) => {
  const data = displayData();
  installDisplay(reports, data);
  reports.set("GET", data.apiPath, {
    status: 503,
    json: { code: "catalogUnavailable" },
  });
  await page.goto(data.browserPath);
  await expect(
    page.getByRole("button", { name: /Try again|Retry/ }),
  ).toBeVisible();
  installDisplay(reports, data);
  await page.getByRole("button", { name: /Try again|Retry/ }).click();
  await expect(table(page)).toBeVisible();
});

test("controlled keyboard field navigation and mobile inspector remain readable without page overflow", async ({
  page,
  reports,
}, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDisplay(page, reports, displayData(true));
  await expect(table(page)).toBeVisible();
  const first = table(page).locator(".wb-row-focus").first();
  await search(page).focus();
  for (let stop = 0; stop < 40; stop++) {
    if (await first.evaluate((element) => element === document.activeElement))
      break;
    await page.keyboard.press("Tab");
  }
  await expect(first).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await expect(table(page).locator(".wb-row-focus").nth(1)).toBeFocused();
  await page.keyboard.press("Home");
  await expect(first).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(inspector(page)).toBeVisible();
  await expect(
    inspector(page).getByRole("heading", { level: 3 }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Inspector", exact: true }).click();
  await first.focus();
  await page.keyboard.press("Escape");
  await expect(first).toHaveAttribute("aria-pressed", "false");
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    ),
  ).toBe(false);
  await page.screenshot({
    path: testInfo.outputPath("saved-mapping-mobile.png"),
    fullPage: true,
  });
});
