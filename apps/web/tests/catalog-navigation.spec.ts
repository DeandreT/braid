import {
  catalogApi,
  catalogWriteAction,
  expect,
  expectRevision,
  mappingPath,
  projectPath,
  projectsPath,
  revisionPath,
  synthetic,
  syntheticCatalog,
  test,
} from "./catalog.fixture";

test("controlled UI: real links, concrete revision routes, breadcrumbs and unknown routes", async ({
  page,
}) => {
  await syntheticCatalog(page);
  await page.goto(projectsPath(synthetic.org));
  const projects = page.getByRole("list", { name: "Projects", exact: true });
  await projects
    .getByRole("link", { name: synthetic.project.name, exact: true })
    .click();
  await expect(page).toHaveURL(
    new RegExp(projectPath(synthetic.project.scope) + "$"),
  );
  await page
    .getByRole("list", { name: "Mappings", exact: true })
    .getByRole("link", { name: synthetic.mapping.name, exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Revision history", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("list", { name: "Revision history", exact: true })
    .getByRole("link", { name: "Revision 1", exact: true })
    .focus();
  await page.keyboard.press("Enter");
  await expectRevision(page, synthetic.revision, synthetic.mapping.name);
  const breadcrumbs = page.getByRole("navigation", {
    name: "Mapping location",
    exact: true,
  });
  await breadcrumbs
    .getByRole("link", { name: synthetic.mapping.name, exact: true })
    .click();
  await expect(page).toHaveURL(
    new RegExp(mappingPath(synthetic.mapping.scope) + "$"),
  );
  await page.goto("/this-route-does-not-exist");
  await expect(
    page.getByRole("heading", { name: "Page unavailable", exact: true }),
  ).toBeVisible();
});

test("controlled UI: opaque page cursor is preserved and Back/First fetch the selected page freshly", async ({
  page,
}) => {
  await syntheticCatalog(page);
  // Canonical base64url wire; no claim that this synthetic token has server authority.
  const cursor = Buffer.from("synthetic UI-only opaque cursor bytes").toString(
    "base64url",
  );
  const endpoint = catalogApi(projectsPath(synthetic.org));
  const requests: string[] = [];
  const projects = Array.from({ length: 11 }, (_, index) => ({
    ...synthetic.project,
    scope: {
      ...synthetic.project.scope,
      projectId: `20000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
    },
    name: `Synthetic project ${index + 1}`,
  }));
  await page.route(`**${endpoint}?*`, async (route) => {
    const url = new URL(route.request().url());
    requests.push(url.search);
    expect(url.searchParams.get("limit")).toBe("10");
    const token = url.searchParams.get("cursor");
    expect(token === null || token === cursor).toBe(true);
    await route.fulfill({
      json: {
        projects: token === null ? projects.slice(0, 10) : projects.slice(10),
        nextCursor: token === null ? cursor : null,
      },
    });
  });
  await page.goto(projectsPath(synthetic.org));
  const list = page.getByRole("list", { name: "Projects", exact: true });
  await expect(list.getByRole("listitem")).toHaveCount(10);
  const nav = page.getByRole("navigation", {
    name: "Project pages",
    exact: true,
  });
  await nav.getByRole("link", { name: "Next page", exact: true }).click();
  await expect(list.getByRole("listitem")).toHaveCount(1);
  expect(new URL(page.url()).searchParams.get("cursor")).toBe(cursor);
  expect(new URLSearchParams(requests.at(-1)).get("cursor")).toBe(cursor);
  await page.goBack();
  await expect(list.getByRole("listitem")).toHaveCount(10);
  await page.goForward();
  await expect(list.getByRole("listitem")).toHaveCount(1);
  await nav.getByRole("link", { name: "First page", exact: true }).click();
  await expect(list.getByRole("listitem")).toHaveCount(10);
  expect(new URL(page.url()).searchParams.has("cursor")).toBe(false);
  expect(requests).toHaveLength(5);
});

test("controlled UI: keyboard and mobile keep long Unicode metadata readable as text", async ({
  page,
}) => {
  const literal = '<img src=x onerror="window.__braidInjected=true">';
  const name = `Synthetic 漢字 🧵 ${"long".repeat(28)} ${literal}`;
  const longProject = {
    ...synthetic.project,
    name,
    description: `Synthetic description ${"𐐷é🧵".repeat(80)} ${literal}`,
  };
  const longRevision = {
    ...synthetic.revision,
    originalFilename: `Synthetic-${"文".repeat(80)}-${literal}.json`,
  };
  await syntheticCatalog(page, {
    [catalogApi(projectPath(longProject.scope))]: longProject,
    [catalogApi(revisionPath(longRevision.scope))]: longRevision,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(projectPath(longProject.scope));
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(name);
  await page.keyboard.press("Tab");
  await expect(
    page.getByRole("link", { name: "Skip to content", exact: true }),
  ).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.locator("main")).toBeFocused();
  await expect(page.locator("main img")).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    ),
  ).toBe(false);
  await page.goto(revisionPath(longRevision.scope));
  await expectRevision(page, longRevision, synthetic.mapping.name);
  await expect(
    page.getByRole("heading", {
      name: "Saved versions",
      exact: true,
    }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    ),
  ).toBe(false);
  expect(await page.evaluate(() => "__braidInjected" in window)).toBe(false);
});

test("controlled UI: project write flags do not expose mutations without matching workspace features", async ({
  page,
}) => {
  const actions = Object.fromEntries(
    Object.keys(synthetic.project.access.actions).map((key) => [key, true]),
  );
  await syntheticCatalog(page, {
    [catalogApi(projectPath(synthetic.project.scope))]: {
      ...synthetic.project,
      access: { role: "administrator", actions },
    },
  });
  await page.goto(projectPath(synthetic.project.scope));
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    synthetic.project.name,
  );
  await expect(
    page.getByRole("button", { name: catalogWriteAction }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("link", { name: catalogWriteAction }),
  ).toHaveCount(0);
});

test("controlled UI: viewing the already-current head refreshes the same concrete revision without a loading hang", async ({
  page,
}) => {
  await syntheticCatalog(page);
  let mappingReads = 0;
  await page.route(
    `**${catalogApi(mappingPath(synthetic.mapping.scope))}`,
    async (route) => {
      mappingReads++;
      await route.fulfill({ json: synthetic.mapping });
    },
  );
  await page.goto(revisionPath(synthetic.revision.scope));
  await expectRevision(page, synthetic.revision, synthetic.mapping.name);
  const before = mappingReads;
  await page
    .getByRole("button", { name: "View latest version", exact: true })
    .click();
  await expect.poll(() => mappingReads).toBeGreaterThan(before);
  await expectRevision(page, synthetic.revision, synthetic.mapping.name);
  await expect(
    page.getByRole("button", { name: "Refresh catalog", exact: true }),
  ).toBeEnabled();
});
