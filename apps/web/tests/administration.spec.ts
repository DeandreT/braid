import { expect, test, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { synthetic } from "./catalog.fixture";

test("capture authorized administration in the shared shell", async ({
  page,
}) => {
  const directory = process.env.BRAID_UI_CAPTURE_DIR;
  test.skip(!directory, "Optional sanitized UI captures");
  await install(page, true);
  await page.goto("/administration");
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  await mkdir(directory!, { recursive: true });
  for (const width of [1440, 1024, 720, 360]) {
    await page.setViewportSize({ width, height: 1000 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: `${directory}/administration-${width}.png`,
      fullPage: true,
    });
  }
});

const org = "11111111-1111-4111-8111-111111111111";
const principal = "22222222-2222-4222-8222-222222222222";
const project = "33333333-3333-4333-8333-333333333333";
const otherProject = "44444444-4444-4444-8444-444444444444";
const path = `/api/v1/organizations/${org}/administration`;
const projectPath = `/api/v1/organizations/${org}/projects/${project}/administration`;
const etag = `"admin-${org}-7"`;
const context = {
  mode: "development",
  actorPrincipalId: principal,
  allowedOrganizationId: org,
  organizationName: "Administration fixture",
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
  requestToken: "Y2F0YWxvZy1vbmx5",
};

async function closeAccount(page: Page) {
  const menu = page.locator("#account-menu");
  if (await menu.isVisible()) await page.locator("#account-trigger").click();
  await expect(menu).toHaveCount(0);
}

async function install(page: Page, session = false) {
  const fixture = {
    writes: [] as {
      path: string;
      body: unknown;
      headers: Record<string, string>;
    }[],
    fail: 0,
    organization: true,
    sessionDisabled: false,
    sessionFailure: 0,
    identityContext: `v1.${"A".repeat(43)}`,
    actor: principal,
    holdWrite: null as Promise<void> | null,
  };
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname === "/api/auth/session" && fixture.sessionFailure)
      return route.fulfill({
        status: fixture.sessionFailure,
        json: { version: 1, code: "unavailable" },
      });
    if (url.pathname === "/api/auth/session")
      return route.fulfill({
        json: {
          version: 1,
          ...(fixture.sessionDisabled
            ? { mode: "disabled" }
            : session
              ? {
                  mode: "session",
                  state: "authenticated",
                  authActionRequestToken: "YXV0aC1h",
                  requestToken: context.requestToken,
                  identityContext: fixture.identityContext,
                  displayName: "Administrator",
                }
              : { mode: "development" }),
        },
      });
    const catalog = session
      ? {
          ...context,
          mode: "session",
          identityContext: fixture.identityContext,
          actorPrincipalId: fixture.actor,
        }
      : context;
    if (url.pathname === "/api/v1/catalog/context")
      return route.fulfill({ json: catalog });
    if (url.pathname === `/api/v1/organizations/${org}/context`)
      return route.fulfill({
        json: {
          schemaVersion: "braid.organization-context.v1",
          admissionVersion: "1",
          catalog,
        },
      });
    if (url.pathname === "/api/v1/health")
      return route.fulfill({
        json: { status: "ok", service: "braid-api", apiVersion: "v1" },
      });
    if (request.method() !== "GET") {
      fixture.writes.push({
        path: url.pathname,
        body: request.postData() ? request.postDataJSON() : null,
        headers: request.headers(),
      });
      if (fixture.holdWrite) await fixture.holdWrite;
      if (fixture.fail)
        return route.fulfill({
          status: fixture.fail,
          json: {
            code:
              fixture.fail === 412 ? "preconditionFailed" : "lastAdministrator",
          },
        });
      if (url.pathname.endsWith("/invitations"))
        return route.fulfill({
          json: {
            invitationId: principal,
            redemptionPath: `/organizations/${org}/invitations#${"a".repeat(64)}`,
          },
        });
      return route.fulfill({ json: { outcome: "updated" } });
    }
    if (url.pathname === path)
      return route.fulfill(
        fixture.organization
          ? {
              headers: { ETag: etag },
              json: {
                version: 7,
                members: [
                  {
                    principalId: principal,
                    active: true,
                    role: "administrator",
                    version: 1,
                    principalActive: true,
                    bindings: [],
                  },
                ],
                invitations: [],
                audit: [],
              },
            }
          : { status: 404, json: { code: "notFound" } },
      );
    if (url.pathname === projectPath)
      return route.fulfill({
        headers: { ETag: etag },
        json: {
          version: 7,
          members: [
            {
              principalId: principal,
              active: true,
              role: "runner",
              version: 1,
              payloadRead: false,
              payloadVersion: 0,
            },
          ],
          grants: [],
        },
      });
    return route.fulfill({ status: 404, json: { code: "notFound" } });
  });
  return fixture;
}

test("account warning follows unsent administration forms independently", async ({
  page,
}, testInfo) => {
  const fixture = await install(page, true);
  await page.goto("/administration");
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  const warning = page.locator("#sign-in-navigation-warning");
  const openAccount = async () => {
    // Outside pointer presses close the menu on the next Angular render.
    await page.evaluate(
      () =>
        new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
    );
    await expect(async () => {
      if (!(await page.locator("#account-menu").isVisible()))
        await page.locator("#account-trigger").click();
      await expect(page.locator("#account-menu")).toBeVisible();
    }).toPass();
  };
  await openAccount();
  await expect(warning).toHaveCount(0);
  await page.screenshot({
    path: testInfo.outputPath("administration-account-clean.png"),
  });
  await page
    .getByLabel("Organization role", { exact: true })
    .first()
    .selectOption("member");
  await openAccount();
  await expect(warning).toBeVisible();
  await closeAccount(page);
  await page
    .getByRole("button", { name: "Refresh access", exact: true })
    .click();
  await openAccount();
  await expect(warning).toBeVisible();
  await page
    .getByLabel("Organization role", { exact: true })
    .first()
    .selectOption("administrator");
  await openAccount();
  await expect(warning).toHaveCount(0);
  await page
    .getByLabel("Existing principal ID", { exact: true })
    .fill("unfinished principal");
  await openAccount();
  await expect(warning).toBeVisible();
  await page.screenshot({
    path: testInfo.outputPath("administration-account-unsent.png"),
  });
  await page
    .getByLabel("Existing principal ID", { exact: true })
    .fill(principal);
  await page
    .getByRole("button", { name: "Admit existing principal", exact: true })
    .click();
  await expect.poll(() => fixture.writes.length).toBe(1);
  await expect(
    page.getByRole("status").filter({ hasText: "Change saved." }),
  ).toBeVisible();
  await openAccount();
  await expect(warning).toHaveCount(0);
  await page
    .getByLabel("Fixed Entra tenant ID", { exact: true })
    .fill("unfinished invitation");
  await page
    .getByLabel("Existing principal ID", { exact: true })
    .fill(otherProject);
  await page
    .getByRole("button", { name: "Admit existing principal", exact: true })
    .click();
  await expect.poll(() => fixture.writes.length).toBe(2);
  await expect(
    page.getByRole("status").filter({ hasText: "Change saved." }),
  ).toBeVisible();
  await openAccount();
  await expect(warning).toBeVisible();
  await page.getByLabel("Fixed Entra tenant ID", { exact: true }).fill("");
  await page.getByLabel("Existing principal ID", { exact: true }).fill("");
  await openAccount();
  await expect(warning).toHaveCount(0);
});

test("account warning includes an in-flight administration mutation but not a completed row action", async ({
  page,
}) => {
  const fixture = await install(page, true);
  let release!: () => void;
  fixture.holdWrite = new Promise<void>((resolve) => {
    release = resolve;
  });
  try {
    await page.goto("/administration");
    await page.getByRole("button", { name: "Suspend", exact: true }).click();
    await expect.poll(() => fixture.writes.length).toBe(1);
    await page.locator("#account-trigger").click();
    await expect(page.locator("#sign-in-navigation-warning")).toBeVisible();
    release();
    await expect(
      page.getByRole("status").filter({ hasText: "Change saved." }),
    ).toBeVisible();
    await expect(page.locator("#sign-in-navigation-warning")).toHaveCount(0);
  } finally {
    release();
  }
});

test("projects you administer load without a click, and a read that settles after an owner change never shows the old owner's projects", async ({
  page,
}) => {
  const fixture = await install(page, true);
  let release!: () => void;
  const held = new Promise<void>((resolve) => (release = resolve));
  let reads = 0;
  const oldProject = {
    ...structuredClone(synthetic.project),
    name: "Previous owner project",
    scope: { organizationId: org, projectId: project },
  };
  oldProject.access.role = "administrator";
  const newProject = {
    ...structuredClone(oldProject),
    name: "Current owner project",
  };
  await page.route(
    `**/api/v1/organizations/${org}/projects*`,
    async (route) => {
      const read = ++reads;
      await held;
      await route
        .fulfill({
          json: {
            projects: [read === 1 ? oldProject : newProject],
            nextCursor: null,
          },
        })
        .catch(() => {});
    },
  );
  await page.goto("/administration");
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  await expect.poll(() => reads).toBe(1);
  await expect(
    page.getByText("Loading projects you administer…"),
  ).toBeVisible();
  // Another owner is admitted before the first read settles.
  fixture.identityContext = `v1.${"C".repeat(42)}A`;
  fixture.actor = otherProject;
  await page.locator("#account-trigger").click();
  await page
    .getByRole("button", { name: "Check session", exact: true })
    .click();
  await closeAccount(page);
  await page
    .getByRole("button", { name: "Refresh access", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  await expect.poll(() => reads).toBe(2);
  release();
  await expect(
    page
      .getByRole("list", { name: "Projects you administer" })
      .getByText("Current owner project", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("Previous owner project")).toHaveCount(0);
  expect(reads).toBe(2);
});

test("unavailable session checks hide administration drafts and restore only the same owner", async ({
  page,
}) => {
  const fixture = await install(page, true);
  await page.goto("/administration");
  await page.getByLabel("Fixed Entra tenant ID", { exact: true }).fill(org);
  await page
    .getByRole("button", { name: "Create invitation", exact: true })
    .click();
  const link = page.getByLabel("Private invitation link", { exact: true });
  await expect(link).toHaveValue(/\/invitations#[a-f0-9]{64}$/);
  const original = await link.inputValue();
  await page
    .getByLabel("Existing principal ID", { exact: true })
    .fill("unsubmitted principal");
  await page
    .getByLabel("Organization role", { exact: true })
    .first()
    .selectOption("member");
  const checkSession = async () => {
    if (!(await page.locator("#account-menu").isVisible()))
      await page.locator("#account-trigger").click();
    await page
      .getByRole("button", { name: "Check session", exact: true })
      .click();
    await closeAccount(page);
  };
  fixture.sessionFailure = 503;
  await checkSession();
  await expect(link).toHaveCount(0);
  await expect(
    page.getByLabel("Existing principal ID", { exact: true }),
  ).toHaveCount(0);
  fixture.sessionFailure = 0;
  await checkSession();
  await page
    .getByRole("button", { name: "Refresh access", exact: true })
    .click();
  await expect(link).toHaveValue(original);
  await expect(
    page.getByLabel("Existing principal ID", { exact: true }),
  ).toHaveValue("unsubmitted principal");
  await expect(
    page.getByLabel("Organization role", { exact: true }).first(),
  ).toHaveValue("member");
  fixture.identityContext = `v1.${"B".repeat(42)}A`;
  fixture.actor = otherProject;
  await checkSession();
  await page
    .getByRole("button", { name: "Refresh access", exact: true })
    .click();
  await expect(
    page.getByLabel("Existing principal ID", { exact: true }),
  ).toHaveValue("");
  await expect(link).toHaveCount(0);
  await expect(
    page.getByLabel("Organization role", { exact: true }).first(),
  ).toHaveValue("administrator");
  expect(fixture.writes).toHaveLength(1);
});

test("unrelated successful and refused edits preserve the one-time invitation link", async ({
  page,
}) => {
  const fixture = await install(page, true);
  await page.goto("/administration");
  await page.getByLabel("Fixed Entra tenant ID", { exact: true }).fill(org);
  await page
    .getByRole("button", { name: "Create invitation", exact: true })
    .click();
  const link = page.getByLabel("Private invitation link", { exact: true });
  await expect(link).toHaveValue(/\/invitations#[a-f0-9]{64}$/);
  const original = await link.inputValue();
  await page.getByRole("button", { name: "Suspend", exact: true }).click();
  await expect.poll(() => fixture.writes.length).toBe(2);
  await expect(link).toHaveValue(original);
  fixture.fail = 412;
  await page.getByRole("button", { name: "Suspend", exact: true }).click();
  await expect.poll(() => fixture.writes.length).toBe(3);
  await expect(link).toHaveCount(0);
  fixture.fail = 0;
  await page
    .getByRole("button", { name: "Refresh access", exact: true })
    .click();
  await expect(link).toHaveValue(original);
  expect(fixture.writes).toHaveLength(3);
});

test("organization role is separate from project access and changes carry the observed condition", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto("/administration");
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Organization admission doesn't grant access to any project.",
      { exact: false },
    ),
  ).toBeVisible();
  await page.getByRole("button", { name: "Suspend", exact: true }).click();
  await expect.poll(() => fixture.writes.length).toBe(1);
  expect(fixture.writes[0]).toMatchObject({
    path: `${path}/members/${principal}`,
    body: { active: false, role: "administrator" },
    headers: { "if-match": etag, "x-xsrf-token": context.requestToken },
  });
});

test("project payload permission is an explicit action independent of role", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto("/administration");
  await page.getByLabel("Project ID", { exact: true }).fill(project);
  await page.getByRole("button", { name: "Open project access" }).click();
  await page.getByRole("button", { name: "Grant payload read" }).click();
  await expect.poll(() => fixture.writes.length).toBe(1);
  expect(fixture.writes[0]).toMatchObject({
    path: `${projectPath}/members/${principal}/payload-read`,
    body: { active: true },
  });
});

test("editing a draft project selector cannot redirect an already loaded member action", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto("/administration");
  await page.getByLabel("Project ID", { exact: true }).fill(project);
  await page.getByRole("button", { name: "Open project access" }).click();
  await expect(
    page.getByRole("button", { name: "Remove member", exact: true }),
  ).toBeVisible();
  await page.getByLabel("Project ID", { exact: true }).fill(otherProject);
  await page
    .getByRole("button", { name: "Remove member", exact: true })
    .click();
  await expect.poll(() => fixture.writes.length).toBe(1);
  expect(fixture.writes[0]?.path).toBe(`${projectPath}/members/${principal}`);
});

test("a concurrent edit is not retried and requires an explicit fresh read", async ({
  page,
}) => {
  const fixture = await install(page);
  fixture.fail = 412;
  await page.goto("/administration");
  await page.getByRole("button", { name: "Suspend", exact: true }).click();
  await expect(
    page
      .getByRole("status")
      .filter({ hasText: "Administration changed since you opened it" }),
  ).toBeVisible();
  expect(fixture.writes).toHaveLength(1);
  await expect(
    page.getByRole("button", { name: "Suspend", exact: true }),
  ).toHaveCount(0);
  fixture.fail = 0;
  await page.getByRole("button", { name: "Refresh access" }).click();
  await expect(
    page.getByRole("button", { name: "Suspend", exact: true }),
  ).toBeVisible();
  expect(fixture.writes).toHaveLength(1);
});

test("private invitation link is shown once and not retained after reloading", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto("/administration");
  await page.getByLabel("Fixed Entra tenant ID", { exact: true }).fill(org);
  await page
    .getByRole("button", { name: "Create invitation", exact: true })
    .click();
  await expect(
    page.getByLabel("Private invitation link", { exact: true }),
  ).toHaveValue(/\/invitations#[a-f0-9]{64}$/);
  expect(fixture.writes[0]?.body).toMatchObject({
    tenantId: org,
    expectedObjectId: null,
    projectRoles: [],
  });
  await page.reload();
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByLabel("Private invitation link", { exact: true }),
  ).toHaveCount(0);
  expect(
    await page.evaluate(() => [localStorage.length, sessionStorage.length]),
  ).toEqual([0, 0]);
  await page.getByLabel("Optional project ID", { exact: true }).fill(project);
  fixture.sessionDisabled = true;
  await page.getByRole("button", { name: "Refresh access" }).click();
  await expect(
    page.getByLabel("Optional project ID", { exact: true }),
  ).toHaveCount(0);
  fixture.sessionDisabled = false;
  await page.getByRole("button", { name: "Refresh access" }).click();
  await expect(
    page.getByRole("button", { name: "Refresh access" }),
  ).toBeEnabled();
  await page.getByRole("button", { name: "Refresh access" }).click();
  await expect(
    page.getByLabel("Optional project ID", { exact: true }),
  ).toHaveValue("");
});

test("non-organization administrators can still open an independently administered project", async ({
  page,
}) => {
  const fixture = await install(page);
  fixture.organization = false;
  await page.goto("/administration");
  await expect(
    page.getByText("Organization administration is available only", {
      exact: false,
    }),
  ).toBeVisible();
  await page.getByLabel("Project ID", { exact: true }).fill(project);
  await page.getByRole("button", { name: "Open project access" }).click();
  await expect(
    page.getByRole("button", { name: "Remove member", exact: true }),
  ).toBeVisible();
});

test("invitation landing removes its credential from the address and browser storage", async ({
  page,
}) => {
  await install(page);
  await page.goto("/invitations#" + "c".repeat(64));
  await expect(page).toHaveURL(/\/invitations$/);
  await expect(
    page.getByRole("heading", { name: "Join an organization" }),
  ).toBeVisible();
  expect(
    await page.evaluate(() => [localStorage.length, sessionStorage.length]),
  ).toEqual([0, 0]);
});

test("administration remains keyboard reachable without widening the mobile page", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await install(page);
  await page.goto("/administration");
  await expect(
    page.getByRole("heading", { name: "Organization members", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Refresh access" }).focus();
  await page.keyboard.press("Tab");
  await expect(
    page.getByRole("link", { name: "Data retention" }),
  ).toBeFocused();
  await page.keyboard.press("Tab");
  await expect(
    page
      .getByRole("combobox", { name: "Organization role", exact: true })
      .first(),
  ).toBeFocused();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});
