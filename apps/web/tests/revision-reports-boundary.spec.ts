import type { Locator, Page } from "@playwright/test";
import {
  ATTEMPT,
  P,
  P2,
  V,
  V2,
  controlledData,
  copy,
  expect,
  notDerived,
  panel,
  pending,
  test,
  type ControlledReports,
  type Reply,
} from "./revision-reports-controlled.fixture";

const graph = (page: Page) =>
  page.getByRole("region", { name: "Source → rule → target", exact: true });
const fact = (region: Locator, label: string) =>
  region
    .locator("dt", { hasText: new RegExp(`^${label}$`) })
    .locator("xpath=following-sibling::dd[1]");
async function metadata(page: Page): Promise<void> {
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Revision 1",
  );
}
async function open(
  page: Page,
  reports: ControlledReports,
  path = reports.data.browserPath,
): Promise<void> {
  await page.goto(path);
  await metadata(page);
}
async function incompatible(page: Page): Promise<void> {
  await expect(panel(page, "projection").getByRole("alert")).toContainText(
    "could not be verified",
  );
  await expect(graph(page)).toHaveCount(0);
  await metadata(page);
}
const error = (status: number, code: string): Reply => ({
  status,
  json: { code },
});
function noValidation(reports: ControlledReports): void {
  reports.set("GET", reports.data.reportPath("validation"), {
    json: notDerived(reports.data.validation),
  });
}
async function prepareGenerate(
  page: Page,
  reports: ControlledReports,
): Promise<void> {
  await prepareGenerateWithoutAdmissionAssertion(page, reports);
  await expect(
    panel(page, "projection").getByRole("button", {
      name: "Generate explanation",
      exact: true,
    }),
  ).toBeEnabled();
}
async function prepareGenerateWithoutAdmissionAssertion(
  page: Page,
  reports: ControlledReports,
): Promise<void> {
  reports.set("GET", reports.data.reportPath("projection"), {
    json: notDerived(reports.data.projection),
  });
  noValidation(reports);
  await open(page, reports);
  await expect(
    panel(page, "projection").getByRole("button", {
      name: "Generate explanation",
      exact: true,
    }),
  ).toBeVisible();
}
async function acceleratedTime(page: Page): Promise<void> {
  // Keep normal render timers flowing; only settled no-retry checks advance the clock.
  await page.clock.install({ time: new Date("2026-10-15T00:00:00Z") });
}
async function settleFocus(page: Page): Promise<void> {
  await page.clock.runFor(200);
}
const generate = (page: Page) =>
  panel(page, "projection").getByRole("button", {
    name: "Generate explanation",
    exact: true,
  });

test("controlled saved reports: independent coverage, usable false validation, evidence and exact integer rendering", async ({
  page,
  reports,
}) => {
  await open(page, reports);
  await expect(graph(page).getByRole("article")).toHaveCount(1);
  await expect(
    page.getByRole("region", { name: "Explanation incomplete", exact: true }),
  ).toContainText("Controlled ambiguity remains visible");
  await expect(fact(panel(page, "validation"), "Validity")).toHaveText(
    "Issues found",
  );
  await expect(fact(panel(page, "validation"), "Total notices")).toHaveText(
    "3",
  );
  await expect(fact(panel(page, "validation"), "Notices returned")).toHaveText(
    "1",
  );
  await expect(fact(panel(page, "validation"), "Notice list")).toContainText(
    "shortened",
  );
  await graph(page)
    .getByText("Expand rule explanation", { exact: true })
    .click();
  await expect(graph(page)).toContainText("9223372036854775807");
  await panel(page, "projection")
    .getByText("Explanation backend evidence", { exact: true })
    .click();
  await expect(panel(page, "projection")).toContainText(
    "controlled-backend-revision",
  );
  await expect(panel(page, "projection")).toContainText(
    "controlled-adapter-version",
  );
  expect(reports.posts("projection")).toEqual([]);
  expect(reports.posts("validation")).toEqual([]);
});

test("controlled saved reports: available unreadable payload keeps successful identity without drawing an empty graph", async ({
  page,
  reports,
}) => {
  const value = {
    ...reports.data.projection,
    readOutcome: "objectUnavailable" as const,
    report: null,
  };
  reports.set("GET", reports.data.reportPath("projection"), { json: value });
  reports.set("GET", reports.data.reportPath("validation"), {
    json: {
      ...reports.data.validation,
      readOutcome: "storageIntegrity",
      report: null,
    },
  });
  await open(page, reports);
  await expect(fact(panel(page, "projection"), "Saved result ID")).toHaveText(
    P,
  );
  await expect(fact(panel(page, "projection"), "Saved payload")).toHaveText(
    "Saved object is unavailable",
  );
  await expect(
    panel(page, "projection").getByRole("link", {
      name: "Open saved explanation link",
      exact: true,
    }),
  ).toHaveAttribute(
    "href",
    reports.data.browserPath + `?projectionDerivationId=${P}`,
  );
  await expect(graph(page)).toHaveCount(0);
  await expect(fact(panel(page, "validation"), "Saved result ID")).toHaveText(
    V,
  );
  await expect(fact(panel(page, "validation"), "Saved payload")).toHaveText(
    "Saved object failed its integrity check",
  );
  await expect(fact(panel(page, "validation"), "Validity")).toHaveCount(0);
  await expect(fact(panel(page, "validation"), "Total notices")).toHaveCount(0);
  await expect(panel(page, "validation")).not.toContainText("Issues found");
  await metadata(page);
});

test("controlled saved selectors: malformed, duplicate and unknown selectors dispatch no report request", async ({
  page,
  reports,
}) => {
  for (const query of [
    "projectionDerivationId=bad",
    "validationDerivationId=00000000-0000-0000-0000-000000000000",
    "projectionDerivationId=50000000-0000-4000-8000-00000000000A",
    `projectionDerivationId=${P}&projectionDerivationId=${P2}`,
    `validationDerivationId=${V}&validationDerivationId=${V2}`,
    `projectionDerivationId=${P}&unknown=value`,
    "repairDerivationId=" + P,
  ]) {
    const before =
      reports.reads("projection").length + reports.reads("validation").length;
    await page.goto(reports.data.browserPath + "?" + query);
    await expect(
      page.getByText(/report link is invalid|Invalid link/).first(),
    ).toBeVisible();
    expect(
      reports.reads("projection").length + reports.reads("validation").length,
    ).toBe(before);
    await expect(graph(page)).toHaveCount(0);
  }
});

test("controlled saved wrappers: schema, operation and every ancestry component must match the immutable revision", async ({
  page,
  reports,
}) => {
  const values: unknown[] = [
    { ...reports.data.projection, schemaVersion: "foreign.schema" },
    { ...reports.data.projection, operation: "validation" },
  ];
  for (const key of [
    "organizationId",
    "projectId",
    "mappingId",
    "revisionId",
  ] as const)
    values.push({
      ...reports.data.projection,
      revision: {
        ...reports.data.revision.scope,
        [key]: "90000000-0000-4000-8000-000000000099",
      },
    });
  for (const value of values) {
    reports.set("GET", reports.data.reportPath("projection"), { json: value });
    await open(page, reports);
    await incompatible(page);
  }
  reports.set("GET", reports.data.reportPath("projection", P), {
    json: {
      ...reports.data.projection,
      result: { ...reports.data.projection.result, derivationId: P2 },
    },
  });
  await open(
    page,
    reports,
    reports.data.browserPath + `?projectionDerivationId=${P}`,
  );
  await incompatible(page);
});

test("controlled saved coherence: contradictory lifecycle, status, payload and validation counts are rejected", async ({
  page,
  reports,
}) => {
  const values: unknown[] = [
    { ...reports.data.projection, result: null },
    { ...reports.data.projection, state: "notDerived" },
    { ...reports.data.projection, status: "backendFailure" },
    { ...reports.data.projection, readOutcome: "objectUnavailable" },
    {
      ...reports.data.projection,
      report: { ...reports.data.projection.report, status: "invalidMapping" },
    },
  ];
  for (const value of values) {
    reports.set("GET", reports.data.reportPath("projection"), { json: value });
    await open(page, reports);
    await incompatible(page);
  }
  reports.set("GET", reports.data.reportPath("projection"), {
    json: reports.data.projection,
  });
  reports.set("GET", reports.data.reportPath("validation"), {
    json: {
      ...reports.data.validation,
      report: {
        ...reports.data.validation.report,
        emittedIssueCount: 4,
        totalIssueCount: 3,
      },
    },
  });
  await open(page, reports);
  await expect(panel(page, "validation").getByRole("alert")).toContainText(
    "could not be verified",
  );
  await expect(graph(page)).toBeVisible();
});

test("controlled saved provenance: operation and document revision or native digest cannot disagree with metadata", async ({
  page,
  reports,
}) => {
  for (const variant of [
    "operationRevision",
    "operationDigest",
    "documentRevision",
    "documentDigest",
  ] as const) {
    const value = copy(reports.data.projection);
    const provenance = variant.startsWith("operation")
      ? value.report!.provenance!
      : value.report!.document!.provenance;
    if (variant.endsWith("Revision"))
      provenance.revision = {
        ...provenance.revision,
        revisionId: "90000000-0000-4000-8000-000000000099",
      };
    else
      provenance.nativeArtifactDigest = {
        ...provenance.nativeArtifactDigest,
        value: "b".repeat(64),
      };
    reports.set("GET", reports.data.reportPath("projection"), { json: value });
    await open(page, reports);
    await incompatible(page);
  }
});

test("controlled independence: projection dependency failure preserves false validation and immutable metadata", async ({
  page,
  reports,
}) => {
  reports.set(
    "GET",
    reports.data.reportPath("projection"),
    error(503, "revisionReportUnavailable"),
  );
  await open(page, reports);
  await expect(panel(page, "projection").getByRole("alert")).toContainText(
    "dependency is unavailable",
  );
  await expect(fact(panel(page, "validation"), "Validity")).toHaveText(
    "Issues found",
  );
  await metadata(page);
});

test("controlled independence: validation failure preserves the actual projection and metadata", async ({
  page,
  reports,
}) => {
  reports.set(
    "GET",
    reports.data.reportPath("validation"),
    error(503, "revisionReportResourceLimit"),
  );
  await open(page, reports);
  await expect(panel(page, "validation").getByRole("alert")).toContainText(
    "service limit",
  );
  await expect(graph(page)).toBeVisible();
  await metadata(page);
});

test("controlled retained identities: exact selectors and Int64 generation pin successes, never an attempt", async ({
  page,
  reports,
}) => {
  reports.data.projection.result!.generation = "9223372036854775807";
  await open(
    page,
    reports,
    reports.data.browserPath +
      `?projectionDerivationId=${P}&validationDerivationId=${V}`,
  );
  await expect(fact(panel(page, "projection"), "Generation")).toHaveText(
    "9223372036854775807",
  );
  expect(
    reports
      .reads("projection")
      .every((r) => r.path === reports.data.reportPath("projection", P)),
  ).toBe(true);
  expect(
    reports
      .reads("validation")
      .every((r) => r.path === reports.data.reportPath("validation", V)),
  ).toBe(true);
  reports.set("GET", reports.data.reportPath("projection"), {
    json: pending(reports.data.projection),
  });
  await page
    .getByRole("link", {
      name: "Read current reports for this revision",
      exact: true,
    })
    .click();
  await expect(fact(panel(page, "projection"), "Lifecycle")).toHaveText(
    "In progress",
  );
  await expect(
    panel(page, "projection").getByRole("link", {
      name: "Open saved explanation link",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(
    reports.reads("projection").some((r) => r.path.endsWith("/" + ATTEMPT)),
  ).toBe(false);
});

test("controlled linked validation: opaque linked404 clears evidence until an explicit authorized unlinked read", async ({
  page,
  reports,
}) => {
  reports.set(
    "GET",
    reports.data.reportPath("validation", V),
    error(404, "notFound"),
    `projectionDerivationId=${P}`,
  );
  await open(page, reports);
  await expect(panel(page, "validation").getByRole("alert")).toContainText(
    "unavailable or you no longer have access",
  );
  await expect(
    page.getByRole("button", { name: "Show validation rule", exact: true }),
  ).toHaveCount(0);
  const before = reports.reads("validation").length;
  const contexts = reports.requests.filter(
    (r) => r.path === "/api/v1/catalog/context",
  ).length;
  await panel(page, "validation")
    .getByRole("button", { name: "Read validation without links", exact: true })
    .click();
  await expect(fact(panel(page, "validation"), "Validity")).toHaveText(
    "Issues found",
  );
  expect(reports.reads("validation").length).toBeGreaterThan(before);
  expect(reports.reads("validation").at(-1)!.query).toBe("");
  expect(
    reports.requests.filter((r) => r.path === "/api/v1/catalog/context").length,
  ).toBe(contexts + 1);
  await expect(
    page.getByRole("button", { name: "Show validation rule", exact: true }),
  ).toHaveCount(0);
});

test("controlled cancellation: late revision A cannot replace a freshly selected revision B", async ({
  page,
  reports,
}) => {
  const other = controlledData("40000000-0000-4000-8000-000000000002");
  other.projection.result!.derivationId = P2;
  other.validation.result!.derivationId = V2;
  other.projection.report!.document!.rules[0].summary =
    "Controlled revision B explanation";
  reports.add(other);
  const held = reports.hold("GET", reports.data.reportPath("projection"), {
    json: reports.data.projection,
  });
  await open(page, reports);
  await expect.poll(held.entered).toBe(true);
  await page
    .getByRole("button", { name: "View current head", exact: true })
    .click();
  await expect(page).toHaveURL(new RegExp(other.browserPath + "$"));
  await expect(graph(page)).toContainText("Controlled revision B explanation");
  held.release();
  await expect.poll(held.cancelled).toBe(true);
  await expect(graph(page)).not.toContainText("Controlled saved explanation");
});

test("controlled cancellation: changing pinned selector clears held old report before current selection wins", async ({
  page,
  reports,
}) => {
  const held = reports.hold("GET", reports.data.reportPath("projection", P), {
    json: reports.data.projection,
  });
  const current = copy(reports.data.projection);
  current.result!.derivationId = P2;
  current.report!.document!.rules[0].summary = "Controlled current selection B";
  reports.set("GET", reports.data.reportPath("projection"), { json: current });
  noValidation(reports);
  await open(
    page,
    reports,
    reports.data.browserPath + `?projectionDerivationId=${P}`,
  );
  await expect.poll(held.entered).toBe(true);
  await expect(graph(page)).toHaveCount(0);
  await page
    .getByRole("link", {
      name: "Read current reports for this revision",
      exact: true,
    })
    .click();
  await expect(graph(page)).toContainText("Controlled current selection B");
  held.release();
  await expect.poll(held.cancelled).toBe(true);
  await expect(graph(page)).not.toContainText("Controlled saved explanation");
});

test("controlled cancellation: a fresh actor snapshot rejects late evidence from the previous identity", async ({
  page,
  reports,
}) => {
  const held = reports.hold("GET", reports.data.reportPath("projection"), {
    json: reports.data.projection,
  });
  await open(page, reports);
  await expect.poll(held.entered).toBe(true);
  reports.data.context.actorPrincipalId =
    "10000000-0000-4000-8000-000000000099";
  const current = copy(reports.data.projection);
  current.report!.document!.rules[0].summary = "Controlled refreshed identity";
  reports.set("GET", reports.data.reportPath("projection"), { json: current });
  noValidation(reports);
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  await expect(graph(page)).toContainText("Controlled refreshed identity");
  held.release();
  await expect.poll(held.cancelled).toBe(true);
  await expect(graph(page)).not.toContainText("Controlled saved explanation");
});

test("controlled ensure conflicts: closed ownership, selection and generation refusals never retry a POST", async ({
  page,
  reports,
}) => {
  await acceleratedTime(page);
  for (const [code, message] of [
    ["revisionReportClaimLost", "no longer owns the work"],
    ["revisionReportSelectionChanged", "selected saved result changed"],
    ["revisionReportGenerationExhausted", "No more report attempts"],
  ] as const) {
    await prepareGenerate(page, reports);
    await settleFocus(page);
    await expect(generate(page)).toBeEnabled();
    reports.set(
      "POST",
      reports.data.reportPath("projection") + "/ensure",
      error(409, code),
    );
    const before = reports.posts("projection").length;
    await generate(page).click();
    await expect(panel(page, "projection").getByRole("alert")).toContainText(
      message,
    );
    await page.clock.runFor(60000);
    expect(reports.posts("projection").length).toBe(before + 1);
    await metadata(page);
  }
});

test("controlled ensure availability: admission, dependency and resource refusals are categorical and closed", async ({
  page,
  reports,
}) => {
  await acceleratedTime(page);
  for (const [status, code, message] of [
    [429, "revisionReportAdmissionBusy", "service is busy"],
    [503, "revisionReportUnavailable", "dependency is unavailable"],
    [503, "revisionReportResourceLimit", "service limit"],
    [409, "foreignConflict", "could not be verified"],
    [429, "foreignAdmission", "could not be verified"],
    [503, "foreignDependency", "could not be verified"],
  ] as const) {
    await prepareGenerate(page, reports);
    await settleFocus(page);
    await expect(generate(page)).toBeEnabled();
    reports.set(
      "POST",
      reports.data.reportPath("projection") + "/ensure",
      error(status, code),
    );
    const before = reports.posts("projection").length;
    await generate(page).click();
    await expect(panel(page, "projection").getByRole("alert")).toContainText(
      message,
    );
    await page.clock.runFor(60000);
    expect(reports.posts("projection").length).toBe(before + 1);
    await metadata(page);
  }
});

test("controlled admission: feature and action intersections govern reads and derivation for every role", async ({
  page,
  reports,
}) => {
  for (const disabled of ["feature", "action"] as const) {
    reports.data.context.features.readReports = disabled !== "feature";
    reports.data.mapping.access.actions.readReports = disabled !== "action";
    const before =
      reports.reads("projection").length + reports.reads("validation").length;
    await open(page, reports);
    await expect(
      page.getByText(
        "Report reading is currently unavailable for this mapping.",
        { exact: false },
      ),
    ).toBeVisible();
    expect(
      reports.reads("projection").length + reports.reads("validation").length,
    ).toBe(before);
    await expect(generate(page)).toHaveCount(0);
  }
  reports.data.context.features.readReports = true;
  reports.data.mapping.access.actions.readReports = true;
  for (const disabled of ["feature", "action"] as const) {
    reports.data.context.features.deriveReports = disabled !== "feature";
    reports.data.mapping.access.actions.deriveReports = disabled !== "action";
    await prepareGenerateWithoutAdmissionAssertion(page, reports);
    await expect(generate(page)).toBeDisabled();
  }
  reports.data.context.features.deriveReports = true;
  reports.data.mapping.access.actions.deriveReports = true;
  for (const role of [
    "viewer",
    "runner",
    "publisher",
    "administrator",
  ] as const) {
    reports.data.project.access.role = role;
    reports.data.mapping.access.role = role;
    await prepareGenerate(page, reports);
    await expect(generate(page)).toBeEnabled();
  }
  // The initially offered action does not authorize a later mutation after access changes.
  reports.data.mapping.access.actions.deriveReports = false;
  await generate(page).click();
  await expect(panel(page, "projection").getByRole("alert")).toContainText(
    "request was not accepted",
  );
  expect(reports.posts("projection")).toEqual([]);
  expect(reports.posts("validation")).toEqual([]);
});

test("controlled repair: only current missing or corrupt saved payloads submit their exact captured identity", async ({
  page,
  reports,
}) => {
  noValidation(reports);
  for (const outcome of ["objectUnavailable", "storageIntegrity"] as const) {
    reports.set("GET", reports.data.reportPath("projection"), {
      json: { ...reports.data.projection, readOutcome: outcome, report: null },
    });
    reports.set(
      "POST",
      reports.data.reportPath("projection") + "/ensure",
      error(409, "revisionReportSelectionChanged"),
      `repairDerivationId=${P}`,
    );
    await open(page, reports);
    const repair = panel(page, "projection").getByRole("button", {
      name: "Repair saved result",
      exact: true,
    });
    await expect(repair).toBeEnabled();
    const before = reports.posts("projection").length;
    await repair.click();
    await expect(panel(page, "projection").getByRole("alert")).toContainText(
      "selected saved result changed",
    );
    expect(reports.posts("projection").length).toBe(before + 1);
    expect(reports.posts("projection").at(-1)!.query).toBe(
      `repairDerivationId=${P}`,
    );
  }
  for (const outcome of ["unsupportedFormat", "resourceLimit"] as const) {
    reports.set("GET", reports.data.reportPath("projection"), {
      json: { ...reports.data.projection, readOutcome: outcome, report: null },
    });
    await open(page, reports);
    await expect(fact(panel(page, "projection"), "Saved result ID")).toHaveText(
      P,
    );
    await expect(
      panel(page, "projection").getByRole("button", {
        name: "Repair saved result",
        exact: true,
      }),
    ).toHaveCount(0);
  }
  reports.set("GET", reports.data.reportPath("projection", P), {
    json: {
      ...reports.data.projection,
      readOutcome: "objectUnavailable",
      report: null,
    },
  });
  await open(
    page,
    reports,
    reports.data.browserPath + `?projectionDerivationId=${P}`,
  );
  await expect(fact(panel(page, "projection"), "Saved result ID")).toHaveText(
    P,
  );
  await expect(
    panel(page, "projection").getByRole("button", {
      name: "Repair saved result",
      exact: true,
    }),
  ).toHaveCount(0);
});

test("controlled transport: explicit empty ensure uses only the fresh memory token while manual reads refresh admission", async ({
  page,
  reports,
}) => {
  await acceleratedTime(page);
  await prepareGenerate(page, reports);
  await settleFocus(page);
  await expect(generate(page)).toBeEnabled();
  reports.set("POST", reports.data.reportPath("projection") + "/ensure", () => {
    reports.set("GET", reports.data.reportPath("projection"), {
      json: reports.data.projection,
    });
    return { json: reports.data.projection };
  });
  reports.data.context.requestToken = "controlled-refreshed-memory-token";
  const contextsBeforeEnsure = reports.requests.filter(
    (r) => r.path === "/api/v1/catalog/context",
  ).length;
  await generate(page).click();
  await expect(graph(page)).toBeVisible();
  expect(
    reports.requests.filter((r) => r.path === "/api/v1/catalog/context").length,
  ).toBe(contextsBeforeEnsure + 1);
  reports.set("POST", reports.data.reportPath("validation") + "/ensure", () => {
    reports.set("GET", reports.data.reportPath("validation"), {
      json: reports.data.validation,
    });
    return { json: reports.data.validation };
  });
  await panel(page, "validation")
    .getByRole("button", { name: "Generate validation", exact: true })
    .click();
  await expect(fact(panel(page, "validation"), "Validity")).toHaveText(
    "Issues found",
  );
  const posts = [
    ...reports.posts("projection"),
    ...reports.posts("validation"),
  ];
  expect(posts).toHaveLength(2);
  for (const request of posts) {
    expect(request.bodyBytes).toBe(0);
    expect(request.hasToken).toBe(true);
    expect(request.hasTokenHeader).toBe(true);
    expect(request.hasContentType).toBe(false);
    expect(request.hasConditional).toBe(false);
    expect(request.query).toBe("");
  }
  const before = reports.requests.filter(
    (r) => r.path === "/api/v1/catalog/context",
  ).length;
  await panel(page, "projection")
    .getByRole("button", { name: "Read again", exact: true })
    .click();
  await expect(graph(page)).toBeVisible();
  expect(
    reports.requests.filter((r) => r.path === "/api/v1/catalog/context").length,
  ).toBe(before + 1);
  expect(reports.posts("projection")).toHaveLength(1);
  expect(reports.posts("validation")).toHaveLength(1);
  for (const request of reports.requests.filter(
    (r) => r.method === "GET" && r.path.includes("/reports/"),
  )) {
    expect(request.bodyBytes).toBe(0);
    expect(request.hasTokenHeader).toBe(false);
    expect(request.hasContentType).toBe(false);
    expect(request.hasConditional).toBe(false);
  }
  const browserStorage = await page.evaluate(() => ({
    local: localStorage.length,
    session: sessionStorage.length,
  }));
  expect(browserStorage).toEqual({ local: 0, session: 0 });
});

test("controlled pending ensure: exact relative location honors Retry-After before one successful GET observation", async ({
  page,
  reports,
}) => {
  await prepareGenerate(page, reports);
  await page.waitForTimeout(200);
  await expect(generate(page)).toBeEnabled();
  reports.set("POST", reports.data.reportPath("projection") + "/ensure", {
    status: 202,
    json: pending(reports.data.projection),
    headers: {
      Location: reports.data.reportPath("projection"),
      "Retry-After": "2",
    },
  });
  reports.set("GET", reports.data.reportPath("projection"), {
    json: reports.data.projection,
  });
  const before = reports.reads("projection").length;
  await generate(page).click();
  await expect(fact(panel(page, "projection"), "Lifecycle")).toHaveText(
    "In progress",
  );
  expect(reports.reads("projection").length).toBe(before);
  await expect(graph(page)).toBeVisible();
  expect(reports.reads("projection").length).toBe(before + 1);
  expect(
    reports.reads("projection").at(-1)!.observedAt -
      reports.posts("projection")[0].observedAt,
  ).toBeGreaterThanOrEqual(2000);
  await page.waitForTimeout(1200);
  expect(reports.reads("projection").length).toBe(before + 1);
  expect(reports.posts("projection")).toHaveLength(1);
});

test("controlled pending metadata: foreign locations and malformed retry delays are incompatible without polling", async ({
  page,
  reports,
}) => {
  await acceleratedTime(page);
  const path = reports.data.reportPath("projection");
  for (const location of [
    path.replace(
      reports.data.revision.scope.revisionId,
      "40000000-0000-4000-8000-000000000099",
    ),
    reports.data.reportPath("validation"),
    path + "?extra=1",
    new URL(path, process.env.WEFT_REPORTS_BASE_URL).href,
  ]) {
    await prepareGenerate(page, reports);
    await settleFocus(page);
    await expect(generate(page)).toBeEnabled();
    reports.set("POST", path + "/ensure", {
      status: 202,
      json: pending(reports.data.projection),
      headers: { Location: location, "Retry-After": "1" },
    });
    const reads = reports.reads("projection").length;
    const posts = reports.posts("projection").length;
    await generate(page).click();
    await incompatible(page);
    await page.clock.runFor(30000);
    expect(reports.reads("projection").length).toBe(reads);
    expect(reports.posts("projection").length).toBe(posts + 1);
  }
  for (const retry of [
    undefined,
    "0",
    "1.5",
    "Wed, 21 Oct 2026 00:00:00 GMT",
    "2147483648",
  ] as const) {
    await prepareGenerate(page, reports);
    await settleFocus(page);
    await expect(generate(page)).toBeEnabled();
    const headers: Record<string, string> = { Location: path };
    if (retry !== undefined) headers["Retry-After"] = retry;
    reports.set("POST", path + "/ensure", {
      status: 202,
      json: pending(reports.data.projection),
      headers,
    });
    const before = reports.reads("projection").length;
    const posts = reports.posts("projection").length;
    await generate(page).click();
    await incompatible(page);
    await page.clock.runFor(30000);
    expect(reports.reads("projection").length).toBe(before);
    expect(reports.posts("projection").length).toBe(posts + 1);
  }
});

test("controlled observation bounds: long delays stay pending and repeated GET observations stop at five", async ({
  page,
  reports,
}) => {
  const path = reports.data.reportPath("projection");
  await prepareGenerate(page, reports);
  await page.waitForTimeout(200);
  await expect(generate(page)).toBeEnabled();
  reports.set("POST", path + "/ensure", {
    status: 202,
    json: pending(reports.data.projection),
    headers: { Location: path, "Retry-After": "16" },
  });
  let before = reports.reads("projection").length;
  const longDelayPosts = reports.posts("projection").length;
  await generate(page).click();
  await expect(
    panel(page, "projection").getByRole("button", {
      name: "Check again",
      exact: true,
    }),
  ).toBeEnabled();
  // A real observation window also catches a timer incorrectly scheduled beyond the 15-second budget.
  await page.waitForTimeout(16100);
  expect(reports.reads("projection").length).toBe(before);
  expect(reports.posts("projection").length).toBe(longDelayPosts + 1);
  await prepareGenerate(page, reports);
  await page.waitForTimeout(200);
  await expect(generate(page)).toBeEnabled();
  reports.set("POST", path + "/ensure", {
    status: 202,
    json: pending(reports.data.projection),
    headers: { Location: path, "Retry-After": "1" },
  });
  reports.set("GET", path, { json: pending(reports.data.projection) });
  before = reports.reads("projection").length;
  const posts = reports.posts("projection").length;
  await generate(page).click();
  await expect(fact(panel(page, "projection"), "Lifecycle")).toHaveText(
    "In progress",
  );
  for (let count = 1; count <= 5; ++count) {
    await expect
      .poll(() => reports.reads("projection").length, { intervals: [50] })
      .toBe(before + count);
    await expect(fact(panel(page, "projection"), "Lifecycle")).toHaveText(
      "In progress",
    );
  }
  const observations = reports.reads("projection").slice(before);
  for (let index = 0; index < observations.length; ++index) {
    const previous =
      index === 0
        ? reports.posts("projection").at(-1)!.observedAt
        : observations[index - 1].observedAt;
    expect(observations[index].observedAt - previous).toBeGreaterThanOrEqual(
      1000,
    );
  }
  await expect(
    panel(page, "projection").getByRole("button", {
      name: "Check again",
      exact: true,
    }),
  ).toBeEnabled();
  await page.waitForTimeout(1200);
  expect(reports.reads("projection").length).toBe(before + 5);
  expect(reports.posts("projection").length).toBe(posts + 1);
  const contexts = reports.requests.filter(
    (r) => r.path === "/api/v1/catalog/context",
  ).length;
  await panel(page, "projection")
    .getByRole("button", { name: "Check again", exact: true })
    .click();
  await expect(fact(panel(page, "projection"), "Lifecycle")).toHaveText(
    "In progress",
  );
  expect(
    reports.requests.filter((r) => r.path === "/api/v1/catalog/context").length,
  ).toBe(contexts + 1);
  expect(reports.reads("projection").length).toBe(before + 6);
  expect(reports.posts("projection").length).toBe(posts + 1);
});

test("controlled ambiguous cancellation: an aborted ensure reconciles through a fresh GET without retrying the POST", async ({
  page,
  reports,
}) => {
  await acceleratedTime(page);
  await prepareGenerate(page, reports);
  await settleFocus(page);
  await expect(generate(page)).toBeEnabled();
  reports.set("POST", reports.data.reportPath("projection") + "/ensure", {
    abort: true,
  });
  await generate(page).click();
  await expect(panel(page, "projection").getByRole("alert")).toContainText(
    "could not be read",
  );
  await page.clock.runFor(60000);
  expect(reports.posts("projection")).toHaveLength(1);
  reports.set("GET", reports.data.reportPath("projection"), {
    json: reports.data.projection,
  });
  const before = reports.requests.filter(
    (r) => r.path === "/api/v1/catalog/context",
  ).length;
  await panel(page, "projection")
    .getByRole("button", { name: "Read again", exact: true })
    .click();
  await expect(graph(page)).toBeVisible();
  expect(
    reports.requests.filter((r) => r.path === "/api/v1/catalog/context").length,
  ).toBe(before + 1);
  expect(reports.posts("projection")).toHaveLength(1);
});

test("controlled accessibility: keyboard actions and literal evidence remain usable at 390 pixels with persistent gaps", async ({
  page,
  reports,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const literal =
    '<img src="controlled-invalid" onerror="document.documentElement.dataset.reportMarkup=1">';
  reports.data.projection.report!.document!.rules[0].summary = literal;
  await open(page, reports);
  await page.keyboard.press("Tab");
  await expect(
    page.getByRole("link", { name: "Skip to content", exact: true }),
  ).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.locator("#main-content")).toBeFocused();
  await expect(graph(page)).toContainText(literal);
  await expect(graph(page).locator("img")).toHaveCount(0);
  expect(
    await page.locator("html").getAttribute("data-report-markup"),
  ).toBeNull();
  await page
    .getByLabel("Find a field or rule", { exact: true })
    .fill("no-controlled-row-matches");
  await expect(graph(page).getByRole("article")).toHaveCount(0);
  await expect(
    page.getByRole("region", { name: "Explanation incomplete", exact: true }),
  ).toContainText("Controlled ambiguity remains visible");
  await page.getByLabel("Find a field or rule", { exact: true }).fill("");
  await expect(graph(page).getByRole("article")).toHaveCount(1);
  await graph(page)
    .getByText("Expand rule explanation", { exact: true })
    .click();
  await expect(graph(page)).toContainText("9223372036854775807");
  const before = reports.reads("projection").length;
  await panel(page, "projection")
    .getByRole("button", { name: "Read again", exact: true })
    .focus();
  await page.keyboard.press("Enter");
  await expect
    .poll(() => reports.reads("projection").length)
    .toBeGreaterThan(before);
  await expect(graph(page)).toContainText(literal);
  const dimensions = await page.evaluate(() => ({
    width: innerWidth,
    scroll: document.documentElement.scrollWidth,
  }));
  expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.width + 1);
  await metadata(page);
});
