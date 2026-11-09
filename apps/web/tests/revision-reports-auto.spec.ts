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
  options: { derive?: boolean; holdReads?: boolean; failEnsure?: boolean } = {},
) {
  const data = controlledData();
  data.mapping.access.actions.deriveReports = options.derive ?? true;
  const posts: Operation[] = [];
  const generated = new Set<Operation>();
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
    } else if (holding.delete(operation)) await held;
    const response = structuredClone(
      generated.has(operation) ? saved : notDerived(saved),
    );
    if (response.operation === "validation")
      response.projectionDerivationId = url.searchParams.get(
        "projectionDerivationId",
      );
    await route.fulfill({ json: response }).catch(() => {});
  });
  await page.goto(data.browserPath);
  return { data, posts, release };
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
