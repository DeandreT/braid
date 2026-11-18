import { test, expect, type Page } from "@playwright/test";
import { synthetic, syntheticCatalog } from "./catalog.fixture";

async function fixture(page: Page) {
  const state = {
    denied: false,
    malformed: false,
    reply: 200,
    writes: [] as { path: string; condition: string; body: unknown }[],
  };
  await syntheticCatalog(page);
  await page.route("**/api/v1/health", (route) =>
    route.fulfill({
      json: {
        status: "ok",
        service: "braid-api",
        version: "0.1.0",
        apiVersion: "v1",
      },
    }),
  );
  await page.route("**/administration/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (!url.pathname.includes("/ai-quotas")) return route.fallback();
    const principal =
      url.pathname.match(/\/principals\/([^/]+)\/ai-quotas/)?.[1] ?? null;
    if (request.method() === "GET") {
      if (state.denied)
        return route.fulfill({ status: 404, json: { code: "notFound" } });
      return route.fulfill({
        headers: {
          ETag: `"ai-quota-${synthetic.org}-${principal ?? "organization"}-9007199254740993"`,
        },
        json: {
          organizationId: synthetic.org,
          principalId: principal,
          version: "9007199254740993",
          limits: [
            {
              period: "day",
              maximumTurns: "12",
              maximumTokens: state.malformed
                ? 9007199254740994
                : "9007199254740995",
            },
          ],
        },
      });
    }
    state.writes.push({
      path: url.pathname,
      condition: request.headers()["if-match"],
      body: request.postDataJSON(),
    });
    if (state.reply === 0) return route.abort("failed");
    return route.fulfill({
      status: state.reply,
      json:
        state.reply === 200
          ? { outcome: "updated" }
          : { code: state.reply === 412 ? "preconditionFailed" : "notFound" },
    });
  });
  await page.goto("/quotas");
  await expect(
    page.getByRole("table", { name: "Current quota policy" }),
  ).toBeVisible();
  return state;
}

test("quota writes preserve exact decimal strings and the scope condition", async ({
  page,
}) => {
  const state = await fixture(page);
  await expect(
    page.getByRole("cell", { name: "9007199254740995", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Edit day", exact: true }).click();
  await page
    .getByRole("textbox", { name: "Maximum turns", exact: true })
    .fill("13");
  await page
    .getByRole("button", { name: "Save quota policy", exact: true })
    .click();
  await expect(
    page.getByText(
      "Quota policy saved. Load current policy before another edit.",
      { exact: true },
    ),
  ).toBeVisible();
  expect(state.writes).toEqual([
    {
      path: `/api/v1/organizations/${synthetic.org}/administration/ai-quotas/day`,
      condition: `"ai-quota-${synthetic.org}-organization-9007199254740993"`,
      body: { maximumTurns: "13", maximumTokens: "9007199254740995" },
    },
  ]);
  await expect(
    page.getByRole("button", { name: "Save quota policy", exact: true }),
  ).toBeDisabled();
});

test("invalid limits stay local and clearing both limits is explicit", async ({
  page,
}) => {
  const state = await fixture(page);
  await page.getByRole("button", { name: "Edit day", exact: true }).click();
  const turns = page.getByRole("textbox", {
    name: "Maximum turns",
    exact: true,
  });
  await turns.fill("01");
  await page
    .getByRole("button", { name: "Save quota policy", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Limits must be positive whole numbers",
  );
  expect(state.writes).toHaveLength(0);
  await turns.fill("");
  await page
    .getByRole("textbox", { name: "Hard token cap", exact: true })
    .fill("");
  await page
    .getByRole("button", { name: "Save quota policy", exact: true })
    .click();
  await expect(
    page.getByText(
      "Quota policy saved. Load current policy before another edit.",
      { exact: true },
    ),
  ).toBeVisible();
  expect(state.writes[0].body).toEqual({
    maximumTurns: null,
    maximumTokens: null,
  });
});

for (const reply of [0, 412]) {
  test(`unconfirmed quota write ${reply} retains values and never resends automatically`, async ({
    page,
  }) => {
    const state = await fixture(page);
    state.reply = reply;
    await page.getByRole("button", { name: "Edit day", exact: true }).click();
    const turns = page.getByRole("textbox", {
      name: "Maximum turns",
      exact: true,
    });
    await turns.fill("17");
    await page
      .getByRole("button", { name: "Save quota policy", exact: true })
      .click();
    await expect(page.getByRole("alert")).toContainText(
      "Your entered limits are retained",
    );
    await expect(turns).toHaveValue("17");
    await expect(
      page.getByRole("button", { name: "Save quota policy", exact: true }),
    ).toBeDisabled();
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await page
      .getByRole("button", { name: "Load current policy", exact: true })
      .click();
    await expect(
      page.getByRole("table", { name: "Current quota policy" }),
    ).toBeVisible();
    await expect(turns).toHaveValue("17");
    expect(state.writes).toHaveLength(1);
  });
}

test("authority loss hides retained editor values on refresh and clears a refused write", async ({
  page,
}) => {
  const state = await fixture(page);
  await page.getByRole("button", { name: "Edit day", exact: true }).click();
  await page
    .getByRole("textbox", { name: "Maximum turns", exact: true })
    .fill("71");
  state.denied = true;
  await page
    .getByRole("button", { name: "Load current policy", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Quota access is unavailable",
  );
  await expect(
    page.getByRole("textbox", { name: "Maximum turns", exact: true }),
  ).toHaveCount(0);
  state.denied = false;
  await page
    .getByRole("button", { name: "Load current policy", exact: true })
    .click();
  await expect(
    page.getByRole("table", { name: "Current quota policy" }),
  ).toBeVisible();
  await expect(
    page.getByRole("textbox", { name: "Maximum turns", exact: true }),
  ).toHaveValue("71");
  state.reply = 404;
  await page
    .getByRole("button", { name: "Save quota policy", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Quota access is unavailable",
  );
  await expect(
    page.getByRole("textbox", { name: "Maximum turns", exact: true }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
});

test("principal selection clears the prior scope and uses its own condition", async ({
  page,
}) => {
  const state = await fixture(page);
  const principal = "70000000-0000-4000-8000-000000000001";
  await page.getByRole("button", { name: "Edit day", exact: true }).click();
  await page
    .getByRole("textbox", {
      name: "Quota principal ID (empty for organization)",
      exact: true,
    })
    .fill(principal);
  await expect(
    page.getByRole("button", { name: "Save quota policy", exact: true }),
  ).toHaveCount(0);
  await page
    .getByRole("button", { name: "Load current policy", exact: true })
    .click();
  await page.getByRole("button", { name: "Edit hour", exact: true }).click();
  await page
    .getByRole("textbox", { name: "Maximum turns", exact: true })
    .fill("3");
  await page
    .getByRole("button", { name: "Save quota policy", exact: true })
    .click();
  await expect(
    page.getByText(
      "Quota policy saved. Load current policy before another edit.",
      { exact: true },
    ),
  ).toBeVisible();
  expect(state.writes[0].condition).toBe(
    `"ai-quota-${synthetic.org}-${principal}-9007199254740993"`,
  );
  expect(state.writes[0].path).toContain(
    `/principals/${principal}/ai-quotas/hour`,
  );
});

test("malformed counts cannot become editable policy", async ({ page }) => {
  const state = await fixture(page);
  state.malformed = true;
  await page
    .getByRole("button", { name: "Load current policy", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Quota access is unavailable",
  );
  await expect(
    page.getByRole("button", { name: "Edit day", exact: true }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(0);
});

test("quota editor remains contained on narrow screens", async ({
  page,
}, info) => {
  await fixture(page);
  await page.getByRole("button", { name: "Edit day", exact: true }).click();
  const rowHeights = await page
    .getByRole("table", { name: "Current quota policy" })
    .locator("tbody tr")
    .evaluateAll((rows) =>
      rows.map((row) => row.getBoundingClientRect().height),
    );
  expect(rowHeights.every((height) => height <= 32)).toBe(true);
  await page.screenshot({
    path: info.outputPath("quotas-desktop.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    page.getByRole("button", { name: "Save quota policy", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: info.outputPath("quotas-mobile.png"),
    fullPage: true,
  });
});
