import type { Locator, Page, Request, Response } from "@playwright/test";
import { isSessionStatusRequest } from "./browser-session-status.fixture";
import {
  expect,
  expectReport,
  graph,
  panel,
  pinnedPath,
  reportApi,
  revisionApi,
  revisionPath,
  test,
  type Operation,
  type ProjectionReport,
  type Report,
  type ReportsOwner,
  type Revision,
  type ValidationReport,
} from "./revision-reports-real.fixture";

interface ExpectedRequest {
  readonly path: string;
  readonly method?: "GET" | "POST";
  readonly query?: Readonly<Record<string, string>>;
  readonly status?: readonly number[];
}
interface Exchange<T = unknown> {
  readonly status: number;
  readonly body: T;
  readonly location: string | null;
  readonly retryAfter: string | null;
}

// Arm every request before the action; admit only its fresh Request object,
// exact same origin/path/method and the complete query multiplicity.
async function captureMany(
  page: Page,
  expected: readonly ExpectedRequest[],
  action: () => Promise<unknown>,
): Promise<Exchange[]> {
  const origin = process.env.BRAID_REPORTS_BASE_URL;
  if (!origin) throw new Error("Expected explicit packaged reports origin");
  const requests = new Map<Request, number>();
  const observe = (request: Request) => {
    const url = new URL(request.url());
    expected.forEach((item, index) => {
      const query = item.query ?? {};
      let count = 0;
      url.searchParams.forEach(() => count++);
      if (
        request.method() === (item.method ?? "GET") &&
        url.origin === origin &&
        url.pathname === item.path &&
        count === Object.keys(query).length &&
        Object.entries(query).every(
          ([key, value]) =>
            url.searchParams.getAll(key).length === 1 &&
            url.searchParams.get(key) === value,
        )
      )
        requests.set(request, index);
    });
  };
  page.on("request", observe);
  try {
    const replies = Promise.all(
      expected.map((item, index) =>
        page
          .waitForResponse(
            (response) => requests.get(response.request()) === index,
            { timeout: item.method === "POST" ? 50_000 : 20_000 },
          )
          .then(async (response) => {
            expect(item.status ?? [200]).toContain(response.status());
            // Read the response body as soon as it arrives, concurrently with UI work.
            const body: unknown = await response.json();
            return {
              status: response.status(),
              body,
              location: await response.headerValue("location"),
              retryAfter: await response.headerValue("retry-after"),
            };
          }),
      ),
    );
    const [values] = await Promise.all([replies, action()]);
    return values;
  } finally {
    page.off("request", observe);
  }
}
async function capture<T>(
  page: Page,
  expected: ExpectedRequest,
  action: () => Promise<unknown>,
): Promise<Exchange<T>> {
  return (await captureMany(page, [expected], action))[0] as Exchange<T>;
}
async function captureMetadataRefusal(
  page: Page,
  revision: Revision,
  action: () => Promise<unknown>,
): Promise<unknown> {
  const base = revisionApi(revision);
  const mapping = base.slice(0, base.indexOf("/revisions/"));
  const project = mapping.slice(0, mapping.indexOf("/mappings/"));
  const paths = [project, mapping, base];
  const requests = new Set<Request>();
  const observe = (request: Request) => {
    const url = new URL(request.url());
    if (
      request.method() === "GET" &&
      url.origin === process.env.BRAID_REPORTS_BASE_URL &&
      paths.includes(url.pathname) &&
      url.search === ""
    )
      requests.add(request);
  };
  page.on("request", observe);
  try {
    // Any one of the three parallel metadata reads can be the first refusal;
    // the client correctly cancels its siblings after that response.
    const body = page
      .waitForResponse(
        (response) =>
          requests.has(response.request()) && response.status() === 404,
      )
      .then((response) => response.json());
    const [value] = await Promise.all([body, action()]);
    return value;
  } finally {
    page.off("request", observe);
  }
}
const fact = (region: Locator, label: string) =>
  region
    .locator("dt", { hasText: new RegExp(`^${label}$`) })
    .locator("xpath=following-sibling::dd[1]");

async function expectRevision(page: Page, revision: Revision): Promise<void> {
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    `Revision ${revision.sequence}`,
  );
  const details = page.getByRole("region", {
    name: "Revision details",
    exact: true,
  });
  await expect(fact(details, "Revision ID")).toHaveText(
    revision.scope.revisionId,
  );
  await expect(fact(details, "Sequence")).toHaveText(revision.sequence);
  await expect(fact(details, "Native digest")).toContainText(
    revision.nativeDigest.value,
  );
  await expect(fact(details, "Original filename")).toHaveText(
    revision.originalFilename,
  );
}
async function expectSaved(page: Page, value: Report): Promise<void> {
  if (!value.result || !value.report)
    throw new Error("Expected a genuinely readable successful saved report");
  const region = panel(page, value.operation);
  await expect(fact(region, "Lifecycle")).toHaveText("Successful result saved");
  await expect(fact(region, "Saved payload")).toHaveText("Read completed");
  await expect(fact(region, "Saved result ID")).toHaveText(
    value.result.derivationId,
  );
  await expect(fact(region, "Generation")).toHaveText(value.result.generation);
  await expect(fact(region, "Started").locator("time")).toHaveAttribute(
    "datetime",
    value.result.startedAtUtc,
  );
  await expect(fact(region, "Saved").locator("time")).toHaveAttribute(
    "datetime",
    value.result.finishedAtUtc,
  );
  const name = value.operation === "projection" ? "explanation" : "validation";
  const link = region.getByRole("link", {
    name: `Open saved ${name} link`,
    exact: true,
  });
  const href = await link.getAttribute("href");
  expect(href).not.toBeNull();
  const url = new URL(href!, process.env.BRAID_REPORTS_BASE_URL);
  expect(url.pathname).toBe(revisionPath({ scope: value.revision }));
  expect([...url.searchParams.entries()]).toEqual([
    [`${value.operation}DerivationId`, value.result.derivationId],
  ]);
}
async function tabTo(page: Page, target: Locator): Promise<void> {
  for (let stop = 0; stop < 80; stop++) {
    if (await target.evaluate((element) => element === document.activeElement))
      return;
    await page.keyboard.press("Tab");
  }
  await expect(target).toBeFocused();
}
async function openCurrent(
  page: Page,
  revision: Revision,
): Promise<{ projection: ProjectionReport; validation: ValidationReport }> {
  const values = await captureMany(
    page,
    [
      { path: reportApi(revision, "projection") },
      { path: reportApi(revision, "validation") },
      { path: revisionApi(revision) },
    ],
    () => page.goto(revisionPath(revision)),
  );
  expect(values[2].body).toEqual(revision);
  const projection = values[0].body as ProjectionReport;
  const validation = values[1].body as ValidationReport;
  expectReport(projection, revision, "projection");
  expectReport(validation, revision, "validation");
  await expectRevision(page, revision);
  return { projection, validation };
}
async function generate(
  page: Page,
  revision: Revision,
  operation: Operation,
  keyboard = false,
): Promise<Report> {
  const result = await capture<Report>(
    page,
    {
      path: reportApi(revision, operation) + "/ensure",
      method: "POST",
      status: [200, 202],
    },
    async () => {
      const button = panel(page, operation).getByRole("button", {
        name:
          operation === "projection"
            ? "Generate explanation"
            : "Generate validation",
        exact: true,
      });
      if (keyboard) {
        await tabTo(page, button);
        await page.keyboard.press("Enter");
      } else await button.click();
    },
  );
  expectReport(result.body, revision, operation);
  // Ordinary unheld worker operations finish synchronously. The separate real
  // pending step exercises the deliberately held operation, not this shortcut.
  expect(result.status).toBe(200);
  expect(result.body.state).toBe("available");
  expect(result.body.readOutcome).toBe("observed");
  expect(result.body.result?.generation).toBe("1");
  await expectSaved(page, result.body);
  return result.body;
}
async function openPinned(
  page: Page,
  revision: Revision,
  projection: ProjectionReport,
  validation: ValidationReport,
): Promise<void> {
  if (!projection.result || !validation.result)
    throw new Error(
      "Expected returned successful IDs before constructing a pin",
    );
  const values = await captureMany(
    page,
    [
      {
        path: reportApi(revision, "projection", projection.result.derivationId),
      },
      {
        path: reportApi(revision, "validation", validation.result.derivationId),
        query: { projectionDerivationId: projection.result.derivationId },
      },
    ],
    () =>
      page.goto(
        pinnedPath(
          revision,
          projection.result!.derivationId,
          validation.result!.derivationId,
        ),
      ),
  );
  const p = values[0].body as ProjectionReport,
    v = values[1].body as ValidationReport;
  expectReport(p, revision, "projection", projection.result.derivationId);
  expectReport(v, revision, "validation", validation.result.derivationId);
  expect(p.report).toEqual(projection.report);
  expect(v.projectionDerivationId).toBe(projection.result.derivationId);
  expect(v.report?.isValid).toBe(validation.report?.isValid);
  await expectSaved(page, p);
  await expectSaved(page, v);
  await expectRevision(page, revision);
  await expect(panel(page, "projection")).toContainText("Pinned saved result");
  await expect(panel(page, "validation")).toContainText("Pinned saved result");
  await expect(
    panel(page, "projection").getByRole("button", {
      name: /Generate explanation|Repair saved result|Try derivation again/,
    }),
  ).toHaveCount(0);
}
async function readProjection(
  page: Page,
  revision: Revision,
): Promise<ProjectionReport> {
  const reply = await capture<ProjectionReport>(
    page,
    { path: reportApi(revision, "projection") },
    () =>
      panel(page, "projection")
        .getByRole("button", { name: "Read again", exact: true })
        .click(),
  );
  expectReport(reply.body, revision, "projection");
  return reply.body;
}
async function restart(
  owner: ReportsOwner,
  profile: "viewer" | "runner" | "publisher" | "administrator",
  mode: "full" | "readOnlyNoWorker" | "profileUnavailable" = "full",
): Promise<void> {
  const ack = await owner.control({ action: "restart-profile", profile, mode });
  expect(ack.action).toBe("restart-profile");
  expect(ack.outcome).toBe("ready");
}

test.describe.configure({ mode: "serial" });
test("real packaged saved reports: explicit derivation, exact native explanations, retained links, independent failures, repair conflicts and revocation", async ({
  page,
  reportsOwner,
}, testInfo) => {
  test.setTimeout(1_200_000);
  const fixture = reportsOwner.manifest;
  const basic = fixture.samples.basic.revision;
  const exact = fixture.samples.exact.revision;
  const partial = fixture.samples.partial.revision;
  const ambiguous = fixture.samples.ambiguous.revision;
  const invalid = fixture.samples.invalid.revision;
  const uncached = fixture.samples.uncached.revision;
  const pending = fixture.samples.pending.revision;
  const pageErrors: string[] = [],
    forbiddenRequests: string[] = [];
  const postChecks: Promise<void>[] = [];
  const posts: string[] = [];
  page.on("pageerror", (error) => pageErrors.push(error.message));
  const id = "[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}";
  const reportPath = new RegExp(
    `^/api/v1/organizations/${id}/projects/${id}/mappings/${id}/revisions/${id}/reports/(projection|validation)(?:/${id}|/ensure)?$`,
  );
  const metadataPath = new RegExp(
    `^/api/v1/organizations/${id}/projects/${id}(?:/mappings/${id}(?:/revisions/${id})?)?$`,
  );
  page.context().on("request", (request) => {
    const url = new URL(request.url());
    if (url.origin !== fixture.readOrigin || !url.pathname.startsWith("/api/"))
      return;
    if (isSessionStatusRequest(request, fixture.readOrigin)) return;
    const isReport = reportPath.test(url.pathname);
    if (request.method() === "POST") {
      posts.push(url.pathname + url.search);
      if (!isReport || !url.pathname.endsWith("/ensure"))
        forbiddenRequests.push("unexpected unsafe endpoint");
      if ((request.postDataBuffer()?.byteLength ?? 0) !== 0)
        forbiddenRequests.push("nonempty Ensure body");
      postChecks.push(
        (async () => {
          for (const name of [
            "content-type",
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "idempotency-key",
          ])
            if ((await request.headerValue(name)) !== null)
              forbiddenRequests.push(`unexpected Ensure header ${name}`);
          if (!(await request.headerValue("x-xsrf-token")))
            forbiddenRequests.push("missing memory-held request token");
          if ((await request.headerValue("origin")) !== fixture.readOrigin)
            forbiddenRequests.push("unexpected Ensure origin");
        })(),
      );
    } else if (
      request.method() !== "GET" ||
      (!isReport &&
        !metadataPath.test(url.pathname) &&
        url.pathname !== "/api/v1/catalog/context")
    )
      forbiddenRequests.push("unexpected endpoint or method");
  });

  let basicP!: ProjectionReport, basicV!: ValidationReport;
  let invalidP!: ProjectionReport, invalidV!: ValidationReport;
  await test.step("entry, focus and repeated GETs leave real report rows and worker creation unchanged", async () => {
    const before = await reportsOwner.effects();
    expect(before.reportAttempts).toBe(0);
    expect(before.reportResults).toBe(0);
    const read = await openCurrent(page, basic);
    await expect(
      fact(
        page.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
        "Your role",
      ),
    ).toHaveText("viewer");
    expect(read.projection.state).toBe("notDerived");
    expect(read.validation.state).toBe("notDerived");
    await expect(fact(panel(page, "projection"), "Lifecycle")).toHaveText(
      "Not generated",
    );
    await expect(fact(panel(page, "validation"), "Lifecycle")).toHaveText(
      "Not generated",
    );
    await expect(graph(page)).toHaveCount(0);
    await expect(
      panel(page, "projection").getByRole("button", {
        name: "Generate explanation",
        exact: true,
      }),
    ).toBeEnabled();
    const focused = await capture<ProjectionReport>(
      page,
      { path: reportApi(basic, "projection") },
      () => page.evaluate(() => window.dispatchEvent(new Event("focus"))),
    );
    expectReport(focused.body, basic, "projection");
    expect(focused.body.state).toBe("notDerived");
    expect((await readProjection(page, basic)).state).toBe("notDerived");
    expect(await reportsOwner.effects()).toEqual(before);
    expect(posts).toEqual([]);
  });

  await test.step("the actual viewer explicitly generates separate reports and renders native paths, provenance and lineage", async () => {
    basicP = (await generate(
      page,
      basic,
      "projection",
      true,
    )) as ProjectionReport;
    basicV = (await generate(page, basic, "validation")) as ValidationReport;
    expect(basicP.report?.document?.coverage.status).toBe("complete");
    expect(basicV.report?.isValid).toBe(true);
    expect(basicV.projectionDerivationId).toBeNull();
    const row = graph(page).getByRole("article", {
      name: 'Mapping to ["dotted"]',
      exact: true,
    });
    await expect(row).toContainText('Source ["a.b"]');
    await expect(
      graph(page).getByRole("article", {
        name: 'Mapping to ["nestedOutput"]["b"]',
        exact: true,
      }),
    ).toContainText('Source ["nested"]["b"]');
    const source = row.getByRole("button", {
      name: 'Source ["a.b"]',
      exact: true,
    });
    await source.click();
    const details = page.getByRole("region", {
      name: "Mapping details",
      exact: true,
    });
    await expect(details).toBeFocused();
    await expect(
      details.getByRole("heading", { name: "Used by", exact: true }),
    ).toBeVisible();
    await details
      .getByRole("button", { name: "Close details", exact: true })
      .click();
    await expect(source).toBeFocused();
    await panel(page, "projection")
      .getByText("Explanation backend evidence", { exact: true })
      .click();
    await expect(panel(page, "projection")).toContainText(
      basic.nativeDigest.value,
    );
    await expect(panel(page, "projection")).toContainText(
      "796eb8230561f200ca81c153c946a8fe08884e75",
    );
    await page.screenshot({
      path: testInfo.outputPath("saved-reports-desktop.png"),
      fullPage: true,
    });
    expect(posts).toHaveLength(2);
  });

  await test.step("each real project role can deliberately derive, with exact numbers and persistent partial or ambiguous gaps", async () => {
    await restart(reportsOwner, "runner");
    await openCurrent(page, exact);
    await expect(
      fact(
        page.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
        "Your role",
      ),
    ).toHaveText("runner");
    const exactP = (await generate(
      page,
      exact,
      "projection",
    )) as ProjectionReport;
    const literals = exactP
      .report!.document!.rules.flatMap((rule) => rule.dependencies)
      .filter((dependency) => dependency.kind === "constant")
      .map((dependency) => dependency.value);
    expect(literals).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          kind: "exactNumber",
          flavor: "integer",
          value: "9007199254740993",
        }),
        expect.objectContaining({
          kind: "exactNumber",
          flavor: "binaryFloat",
          value: "3ff0000000000000",
        }),
        expect.objectContaining({
          kind: "exactNumber",
          flavor: "binaryFloat",
          value: "8000000000000000",
        }),
      ]),
    );
    await expect(
      graph(page).getByRole("article", {
        name: 'Mapping to ["exactInteger"]',
        exact: true,
      }),
    ).toContainText("9007199254740993");
    const zero = graph(page).getByRole("article", {
      name: 'Mapping to ["negativeZero"]',
      exact: true,
    });
    await zero.getByText("Expand rule explanation", { exact: true }).click();
    await expect(zero.locator(".literal-value")).toHaveText("-0");
    await expect(zero.locator(".literal-bits")).toContainText(
      "8000000000000000",
    );
    const integralFloat = graph(page).getByRole("article", {
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
    await restart(reportsOwner, "publisher");
    await openCurrent(page, partial);
    await expect(
      fact(
        page.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
        "Your role",
      ),
    ).toHaveText("publisher");
    const partialP = (await generate(
      page,
      partial,
      "projection",
    )) as ProjectionReport;
    expect(partialP.report!.document!.coverage.status).toBe("partial");
    expect(partialP.report!.document!.coverage.gaps.length).toBeGreaterThan(0);
    await page.setViewportSize({ width: 390, height: 844 });
    await page
      .getByRole("region", { name: "Explanation incomplete", exact: true })
      .scrollIntoViewIfNeeded();
    await page.screenshot({
      path: testInfo.outputPath("saved-reports-mobile.png"),
    });
    await page
      .getByLabel("Find a field or rule", { exact: true })
      .fill("there-is-no-such-field");
    await expect(
      page.getByRole("heading", { name: "No matching rows", exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("region", { name: "Explanation incomplete", exact: true }),
    ).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
    await restart(reportsOwner, "administrator");
    await openCurrent(page, ambiguous);
    await expect(
      fact(
        page.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
        "Your role",
      ),
    ).toHaveText("administrator");
    const ambiguousP = (await generate(
      page,
      ambiguous,
      "projection",
    )) as ProjectionReport;
    expect(ambiguousP.report!.document!.coverage.status).toBe("partial");
    expect(
      ambiguousP.report!.document!.coverage.gaps.some(
        (gap) => gap.projectionLocator == null,
      ),
    ).toBe(true);
    const coverage = page.getByRole("region", {
      name: "Explanation incomplete",
      exact: true,
    });
    await expect(coverage).toContainText(
      "Location is available in native evidence.",
    );
    await expect(
      page.locator(".mapping-row .incomplete-label").first(),
    ).toContainText("reads may be possible");
    await page
      .getByLabel("Find a field or rule", { exact: true })
      .fill("there-is-no-such-field");
    await expect(coverage).toBeVisible();
    await page.setViewportSize({ width: 1280, height: 900 });
    expect(posts).toHaveLength(5);
  });

  await test.step("usable false validation links findings only to the exact rendered retained projection, and deliberate unlinked reads remain authorized", async () => {
    await openCurrent(page, invalid);
    invalidP = (await generate(
      page,
      invalid,
      "projection",
    )) as ProjectionReport;
    invalidV = (await generate(
      page,
      invalid,
      "validation",
    )) as ValidationReport;
    expect(invalid.admission).toBe("decodedInvalid");
    expect(invalidP.report!.document!.coverage.status).toBe("partial");
    expect(invalidV.status).toBe("invalidMapping");
    expect(invalidV.report!.isValid).toBe(false);
    expect(invalidV.report!.totalIssueCount).toBeGreaterThan(0);
    expect(invalidV.report!.emittedIssueCount).toBe(
      invalidV.report!.diagnostics.length,
    );
    expect(invalidV.projectionDerivationId).toBeNull();
    expect(
      invalidV.report!.diagnostics.every(
        (diagnostic) => diagnostic.projectionLocator == null,
      ),
    ).toBe(true);
    const linked = await capture<ValidationReport>(
      page,
      {
        path: reportApi(invalid, "validation", invalidV.result!.derivationId),
        query: { projectionDerivationId: invalidP.result!.derivationId },
      },
      () =>
        panel(page, "validation")
          .getByRole("button", { name: "Read again", exact: true })
          .click(),
    );
    expectReport(
      linked.body,
      invalid,
      "validation",
      invalidV.result!.derivationId,
    );
    expect(linked.body.projectionDerivationId).toBe(
      invalidP.result!.derivationId,
    );
    const location = linked.body.report!.diagnostics.find(
      (diagnostic) => diagnostic.projectionLocator?.kind === "rule",
    );
    expect(location).toBeDefined();
    expect(location!.owner).toEqual({
      kind: "graphNode",
      functionId: null,
      nodeId: "1",
    });
    await expect(
      panel(page, "validation").getByRole("heading", {
        name: "Native mapping has validation issues",
        exact: true,
      }),
    ).toBeVisible();
    await expect(fact(panel(page, "validation"), "Validity")).toHaveText(
      "Issues found",
    );
    await expect(fact(panel(page, "validation"), "Total notices")).toHaveText(
      String(invalidV.report!.totalIssueCount),
    );
    const locations = page.getByRole("region", {
      name: "Validation locations",
      exact: true,
    });
    await locations
      .getByRole("button", { name: "Show validation rule", exact: true })
      .first()
      .click();
    const details = page.getByRole("region", {
      name: "Mapping details",
      exact: true,
    });
    await expect(details).toBeFocused();
    const ruleId =
      location!.projectionLocator!.kind === "rule"
        ? location!.projectionLocator!.ruleId
        : "";
    const rule = invalidP.report!.document!.rules.find(
      (item) => item.id === ruleId,
    );
    expect(rule).toBeDefined();
    await expect(details).toContainText(rule!.summary);
    const unlinked = await capture<ValidationReport>(
      page,
      { path: reportApi(invalid, "validation", invalidV.result!.derivationId) },
      () =>
        panel(page, "validation")
          .getByRole("button", {
            name: "Read validation without links",
            exact: true,
          })
          .click(),
    );
    expectReport(
      unlinked.body,
      invalid,
      "validation",
      invalidV.result!.derivationId,
    );
    expect(unlinked.body.projectionDerivationId).toBeNull();
    expect(
      unlinked.body.report!.diagnostics.every(
        (diagnostic) => diagnostic.projectionLocator == null,
      ),
    ).toBe(true);
    await expect(
      page.getByRole("region", { name: "Validation locations", exact: true }),
    ).toHaveCount(0);
    const native = page.getByRole("region", {
      name: "Native validation notices",
      exact: true,
    });
    await expect(native).toContainText(
      "These findings are not linked to the explanation shown.",
    );
    await native.getByText("Native evidence", { exact: true }).first().click();
    await expect(native).toContainText('"nodeId":"1"');
    expect(posts).toHaveLength(7);
  });

  await test.step("an actual committed pending derivation returns202 and reconciles with GETs after the bounded private process hold is released", async () => {
    await openCurrent(page, pending);
    const before = await reportsOwner.effects();
    const priorPosts = posts.length;
    const observations: Promise<ProjectionReport | null>[] = [];
    const observationFailures: string[] = [];
    const observe = (response: Response) => {
      const url = new URL(response.url());
      if (
        response.request().method() !== "GET" ||
        url.origin !== fixture.readOrigin ||
        url.pathname !== reportApi(pending, "projection") ||
        url.search !== ""
      )
        return;
      observations.push(
        response
          .json()
          .then((value) => value as ProjectionReport)
          .catch(() => {
            observationFailures.push(
              "pending progress response could not be read",
            );
            return null;
          }),
      );
    };
    let started = false,
      releaseRequested = false;
    let primaryFailure: unknown;
    page.on("response", observe);
    try {
      const start = await reportsOwner.control({ action: "pending-start" });
      expect(start.action).toBe("pending-start");
      expect(start.outcome).toBe("pending");
      started = true;
      const inProgress = await capture<ProjectionReport>(
        page,
        {
          path: reportApi(pending, "projection") + "/ensure",
          method: "POST",
          status: [202],
        },
        () =>
          panel(page, "projection")
            .getByRole("button", { name: "Generate explanation", exact: true })
            .click(),
      );
      // Release the exact owned private process immediately after the real202.
      // Its independent three-second SIGCONT safety bound is operator-owned.
      releaseRequested = true;
      const released = reportsOwner.control({ action: "pending-release" });
      expectReport(inProgress.body, pending, "projection");
      expect(inProgress.body.state).toBe("pending");
      expect(inProgress.body.result).toBeNull();
      expect(inProgress.body.report).toBeNull();
      expect(inProgress.body.currentAttempt?.generation).toBe("1");
      expect(inProgress.location).toBe(reportApi(pending, "projection"));
      expect(inProgress.retryAfter).toMatch(/^[1-9][0-9]*$/);
      const ack = await released;
      if (ack.action !== "pending-release")
        throw new Error("Expected actual pending operation settlement");
      expect(ack.result.generation).toBe("1");
      const region = panel(page, "projection");
      const checkAgain = region.getByRole("button", {
        name: "Check again",
        exact: true,
      });
      await expect
        .poll(
          async () => {
            const savedId = fact(region, "Saved result ID");
            return (
              ((await savedId.count()) === 1 &&
                (await savedId.textContent()) === ack.result.derivationId) ||
              (await checkAgain.isVisible())
            );
          },
          { timeout: 20_000 },
        )
        .toBe(true);
      if (await checkAgain.isVisible()) {
        const checked = await capture<ProjectionReport>(
          page,
          { path: reportApi(pending, "projection") },
          () => checkAgain.click(),
        );
        expectReport(
          checked.body,
          pending,
          "projection",
          ack.result.derivationId,
        );
      }
      await expect(fact(region, "Saved result ID")).toHaveText(
        ack.result.derivationId,
      );
      await expect(fact(region, "Saved payload")).toHaveText("Read completed");
      await expect(graph(page)).toBeVisible();
      const reads = await Promise.all(observations);
      expect(observationFailures).toEqual([]);
      expect(reads.length).toBeGreaterThan(0);
      for (const value of reads)
        if (value) expectReport(value, pending, "projection");
      expect(
        reads.some(
          (value) =>
            value?.state === "available" &&
            value.result?.derivationId === ack.result.derivationId &&
            value.report !== null,
        ),
      ).toBe(true);
      expect(posts).toHaveLength(priorPosts + 1);
      expect(await reportsOwner.effects()).toEqual({
        reportAttempts: before.reportAttempts + 1,
        reportResults: before.reportResults + 1,
        workerCreates: before.workerCreates + 1,
      });
    } catch (error) {
      primaryFailure = error;
      throw error;
    } finally {
      page.off("response", observe);
      if (started && !releaseRequested) {
        try {
          await reportsOwner.control({ action: "pending-release" });
        } catch (error) {
          if (!primaryFailure) throw error;
        }
      }
    }
    expect(posts).toHaveLength(8);
  });

  await test.step("actual combined pin links keep successful IDs across a newer mapping head, a new report and normal API/PostgreSQL restart", async () => {
    await openCurrent(page, basic);
    const combined = panel(page, "validation").getByRole("link", {
      name: "Link validation to this explanation",
      exact: true,
    });
    await expect(combined).toHaveAttribute(
      "href",
      pinnedPath(
        basic,
        basicP.result!.derivationId,
        basicV.result!.derivationId,
      ),
    );
    const beforeReads = await reportsOwner.effects();
    await openPinned(page, basic, basicP, basicV);
    expect(await reportsOwner.effects()).toEqual(beforeReads);
    const appended = await reportsOwner.control({
      action: "append-primary-revision",
    });
    if (appended.action !== "append-primary-revision")
      throw new Error("Expected actual append acknowledgement");
    const newer = appended.appendedRevision;
    expect({ ...newer.scope, revisionId: basic.scope.revisionId }).toEqual(
      basic.scope,
    );
    expect(BigInt(newer.sequence)).toBeGreaterThan(BigInt(basic.sequence));
    expect(newer.nativeDigest.value).not.toBe(basic.nativeDigest.value);
    await expect(page).toHaveURL(
      new URL(
        pinnedPath(
          basic,
          basicP.result!.derivationId,
          basicV.result!.derivationId,
        ),
        fixture.readOrigin,
      ).href,
    );
    await expectRevision(page, basic);
    await openCurrent(page, newer);
    const newerP = (await generate(
      page,
      newer,
      "projection",
    )) as ProjectionReport;
    expect(newerP.result!.derivationId).not.toBe(basicP.result!.derivationId);
    await openPinned(page, basic, basicP, basicV);
    await restart(reportsOwner, "administrator");
    await reportsOwner.control({ action: "pg-normal-restart" });
    const afterRestart = await reportsOwner.effects();
    await openPinned(page, basic, basicP, basicV);
    expect(await reportsOwner.effects()).toEqual(afterRestart);
    expect(posts).toHaveLength(9);
  });

  await test.step("retained payloads remain readable on a genuine workerless read-only host; an explicit unavailable derivation leaves revision metadata usable", async () => {
    await restart(reportsOwner, "viewer", "readOnlyNoWorker");
    const before = await reportsOwner.effects();
    await openPinned(page, basic, basicP, basicV);
    await expect(graph(page)).toBeVisible();
    expect(await reportsOwner.effects()).toEqual(before);
    const refused = await capture(
      page,
      { path: reportApi(basic, "projection"), status: [503] },
      () => page.goto(revisionPath(basic)),
    );
    expect(refused.body).toEqual({ code: "revisionReportUnavailable" });
    await expectRevision(page, basic);
    await expect(panel(page, "projection").getByRole("alert")).toContainText(
      "A report dependency is unavailable.",
    );
    await expect(graph(page)).toHaveCount(0);
    expect(await reportsOwner.effects()).toEqual(before);
    await restart(reportsOwner, "viewer");
    const fresh = await openCurrent(page, uncached);
    expect(fresh.projection.state).toBe("notDerived");
    await restart(reportsOwner, "viewer", "profileUnavailable");
    const unavailable = await capture(
      page,
      {
        path: reportApi(uncached, "projection") + "/ensure",
        method: "POST",
        status: [503],
      },
      () =>
        panel(page, "projection")
          .getByRole("button", { name: "Generate explanation", exact: true })
          .click(),
    );
    expect(unavailable.body).toEqual({ code: "revisionReportUnavailable" });
    await expectRevision(page, uncached);
    await expect(panel(page, "projection").getByRole("alert")).toContainText(
      "A report dependency is unavailable.",
    );
    expect(await reportsOwner.effects()).toEqual(before);
    expect(posts).toHaveLength(10);
    await restart(reportsOwner, "viewer");
  });

  await test.step("missing and corrupt real saved objects never auto-derive; explicit exact-result repair and competing selection409 retain independent metadata", async () => {
    await openCurrent(page, basic);
    await reportsOwner.control({
      action: "fault-report",
      sample: "basic",
      operation: "projection",
      derivationId: basicP.result!.derivationId,
      fault: "missing",
    });
    const before = await reportsOwner.effects();
    const missing = await readProjection(page, basic);
    expect(missing.readOutcome).toBe("objectUnavailable");
    expect(missing.report).toBeNull();
    expect(missing.result).toEqual(basicP.result);
    await expect(fact(panel(page, "projection"), "Saved payload")).toHaveText(
      "Saved object is unavailable",
    );
    await expect(graph(page)).toHaveCount(0);
    await expectRevision(page, basic);
    await expectSaved(page, basicV);
    await expect(fact(panel(page, "validation"), "Validity")).toHaveText(
      "Valid",
    );
    expect(await reportsOwner.effects()).toEqual(before);
    const repaired = await capture<ProjectionReport>(
      page,
      {
        path: reportApi(basic, "projection") + "/ensure",
        method: "POST",
        query: { repairDerivationId: basicP.result!.derivationId },
      },
      () =>
        panel(page, "projection")
          .getByRole("button", { name: "Repair saved result", exact: true })
          .click(),
    );
    expectReport(repaired.body, basic, "projection");
    expect(repaired.body.result!.derivationId).not.toBe(
      basicP.result!.derivationId,
    );
    expect(repaired.body.result!.generation).toBe("2");
    await expectSaved(page, repaired.body);
    await reportsOwner.control({
      action: "restore-report",
      sample: "basic",
      operation: "projection",
      derivationId: basicP.result!.derivationId,
    });
    await reportsOwner.control({
      action: "fault-report",
      sample: "basic",
      operation: "projection",
      derivationId: repaired.body.result!.derivationId,
      fault: "integrity",
    });
    const corrupt = await readProjection(page, basic);
    expect(corrupt.readOutcome).toBe("storageIntegrity");
    expect(corrupt.report).toBeNull();
    expect(corrupt.result).toEqual(repaired.body.result);
    await expect(graph(page)).toHaveCount(0);
    await expectSaved(page, basicV);
    await expect(fact(panel(page, "validation"), "Validity")).toHaveText(
      "Valid",
    );
    const competing = await reportsOwner.control({
      action: "derive-by-http",
      sample: "basic",
      operation: "projection",
      repairDerivationId: repaired.body.result!.derivationId,
    });
    if (competing.action !== "derive-by-http")
      throw new Error("Expected competing HTTP repair acknowledgement");
    expect(competing.result.generation).toBe("3");
    expect(competing.result.derivationId).not.toBe(
      repaired.body.result!.derivationId,
    );
    const beforeConflict = await reportsOwner.effects();
    const count = posts.length;
    const changed = await capture(
      page,
      {
        path: reportApi(basic, "projection") + "/ensure",
        method: "POST",
        query: { repairDerivationId: repaired.body.result!.derivationId },
        status: [409],
      },
      () =>
        panel(page, "projection")
          .getByRole("button", { name: "Repair saved result", exact: true })
          .click(),
    );
    expect(changed.body).toEqual({ code: "revisionReportSelectionChanged" });
    await expect(panel(page, "projection").getByRole("alert")).toHaveText(
      "The selected saved result changed. Read again before choosing another action.",
    );
    await expectRevision(page, basic);
    expect(posts).toHaveLength(count + 1);
    expect(await reportsOwner.effects()).toEqual(beforeConflict);
    const current = await readProjection(page, basic);
    expect(current.result).toEqual(competing.result);
    expect(current.readOutcome).toBe("observed");
    await expectSaved(page, current);
    expect(posts).toHaveLength(count + 1);
    await reportsOwner.control({
      action: "restore-report",
      sample: "basic",
      operation: "projection",
      derivationId: repaired.body.result!.derivationId,
    });
    const beforeRetainedReads = await reportsOwner.effects();
    await openPinned(page, basic, basicP, basicV);
    expect(await reportsOwner.effects()).toEqual(beforeRetainedReads);
    await expect(
      graph(page).getByRole("article", {
        name: 'Mapping to ["dotted"]',
        exact: true,
      }),
    ).toContainText('Source ["a.b"]');
    expect(posts).toHaveLength(12);
  });

  await test.step("one-way actual project and organization revocations remove saved graphs and details on a fresh authorized read", async () => {
    await openPinned(page, invalid, invalidP, invalidV);
    await expect(graph(page)).toBeVisible();
    const before = await reportsOwner.effects();
    await reportsOwner.control({ action: "revoke-project" });
    const denied = await captureMetadataRefusal(page, invalid, () =>
      page.evaluate(() => window.dispatchEvent(new Event("focus"))),
    );
    expect(denied).toEqual({ code: "notFound" });
    await expect(
      page.getByRole("heading", { name: "Not found", exact: true }),
    ).toBeVisible();
    await expect(graph(page)).toHaveCount(0);
    await expect(
      page.getByRole("region", { name: "Mapping details", exact: true }),
    ).toHaveCount(0);
    await expect(panel(page, "projection")).toHaveCount(0);
    await reportsOwner.control({ action: "revoke-org" });
    const context = await capture(
      page,
      { path: "/api/v1/catalog/context", status: [404] },
      () =>
        page
          .getByRole("button", { name: "Refresh catalog", exact: true })
          .click(),
    );
    expect(context.body).toEqual({ code: "notFound" });
    await expect(graph(page)).toHaveCount(0);
    await expect(panel(page, "validation")).toHaveCount(0);
    expect(await reportsOwner.effects()).toEqual(before);
    expect(posts).toHaveLength(12);
  });
  await Promise.all(postChecks);
  expect(forbiddenRequests).toEqual([]);
  expect(pageErrors).toEqual([]);
});
