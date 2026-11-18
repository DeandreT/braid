import { test, expect, type Page } from "@playwright/test";
import { synthetic, syntheticCatalog } from "./catalog.fixture";
import type { components } from "../src/api/generated";

type Entry = components["schemas"]["AuditLogEntryV1"];
const first: Entry = {
  eventId: "90071992-5474-4099-8300-000000000001",
  occurredAtUtc: "2026-10-28T12:00:00Z",
  kind: "approval",
  action: "approved",
  actorKind: "user",
  actorId: synthetic.actor,
  projectId: synthetic.project.scope.projectId,
  mappingId: null,
  resourceId: "70000000-0000-4000-8000-000000000001",
  outcome: "approved",
  historicalObservation: false,
};
const second: Entry = {
  ...first,
  eventId: "90071992-5474-4099-8300-000000000002",
  kind: "build",
  action: "stateChanged",
  actorKind: "system",
  actorId: null,
  outcome: "succeeded",
  historicalObservation: true,
};

async function fixture(page: Page) {
  const state = { denied: false, writes: 0, requests: [] as URL[] };
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
  await page.route("**/audit?*", (route) => {
    if (route.request().method() !== "GET") state.writes++;
    const url = new URL(route.request().url());
    state.requests.push(url);
    if (state.denied)
      return route.fulfill({ status: 404, json: { code: "notFound" } });
    return route.fulfill({
      json: {
        entries: url.searchParams.has("cursor") ? [second] : [first],
        nextCursor: url.searchParams.has("cursor") ? null : "opaque-cursor",
        since: "2026-10-01T00:00:00Z",
        until: "2026-11-01T00:00:00Z",
        organizationWide: false,
      },
    });
  });
  await page.goto("/audit");
  await expect(
    page.getByRole("cell", { name: "approval / approved" }),
  ).toBeVisible();
  return state;
}

test("audit filters and paging retain the server window and never mutate", async ({
  page,
}) => {
  const state = await fixture(page);
  await expect(page.getByText(/Your administered projects only/)).toBeVisible();
  await page.getByRole("button", { name: "Next page", exact: true }).click();
  await expect(
    page.getByRole("cell", { name: "build / stateChanged" }),
  ).toBeVisible();
  const request = state.requests.at(-1)!;
  expect(request.searchParams.get("since")).toBe("2026-10-01T00:00:00Z");
  expect(request.searchParams.get("until")).toBe("2026-11-01T00:00:00Z");
  expect(request.searchParams.get("cursor")).toBe("opaque-cursor");
  await page
    .getByLabel("Project ID", { exact: true })
    .fill(synthetic.project.scope.projectId);
  await page
    .getByRole("combobox", { name: "Kind", exact: true })
    .selectOption("approval");
  await page
    .getByRole("button", { name: "Apply filters", exact: true })
    .click();
  await expect(
    page.getByRole("cell", { name: "approval / approved" }),
  ).toBeVisible();
  expect(state.requests.at(-1)!.searchParams.get("kind")).toBe("approval");
  expect(state.writes).toBe(0);
});

test("CSV export includes all filtered pages and no private text", async ({
  page,
}) => {
  const state = await fixture(page);
  const downloaded = page.waitForEvent("download");
  await page
    .getByRole("button", { name: "Export filtered CSV", exact: true })
    .click();
  const download = await downloaded;
  expect(download.suggestedFilename()).toBe("braid-audit.csv");
  const stream = await download.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(Buffer.from(chunk));
  const text = Buffer.concat(chunks).toString();
  expect(text).toContain('"90071992-5474-4099-8300-000000000001"');
  expect(text).toContain('"system",""');
  expect(text).toContain('"true"');
  expect(text).not.toContain("requestToken");
  expect(state.writes).toBe(0);
});

test("a current refusal clears the old audit data", async ({ page }) => {
  const state = await fixture(page);
  state.denied = true;
  await page
    .getByRole("button", { name: "Apply filters", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Audit access is unavailable",
  );
  await expect(page.getByRole("table")).toHaveCount(0);
  expect(state.writes).toBe(0);
});

test("returning to the tab rechecks audit authority", async ({ page }) => {
  const state = await fixture(page);
  state.denied = true;
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(page.getByRole("table")).toHaveCount(0);
  await expect(page.getByRole("alert")).toContainText(
    "Audit access is unavailable",
  );
});

test("a session boundary fences an in-flight CSV export", async ({ page }) => {
  await fixture(page);
  let release!: () => void;
  let arrive!: () => void;
  let settled!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const arrived = new Promise<void>((resolve) => {
    arrive = resolve;
  });
  const finished = new Promise<void>((resolve) => {
    settled = resolve;
  });
  let downloads = 0;
  page.on("download", () => downloads++);
  await page.route(
    "**/audit?*",
    async (route) => {
      arrive();
      await gate;
      try {
        await route.abort();
      } finally {
        settled();
      }
    },
    { times: 1 },
  );
  await page
    .getByRole("button", { name: "Export filtered CSV", exact: true })
    .click();
  await arrived;
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({ json: { version: 1, mode: "disabled" } }),
  );
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  try {
    await expect(page.getByRole("table")).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Export filtered CSV", exact: true }),
    ).toBeDisabled();
  } finally {
    release();
  }
  await finished;
  expect(downloads).toBe(0);
});

test("audit keyboard controls and table remain usable on a narrow screen", async ({
  page,
}, info) => {
  await fixture(page);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await expect(
    page.getByRole("table", { name: "Audit events, newest first" }),
  ).toBeVisible();
  await expect(page.getByRole("columnheader")).toHaveCount(6);
  await page.getByLabel("Project ID", { exact: true }).focus();
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Actor ID", { exact: true })).toBeFocused();
  await page.screenshot({
    path: info.outputPath("audit-mobile.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({
    path: info.outputPath("audit-desktop.png"),
    fullPage: true,
  });
});
