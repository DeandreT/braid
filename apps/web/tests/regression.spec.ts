import { test, expect, type Page, type Route } from "@playwright/test";
import type { components } from "../src/api/generated";
import { synthetic, syntheticCatalog, revisionPath } from "./catalog.fixture";

type Definition = components["schemas"]["RegressionCasePutV1"];
type Run = components["schemas"]["RegressionRunV1"];
const CASE = "50000000-0000-4000-8000-000000000001";
const RUN = "60000000-0000-4000-8000-000000000001";
const scope = synthetic.revision.scope;
const root = `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}`;
const runtime = {
  backendId: "ferrule",
  engineRevision: "engine",
  backendBuild: "sha256:" + "c".repeat(64),
  adapterBuild: "adapter",
  profile: "weft.ferrule-json-preview.v1",
};
const limits = {
  maximumInputBytes: 8388608,
  maximumOutputBytes: 8388608,
  maximumArtifacts: 64,
  maximumTextBytes: 2048,
};
const policy = "weft.regression.exact-bytes.v1";
const definition: Definition = {
  name: "Order fixture",
  revisionId: scope.revisionId,
  runtime,
  limits,
  comparisonPolicy: policy,
  currentDateTime: "2026-10-20T12:00:00.1234567Z",
  target: { kind: "all", name: null },
  primaryBase64Url: "e30",
  namedInputs: [],
  parameters: [{ name: "count", kind: "integer", value: "9007199254740993" }],
  expected: [{ name: null, bytesBase64Url: "e30" }],
};

function run(state = "completed", outcome = "passed", runId = RUN): Run {
  return {
    run: {
      runId,
      caseId: CASE,
      version: "1",
      revision: scope,
      state,
      attempt: state === "queued" ? 0 : 1,
      createdAtUtc: "2026-10-20T12:00:00Z",
    },
    executionOutcome: state === "completed" ? "completed" : null,
    previewStatus: state === "completed" ? "succeeded" : null,
    outputs:
      state === "completed" ? [{ name: null, bytesBase64Url: "e30" }] : null,
    comparison:
      state === "completed"
        ? {
            policy,
            outcome,
            notEvaluatedReason: "none",
            expectedOutputs: 1,
            actualOutputs: 1,
            totalDifferences: outcome === "passed" ? 0 : 1,
            truncated: false,
            differences:
              outcome === "passed"
                ? []
                : [
                    {
                      name: null,
                      kind: "bytesDiffer",
                      expectedBytes: 3,
                      actualBytes: 2,
                      expectedDigest: "a".repeat(64),
                      actualDigest: "b".repeat(64),
                      firstDifferentByte: 1,
                    },
                  ],
          }
        : null,
  };
}

class RegressionFixture {
  capabilities: components["schemas"]["RegressionCaseCapabilitiesV1"] = {
    runtime,
    limits,
    comparisonPolicy: policy,
    readSamples: true,
    publishCases: true,
  };
  publications: { id: string; version: string; body: Definition }[] = [];
  submissions: { id: string; body: { caseId: string; version: string } }[] = [];
  published: Definition = structuredClone(definition);
  savedId = CASE;
  savedVersion = "1";
  response = run();
  failPublication = false;
  failSubmission = false;
  reads = 0;
  runReads = 0;
  cancels = 0;
  token = synthetic.context.requestToken;
  async install(page: Page) {
    await syntheticCatalog(page);
    await page.route(new RegExp(root + "/regression-"), (route) =>
      this.route(route),
    );
    await page.goto(revisionPath(scope));
    await page.getByRole("tab", { name: "Tests", exact: true }).click();
    await page
      .getByRole("button", { name: "Load regression cases", exact: true })
      .click();
    await expect(
      page.getByRole("button", { name: "Refresh cases", exact: true }),
    ).toBeVisible();
  }
  private async route(route: Route) {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path.endsWith("/capabilities"))
      return route.fulfill({ json: this.capabilities });
    if (request.method() === "GET" && path.endsWith("/regression-cases"))
      return route.fulfill({
        json: {
          cases: [
            {
              caseId: this.savedId,
              version: this.savedVersion,
              revision: scope,
              name: this.published.name,
              createdAtUtc: "2026-10-20T12:00:00Z",
            },
          ],
          nextCaseId: null,
        },
      });
    if (request.method() === "PUT") {
      expect(request.headers()["x-xsrf-token"]).toBe(this.token);
      if (path.includes("/regression-cases/")) {
        expect(request.headers()["content-type"]).toContain(
          "application/vnd.weft.regression-case.v1+json",
        );
        const parts = path.split("/");
        const id = parts.at(-3)!;
        const version = parts.at(-1)!;
        const body = request.postDataJSON() as Definition;
        this.publications.push({ id, version, body });
        this.published = body;
        this.savedId = id;
        this.savedVersion = version;
        if (this.failPublication) {
          this.failPublication = false;
          return route.abort("failed");
        }
        return route.fulfill({
          status: 201,
          json: {
            caseId: id,
            version,
            revision: scope,
            outcome: version === "1" ? "created" : "appended",
          },
        });
      }
      const id = path.split("/").at(-1)!;
      const body = request.postDataJSON() as {
        caseId: string;
        version: string;
      };
      this.submissions.push({ id, body });
      this.response = run("completed", "passed", id);
      if (this.failSubmission) {
        this.failSubmission = false;
        return route.abort("failed");
      }
      return route.fulfill({
        status: this.submissions.length === 1 ? 201 : 200,
        json: {
          runId: id,
          outcome: this.submissions.length === 1 ? "created" : "replayed",
        },
      });
    }
    if (path.includes("/versions/")) {
      this.reads++;
      const definition = structuredClone(this.published);
      const compare = (
        a: { name: string | null },
        b: { name: string | null },
      ) =>
        (a.name || "") < (b.name || "")
          ? -1
          : (a.name || "") === (b.name || "")
            ? 0
            : 1;
      definition.namedInputs.sort(compare);
      definition.parameters.sort(compare);
      definition.expected.sort(compare);
      return route.fulfill({
        json: {
          caseId: path.split("/").at(-3),
          version: path.split("/").at(-1),
          revision: { ...scope, revisionId: definition.revisionId },
          definition,
        },
      });
    }
    if (path.endsWith("/regression-runs"))
      return route.fulfill({
        json: { runs: [this.response.run], nextRunId: null },
      });
    if (path.endsWith("/cancel")) {
      this.cancels++;
      this.response = run("canceled");
    } else this.runReads++;
    return route.fulfill({ json: this.response });
  }
}
test("document regression saves explicit media and comparison options without filename inference", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.capabilities.documentRuntime = {
    ...runtime,
    profile: "weft.ferrule-document-preview.v1",
    adapterBuild: "document-adapter",
  };
  fixture.capabilities.comparisonPolicies = [
    policy,
    "weft.regression.edi-segments.v1",
  ];
  await fixture.install(page);
  await enterDraft(page);
  await panel(page)
    .getByLabel("Execution profile", { exact: true })
    .selectOption({
      label: "Declared document formats (JSON / X12 / EDIFACT)",
    });
  await panel(page)
    .getByLabel("Comparison policy", { exact: true })
    .selectOption("weft.regression.edi-segments.v1");
  await panel(page)
    .getByLabel(
      "Ignore supported envelope and acknowledgement control references",
      { exact: true },
    )
    .check();
  await panel(page)
    .getByLabel("Expected media type", { exact: true })
    .selectOption("application/edi-x12");
  const output = panel(page).locator(".regression-document").nth(1);
  await output
    .getByLabel("Expected UTF-8 text")
    .fill(
      "ISA*00*          *00*          *ZZ*WEFTSENDER     *ZZ*WEFTRECEIVER   *261024*1200*^*00501*000000330*0*T*:~GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*330*X*005010~ST*850*0330~BEG*00*SA*WEFT-330~SE*3*0330~GE*1*330~IEA*1*000000330~",
    );
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect.poll(() => fixture.publications.length).toBe(1);
  const saved = fixture.publications[0].body;
  expect(saved.runtime).toEqual(fixture.capabilities.documentRuntime);
  expect(saved.comparisonOptions).toEqual({
    compareSeparators: true,
    ignoreControlNumbers: true,
    ignoreDates: false,
  });
  expect(saved.expected[0].mediaType).toBe("application/edi-x12");
  expect(saved.namedInputs).toEqual([]);
  await expect(
    panel(page).getByRole("heading", { name: "Expected outputs", exact: true }),
  ).toBeVisible();
  await panel(page)
    .getByRole("button", { name: "Edit as next version", exact: true })
    .click();
  await expect(
    panel(page).getByLabel("Expected media type", { exact: true }),
  ).toHaveValue("application/edi-x12");
  await expect(
    panel(page).getByLabel(
      "Ignore supported envelope and acknowledgement control references",
      { exact: true },
    ),
  ).toBeChecked();
});
const panel = (page: Page) =>
  page.getByRole("region", { name: "Regression tests", exact: true });

test("document comparison choices survive same-owner draft recovery", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.capabilities.documentRuntime = {
    ...runtime,
    profile: "weft.ferrule-document-preview.v1",
    adapterBuild: "document-adapter",
  };
  fixture.capabilities.comparisonPolicies = [
    policy,
    "weft.regression.edi-segments.v1",
  ];
  await fixture.install(page);
  await enterDraft(page);
  await panel(page)
    .getByLabel("Execution profile", { exact: true })
    .selectOption({
      label: "Declared document formats (JSON / X12 / EDIFACT)",
    });
  await panel(page)
    .getByLabel("Comparison policy", { exact: true })
    .selectOption("weft.regression.edi-segments.v1");
  await panel(page)
    .getByLabel("Compare service separators", { exact: true })
    .uncheck();
  await panel(page)
    .getByLabel("Ignore supported envelope and date/time elements", {
      exact: true,
    })
    .check();
  await panel(page)
    .getByLabel("Expected media type", { exact: true })
    .selectOption("application/edifact");
  await panel(page)
    .locator(".regression-document")
    .nth(1)
    .getByLabel("Expected UTF-8 text")
    .fill(
      "UNB+UNOC:3+WEFTSENDER+WEFTRECEIVER+261024:1200+330'UNH+330+ORDERS:D:96A:UN'BGM+220+WEFT-330'UNT+3+330'UNZ+1+330'",
    );
  await failSessionCheck(page, "unavailable");
  await recoverAccess(page);
  await panel(page)
    .getByRole("button", { name: "Load regression cases", exact: true })
    .click();
  await expect(
    panel(page).getByLabel("Comparison policy", { exact: true }),
  ).toHaveValue("weft.regression.edi-segments.v1");
  await expect(
    panel(page).getByLabel("Compare service separators", { exact: true }),
  ).not.toBeChecked();
  await expect(
    panel(page).getByLabel("Ignore supported envelope and date/time elements", {
      exact: true,
    }),
  ).toBeChecked();
  await expect(
    panel(page).getByLabel("Expected media type", { exact: true }),
  ).toHaveValue("application/edifact");
  expect(fixture.publications).toEqual([]);
});

test("document regression renders bounded segment locations and invalid-document diagnostics", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.response = run("completed", "failed");
  fixture.response.comparison!.policy = "weft.regression.edi-segments.v1";
  const difference = fixture.response.comparison!.differences[0];
  difference.kind = "segmentsDiffer";
  difference.firstDifferentByte = null;
  difference.location = {
    interchange: 2,
    segment: 9,
    segmentId: "BEG",
    element: 3,
    repetition: 1,
    component: 1,
  };
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await expect(panel(page)).toContainText(
    "Interchange 2 · segment 9 BEG · element 3 · repetition 1 · component 1",
  );
  fixture.response.comparison!.outcome = "notEvaluated";
  fixture.response.comparison!.notEvaluatedReason = "invalidDocument";
  fixture.response.comparison!.differences = [];
  fixture.response.comparison!.totalDifferences = 0;
  fixture.response.comparison!.documentDiagnostic = {
    name: null,
    side: "actual",
    location: {
      interchange: 1,
      segment: 7,
      segmentId: "IEA",
      element: 2,
      repetition: 0,
      component: 0,
    },
  };
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await expect(panel(page)).toContainText(
    "Invalid actual document: Primary · interchange 1 · segment 7 IEA · element 2",
  );
});
/** The revision page's in-place re-read has finished. */
const settled = (page: Page) =>
  expect(page.locator("weft-workbench-frame")).not.toHaveAttribute(
    "aria-busy",
    "true",
  );
/** The revision page's own catalog read of the displayed revision. */
const revisionRead = (page: Page) =>
  page.waitForResponse(
    (response) =>
      response.request().method() === "GET" &&
      new URL(response.url()).pathname ===
        `${root}/revisions/${scope.revisionId}`,
  );
async function openCase(page: Page) {
  await panel(page)
    .getByRole("button", { name: "Open Order fixture", exact: true })
    .click();
}

test("another revision's case is linked rather than displayed under the pinned revision", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  const other = "40000000-0000-4000-8000-000000000099";
  fixture.published.revisionId = other;
  await fixture.install(page);
  await openCase(page);
  await expect(panel(page)).toContainText(
    "This evidence belongs to another pinned revision",
  );
  await expect(
    panel(page).getByRole("link", { name: "Open that revision" }),
  ).toHaveAttribute("href", revisionPath({ ...scope, revisionId: other }));
  await expect(
    panel(page).getByRole("button", { name: "Run saved version", exact: true }),
  ).toHaveCount(0);
  await expect(panel(page)).not.toContainText("9007199254740993");
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await expect(panel(page)).not.toContainText(
    "This evidence belongs to another pinned revision",
  );
});

test("another revision's run cannot replace the selected case or expose its outputs", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.response.run.revision = {
    ...scope,
    revisionId: "40000000-0000-4000-8000-000000000099",
  };
  await fixture.install(page);
  await openCase(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await expect(panel(page)).toContainText(
    "This evidence belongs to another pinned revision",
  );
  await expect(
    panel(page).getByRole("heading", {
      name: "Regression run result",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(fixture.reads).toBe(1);
});

test("a mismatched revision response retains the original uncertain run request", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.failSubmission = true;
  await fixture.install(page);
  await openCase(page);
  await panel(page)
    .getByRole("button", { name: "Run saved version", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", {
      name: "Check original run",
      exact: true,
    }),
  ).toBeEnabled();
  fixture.response.run.revision = {
    ...scope,
    revisionId: "40000000-0000-4000-8000-000000000099",
  };
  await panel(page)
    .getByRole("button", { name: "Check original run", exact: true })
    .click();
  await expect(panel(page)).toContainText(
    "The original request is retained for recovery.",
  );
  await expect(
    panel(page).getByRole("button", {
      name: "Retry original run request",
      exact: true,
    }),
  ).toBeVisible();
  expect(fixture.submissions).toHaveLength(1);
});

test("saved fixture keeps lexical values and executes its exact version", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await openCase(page);
  await expect(panel(page)).toContainText("9007199254740993");
  await expect(panel(page)).toContainText("2026-10-20T12:00:00.1234567Z");
  await panel(page)
    .getByRole("button", { name: "Run saved version", exact: true })
    .click();
  await expect(panel(page)).toContainText("Matches expected result");
  expect(fixture.submissions[0].body).toEqual({ caseId: CASE, version: "1" });
  await panel(page)
    .getByRole("button", { name: "View actual primary", exact: true })
    .click();
  await expect(
    page
      .getByRole("region", { name: "Document bytes", exact: true })
      .locator("pre"),
  ).toHaveText("{}");
});

test("a saved fixture refreshes the case table without a manual reload", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await panel(page)
    .getByLabel("Test name", { exact: true })
    .fill("Freshly saved case");
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect(
    panel(page).getByRole("table", { name: "Saved regression cases" }),
  ).toContainText("Freshly saved case");
  await expect(
    panel(page).getByRole("heading", {
      name: "Freshly saved case · Version 1",
      exact: true,
    }),
  ).toBeVisible();
  expect(fixture.publications).toHaveLength(1);
});

test("recovering a new fixture clears a previously selected run from another case", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await openCase(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Regression run result",
      exact: true,
    }),
  ).toBeVisible();
  fixture.failPublication = true;
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await panel(page)
    .getByLabel("Test name", { exact: true })
    .fill("Recovered new case");
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", {
      name: "Check saved version",
      exact: true,
    }),
  ).toBeEnabled();
  await panel(page)
    .getByRole("button", { name: "Check saved version", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Recovered new case · Version 1",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("heading", {
      name: "Regression run result",
      exact: true,
    }),
  ).toHaveCount(0);
});

test("viewer loads metadata without touching samples or run history", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.capabilities.readSamples = false;
  fixture.capabilities.publishCases = false;
  await fixture.install(page);
  await expect(panel(page)).toContainText("Order fixture");
  await expect(panel(page)).toContainText("Metadata only");
  await expect(
    panel(page).getByRole("button", { name: "Open Order fixture" }),
  ).toHaveCount(0);
  await expect(
    panel(page).getByRole("button", { name: "New test case" }),
  ).toHaveCount(0);
  await expect(
    panel(page).getByRole("button", { name: "Load run history" }),
  ).toHaveCount(0);
  expect(fixture.reads).toBe(0);
});

test("file publication preserves binary bytes and named inputs", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await panel(page)
    .getByLabel("Test name", { exact: true })
    .fill("Binary fixture");
  await panel(page)
    .getByLabel("Test date and time (UTC or offset)", { exact: true })
    .fill("2026-10-20T12:00:00Z");
  const files = panel(page).locator('input[type="file"]');
  await files.nth(0).setInputFiles({
    name: "sample.bin",
    mimeType: "application/octet-stream",
    buffer: Buffer.from([0, 255, 128, 10]),
  });
  await files.nth(1).setInputFiles({
    name: "expected.bin",
    mimeType: "application/octet-stream",
    buffer: Buffer.from([255, 0, 1]),
  });
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect(panel(page)).toContainText(
    "The exact fixture version is saved.",
  );
  expect(fixture.publications[0].body.primaryBase64Url).toBe("AP-ACg");
  expect(fixture.publications[0].body.expected[0].bytesBase64Url).toBe("_wAB");
  expect(fixture.publications[0].body.revisionId).toBe(scope.revisionId);
  const stores = await page.evaluate(() => [
    JSON.stringify(localStorage),
    JSON.stringify(sessionStorage),
  ]);
  expect(stores.join(" ")).not.toContain("AP-ACg");
});

test("lost publication response retries the same immutable request", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.failPublication = true;
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await panel(page)
    .getByLabel("Test name", { exact: true })
    .fill("Original fixture");
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toBeVisible();
  await expect(
    panel(page).getByLabel("Test name", { exact: true }),
  ).toBeDisabled();
  await panel(page)
    .getByRole("button", { name: "Retry original publication", exact: true })
    .click();
  await expect.poll(() => fixture.publications.length).toBe(2);
  expect(fixture.publications[1]).toEqual(fixture.publications[0]);
});

test("lost run response can be recovered without submitting again", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.failSubmission = true;
  await fixture.install(page);
  await openCase(page);
  await panel(page)
    .getByRole("button", { name: "Run saved version", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toBeVisible();
  await panel(page)
    .getByRole("button", { name: "Check original run", exact: true })
    .click();
  await expect(panel(page)).toContainText("Matches expected result");
  expect(fixture.submissions).toHaveLength(1);
});

test("failed comparison displays a byte offset rather than a passing result", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.response = run("completed", "failed");
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await expect(panel(page)).toContainText("Result differs from expected");
  await expect(
    panel(page).getByRole("cell", { name: "Bytes Differ", exact: true }),
  ).toBeVisible();
  await expect(panel(page)).not.toContainText("Matches expected result");
});

test("running case can be canceled without fabricating a comparison", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.response = run("running");
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Cancel run", exact: true })
    .click();
  await expect(panel(page)).toContainText("Canceled");
  expect(fixture.cancels).toBe(1);
  await expect(panel(page)).not.toContainText("Matches expected result");
});

test("a malformed passing response is rejected", async ({ page }) => {
  const fixture = new RegressionFixture();
  fixture.response.comparison!.totalDifferences = 1;
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText(
    "could not be verified",
  );
  await expect(panel(page)).not.toContainText("Matches expected result");
});

test("output text is never interpreted as HTML", async ({ page }) => {
  const fixture = new RegressionFixture();
  fixture.response.outputs![0].bytesBase64Url = Buffer.from(
    '<img src=x onerror="window.injected=true">',
  ).toString("base64url");
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "View actual primary", exact: true })
    .click();
  await expect(
    page
      .getByRole("region", { name: "Document bytes", exact: true })
      .locator("pre"),
  ).toContainText("<img");
  await expect(panel(page).locator("pre img")).toHaveCount(0);
});

test("same-session authority refusal clears cached samples and download controls", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await openCase(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "View actual primary", exact: true })
    .click();
  await expect(
    page
      .getByRole("region", { name: "Document bytes", exact: true })
      .locator("pre"),
  ).toBeVisible();
  await page.route(`**${root}/regression-runs/${RUN}`, (route) =>
    route.fulfill({ status: 404, json: { code: "notFound" } }),
  );
  await panel(page)
    .getByRole("button", { name: "Refresh result", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Document bytes", exact: true }),
  ).toHaveCount(0);
  await expect(
    panel(page).getByRole("button", { name: /^Download/ }),
  ).toHaveCount(0);
  await expect(panel(page)).not.toContainText("9007199254740993");
});

test("publication remains busy until its chained case read finishes", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let entered = false;
  await page.route(
    new RegExp(root + "/regression-cases/.+/versions/"),
    async (route) => {
      if (route.request().method() !== "GET") return route.fallback();
      entered = true;
      await gate;
      await route.fallback();
    },
  );
  try {
    await panel(page)
      .getByRole("button", { name: "New test case", exact: true })
      .click();
    await panel(page)
      .getByLabel("Test name", { exact: true })
      .fill("Chained fixture");
    await panel(page)
      .getByRole("button", { name: "Save test version", exact: true })
      .click();
    await expect.poll(() => entered).toBe(true);
    await expect(
      panel(page).getByRole("button", { name: "New test case", exact: true }),
    ).toBeDisabled();
    release();
    await expect(
      panel(page).getByRole("heading", {
        name: "Chained fixture · Version 1",
        exact: true,
      }),
    ).toBeVisible();
  } finally {
    release();
  }
});

test("publication waits for all selected file bytes", async ({ page }) => {
  await page.addInitScript(() => {
    const original = Blob.prototype.arrayBuffer;
    Blob.prototype.arrayBuffer = function () {
      if (!(this instanceof File) || this.name !== "slow.bin")
        return original.call(this);
      return new Promise<ArrayBuffer>((resolve, reject) => {
        (
          window as Window & { releaseFixtureFile?: () => void }
        ).releaseFixtureFile = () => {
          original.call(this).then(resolve, reject);
        };
      });
    };
  });
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await panel(page).getByLabel("Test name", { exact: true }).fill("Slow file");
  await panel(page)
    .locator('input[type="file"]')
    .first()
    .setInputFiles({
      name: "slow.bin",
      mimeType: "application/octet-stream",
      buffer: Buffer.from([0, 255, 1]),
    });
  await expect(panel(page)).toContainText("Reading selected files");
  await expect(
    panel(page).getByRole("button", {
      name: "Save test version",
      exact: true,
    }),
  ).toBeDisabled();
  await page.evaluate(() =>
    (
      window as Window & { releaseFixtureFile?: () => void }
    ).releaseFixtureFile?.(),
  );
  await expect(panel(page)).toContainText("slow.bin · 3 exact bytes");
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect.poll(() => fixture.publications.length).toBe(1);
  expect(fixture.publications[0].body.primaryBase64Url).toBe("AP8B");
});

for (const defect of [
  "emptyBaseline",
  "outputCount",
  "arrayState",
  "arrayOutcome",
]) {
  test(`incompatible ${defect} never renders a passing verdict`, async ({
    page,
  }) => {
    const fixture = new RegressionFixture();
    const response = fixture.response as unknown as {
      run: { state: unknown };
      comparison: {
        expectedOutputs: number;
        actualOutputs: number;
        outcome: unknown;
      };
      outputs: unknown[];
    };
    if (defect === "emptyBaseline") {
      response.comparison.expectedOutputs = 0;
      response.comparison.actualOutputs = 0;
      response.outputs = [];
    }
    if (defect === "outputCount") response.outputs = [];
    if (defect === "arrayState") response.run.state = ["completed"];
    if (defect === "arrayOutcome") response.comparison.outcome = ["passed"];
    await fixture.install(page);
    await panel(page)
      .getByRole("button", { name: "Load run history", exact: true })
      .click();
    if (defect !== "arrayState")
      await panel(page)
        .getByRole("button", { name: `Open run ${RUN}`, exact: true })
        .click();
    await expect(panel(page).getByRole("alert")).toContainText(
      "could not be verified",
    );
    await expect(panel(page)).not.toContainText("Matches expected result");
  });
}

test("publication recovery accepts canonical server collection ordering", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.failPublication = true;
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await panel(page)
    .getByLabel("Test name", { exact: true })
    .fill("Ordered fixture");
  await panel(page)
    .getByRole("button", { name: "Add named input", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Add named input", exact: true })
    .click();
  await panel(page)
    .getByLabel("Document name", { exact: true })
    .nth(0)
    .fill("zeta");
  await panel(page)
    .getByLabel("Document name", { exact: true })
    .nth(1)
    .fill("alpha");
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toBeVisible();
  await panel(page)
    .getByRole("button", { name: "Check saved version", exact: true })
    .click();
  await expect(panel(page)).toContainText(
    "The original saved fixture was recovered.",
  );
  await expect(panel(page)).not.toContainText("differs from the original");
});

test("next version preserves offset nanoseconds and intentional invalid parameter spelling", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.published.currentDateTime = "2026-10-20T12:00:00.123456789-07:00";
  fixture.published.parameters[0].value = "intentionally-invalid";
  await fixture.install(page);
  await openCase(page);
  await panel(page)
    .getByRole("button", { name: "Edit as next version", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect.poll(() => fixture.publications.length).toBe(1);
  expect(fixture.publications[0].version).toBe("2");
  expect(fixture.publications[0].body.currentDateTime).toBe(
    "2026-10-20T12:00:00.123456789-07:00",
  );
  expect(fixture.publications[0].body.parameters[0]).toEqual({
    name: "count",
    kind: "integer",
    value: "intentionally-invalid",
  });
});

const workspaceAccess = (page: Page) =>
  page.getByRole("alert").filter({
    has: page.getByRole("heading", {
      name: "Workspace access unavailable",
      exact: true,
    }),
  });

/** A background focus refresh whose session status observation fails once. */
async function failSessionCheck(
  page: Page,
  failure: "unavailable" | "timeout",
) {
  await page.route(
    "**/api/auth/session*",
    (route) =>
      failure === "timeout"
        ? route.abort("timedout")
        : route.fulfill({ status: 503, json: { code: "unavailable" } }),
    { times: 1 },
  );
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(workspaceAccess(page)).toBeVisible();
  await expect(panel(page)).toHaveCount(0);
}

/** The shell Retry restores status and context only; the revision read and panel load stay explicit. */
async function recoverAccess(page: Page) {
  await workspaceAccess(page)
    .getByRole("button", { name: "Retry", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Catalog unavailable", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await page.getByRole("tab", { name: "Tests", exact: true }).click();
  await expect(
    panel(page).getByRole("button", {
      name: "Load regression cases",
      exact: true,
    }),
  ).toBeVisible();
}

async function enterDraft(page: Page) {
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await panel(page)
    .getByLabel("Test name", { exact: true })
    .fill("Unsaved west order");
  await panel(page)
    .getByLabel("Test date and time (UTC or offset)", { exact: true })
    .fill("2026-10-20T12:00:00Z");
  const documents = panel(page).locator(".regression-document");
  await documents.nth(0).getByLabel("Input UTF-8 text").fill('{"order":1}');
  await documents.nth(1).getByLabel("Expected UTF-8 text").fill('{"ok":1}');
  await panel(page)
    .getByRole("button", { name: "Add parameter", exact: true })
    .click();
  const parameter = panel(page).locator(".regression-parameter");
  await parameter.getByLabel("Parameter name").fill("region");
  await parameter.getByLabel("Exact parameter value").fill("west");
}

async function expectDraft(page: Page) {
  await expect(
    panel(page).getByLabel("Test name", { exact: true }),
  ).toHaveValue("Unsaved west order");
  await expect(
    panel(page).getByLabel("Test date and time (UTC or offset)", {
      exact: true,
    }),
  ).toHaveValue("2026-10-20T12:00:00Z");
  const documents = panel(page).locator(".regression-document");
  await expect(documents.nth(0).getByLabel("Input UTF-8 text")).toHaveValue(
    '{"order":1}',
  );
  await expect(documents.nth(1).getByLabel("Expected UTF-8 text")).toHaveValue(
    '{"ok":1}',
  );
  const parameter = panel(page).locator(".regression-parameter");
  await expect(parameter.getByLabel("Parameter name")).toHaveValue("region");
  await expect(parameter.getByLabel("Exact parameter value")).toHaveValue(
    "west",
  );
}

for (const failure of ["unavailable", "timeout"] as const) {
  test(`a draft survives a session check that is ${failure}`, async ({
    page,
  }) => {
    const fixture = new RegressionFixture();
    await fixture.install(page);
    await enterDraft(page);
    await failSessionCheck(page, failure);
    await recoverAccess(page);
    // The kept draft is announced but stays hidden until the panel's own fresh admission.
    await expect(panel(page)).toContainText(
      "An unsaved test case draft is kept in this tab",
    );
    await expect(
      panel(page).getByLabel("Test name", { exact: true }),
    ).toHaveCount(0);
    await panel(page)
      .getByRole("button", { name: "Load regression cases", exact: true })
      .click();
    await expectDraft(page);
    await panel(page)
      .getByRole("button", { name: "Save test version", exact: true })
      .click();
    await expect.poll(() => fixture.publications.length).toBe(1);
    const sent = fixture.publications[0].body;
    expect(sent.name).toBe("Unsaved west order");
    expect(sent.parameters).toEqual([
      { name: "region", kind: "string", value: "west" },
    ]);
  });
}

test("a successful background refresh keeps the panel and its draft in place", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await enterDraft(page);
  const reread = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/v1/catalog/context",
  );
  const revision = revisionRead(page);
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  expect((await reread).status()).toBe(200);
  expect((await revision).status()).toBe(200);
  // The revision page's own re-read has finished, not only the context read.
  await settled(page);
  // A same-owner re-read leaves the loaded panel and its draft on screen.
  await expectDraft(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Load regression cases",
      exact: true,
    }),
  ).toHaveCount(0);
});

/** Holds the next GET of `path` until released. */
async function holdRead(page: Page, path: string, reply: () => unknown) {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let arrive!: () => void;
  const arrived = new Promise<void>((resolve) => {
    arrive = resolve;
  });
  await page.route(
    `**${path}`,
    async (route) => {
      arrive();
      await gate;
      await route.fulfill({ json: reply() });
    },
    { times: 1 },
  );
  return { release, arrived };
}

test("a run clicked during a routine refresh waits for the fresh context and sends with its token", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await openCase(page);
  const start = panel(page).getByRole("button", {
    name: "Run saved version",
    exact: true,
  });
  const fresh = "synthetic-ui-only-fresh-token";
  const held = await holdRead(page, "/api/v1/catalog/context", () => ({
    ...synthetic.context,
    requestToken: fresh,
  }));
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  await held.arrived;
  try {
    // The session check has settled; the panel's catalog admission is still being read again.
    await start.click();
    await expect(start).toBeDisabled();
    await expect(
      panel(page).getByRole("heading", {
        name: "Check previous run",
        exact: true,
      }),
    ).toHaveCount(0);
    expect(fixture.submissions).toHaveLength(0);
    fixture.token = fresh;
  } finally {
    held.release();
  }
  await expect(panel(page)).toContainText("Matches expected result");
  expect(fixture.submissions).toHaveLength(1);
  await expect(start).toBeEnabled();
});

test("a run sent before the page's own re-read finishes uses the fresh context's token", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await openCase(page);
  const fresh = "synthetic-ui-only-fresh-token";
  await page.route(
    "**/api/v1/catalog/context",
    (route) =>
      route.fulfill({ json: { ...synthetic.context, requestToken: fresh } }),
    { times: 1 },
  );
  const held = await holdRead(
    page,
    `${root}/revisions/${scope.revisionId}`,
    () => synthetic.revision,
  );
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  await held.arrived;
  try {
    fixture.token = fresh;
    await panel(page)
      .getByRole("button", { name: "Run saved version", exact: true })
      .click();
    await expect(panel(page)).toContainText("Matches expected result");
    expect(fixture.submissions).toHaveLength(1);
  } finally {
    held.release();
  }
  await settled(page);
});

test("result polling waits out a routine check without taking keyboard focus", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.response = run("running");
  await fixture.install(page);
  const history = panel(page).getByRole("button", {
    name: "Load run history",
    exact: true,
  });
  await history.click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  await expect.poll(() => fixture.runReads).toBeGreaterThan(0);
  await expect(
    panel(page).getByRole("heading", {
      name: "Regression run result",
      exact: true,
    }),
  ).toBeVisible();
  await expect(history).toBeEnabled();
  await history.focus();
  await expect(history).toBeFocused();
  const held = await holdRead(page, "/api/auth/session", () => ({
    version: 1,
    mode: "development",
  }));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await held.arrived;
  try {
    // The two-second poll comes due during the check and skips its turn.
    const reads = fixture.runReads;
    await page.waitForTimeout(3000);
    await expect(
      history,
      await page.evaluate(
        () =>
          document.activeElement?.outerHTML.slice(0, 500) ??
          "no active element",
      ),
    ).toBeFocused();
    await expect(history).toBeEnabled();
    expect(fixture.runReads).toBe(reads);
    fixture.response = run("completed", "passed");
  } finally {
    held.release();
  }
  await expect(panel(page)).toContainText("Matches expected result");
});

test("an uncertain publication keeps its original request across a failed session check", async ({
  page,
}) => {
  const fixture = new RegressionFixture();
  fixture.failPublication = true;
  await fixture.install(page);
  await enterDraft(page);
  await panel(page)
    .getByRole("button", { name: "Save test version", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", { name: "Check previous save" }),
  ).toBeVisible();
  await failSessionCheck(page, "unavailable");
  await recoverAccess(page);
  await panel(page)
    .getByRole("button", { name: "Load regression cases", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", { name: "Check previous save" }),
  ).toBeVisible();
  await expect(
    panel(page).getByLabel("Test name", { exact: true }),
  ).toBeDisabled();
  await panel(page)
    .getByRole("button", { name: "Retry original publication", exact: true })
    .click();
  await expect.poll(() => fixture.publications.length).toBe(2);
  expect(fixture.publications[1]).toEqual(fixture.publications[0]);
});

test("a confirmed owner change clears the unsent draft", async ({ page }) => {
  const fixture = new RegressionFixture();
  await fixture.install(page);
  await enterDraft(page);
  await failSessionCheck(page, "unavailable");
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({
      json: {
        ...synthetic.context,
        actorPrincipalId: "10000000-0000-4000-8000-000000000099",
      },
    }),
  );
  await recoverAccess(page);
  await expect(panel(page)).not.toContainText("draft is kept");
  await panel(page)
    .getByRole("button", { name: "Load regression cases", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", { name: "Refresh cases", exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByLabel("Test name", { exact: true }),
  ).toHaveCount(0);
  await panel(page)
    .getByRole("button", { name: "New test case", exact: true })
    .click();
  await expect(
    panel(page).getByLabel("Test name", { exact: true }),
  ).toHaveValue("");
  await expect(panel(page)).not.toContainText('{"order":1}');
});

test("active result polling resumes after a slower history request", async ({
  page,
}) => {
  await page.clock.install();
  const fixture = new RegressionFixture();
  fixture.response = run("running");
  await fixture.install(page);
  await panel(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: `Open run ${RUN}`, exact: true })
    .click();
  let release!: () => void;
  let entered = false;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(`**${root}/regression-runs?limit=20`, async (route) => {
    entered = true;
    await gate;
    await route.fallback();
  });
  try {
    await panel(page)
      .getByRole("button", { name: "Load run history", exact: true })
      .click();
    await expect.poll(() => entered).toBe(true);
    await page.clock.fastForward(2500);
    fixture.response = run();
    release();
    await expect(
      panel(page).getByRole("button", {
        name: "Load run history",
        exact: true,
      }),
    ).toBeEnabled();
    await page.clock.fastForward(2500);
    await expect(panel(page)).toContainText("Matches expected result");
  } finally {
    release();
  }
});
