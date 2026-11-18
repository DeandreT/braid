import {
  expect,
  test,
  type Page,
  type Request,
  type Route,
} from "@playwright/test";
import { synthetic } from "./catalog.fixture";
import { workspaceFixture } from "./workspace-chat.fixture";

// Synthetic two-organization Session fixture: one cookie, one principal, A is the root.
// The root is the synthetic catalog organization, which the shared chat fixture also serves.
const rootOrg = synthetic.revision.scope.organizationId;
const otherOrg = "22222222-2222-4222-8222-222222222222";
const identityContext = `v1.${"A".repeat(43)}`;
const names = {
  [rootOrg]: "Root organization",
  [otherOrg]: "Second organization",
};

function context(organizationId: string) {
  return {
    ...synthetic.context,
    features: {
      ...synthetic.context.features,
      chat: true,
      manageMembership: true,
    },
    mode: "session",
    identityContext,
    allowedOrganizationId: organizationId,
    organizationName: names[organizationId as keyof typeof names],
    requestToken:
      organizationId === rootOrg ? "cm9vdC10b2tlbg" : "c2Vjb25kLXRva2Vu",
  };
}

function project(organizationId: string) {
  return {
    ...synthetic.project,
    scope: { organizationId, projectId: synthetic.project.scope.projectId },
    name: organizationId === rootOrg ? "Root project" : "Second project",
  };
}

async function install(
  page: Page,
  state: "authenticated" | "anonymous" = "authenticated",
  fallback = false,
  sessionCookie?: string,
) {
  const fixture = {
    state,
    reads: [] as string[],
    logins: [] as Request[],
  };
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    fixture.reads.push(path);
    if (
      sessionCookie &&
      (await request.headerValue("cookie")) !== sessionCookie
    )
      return route.fulfill({ status: 401, json: { code: "identityRejected" } });
    if (path === "/api/v1/health")
      return route.fulfill({
        json: { status: "ok", service: "braid-api", apiVersion: "v1" },
      });
    if (path === "/api/auth/session")
      return route.fulfill({
        json: {
          version: 1,
          mode: "session",
          state: fixture.state,
          authActionRequestToken: "YXV0aC1h",
          requestToken:
            fixture.state === "authenticated" ? "cm9vdC10b2tlbg" : null,
          identityContext:
            fixture.state === "authenticated" ? identityContext : null,
          displayName: fixture.state === "authenticated" ? "Member" : null,
        },
      });
    if (path === "/api/auth/login") {
      fixture.logins.push(request);
      return route.fulfill({
        status: 503,
        json: { version: 1, code: "unavailable" },
      });
    }
    if (path === "/api/v1/catalog/context")
      return route.fulfill({ json: context(rootOrg) });
    if (path === "/api/v1/organizations")
      return route.fulfill({
        json: {
          schemaVersion: "braid.organizations.v1",
          organizations: [rootOrg, otherOrg].map((organizationId) => ({
            organizationId,
            name: names[organizationId],
          })),
        },
      });
    const qualified =
      /^\/api\/v1\/organizations\/([0-9a-f-]{36})\/(context|projects)$/.exec(
        path,
      );
    if (qualified && qualified[2] === "context")
      return qualified[1] === otherOrg || qualified[1] === rootOrg
        ? route.fulfill({
            json: {
              schemaVersion: "braid.organization-context.v1",
              admissionVersion: "1",
              catalog: context(qualified[1]),
            },
          })
        : route.fulfill({ status: 404, json: { code: "notFound" } });
    if (qualified) {
      return route.fulfill({
        json: { projects: [project(qualified[1])], nextCursor: null },
      });
    }
    const administration = [rootOrg, otherOrg].find(
      (id) => path === `/api/v1/organizations/${id}/administration`,
    );
    if (administration)
      return route.fulfill({
        // The client admits only the organization-bound validator for this snapshot.
        headers: { ETag: `"admin-${administration}-7"` },
        json: { version: 7, members: [], invitations: [], audit: [] },
      });
    if (fallback) return route.fallback();
    return route.fulfill({ status: 404, json: { code: "notFound" } });
  });
  return fixture;
}

const projects = (organizationId: string) =>
  `/catalog/organizations/${organizationId}/projects`;

test("bare catalog landing cannot clear a dirty non-root administration view", async ({
  page,
}) => {
  const fixture = await install(page);
  const source = `/organizations/${otherOrg}/administration`;
  await page.goto(source);
  const principal = page.getByLabel("Existing principal ID", { exact: true });
  await principal.fill("keep this non-root draft");
  const prompts: string[] = [];
  let accept = false;
  page.on("dialog", async (dialog) => {
    prompts.push(dialog.message());
    if (accept) await dialog.accept();
    else await dialog.dismiss();
  });
  const reads = fixture.reads.length;
  await page.getByRole("link", { name: "braid home", exact: true }).click();
  await expect(page).toHaveURL(source);
  await expect(principal).toHaveValue("keep this non-root draft");
  await expect(page.locator(".workspace-organization strong")).toHaveText(
    names[otherOrg],
  );
  expect(prompts).toHaveLength(1);
  expect(fixture.reads.slice(reads)).not.toContain("/api/v1/organizations");
  accept = true;
  await page.getByRole("link", { name: "braid home", exact: true }).click();
  await expect(page).toHaveURL("/catalog");
  await expect(
    page.getByRole("list", { name: "Your organizations" }),
  ).toBeVisible();
  expect(prompts).toHaveLength(2);
});

test("auth redirect from history asks once and cancellation keeps the source URL", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto(`/organizations/${rootOrg}/administration`);
  await switchTo(page, names[otherOrg]);
  await page.getByRole("link", { name: "Administration", exact: true }).click();
  const source = `/organizations/${otherOrg}/administration`;
  await expect(page).toHaveURL(source);
  await page
    .getByLabel("Existing principal ID", { exact: true })
    .fill("retained during sign-in");
  fixture.state = "anonymous";
  await page.locator("#account-trigger").click();
  const observed = page.waitForResponse("**/api/auth/session");
  await page
    .getByRole("button", { name: "Check session", exact: true })
    .click();
  await observed;
  const prompts: string[] = [];
  page.on("dialog", async (dialog) => {
    prompts.push(dialog.message());
    await dialog.dismiss();
  });
  const reads = fixture.reads.length;
  await page.evaluate(() => history.go(-2));
  await expect.poll(() => prompts.length).toBe(1);
  await expect(page).toHaveURL(source);
  expect(prompts).toEqual([
    "Leave this workspace? Unsent work and recovery information kept only in this tab may be lost. A sent request may already have committed.",
  ]);
  expect(fixture.reads.slice(reads)).not.toContain(
    `/api/v1/organizations/${rootOrg}/context`,
  );
});

for (const sharedChat of [false, true]) {
  test(`organization leave preserves administration${sharedChat ? " and shared chat" : ""} on cancel and asks once`, async ({
    page,
  }) => {
    if (sharedChat) await workspaceFixture(page);
    const fixture = await install(page, "authenticated", sharedChat);
    await page.goto("/administration");
    const principal = page.getByLabel("Existing principal ID", { exact: true });
    await principal.fill("unfinished principal");
    if (sharedChat) {
      // The chat panel opens by default; open it only if it was closed.
      if (
        (await page
          .locator("#workspace-chat-toggle")
          .getAttribute("aria-expanded")) === "false"
      )
        await page.locator("#workspace-chat-toggle").click();
      await page
        .getByRole("button", { name: "New conversation", exact: true })
        .click();
      await page
        .getByLabel("Question", { exact: true })
        .fill("Keep this unsent question");
    }
    const prompts: string[] = [];
    let accept = false;
    page.on("dialog", async (dialog) => {
      prompts.push(dialog.message());
      if (accept) await dialog.accept();
      else await dialog.dismiss();
    });
    await switchTo(page, names[otherOrg]);
    await expect(page).toHaveURL("/administration");
    await expect(principal).toHaveValue("unfinished principal");
    if (sharedChat)
      await expect(page.getByLabel("Question", { exact: true })).toHaveValue(
        "Keep this unsent question",
      );
    expect(prompts).toEqual([
      "Leave this workspace? Unsent work and recovery information kept only in this tab may be lost. A sent request may already have committed.",
    ]);
    expect(fixture.reads).not.toContain(
      `/api/v1/organizations/${otherOrg}/context`,
    );
    accept = true;
    await switchTo(page, names[otherOrg]);
    await expect(page).toHaveURL(projects(otherOrg));
    await expect(
      page.getByRole("link", { name: "Second project" }),
    ).toBeVisible();
    expect(prompts).toHaveLength(2);
    await expect(principal).toHaveCount(0);
  });
}

async function switchTo(page: Page, name: string) {
  await page.locator("#organization-switcher").click();
  await page
    .locator("#organization-switcher-menu")
    .getByRole("button", { name, exact: true })
    .click();
}

test("the chooser requires a choice and the switcher opens the destination home", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto("/catalog");
  const choices = page.getByRole("list", { name: "Your organizations" });
  await expect(choices.getByRole("link")).toHaveText([
    names[rootOrg],
    names[otherOrg],
  ]);
  await choices.getByRole("link", { name: names[otherOrg] }).click();
  await expect(page).toHaveURL(projects(otherOrg));
  await expect(
    page.getByRole("link", { name: "Second project" }),
  ).toBeVisible();
  await expect(page.locator(".workspace-organization strong")).toHaveText(
    names[otherOrg],
  );
  expect(fixture.reads).toContain(`/api/v1/organizations/${otherOrg}/context`);

  // Standalone links keep the selected organization; root links stay root-only.
  await expect(
    page.getByRole("link", { name: "Notifications" }),
  ).toHaveAttribute("href", `/organizations/${otherOrg}/notifications`);
  await switchTo(page, names[rootOrg]);
  await expect(page).toHaveURL(projects(rootOrg));
  await expect(page.getByRole("link", { name: "Root project" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Second project" })).toHaveCount(
    0,
  );
  await expect(page.locator(".workspace-organization strong")).toHaveText(
    names[rootOrg],
  );
  await expect(
    page.getByRole("link", { name: "Notifications" }),
  ).toHaveAttribute("href", "/notifications");
});

test("a late response for the organization left behind never appears under the next", async ({
  page,
}) => {
  await install(page);
  const pending = await holdReply(
    page,
    `**/api/v1/organizations/${rootOrg}/projects?*`,
    () => ({
      json: { projects: [project(rootOrg)], nextCursor: null },
    }),
  );
  await page.goto(projects(rootOrg));
  await pending.entered;
  try {
    await switchTo(page, names[otherOrg]);
    await expect(projectLink(page, "Second")).toBeVisible();
  } finally {
    await pending.finish();
  }
  await expect(page.getByRole("link", { name: "Root project" })).toHaveCount(0);
  await expect(page).toHaveURL(projects(otherOrg));
  await expect(page.locator(".workspace-organization strong")).toHaveText(
    names[otherOrg],
  );
});

// Hold one synthetic reply until the browser has crossed the boundary. Join the
// original handler even if navigation cancels its request before it can reply.
async function holdReply(
  page: Page,
  path: string,
  reply: () => Parameters<Route["fulfill"]>[0],
) {
  const latch = () => {
    let open!: () => void;
    const promise = new Promise<void>((resolve) => (open = resolve));
    return { open, promise };
  };
  const entered = latch(),
    release = latch(),
    finished = latch();
  await page.route(
    path,
    async (route) => {
      entered.open();
      try {
        await release.promise;
        await route.fulfill(reply());
      } finally {
        finished.open();
      }
    },
    { times: 1 },
  );
  return {
    entered: entered.promise,
    finish: async () => {
      release.open();
      await finished.promise;
    },
  };
}

const projectLink = (page: Page, name: string) =>
  page.getByRole("link", { name: `${name} project`, exact: true });

test("suspension during a switch refuses the destination without reading either project's data", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto(projects(rootOrg));
  await expect(projectLink(page, "Root")).toBeVisible();
  const reads = fixture.reads.length;
  const pending = await holdReply(
    page,
    `**/organizations/${otherOrg}/context`,
    () => ({ status: 404, json: { code: "notFound" } }),
  );
  await switchTo(page, names[otherOrg]);
  await pending.entered;
  await pending.finish();
  await expect(
    page.getByRole("heading", { name: "Not found", exact: true }),
  ).toBeVisible();
  await expect(page).toHaveURL(projects(otherOrg));
  await expect(projectLink(page, "Root")).toHaveCount(0);
  await expect(projectLink(page, "Second")).toHaveCount(0);
  expect(
    fixture.reads.slice(reads).filter((path) => path.endsWith("/projects")),
  ).toEqual([]);
});

test("sign-out during a switch hides protected content despite a late successful context", async ({
  page,
}) => {
  const fixture = await install(page);
  await page.goto(projects(rootOrg));
  await expect(projectLink(page, "Root")).toBeVisible();
  const pending = await holdReply(
    page,
    `**/organizations/${otherOrg}/context`,
    () => ({
      json: {
        schemaVersion: "braid.organization-context.v1",
        admissionVersion: "1",
        catalog: context(otherOrg),
      },
    }),
  );
  await switchTo(page, names[otherOrg]);
  await pending.entered;
  try {
    await page.route(
      "**/api/auth/logout",
      async (route) => {
        expect(route.request().method()).toBe("POST");
        fixture.state = "anonymous";
        await route.fulfill({ status: 204 });
      },
      { times: 1 },
    );
    await page.locator("#account-trigger").click();
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    await expect.poll(() => fixture.state).toBe("anonymous");
    await expect(page.locator("#organization-switcher")).toHaveCount(0);
  } finally {
    await pending.finish();
  }
  await expect(projectLink(page, "Root")).not.toBeVisible();
  await expect(projectLink(page, "Second")).not.toBeVisible();
  expect(fixture.reads).not.toContain(
    `/api/v1/organizations/${otherOrg}/projects`,
  );
});

test("a late root CSV cannot download in the destination workspace", async ({
  page,
}) => {
  await install(page);
  const json = {
    entries: [],
    nextCursor: null,
    since: "2026-10-01T00:00:00Z",
    until: "2026-11-01T00:00:00Z",
    organizationWide: true,
  };
  const audit = `**/organizations/${rootOrg}/audit?*`;
  await page.route(audit, (route) => route.fulfill({ json }));
  await page.goto("/audit");
  const button = page.getByRole("button", { name: "Export filtered CSV" });
  const [valid] = await Promise.all([
    page.waitForEvent("download"),
    button.click(),
  ]);
  expect(valid.suggestedFilename()).toBe("braid-audit.csv");
  const downloads: string[] = [];
  page.on("download", (item) => downloads.push(item.suggestedFilename()));
  const pending = await holdReply(page, audit, () => ({ json }));
  await button.click();
  await pending.entered;
  try {
    await switchTo(page, names[otherOrg]);
    await expect(projectLink(page, "Second")).toBeVisible();
  } finally {
    await pending.finish();
  }
  await expect(page).toHaveURL(projects(otherOrg));
  // A subsequent action/refresh provides a browser round trip after releasing the response.
  await Promise.all([
    page.waitForResponse(`**/organizations/${otherOrg}/projects*`),
    page.getByRole("button", { name: "Refresh catalog" }).click(),
  ]);
  await expect(projectLink(page, "Second")).toBeVisible();
  expect(downloads).toEqual([]);
});

test("two tabs under one session keep independent organizations", async ({
  context: browser,
  baseURL,
}, testInfo) => {
  const cookie = { name: "synthetic_session", value: "member-a-and-b" };
  await browser.addCookies([{ ...cookie, url: baseURL!, httpOnly: true }]);
  const first = await browser.newPage();
  const second = await browser.newPage();
  const sharedCookie = `${cookie.name}=${cookie.value}`;
  await install(first, "authenticated", false, sharedCookie);
  await install(second, "authenticated", false, sharedCookie);
  await first.goto(projects(rootOrg));
  await second.goto(projects(rootOrg));
  await switchTo(second, names[otherOrg]);
  await expect(first.getByRole("link", { name: "Root project" })).toBeVisible();
  await expect(
    second.getByRole("link", { name: "Second project" }),
  ).toBeVisible();
  await first.bringToFront();
  await first.reload();
  await expect(first.getByRole("link", { name: "Root project" })).toBeVisible();
  await expect(
    second.getByRole("link", { name: "Second project" }),
  ).toBeVisible();
  await expect(first).toHaveURL(projects(rootOrg));
  await expect(second).toHaveURL(projects(otherOrg));
  await expect(first.getByRole("link", { name: "Second project" })).toHaveCount(
    0,
  );
  await expect(second.getByRole("link", { name: "Root project" })).toHaveCount(
    0,
  );
  await testInfo.attach("second-tab-organization", {
    body: await second.screenshot(),
    contentType: "image/png",
  });
});

test("malformed qualified standalone routes never fall back to the root", async ({
  page,
}) => {
  await install(page);
  await page.goto("/organizations/not-an-id/notifications");
  await expect(page).toHaveURL("/catalog");
});

test("an invitation link sends its target organization with the secret", async ({
  page,
}) => {
  const fixture = await install(page, "anonymous");
  const secret = "a".repeat(64);
  await page.goto(`/organizations/${otherOrg}/invitations#${secret}`);
  await expect(page).toHaveURL(`/organizations/${otherOrg}/invitations`);
  await page
    .getByRole("button", { name: "Sign in and redeem invitation" })
    .click();
  await expect.poll(() => fixture.logins.length).toBe(1);
  const headers = fixture.logins[0].headers();
  expect(headers["x-braid-invitation"]).toBe(secret);
  expect(headers["x-braid-invitation-organization"]).toBe(otherOrg);
  expect(headers["x-braid-return-to"]).toBe(projects(otherOrg));
});
