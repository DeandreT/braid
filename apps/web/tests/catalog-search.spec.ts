import { expect, test, type Page } from "@playwright/test";
import { controlledData, P } from "./revision-reports-controlled.fixture";
import {
  catalogApi,
  projectPath,
  mappingPath,
  synthetic,
  syntheticCatalog,
} from "./catalog.fixture";
import {
  isSearchPage,
  searchTextParts,
} from "../src/catalog/search/catalog-search";
import type { components } from "../src/api/generated";
import {
  isAuthStatus,
  type SessionAuthStatus,
} from "../src/auth/browser-session-types";

type SearchHit = components["schemas"]["CatalogSearchHitV1"];
const identity = `v1.${"A".repeat(43)}`;
function sessionStatus(authenticated = true): SessionAuthStatus {
  if (authenticated)
    return {
      version: 1,
      mode: "session",
      state: "authenticated",
      authActionRequestToken: "YXV0aC1h",
      requestToken: "ZXBvY2gtYQ",
      identityContext: identity,
      displayName: "Search User",
    };
  return {
    version: 1,
    mode: "session",
    state: "authenticationRequired",
    authActionRequestToken: "YXV0aC1h",
    requestToken: null,
    identityContext: null,
    displayName: null,
  };
}

test("catalog header exposes accessible search without overflowing", async ({
  page,
}, testInfo) => {
  await syntheticCatalog(page);
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({ json: { version: 1, mode: "development" } }),
  );
  for (const [name, width, height] of [
    ["desktop", 1280, 800],
    ["mobile", 390, 844],
  ] as const) {
    await page.setViewportSize({ width, height });
    await page.goto("/catalog");
    await expect(
      page.getByRole("heading", { name: "Projects", exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("link", { name: "Synthetic project", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: /^Search \/ Go to/ }).click();
    await expect(
      page.getByRole("searchbox", { name: "Search catalog" }),
    ).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: testInfo.outputPath(`header-${name}.png`),
      fullPage: true,
    });
  }
});

test("notification popover closes on Escape from the bell and drops a read across an owner change", async ({
  page,
}) => {
  await syntheticCatalog(page);
  let actor = synthetic.context.actorPrincipalId;
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({ json: { ...synthetic.context, actorPrincipalId: actor } }),
  );
  let release = () => {};
  const held = new Promise<void>((resolve) => (release = resolve));
  let reads = 0;
  await page.route(
    `**/api/v1/organizations/${synthetic.org}/notifications*`,
    async (route) => {
      reads += 1;
      // Only the second opening's read is held across the owner change.
      if (reads === 2) await held;
      await route
        .fulfill({
          json: {
            notifications: [
              {
                id: synthetic.revision.scope.revisionId,
                resourceId: synthetic.revision.scope.revisionId,
                event: "regressionFailed",
                projectId: synthetic.project.scope.projectId,
                mappingId: synthetic.mapping.scope.mappingId,
                revisionId: synthetic.revision.scope.revisionId,
                link: mappingPath(synthetic.mapping.scope),
                createdAtUtc: "2026-10-25T12:00:00Z",
                read: false,
              },
            ],
            nextId: null,
          },
        })
        // The held request may already be cancelled by the client.
        .catch(() => {});
    },
  );
  await page.goto("/catalog");
  await expect(
    page.getByRole("link", { name: "Synthetic project", exact: true }),
  ).toBeVisible();
  const bell = page.getByRole("button", {
    name: "Recent notifications",
    exact: true,
  });
  const popover = page.getByRole("region", { name: "Notifications" });
  // Escape right after opening, while focus is still on the bell.
  await bell.click();
  await expect(popover).toBeVisible();
  await expect(bell).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(popover).toHaveCount(0);
  await expect(bell).toBeFocused();
  await expect(bell).toHaveAttribute("aria-expanded", "false");
  // Another owner is admitted while the inbox read is held.
  await bell.click();
  await expect.poll(() => reads).toBe(2);
  actor = synthetic.project.scope.projectId;
  const refresh = page.getByRole("button", {
    name: "Refresh catalog",
    exact: true,
  });
  await refresh.focus();
  await refresh.press("Enter");
  await expect(popover).toHaveCount(0);
  release();
  await page.waitForTimeout(300);
  await expect(page.getByText("Regression failed")).toHaveCount(0);
});

test("overflow menus close on Escape and outside clicks without stranding focus", async ({
  page,
}) => {
  await syntheticCatalog(page);
  await page.goto(projectPath(synthetic.project.scope));
  const summary = page.getByLabel("More project actions", { exact: true });
  const menu = page.locator("details.catalog-menu");
  const automation = page.getByRole("link", {
    name: "Automation and notifications",
    exact: true,
  });
  await summary.click();
  await expect(menu).toHaveAttribute("open", "");
  await automation.focus();
  await page.keyboard.press("Escape");
  await expect(menu).not.toHaveAttribute("open", "");
  await expect(summary).toBeFocused();
  await summary.press("Enter");
  await expect(menu).toHaveAttribute("open", "");
  await automation.focus();
  await page.mouse.click(5, 400);
  await expect(menu).not.toHaveAttribute("open", "");
  await expect(summary).toBeFocused();
});

async function fixture(page: Page, session = false) {
  const data = controlledData();
  const context = session
    ? {
        ...data.context,
        mode: "session" as const,
        identityContext: identity,
        requestToken: "Y2F0YWxvZy1vbmx5",
      }
    : data.context;
  const requests: URL[] = [];
  let unavailable = false;
  let authenticated = true;
  const hit: SearchHit = {
    kind: "field",
    ...data.revision.scope,
    projectionDerivationId: P,
    resourceId: data.projection.report!.document!.fields[0]!.id,
    title: {
      text: "🧵Order <script>alert(1)</script>",
      highlights: [{ start: 2, length: 5 }],
    },
    description: {
      text: "Orders:/Order/Id",
      highlights: [{ start: 8, length: 5 }],
    },
  };
  await page.route("**/api/**", async (route) => {
    const url = new URL(route.request().url()),
      path = url.pathname;
    if (path === "/api/auth/session")
      return route.fulfill({
        json: session
          ? sessionStatus(authenticated)
          : { version: 1, mode: "development" },
      });
    if (path === "/api/v1/catalog/context")
      return route.fulfill({ json: context });
    if (path.endsWith("/search")) {
      requests.push(url);
      if (!authenticated) return route.fulfill({ status: 401 });
      if (unavailable)
        return route.fulfill({
          status: 503,
          json: { code: "unavailable" },
          headers: { "Retry-After": "2" },
        });
      return route.fulfill({
        json: {
          query: url.searchParams.get("q"),
          hits: url.searchParams.has("cursor") ? [] : [hit],
          nextCursor: url.searchParams.has("cursor") ? null : "bmV4dA",
        },
      });
    }
    if (path === catalogApi(projectPath(data.project.scope)))
      return route.fulfill({ json: data.project });
    if (path === catalogApi(mappingPath(data.mapping.scope)))
      return route.fulfill({ json: data.mapping });
    if (path === data.apiPath) return route.fulfill({ json: data.revision });
    if (path.startsWith(data.reportPath("projection")))
      return route.fulfill({ json: data.projection });
    if (path.startsWith(data.reportPath("validation")))
      return route.fulfill({ json: data.validation });
    return route.continue();
  });
  return {
    data,
    hit,
    requests,
    expire: () => {
      authenticated = false;
    },
    unavailable: () => {
      unavailable = true;
    },
  };
}

test("session fixtures obey display-name and canonical identity and token contracts", () => {
  const status = sessionStatus();
  expect(isAuthStatus(status)).toBe(true);
  expect(isAuthStatus(sessionStatus(false))).toBe(true);
  expect(isAuthStatus({ ...status, displayName: null })).toBe(true);
  const { displayName: _, ...missingName } = status;
  expect(isAuthStatus(missingName)).toBe(false);
  expect(isAuthStatus({ ...status, identityContext: "search-user" })).toBe(
    false,
  );
  expect(isAuthStatus({ ...status, requestToken: "invalid=" })).toBe(false);
});

test("response guards reject cross-organization and scalar-splitting spans", () => {
  const value = {
    query: "Order",
    hits: [
      {
        kind: "project",
        organizationId: "10000000-0000-4000-8000-000000000001",
        projectId: "20000000-0000-4000-8000-000000000001",
        mappingId: null,
        revisionId: null,
        projectionDerivationId: null,
        resourceId: null,
        title: { text: "🧵Order", highlights: [{ start: 2, length: 5 }] },
        description: { text: "", highlights: [] },
      },
    ],
    nextCursor: null,
  };
  expect(isSearchPage(value, value.hits[0]!.organizationId, "Order")).toBe(
    true,
  );
  expect(
    isSearchPage(value, "30000000-0000-4000-8000-000000000001", "Order"),
  ).toBe(false);
  value.hits[0]!.title.highlights[0]!.start = 1;
  expect(isSearchPage(value, value.hits[0]!.organizationId, "Order")).toBe(
    false,
  );
  expect(
    searchTextParts({
      text: "<script>",
      highlights: [{ start: 0, length: 8 }],
    }),
  ).toEqual([
    { text: "", highlight: false },
    { text: "<script>", highlight: true },
    { text: "", highlight: false },
  ]);
});

test("header search, literal safe highlights and query-bound paging", async ({
  page,
}, testInfo) => {
  const source = await fixture(page);
  await page.goto("/catalog/search");
  await page.getByRole("button", { name: /^Search \/ Go to/ }).click();
  const header = page.getByRole("search");
  await header.getByRole("searchbox", { name: "Search catalog" }).fill("Order");
  await header
    .getByRole("button", { name: "Search catalog", exact: true })
    .click();
  await expect(
    page.getByRole("status").filter({ hasText: "result on this page" }),
  ).toHaveText("1 result on this page for “Order”");
  const result = page.locator(".catalog-search-results li");
  await expect(result.locator("mark").first()).toHaveText("Order");
  await expect(result.locator("script")).toHaveCount(0);
  await expect(result).toContainText("<script>alert(1)</script>");
  expect(source.requests[0]!.searchParams.get("q")).toBe("Order");
  await page.screenshot({
    path: testInfo.outputPath("search-desktop.png"),
    fullPage: true,
  });
  await page.getByRole("link", { name: "Next page" }).click();
  await expect(
    page.getByText("No matching resources you can read."),
  ).toBeVisible();
  expect(source.requests.at(-1)!.searchParams.get("cursor")).toBe("bmV4dA");
  await page.getByRole("link", { name: "First page" }).click();
  await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
});

test("a search result opens its retained projection at the exact field", async ({
  page,
}) => {
  const source = await fixture(page);
  await page.goto("/catalog/search?q=Order");
  const result = page.locator(".catalog-search-results a");
  await expect(result).toHaveAttribute(
    "href",
    new RegExp(`projectionDerivationId=${P}#field:`),
  );
  await result.click();
  await expect(page).toHaveURL(
    new RegExp(
      `${source.data.revision.scope.revisionId}\\?projectionDerivationId=${P}#field:`,
    ),
  );
  await expect(
    page.getByRole("complementary", { name: "Context inspector" }),
  ).toContainText(source.data.projection.report!.document!.fields[0]!.name);
});

test("incomplete indexing clears results and small screens do not overflow", async ({
  page,
}, testInfo) => {
  const source = await fixture(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/catalog/search?q=Order");
  await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: testInfo.outputPath("search-mobile.png"),
    fullPage: true,
  });
  source.unavailable();
  await page
    .getByRole("button", { name: "Search", exact: true })
    .last()
    .click();
  await expect(
    page.getByText(/incomplete results are never shown/),
  ).toBeVisible();
  await expect(page.locator(".catalog-search-results li")).toHaveCount(0);
});

test("keyboard submission resets paging and a changed owner cannot reuse a cursor", async ({
  page,
}) => {
  const source = await fixture(page);
  await page.goto("/catalog/search?q=Order");
  await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
  await page.getByRole("link", { name: "Next page" }).click();
  await expect(
    page.getByText("No matching resources you can read."),
  ).toBeVisible();
  const input = page.getByLabel("Search text", { exact: true });
  await input.fill("Invoice");
  await input.press("Enter");
  await expect(page).toHaveURL(/q=Invoice$/);
  await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
  expect(source.requests.at(-1)!.searchParams.has("cursor")).toBe(false);
  const count = source.requests.length;
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({
      json: {
        ...source.data.context,
        actorPrincipalId: "30000000-0000-4000-8000-000000000099",
      },
    }),
  );
  await page.getByRole("link", { name: "Next page" }).click();
  await expect(page.locator(".catalog-search-results li")).toHaveCount(0);
  await expect(page.getByText(/link is incomplete or invalid/i)).toBeVisible();
  expect(source.requests).toHaveLength(count);
});

test("signed-in search input and navigation do not claim unsent authoring work", async ({
  page,
}, testInfo) => {
  await fixture(page, true);
  await page.goto("/catalog/search?q=Order");
  await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
  await page.getByRole("button", { name: /^Search \/ Go to/ }).click();
  await page.getByRole("searchbox", { name: "Search catalog" }).fill("Invoice");
  await page.getByRole("button", { name: "Close search", exact: true }).click();
  await page.getByLabel("Search text", { exact: true }).fill("Customer");
  await page.locator("#account-trigger").click();
  const menu = page.getByRole("region", { name: "Sign-in and session" });
  await expect(menu).toContainText("Search User");
  await expect(
    menu.getByText("This page holds unsent work.", { exact: false }),
  ).toHaveCount(0);
  await expect(
    menu.getByRole("button", { name: "Sign in again", exact: true }),
  ).not.toHaveAttribute("aria-describedby");
  for (const [name, width, height] of [
    ["desktop", 1280, 800],
    ["mobile", 390, 844],
  ] as const) {
    await page.setViewportSize({ width, height });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: testInfo.outputPath(`signed-in-search-${name}.png`),
      fullPage: true,
    });
  }
  await page.locator("#account-trigger").click();
  await page.getByRole("link", { name: "Next page" }).click();
  await expect(
    page.getByText("No matching resources you can read."),
  ).toBeVisible();
  await page.locator("#account-trigger").click();
  await expect(page.locator("#sign-in-navigation-warning")).toHaveCount(0);
});

test("expired search clears retained results without an authoring warning", async ({
  page,
}) => {
  const source = await fixture(page, true);
  await page.goto("/catalog/search?q=Order");
  await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
  source.expire();
  await page.getByRole("link", { name: "Next page" }).click();
  await expect(page.locator(".catalog-search-results li")).toHaveCount(0);
  await page.locator("#account-trigger").click();
  const menu = page.getByRole("region", { name: "Sign-in and session" });
  await expect(
    menu.getByRole("button", { name: "Sign in", exact: true }),
  ).toBeVisible();
  await expect(page.locator("#sign-in-navigation-warning")).toHaveCount(0);
});

test("session search keeps retained navigation and MFD warning ownership separate", async ({
  page,
}, testInfo) => {
  const source = await fixture(page, true);
  await page.route("**/api/v1/catalog/interchange", (route) =>
    route.fulfill({ json: { enabled: true } }),
  );
  let started = false;
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/mfd-exports/review", async (route) => {
    started = true;
    await pending;
    await route.fulfill({
      json: {
        reviewId: "99999999-1111-4111-8111-111111111111",
        reviewDigest: "1".repeat(64),
        packageDigest: "2".repeat(64),
        nativeDigest: "3".repeat(64),
        engine: {
          backendId: "ferrule",
          engineRevision: "synthetic",
          backendBuild: "synthetic",
          adapterBuild: "synthetic",
        },
        summary: "Retained export review",
        warnings: [],
        expiresAt: "2099-01-01T00:00:00Z",
        targetMappingId: source.data.mapping.scope.mappingId,
        expectedHeadRevisionId: source.data.revision.scope.revisionId,
        mappingName: null,
      },
    });
  });
  try {
    await page.goto("/catalog/search?q=Order");
    await expect(
      page.getByRole("link", { name: "Administration", exact: true }),
    ).toHaveAttribute("href", "/administration");
    await page.locator(".catalog-search-results a").click();
    await expect(page).toHaveURL(
      new RegExp(`projectionDerivationId=${P}#field:`),
    );
    await expect(
      page.getByRole("complementary", { name: "Context inspector" }),
    ).toContainText(source.data.projection.report!.document!.fields[0]!.name);
    const exporter = page.locator("weft-mfd-export");
    await exporter
      .getByRole("button", { name: "Review .mfd export", exact: true })
      .click();
    await expect.poll(() => started).toBe(true);
    await page.locator("#account-trigger").click();
    await expect(page.locator("#sign-in-navigation-warning")).toBeVisible();
    release();
    await expect(
      exporter.getByText("Retained export review", { exact: true }),
    ).toBeVisible();
    await expect(page.locator("#sign-in-navigation-warning")).toHaveCount(0);
    await page.locator("#account-trigger").click();
    const header = page.getByRole("search");
    await page.getByRole("button", { name: /^Search \/ Go to/ }).click();
    await header
      .getByRole("searchbox", { name: "Search catalog" })
      .fill("Order");
    await header
      .getByRole("button", { name: "Search catalog", exact: true })
      .click();
    await expect(page.locator(".catalog-search-results li")).toHaveCount(1);
    await page.locator("#account-trigger").click();
    await expect(page.locator("#sign-in-navigation-warning")).toHaveCount(0);
    for (const [name, width, height] of [
      ["desktop", 1280, 800],
      ["mobile", 390, 844],
    ] as const) {
      await page.setViewportSize({ width, height });
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
      ).toBe(true);
      await page.screenshot({
        path: testInfo.outputPath(`combined-search-${name}.png`),
        fullPage: true,
      });
    }
  } finally {
    release();
  }
});
