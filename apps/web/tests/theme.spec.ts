import { expect, test, type Page } from "@playwright/test";

// Synthetic shell: the appearance choice needs no catalog data.
async function install(page: Page): Promise<void> {
  await page.route("**/api/**", (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/auth/session")
      return route.fulfill({ json: { version: 1, mode: "development" } });
    if (path === "/api/v1/health")
      return route.fulfill({
        json: { status: "ok", service: "braid-api", apiVersion: "v1" },
      });
    if (path === "/api/v1/help/sections")
      return route.fulfill({ json: { sections: [] } });
    return route.fulfill({ status: 404, json: { code: "notFound" } });
  });
}

const theme = (page: Page) =>
  page.evaluate(() => document.documentElement.dataset["theme"]);
const token = (page: Page, name: string) =>
  page.evaluate(
    (name) =>
      getComputedStyle(document.documentElement).getPropertyValue(name).trim(),
    name,
  );

async function appearance(page: Page) {
  if (!(await page.locator("#account-menu").isVisible()))
    await page.locator("#account-trigger").click();
  return page.getByRole("group", { name: "Appearance" });
}

test("System is the default and follows the OS setting live", async ({
  page,
}) => {
  await install(page);
  await page.emulateMedia({ colorScheme: "dark" });
  await page.goto("/help");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  const group = await appearance(page);
  await expect(group.getByRole("radio", { name: "System" })).toBeChecked();
  await page.emulateMedia({ colorScheme: "light" });
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await page.emulateMedia({ colorScheme: "dark" });
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
});

test("the menu switches the theme from the keyboard and the choice persists", async ({
  page,
}) => {
  await install(page);
  await page.goto("/help");
  expect(await theme(page)).toBe("light");
  const group = await appearance(page);
  await group.getByRole("radio", { name: "Light" }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(group.getByRole("radio", { name: "Dark" })).toBeChecked();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  expect(
    await page.evaluate(() => getComputedStyle(document.body).colorScheme),
  ).toBe("dark");
  expect(
    await page.evaluate(
      () => getComputedStyle(document.documentElement).backgroundColor,
    ),
  ).not.toBe("rgb(246, 248, 245)");

  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  // An explicit choice wins over the OS setting.
  await page.emulateMedia({ colorScheme: "dark" });
  await (await appearance(page)).getByRole("radio", { name: "Light" }).check();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");

  await (await appearance(page)).getByRole("radio", { name: "System" }).check();
  expect(
    await page.evaluate(() => localStorage.getItem("braid.theme.v1")),
  ).toBeNull();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
});

test("a saved dark choice is applied before the first frame", async ({
  page,
}) => {
  await install(page);
  await page.addInitScript(() => {
    localStorage.setItem("braid.theme.v1", "dark");
    // Rendering is blocked until head scripts and styles load, so the first
    // animation frame shows what the first paint shows.
    requestAnimationFrame(() => {
      const root = document.documentElement;
      (window as unknown as { firstFrame: string[] }).firstFrame = [
        root.dataset["theme"] ?? "",
        getComputedStyle(root).backgroundColor,
      ];
    });
  });
  await page.goto("/help");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  const dark = await page.evaluate(
    () => getComputedStyle(document.documentElement).backgroundColor,
  );
  expect(
    await page.evaluate(
      () => (window as unknown as { firstFrame: string[] }).firstFrame,
    ),
  ).toEqual(["dark", dark]);
});

/** WCAG relative-luminance contrast between two computed rgb() colours. */
function contrast(a: string, b: string): number {
  const luminance = (value: string) => {
    const [r, g, b] = value
      .match(/[\d.]+/g)!
      .slice(0, 3)
      .map((part) => {
        const c = Number(part) / 255;
        return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
      });
    return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
  };
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (high! + 0.05) / (low! + 0.05);
}

test("key dark tokens meet WCAG AA contrast", async ({ page }) => {
  await install(page);
  await page.emulateMedia({ colorScheme: "dark" });
  await page.goto("/help");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  // Resolve each token through a probe so var() chains become rgb() values.
  const resolve = (name: string) =>
    page.evaluate((name) => {
      const probe = document.createElement("span");
      probe.style.color = `var(${name})`;
      document.body.append(probe);
      const value = getComputedStyle(probe).color;
      probe.remove();
      return value;
    }, name);
  const surfaces = [
    "--page",
    "--surface",
    "--surface-subtle",
    "--surface-muted",
  ];
  const pairs: [string, string[], number][] = [
    ["--text", surfaces, 4.5],
    ["--text-muted", surfaces, 4.5],
    ["--text-accent", surfaces, 4.5],
    ["--text-warn", ["--surface", "--surface-warn"], 4.5],
    ["--text-danger", ["--surface", "--surface-danger"], 4.5],
    ["--text-on-accent", ["--primary", "--accent"], 4.5],
    ["--mark-text", ["--mark"], 4.5],
    ["--focus-ring", surfaces, 3],
    ["--wb-control-border", surfaces, 3],
    ...(
      [
        "source",
        "rule",
        "output",
        "evidence",
        "selection",
        "success",
        "running",
        "attention",
        "failure",
        "unavailable",
      ] as const
    ).map((role): [string, string[], number] => [
      `--wb-${role}-text`,
      [`--wb-${role}-tint`, "--surface"],
      4.5,
    ]),
  ];
  for (const [foreground, backgrounds, minimum] of pairs)
    for (const background of backgrounds)
      expect
        .soft(
          contrast(await resolve(foreground), await resolve(background)),
          `${foreground} on ${background}`,
        )
        .toBeGreaterThanOrEqual(minimum);
  expect(await token(page, "--page")).not.toBe("#f6f8f5");
});
