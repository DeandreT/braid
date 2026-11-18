import { test, expect, type Page } from "@playwright/test";
import { synthetic, syntheticCatalog } from "./catalog.fixture";
import type { AiUsagePage } from "../src/administration/ai-usage-api";

async function fixture(page: Page) {
  const state = {
    denied: false,
    malformed: false,
    writes: 0,
    requests: [] as URL[],
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
  await page.route("**/usage?*", (route) => {
    if (route.request().method() !== "GET") state.writes++;
    const url = new URL(route.request().url());
    state.requests.push(url);
    if (state.denied)
      return route.fulfill({ status: 404, json: { code: "notFound" } });
    const data: AiUsagePage = {
      since: "2026-10-01T00:00:00Z",
      until: "2026-11-01T00:00:00Z",
      observedAtUtc: "2026-10-28T12:00:00Z",
      completeHistorySince: "2026-10-20T00:00:00Z",
      includesPartialHistory: true,
      organizationWide: false,
      nextCursor: url.searchParams.has("cursor") ? null : "next-cursor",
      groups: [
        {
          organizationId: synthetic.org,
          projectId: null,
          principalId: null,
          profileId:
            url.searchParams.get("groupBy") === "modelProfile"
              ? "bounded-model"
              : null,
          profileVersion:
            url.searchParams.get("groupBy") === "modelProfile" ? 2 : null,
          usage: {
            turns: state.malformed ? "01" : "9007199254740993",
            rounds: "1",
            toolCalls: "0",
            failedTurns: "0",
            inputTokens: null,
            outputTokens: null,
            totalTokens: null,
            knownInputTokens: "0",
            knownOutputTokens: "0",
            knownTotalTokens: "0",
            unknownInputRounds: "1",
            unknownOutputRounds: "1",
            unknownTotalRounds: "1",
            failures: [],
          },
        },
      ],
    };
    return route.fulfill({ json: data });
  });
  await page.goto("/usage");
  await expect(page.getByRole("table")).toBeVisible();
  return state;
}

test("large usage counts stay exact and unknown tokens do not become zero", async ({
  page,
}) => {
  const state = await fixture(page);
  const count = page.getByRole("row").nth(1).getByRole("cell").nth(1);
  expect((await count.innerText()).replaceAll(/[^0-9]/g, "")).toBe(
    "9007199254740993",
  );
  await expect(
    page.getByRole("cell", {
      name: "Unknown total; known subtotal 0; 1 unreported rounds",
      exact: true,
    }),
  ).toHaveCount(3);
  await expect(page.getByRole("note")).toContainText("Partial history");
  await expect(
    page.getByRole("cell", { name: "None recorded", exact: true }),
  ).toBeVisible();
  expect(state.writes).toBe(0);
});

test("paging retains the reported window and edited filters must be applied", async ({
  page,
}) => {
  const state = await fixture(page);
  await page.getByRole("button", { name: "Next page", exact: true }).click();
  await expect
    .poll(() => state.requests.at(-1)?.searchParams.get("cursor"))
    .toBe("next-cursor");
  expect(state.requests.at(-1)!.searchParams.get("since")).toBe(
    "2026-10-01T00:00:00Z",
  );
  expect(state.requests.at(-1)!.searchParams.get("until")).toBe(
    "2026-11-01T00:00:00Z",
  );
  await page
    .getByRole("combobox", { name: "Group by", exact: true })
    .selectOption("modelProfile");
  await expect(
    page.getByRole("button", { name: "Next page", exact: true }),
  ).toBeDisabled();
  await page
    .getByRole("button", { name: "Apply filters", exact: true })
    .click();
  await expect(
    page.getByRole("cell", { name: "bounded-model v2", exact: true }),
  ).toBeVisible();
  expect(state.requests.at(-1)!.searchParams.has("cursor")).toBe(false);
  expect(state.writes).toBe(0);
});

test("focus rechecks authority and removes previously displayed usage", async ({
  page,
}) => {
  const state = await fixture(page);
  state.denied = true;
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByRole("table")).toHaveCount(0);
  await expect(page.getByRole("alert")).toContainText(
    "Usage access is unavailable",
  );
});

test("malformed numeric strings are refused instead of rounded", async ({
  page,
}) => {
  const state = await fixture(page);
  state.malformed = true;
  await page
    .getByRole("button", { name: "Refresh usage", exact: true })
    .click();
  await expect(page.getByRole("table")).toHaveCount(0);
  await expect(page.getByRole("alert")).toBeVisible();
});

test("usage controls and scrollable table remain usable on narrow screens", async ({
  page,
}, info) => {
  await fixture(page);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.getByLabel("Project ID", { exact: true }).focus();
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Principal ID", { exact: true })).toBeFocused();
  await page.screenshot({
    path: info.outputPath("usage-mobile.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({
    path: info.outputPath("usage-desktop.png"),
    fullPage: true,
  });
});
