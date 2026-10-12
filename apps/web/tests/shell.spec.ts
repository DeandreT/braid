import { expect, test } from "@playwright/test";

test("the packaged C# host serves nested Angular navigation and preserves API 404s", async ({
  page,
  request,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("http://127.0.0.1:4101/projects/example");
  await expect(page.getByRole("heading", { level: 1 })).toContainText(
    "Every mapping.",
  );
  await expect(page.getByRole("status")).toHaveText("Workspace connected");
  const missing = await request.get("http://127.0.0.1:4101/api/v1/missing");
  expect(missing.status()).toBe(404);
  expect(missing.headers()["content-type"] ?? "").not.toContain("text/html");
  expect(errors).toEqual([]);
});

test("the workspace connects to the real C# service", async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1 })).toContainText(
    "Every mapping.",
  );
  await expect(page.getByRole("status")).toHaveText("Workspace connected");
  await page.getByRole("button", { name: "Check connection" }).click();
  await expect(page.getByRole("status")).toHaveText("Workspace connected");
  expect(errors).toEqual([]);
  await page.screenshot({
    path: testInfo.outputPath("workspace.png"),
    fullPage: true,
  });
});

test("an unavailable service can be retried without refreshing", async ({
  page,
}) => {
  await page.route("**/api/v1/health", (route) =>
    route.fulfill({ status: 503, body: "Unavailable" }),
  );
  await page.goto("/");
  await expect(page.getByRole("status")).toHaveText("Workspace unavailable");
  await expect(
    page.getByText(
      "Check that your workspace service is running, then try again.",
    ),
  ).toBeVisible();
  await page.unroute("**/api/v1/health");
  await page.getByRole("button", { name: "Check connection" }).click();
  await expect(page.getByRole("status")).toHaveText("Workspace connected");
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
  await expect(page.getByRole("status")).toHaveText("Workspace unavailable");
});

test("the small-screen layout stays readable and keyboard accessible", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await expect(page.getByRole("status")).toHaveText("Workspace connected");
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
