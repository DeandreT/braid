import { expect, test, type Page } from "@playwright/test";
import {
  controlledData,
  notDerived,
  type Operation,
} from "./revision-reports-controlled.fixture";
import {
  catalogApi,
  mappingPath,
  projectPath,
  syntheticCatalog,
} from "./catalog.fixture";

/** Synthetic UI boundary: reports start missing and an ensure makes each one available. */
async function missingReports(
  page: Page,
  options: {
    derive?: boolean;
    holdReads?: boolean;
    failEnsure?: boolean;
    pinned?: boolean;
    damaged?: "objectUnavailable" | "storageIntegrity";
    revokeOnRefresh?: boolean;
  } = {},
) {
  const data = controlledData();
  data.mapping.access.actions.deriveReports = options.derive ?? true;
  const posts: Operation[] = [];
  const generated = new Set<Operation>();
  let refusedAdmissions = 0;
  let release = () => {};
  const held = new Promise<void>((resolve) => (release = resolve));
  const holding = new Set<Operation>(
    options.holdReads ? ["projection", "validation"] : [],
  );
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": data.context,
    [catalogApi(projectPath(data.project.scope))]: data.project,
    [catalogApi(mappingPath(data.mapping.scope))]: data.mapping,
    [data.apiPath]: data.revision,
  });
  if (options.revokeOnRefresh) {
    let mappingReads = 0;
    await page.route(
      "**" + catalogApi(mappingPath(data.mapping.scope)),
      (route) => {
        const mapping = structuredClone(data.mapping);
        // The page's initial metadata allows derivation. The automatic write
        // must refresh that metadata and respect the newly withdrawn permission.
        if (++mappingReads > 1) {
          mapping.access.actions.deriveReports = false;
          ++refusedAdmissions;
        }
        return route.fulfill({ json: mapping });
      },
    );
  }
  await page.route("**/reports/**", async (route) => {
    const request = route.request(),
      url = new URL(request.url()),
      operation: Operation = url.pathname.includes("/projection")
        ? "projection"
        : "validation",
      saved = operation === "projection" ? data.projection : data.validation;
    if (request.method() === "POST") {
      expect(url.pathname).toBe(data.reportPath(operation) + "/ensure");
      expect(request.headers()["x-xsrf-token"]).toBe(data.context.requestToken);
      posts.push(operation);
      if (options.failEnsure) {
        await route
          .fulfill({ status: 503, json: { code: "unavailable" } })
          .catch(() => {});
        return;
      }
      generated.add(operation);
    } else {
      if (holding.delete(operation)) await held;
    }
    const response = structuredClone(
      generated.has(operation) || options.pinned || options.damaged
        ? saved
        : notDerived(saved),
    );
    if (options.damaged) {
      response.readOutcome = options.damaged;
      response.report = null;
    }
    if (response.operation === "validation")
      response.projectionDerivationId = url.searchParams.get(
        "projectionDerivationId",
      );
    await route.fulfill({ json: response }).catch(() => {});
  });
  await page.goto(
    data.browserPath +
      (options.pinned
        ? `?projectionDerivationId=${data.projection.result!.derivationId}&validationDerivationId=${data.validation.result!.derivationId}`
        : ""),
  );
  return { data, posts, release, refusedAdmissions: () => refusedAdmissions };
}

const callToAction = (page: Page) =>
  page.getByRole("alert").filter({ has: page.locator("#report-cta-title") });

test("a revision without saved reports generates them once on first open, without a click", async ({
  page,
}) => {
  const { posts } = await missingReports(page);
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toBeVisible();
  await expect(page.locator("#saved-validation-title")).toContainText(
    "Native mapping has validation issues",
  );
  await expect(callToAction(page)).toHaveCount(0);
  await page.waitForTimeout(500);
  expect(posts.sort()).toEqual(["projection", "validation"]);
});

test("someone who can't generate reports gets a prominent call to action and nothing is requested", async ({
  page,
}) => {
  const { posts } = await missingReports(page, { derive: false });
  await expect(callToAction(page)).toContainText(
    "This version's explanation and validation haven't been generated",
  );
  await expect(callToAction(page)).toContainText(
    "You don't have permission to generate reports",
  );
  await expect(
    callToAction(page).getByRole("button", { name: "Generate now" }),
  ).toHaveCount(0);
  expect(posts).toEqual([]);
});

test("leaving the revision before its first read settles never generates its reports", async ({
  page,
}) => {
  const { data, posts, release } = await missingReports(page, {
    holdReads: true,
  });
  await expect(page.locator("#saved-explanation-title")).toContainText(
    "Reading saved explanation",
  );
  await page
    .getByRole("link", { name: data.mapping.name, exact: true })
    .first()
    .click();
  await expect(page).not.toHaveURL(new RegExp(data.revision.scope.revisionId));
  release();
  await page.waitForTimeout(1000);
  expect(posts).toEqual([]);
});

test("a failed automatic generation is not repeated and leaves a prominent retry", async ({
  page,
}) => {
  const { posts } = await missingReports(page, { failEnsure: true });
  await expect(
    callToAction(page).getByRole("button", { name: "Generate now" }),
  ).toBeVisible();
  await page.waitForTimeout(1000);
  expect(posts.sort()).toEqual(["projection", "validation"]);
});

test("pinned saved reports are displayed without automatic generation", async ({
  page,
}) => {
  const { posts } = await missingReports(page, { pinned: true });
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toBeVisible();
  await expect(callToAction(page)).toHaveCount(0);
  await page.getByText("Report controls and evidence", { exact: true }).click();
  await expect(
    page.getByRole("button", { name: /Generate|Repair saved result/ }),
  ).toHaveCount(0);
  expect(posts).toEqual([]);
});

for (const damaged of ["objectUnavailable", "storageIntegrity"] as const) {
  test(`a saved report with ${damaged} requires explicit repair, not automatic regeneration`, async ({
    page,
  }) => {
    const { posts } = await missingReports(page, { damaged });
    await page
      .getByText("Report controls and evidence", { exact: true })
      .click();
    await expect(
      page.getByRole("button", { name: "Repair saved result", exact: true }),
    ).toHaveCount(2);
    await expect(
      page.getByRole("table", { name: "Mapping field connections" }),
    ).toHaveCount(0);
    await page.waitForTimeout(500);
    expect(posts).toEqual([]);
  });
}

test("fresh admission can refuse automatic generation after the initial read allowed it", async ({
  page,
}) => {
  const { posts, refusedAdmissions } = await missingReports(page, {
    revokeOnRefresh: true,
  });
  await expect(page.getByRole("alert")).toContainText(
    "The report request was not accepted",
  );
  expect(refusedAdmissions()).toBeGreaterThan(0);
  await expect(
    callToAction(page).getByRole("button", { name: "Generate now" }),
  ).toHaveCount(0);
  await page.waitForTimeout(500);
  expect(posts).toEqual([]);
});

test("refreshing the same version after failed automatic generation does not resend it", async ({
  page,
}) => {
  const { data, posts } = await missingReports(page, { failEnsure: true });
  await expect(
    callToAction(page).getByRole("button", { name: "Generate now" }),
  ).toBeEnabled();
  expect(posts.sort()).toEqual(["projection", "validation"]);
  // The retry control stays visible during refresh. Observe the new reads
  // instead of treating that old control as proof that refresh has finished.
  const refreshed = [
    data.apiPath,
    data.reportPath("projection"),
    data.reportPath("validation"),
  ].map((path) =>
    page.waitForResponse(
      (response) =>
        response.request().method() === "GET" &&
        new URL(response.url()).pathname === path,
    ),
  );
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  const responses = await Promise.all(refreshed);
  await Promise.all(responses.map((response) => response.finished()));
  await expect(page.locator("#saved-explanation-title")).toContainText(
    "Not generated",
  );
  await expect(page.locator("#saved-validation-title")).toContainText(
    "Saved validation read completed.",
  );
  await expect(
    callToAction(page).getByRole("button", { name: "Generate now" }),
  ).toBeEnabled();
  expect(posts.sort()).toEqual(["projection", "validation"]);
});
