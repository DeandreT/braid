import { expect, test, type Page } from "@playwright/test";

// The default browser suite runs the packaged host without a catalog or browser sign-in.
const connectionNotice = (page: Page) =>
  page.getByRole("status", { name: "Workspace connection" });
const disabledHost = (page: Page) =>
  page.getByRole("heading", { name: "No workspace on this host" });

test("the packaged C# host serves nested Angular navigation and preserves API 404s", async ({
  page,
  request,
  baseURL,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const origin = process.env.BRAID_EDIT_SHELL_DLL
    ? baseURL!
    : "http://127.0.0.1:4101";
  await page.goto(
    origin +
      "/catalog/organizations/10000000-0000-4000-8000-000000000001/projects",
  );
  await expect(page.getByRole("link", { name: "braid home" })).toBeVisible();
  await expect(disabledHost(page)).toBeVisible();
  const missing = await request.get(origin + "/api/v1/missing");
  expect(missing.status()).toBe(404);
  expect(missing.headers()["content-type"] ?? "").not.toContain("text/html");
  const signIn = await request.get(origin + "/sign-in");
  expect(signIn.status()).toBe(200);
  expect(signIn.headers()["content-type"] ?? "").toContain("text/html");
  expect(errors).toEqual([]);
});

test("the application opens the catalog and stays quiet while the service is connected", async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const health = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v1/health",
  );
  await page.goto("/");
  await expect(page).toHaveURL(/\/catalog$/);
  expect((await health).status()).toBe(200);
  await expect(disabledHost(page)).toBeVisible();
  await expect(connectionNotice(page)).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Examples" })).toHaveCount(0);
  expect(errors).toEqual([]);
  await page.screenshot({
    path: testInfo.outputPath("workspace.png"),
    fullPage: true,
  });
});

test("an unavailable service is reported in the shell and can be retried without refreshing", async ({
  page,
}) => {
  await page.route("**/api/v1/health", (route) =>
    route.fulfill({ status: 503, body: "Unavailable" }),
  );
  await page.goto("/");
  await expect(connectionNotice(page)).toContainText(
    "Check that your workspace service is running, then try again.",
  );
  await page.unroute("**/api/v1/health");
  await page.getByRole("button", { name: "Check connection" }).click();
  await expect(connectionNotice(page)).toHaveCount(0);
});

test("an incompatible health response is not reported as connected", async ({
  page,
}) => {
  await page.route("**/api/v1/health", (route) =>
    route.fulfill({
      json: { status: "ok", service: "another-service", apiVersion: "v2" },
    }),
  );
  await page.goto("/");
  await expect(connectionNotice(page)).toContainText("Workspace unavailable.");
});

test("a host without browser sign-in explains its sign-in page", async ({
  page,
}) => {
  await page.goto("/sign-in?returnTo=%2Fcatalog");
  await expect(
    page.getByRole("heading", { level: 1, name: "Sign in to braid" }),
  ).toBeVisible();
  await expect(page.getByRole("status").first()).toHaveText(
    "This host has no browser sign-in.",
  );
  await expect(
    page.getByRole("button", { name: "Sign in with Microsoft" }),
  ).toHaveCount(0);
});

for (const target of ["/help", "/help/reports#coverage"]) {
  test(`signed-out help navigation returns through sign-in: ${target}`, async ({
    page,
  }) => {
    let helpReads = 0;
    page.on("request", (request) => {
      if (new URL(request.url()).pathname.startsWith("/api/v1/help/"))
        ++helpReads;
    });
    await page.route("**/api/v1/health", (route) =>
      route.fulfill({
        json: { status: "ok", service: "braid-api", apiVersion: "v1" },
      }),
    );
    await page.route("**/api/auth/session", (route) =>
      route.fulfill({
        json: {
          version: 1,
          mode: "session",
          state: "anonymous",
          authActionRequestToken: "YXV0aC1h",
          requestToken: null,
          identityContext: null,
          displayName: null,
        },
      }),
    );
    await page.goto(target);
    await expect(
      page.getByRole("heading", { level: 1, name: "Sign in to braid" }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Sign in with Microsoft", exact: true }),
    ).toBeVisible();
    const destination = new URL(page.url());
    expect(destination.pathname).toBe("/sign-in");
    expect(destination.searchParams.get("returnTo")).toBe(target);
    expect(helpReads).toBe(0);
  });
}

test("the small-screen layout stays readable and keyboard accessible", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await expect(disabledHost(page)).toBeVisible();
  await page.keyboard.press("Tab");
  await expect(
    page.getByRole("link", { name: "Skip to content" }),
  ).toBeFocused();
  await expect(
    page.getByRole("link", { name: "Skip to content" }),
  ).toBeVisible();
  const overflows = await page.evaluate(
    () => document.documentElement.scrollWidth > window.innerWidth,
  );
  expect(overflows).toBe(false);
});
