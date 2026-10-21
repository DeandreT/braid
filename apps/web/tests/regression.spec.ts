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
  capabilities = {
    runtime,
    limits,
    comparisonPolicy: policy,
    readSamples: true,
    publishCases: true,
  };
  publications: { id: string; version: string; body: Definition }[] = [];
  submissions: { id: string; body: { caseId: string; version: string } }[] = [];
  published: Definition = structuredClone(definition);
  response = run();
  failPublication = false;
  failSubmission = false;
  reads = 0;
  cancels = 0;
  async install(page: Page) {
    await syntheticCatalog(page);
    await page.route(new RegExp(root + "/regression-"), (route) =>
      this.route(route),
    );
    await page.goto(revisionPath(scope));
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
              caseId: CASE,
              version: "1",
              revision: scope,
              name: "Order fixture",
              createdAtUtc: "2026-10-20T12:00:00Z",
            },
          ],
          nextCaseId: null,
        },
      });
    if (request.method() === "PUT") {
      expect(request.headers()["x-xsrf-token"]).toBe(
        synthetic.context.requestToken,
      );
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
          revision: scope,
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
    }
    return route.fulfill({ json: this.response });
  }
}
const panel = (page: Page) =>
  page.getByRole("region", { name: "Regression tests", exact: true });
async function openCase(page: Page) {
  await panel(page)
    .getByRole("button", { name: "Open Order fixture", exact: true })
    .click();
}

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
  await expect(panel(page)).toContainText("Comparison: Passed");
  expect(fixture.submissions[0].body).toEqual({ caseId: CASE, version: "1" });
  await panel(page)
    .getByRole("button", { name: "View actual primary", exact: true })
    .click();
  await expect(panel(page).locator("pre")).toHaveText("{}");
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
    .getByLabel("Fixture name", { exact: true })
    .fill("Binary fixture");
  await panel(page)
    .getByLabel("Fixed clock (UTC or offset)", { exact: true })
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
    .getByRole("button", { name: "Publish fixture version", exact: true })
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
    .getByLabel("Fixture name", { exact: true })
    .fill("Original fixture");
  await panel(page)
    .getByRole("button", { name: "Publish fixture version", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toBeVisible();
  await expect(
    panel(page).getByLabel("Fixture name", { exact: true }),
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
  await expect(panel(page)).toContainText("Comparison: Passed");
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
  await expect(panel(page)).toContainText("Comparison: Failed");
  await expect(
    panel(page).getByRole("cell", { name: "Bytes Differ", exact: true }),
  ).toBeVisible();
  await expect(panel(page)).not.toContainText("Comparison: Passed");
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
  await expect(panel(page)).not.toContainText("Comparison: Passed");
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
  await expect(panel(page)).not.toContainText("Comparison: Passed");
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
  await expect(panel(page).locator("pre")).toContainText("<img");
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
  await expect(panel(page).locator("pre")).toBeVisible();
  await page.route(`**${root}/regression-runs/${RUN}`, (route) =>
    route.fulfill({ status: 404, json: { code: "notFound" } }),
  );
  await panel(page)
    .getByRole("button", { name: "Refresh result", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toBeVisible();
  await expect(panel(page).locator("pre")).toHaveCount(0);
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
      .getByLabel("Fixture name", { exact: true })
      .fill("Chained fixture");
    await panel(page)
      .getByRole("button", { name: "Publish fixture version", exact: true })
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
  await panel(page)
    .getByLabel("Fixture name", { exact: true })
    .fill("Slow file");
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
      name: "Publish fixture version",
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
    .getByRole("button", { name: "Publish fixture version", exact: true })
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
    await expect(panel(page)).not.toContainText("Comparison: Passed");
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
    .getByLabel("Fixture name", { exact: true })
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
    .getByRole("button", { name: "Publish fixture version", exact: true })
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
    .getByRole("button", { name: "Publish fixture version", exact: true })
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
    await expect(panel(page)).toContainText("Comparison: Passed");
  } finally {
    release();
  }
});
