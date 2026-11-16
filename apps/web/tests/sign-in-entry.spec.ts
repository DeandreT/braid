import { expect, test, type Page, type Route } from "@playwright/test";

// Synthetic browser contracts only; this is not live provider qualification.
const identity = `v1.${"A".repeat(43)}`;
const organizationId = "10000000-0000-4000-8000-000000000001";
const organizationName = "<Example> & Company";
const context = {
  mode: "session",
  identityContext: identity,
  requestToken: "Y2F0YWxvZy1vbmx5",
  actorPrincipalId: "10000000-0000-4000-8000-000000000002",
  allowedOrganizationId: organizationId,
  organizationName,
  features: {
    readCatalog: true,
    createProjects: false,
    createMappings: false,
    editMetadata: false,
    edit: false,
    uploadRevisions: false,
    readReports: false,
    chat: false,
    preview: false,
    manageMembership: false,
    deriveReports: false,
  },
  organizationActions: { createProjects: false },
};

function status(authenticated: boolean) {
  return {
    version: 1,
    mode: "session",
    state: authenticated ? "authenticated" : "anonymous",
    authActionRequestToken: "YXV0aC1h",
    requestToken: authenticated ? "ZXBvY2gtYQ" : null,
    identityContext: authenticated ? identity : null,
    displayName: authenticated ? "Example User" : null,
  };
}

async function install(page: Page, development = false) {
  const fixture = {
    authenticated: true,
    contextReads: 0,
    sessionReads: 0,
    helpReads: 0,
    unexpected: [] as string[],
    contextReply: (route: Route) => route.fulfill({ json: context }),
  };
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    switch (path) {
      case "/api/auth/session":
        ++fixture.sessionReads;
        return route.fulfill({
          json: development
            ? { version: 1, mode: "development" }
            : status(fixture.authenticated),
        });
      case "/api/v1/health":
        return route.fulfill({
          json: { status: "ok", service: "braid-api", apiVersion: "v1" },
        });
      case "/api/v1/catalog/context":
        ++fixture.contextReads;
        return development
          ? route.fulfill({
              json: {
                ...context,
                mode: "development",
                identityContext: undefined,
              },
            })
          : fixture.contextReply(route);
      case `/api/v1/organizations/${organizationId}/projects`:
        return route.fulfill({ json: { projects: [], nextCursor: null } });
      case "/api/v1/help/sections":
        ++fixture.helpReads;
        return route.fulfill({
          json: {
            sections: [
              { id: "overview", title: "Getting started", summary: "Guide." },
            ],
          },
        });
      default:
        fixture.unexpected.push(path);
        return route.fulfill({ status: 404 });
    }
  });
  return fixture;
}

for (const development of [false, true]) {
  test(`${development ? "Development" : "a signed-in user"} opens the catalog from sign-in, not its returnTo`, async ({
    page,
  }) => {
    const fixture = await install(page, development);
    await page.goto("/sign-in?returnTo=%2Fhelp");
    await expect(page).toHaveURL(
      new RegExp(`/catalog/organizations/${organizationId}/projects$`),
    );
    await expect(page.locator("#account-trigger")).toHaveCount(1);
    expect(fixture.helpReads).toBe(0);
    expect(fixture.unexpected).toEqual([]);
  });
}

test("a direct guide visit loads the organization without visiting the catalog", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto("/help");
  await expect(
    page.getByRole("heading", { name: "User guide", exact: true }),
  ).toBeVisible();
  await page.locator("#account-trigger").click();
  const menu = page.getByRole("region", { name: "Sign-in and session" });
  await expect(menu.locator(".session-organization")).toHaveText(
    organizationName,
  );
  await expect(page.locator("#account-trigger")).toHaveText("Example User");
  await expect(menu.locator("example")).toHaveCount(0);
  expect(fixture.contextReads).toBe(1);
  expect(fixture.unexpected).toEqual([]);
  await page.screenshot({
    path: test.info().outputPath("direct-help-account.png"),
    fullPage: true,
  });
});

test("an unsubmitted project draft warns before sign-in without exposing its contents", async ({
  page,
}) => {
  const fixture = await install(page);
  fixture.contextReply = (route) =>
    route.fulfill({
      json: {
        ...context,
        features: { ...context.features, createProjects: true },
        organizationActions: { createProjects: true },
      },
    });
  let writes = 0;
  page.on("request", (request) => {
    if (
      new URL(request.url()).pathname.startsWith("/api/") &&
      request.method() !== "GET"
    )
      ++writes;
  });
  await page.goto(`/catalog/organizations/${organizationId}/projects/new`);
  const name = page.locator("#project-name");
  const description = page.locator("#project-description");
  const account = page.locator("#account-trigger");
  const warning = page.locator("#sign-in-navigation-warning");
  await expect(name).toBeEditable();
  await account.click();
  await expect(warning).toHaveCount(0);
  await account.click();
  await name.fill("Unsubmitted private project");
  await account.click();
  await expect(warning).toBeVisible();
  await expect(warning).not.toContainText("Unsubmitted private project");
  await account.click();
  await name.fill("");
  await account.click();
  await expect(warning).toHaveCount(0);
  await account.click();
  await description.fill("Unsubmitted private notes");
  await account.click();
  await expect(warning).toBeVisible();
  await page.getByRole("link", { name: "Help & guide", exact: true }).click();
  await expect(page).toHaveURL(/\/help$/);
  await account.click();
  await expect(warning).toBeVisible();
  await expect(warning).not.toContainText("Unsubmitted private notes");
  expect(writes).toBe(0);
  expect(fixture.unexpected).toEqual([]);
});

test("an unavailable organization read does not block the guide or retry endlessly", async ({
  page,
}) => {
  const fixture = await install(page);
  fixture.contextReply = (route) => route.fulfill({ status: 503 });
  await page.goto("/help");
  await expect(
    page.getByRole("heading", { name: "User guide", exact: true }),
  ).toBeVisible();
  await page.locator("#account-trigger").click();
  const menu = page.getByRole("region", { name: "Sign-in and session" });
  await expect(menu).toContainText("Signed in to braid");
  await expect(menu.locator(".session-organization")).toHaveCount(0);
  expect(fixture.contextReads).toBe(1);
  expect(fixture.unexpected).toEqual([]);
});

test("a pending organization read cannot survive a session boundary", async ({
  page,
}) => {
  const fixture = await install(page);
  let release!: () => void;
  const held = new Promise<void>((resolve) => (release = resolve));
  let requested!: () => void;
  const started = new Promise<void>((resolve) => (requested = resolve));
  let finished!: () => void;
  const completed = new Promise<void>((resolve) => (finished = resolve));
  fixture.contextReply = async (route) => {
    requested();
    await held;
    try {
      await route.fulfill({ json: context });
    } finally {
      finished();
    }
  };
  await page.goto("/help");
  await started;
  await page.locator("#account-trigger").click();
  fixture.authenticated = false;
  const menu = page.getByRole("region", { name: "Sign-in and session" });
  try {
    await menu
      .getByRole("button", { name: "Check session", exact: true })
      .click();
    await expect(menu).toContainText("Not signed in");
  } finally {
    release();
  }
  await completed;
  await expect(menu.locator(".session-organization")).toHaveCount(0);
  await expect(menu).not.toContainText(organizationName);
  await expect(page.locator("#account-trigger")).not.toContainText(
    "Example User",
  );
  expect(fixture.contextReads).toBe(1);
  expect(fixture.unexpected).toEqual([]);
});

for (const refusal of [401, 403]) {
  test(`an organization ${refusal} with a still-valid session does not start a read loop`, async ({
    page,
  }) => {
    const fixture = await install(page);
    fixture.contextReply = (route) => route.fulfill({ status: refusal });
    const reclassified = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === "/api/auth/session" &&
        fixture.sessionReads >= 3,
    );
    await page.goto("/help");
    await reclassified;
    await expect(
      page.getByRole("heading", { name: "User guide", exact: true }),
    ).toBeVisible();
    await page.locator("#account-trigger").click();
    const menu = page.getByRole("region", { name: "Sign-in and session" });
    await expect(menu).toContainText("Signed in to braid");
    // Let the reclassification render and any scheduled effects run before
    // asserting the read count. The denied organization is never auto-retried.
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
    expect(fixture.contextReads).toBe(1);
    expect(fixture.sessionReads).toBe(3);
    await expect(menu.locator(".session-organization")).toHaveCount(0);
    expect(fixture.unexpected).toEqual([]);
  });
}
