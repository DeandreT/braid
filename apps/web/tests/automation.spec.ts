import { expect, test, type Page } from "@playwright/test";
import {
  catalogApi,
  mappingPath,
  projectPath,
  synthetic,
  syntheticCatalog,
} from "./catalog.fixture";

const base = catalogApi(projectPath(synthetic.project.scope));
const organizationBase = `/api/v1/organizations/${synthetic.org}`;
const notificationId = "50000000-0000-4000-8000-000000000001";
const events = [
  "revisionUploaded",
  "regressionFailed",
  "regressionRecovered",
  "buildQualified",
  "buildFailed",
  "releaseActivated",
  "releaseRolledBack",
  "endpointErrorRate",
  "invitation",
];

for (const kind of ["centre", "project", "deliveries", "cases"] as const) {
  test(`review pagination ${kind} sends afterId and renders page two`, async ({
    page,
  }, info) => {
    await fixture(page);
    const ids = Array.from(
      { length: 21 },
      (_, i) =>
        `60000000-0000-4000-8000-${(i + 1).toString().padStart(12, "0")}`,
    );
    const cursor = ids[19];
    const endpoint =
      kind === "centre"
        ? organizationBase + "/notifications"
        : kind === "project"
          ? base + "/notifications"
          : kind === "deliveries"
            ? base + "/notification-deliveries"
            : base +
              "/regression-suites/" +
              synthetic.revision.scope.revisionId +
              "/cases";
    const pageTwo: string[] = [];
    await page.route("**/api/v1/**", async (route) => {
      const url = new URL(route.request().url());
      if (
        kind === "cases" &&
        url.pathname ===
          base + "/regression-suites/" + synthetic.revision.scope.revisionId
      )
        return route.fulfill({
          json: {
            suiteId: synthetic.revision.scope.revisionId,
            revisionId: synthetic.revision.scope.revisionId,
            state: "passed",
            createdAtUtc: "2026-10-25T12:00:00Z",
            totalCases: 21,
            passedCases: 21,
            failedCases: 0,
            pendingCases: 0,
          },
        });
      if (url.pathname !== endpoint) return route.fallback();
      expect(url.searchParams.get("limit")).toBe("20");
      const after = url.searchParams.get("afterId");
      if (after !== null) {
        expect(after).toBe(cursor);
        pageTwo.push(after);
      }
      const selected = after ? ids.slice(20) : ids.slice(0, 20);
      const next = after ? null : cursor;
      const entries = selected.map((id) => ({
        id,
        resourceId: id,
        event: "regressionFailed",
        projectId: synthetic.project.scope.projectId,
        mappingId: synthetic.mapping.scope.mappingId,
        revisionId: synthetic.revision.scope.revisionId,
        link: mappingPath(synthetic.mapping.scope),
        createdAtUtc: "2026-10-25T12:00:00Z",
        read: true,
      }));
      const json =
        kind === "cases"
          ? {
              cases: selected.map((caseId) => ({
                caseId,
                version: 1,
                state: "passed",
                attempts: 1,
                originalRevisionId: synthetic.revision.scope.revisionId,
              })),
              nextCaseId: next,
            }
          : kind === "deliveries"
            ? {
                deliveries: entries.map((entry) => ({
                  ...entry,
                  destinationId: "webhook:operations",
                  state: "delivered",
                  attempts: 1,
                  nextAttemptAtUtc: null,
                  history: [],
                })),
                nextId: next,
              }
            : { notifications: entries, nextId: next };
      await route.fulfill({ json });
    });
    await page.goto(
      kind === "centre"
        ? "/notifications"
        : kind === "cases"
          ? mappingPath(synthetic.mapping.scope)
          : projectPath(synthetic.project.scope) + "/automation",
    );
    if (kind === "cases")
      await page
        .getByRole("button", { name: "Show case results", exact: true })
        .click();
    const rows =
      kind === "centre"
        ? page.locator(".catalog-list-item").filter({ hasText: "Resource" })
        : kind === "project"
          ? page.locator("article.automation-event")
          : kind === "deliveries"
            ? page.locator("details.automation-event")
            : page.locator("weft-regression-status li");
    await expect(rows).toHaveCount(20);
    if (kind === "centre")
      await page
        .getByRole("link", { name: "Older notifications", exact: true })
        .click();
    else
      await page
        .getByRole("button", {
          name:
            kind === "project"
              ? "Next notifications"
              : kind === "deliveries"
                ? "Next deliveries"
                : "Next case results",
          exact: true,
        })
        .click();
    await expect.poll(() => pageTwo.length).toBe(1);
    await expect(rows).toHaveCount(1);
    if (kind === "deliveries") await rows.locator("summary").click();
    await expect(rows).toContainText(ids[20]);
    await page.screenshot({
      path: info.outputPath(`after-pagination-${kind}.png`),
      fullPage: true,
    });
  });
}

test("baseline before automation shows the original project view", async ({
  page,
}, info) => {
  test.skip(
    !process.env.WEFT_AUTOMATION_BASELINE_ROOT,
    "Only the archived original UI is a before screenshot.",
  );
  await syntheticCatalog(page);
  await page.goto(projectPath(synthetic.project.scope));
  await expect(
    page.getByRole("heading", { name: synthetic.project.name, exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("link", {
      name: "Automation and notifications",
      exact: true,
    }),
  ).toHaveCount(0);
  await page.screenshot({
    path: info.outputPath("automation-before-desktop.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: info.outputPath("automation-before-mobile.png"),
    fullPage: true,
  });
});

async function fixture(page: Page, admin = true) {
  const project = structuredClone(synthetic.project);
  const mapping = structuredClone(synthetic.mapping);
  project.access.role = admin ? "administrator" : "viewer";
  mapping.access.role = project.access.role;
  const policy = {
    requirePassingRegression: true,
    errorRatePercent: 20,
    minimumRequests: 10,
    windowSeconds: 300,
  };
  const subscription = {
    events: ["regressionFailed"],
    inApp: true,
    email: false,
    webhooks: [] as string[],
  };
  const notification = {
    id: notificationId,
    event: "regressionFailed",
    projectId: project.scope.projectId,
    mappingId: mapping.scope.mappingId,
    revisionId: synthetic.revision.scope.revisionId,
    resourceId: synthetic.revision.scope.revisionId,
    link: mappingPath(mapping.scope),
    createdAtUtc: "2026-10-25T12:00:00Z",
    read: false,
  };
  const writes: { path: string; body: unknown; token: string | undefined }[] =
    [];
  const invitation = {
    ...notification,
    id: "50000000-0000-4000-8000-000000000002",
    event: "invitation",
    projectId: null,
    mappingId: null,
    revisionId: null,
    resourceId: "50000000-0000-4000-8000-000000000003",
    link: "/administration",
  };
  await syntheticCatalog(page, {
    "/api/v1/health": {
      status: "ok",
      service: "weft-api",
      version: "test",
      apiVersion: "v1",
    },
    [organizationBase + "/notifications"]: {
      notifications: [invitation, notification],
      nextId: null,
    },
    [base]: project,
    [catalogApi(mappingPath(mapping.scope))]: mapping,
    [base + "/automation"]: policy,
    [base + "/notification-subscription"]: subscription,
    [base + "/notification-options"]: {
      events,
      emailAvailable: false,
      webhooks: ["operations"],
    },
    [base + "/notifications"]: { notifications: [notification], nextId: null },
    [base + "/notification-deliveries"]: {
      deliveries: [
        {
          id: notificationId,
          event: "regressionFailed",
          resourceId: notification.resourceId,
          destinationId: "webhook:operations",
          state: "failed",
          attempts: 5,
          nextAttemptAtUtc: null,
          history: [
            {
              attempt: 1,
              outcome: "transportFailure",
              recordedAtUtc: notification.createdAtUtc,
            },
          ],
        },
      ],
      nextId: null,
    },
    [base + "/notification-destinations"]: {
      destinations: [
        { id: "webhook:operations", channel: "webhook", enabled: false },
      ],
    },
    [base + "/mappings/" + mapping.scope.mappingId + "/automation"]: {
      enabled: true,
    },
    [base + "/regression-suites/" + notification.revisionId]: {
      suiteId: notification.revisionId,
      revisionId: notification.revisionId,
      state: "queued",
      createdAtUtc: notification.createdAtUtc,
      totalCases: 3,
      passedCases: 1,
      failedCases: 0,
      pendingCases: 2,
    },
  });
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request();
    if (request.method() !== "PUT") return route.fallback();
    writes.push({
      path: new URL(request.url()).pathname,
      body: request.postDataJSON(),
      token: request.headers()["x-xsrf-token"],
    });
    if (
      new URL(request.url()).pathname ===
      organizationBase + "/notifications/" + invitation.id + "/read"
    )
      invitation.read = true;
    await route.fulfill({ json: { saved: true } });
  });
  return { writes, policy, subscription };
}

test("administrator sees policy, notification centre and redacted delivery history", async ({
  page,
}, info) => {
  await fixture(page);
  await page.goto(projectPath(synthetic.project.scope) + "/automation");
  await expect(
    page.getByRole("heading", { name: "Notification centre" }),
  ).toBeVisible();
  await expect(
    page.getByLabel("Require passing exact-revision regression"),
  ).toBeChecked();
  await expect(page.getByLabel("Email", { exact: true })).toBeDisabled();
  await expect(page.getByText("webhook:operations: disabled")).toBeVisible();
  await page
    .getByText("Regression failed · webhook:operations · failed")
    .click();
  await expect(page.getByText("Attempt 1: transportFailure")).toBeVisible();
  await page.screenshot({
    path: info.outputPath("automation-after-desktop.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    page.getByRole("heading", { name: "Your project subscriptions" }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: info.outputPath("automation-after-mobile.png"),
    fullPage: true,
  });
});

test("policy and subscription saves send only the selected metadata under the current token", async ({
  page,
}) => {
  const data = await fixture(page);
  await page.goto(projectPath(synthetic.project.scope) + "/automation");
  await page.getByLabel("Require passing exact-revision regression").uncheck();
  await page.getByRole("button", { name: "Save policy", exact: true }).click();
  await expect(
    page.getByRole("status").filter({ hasText: "Saved." }),
  ).toBeVisible();
  await page.getByLabel("Revision uploaded", { exact: true }).check();
  await page.getByLabel("Webhook operations", { exact: true }).check();
  await page
    .getByRole("button", { name: "Save subscriptions", exact: true })
    .click();
  await expect.poll(() => data.writes.length).toBe(2);
  expect(data.writes[0].body).toEqual({
    ...data.policy,
    requirePassingRegression: false,
  });
  expect(data.writes[1].body).toEqual({
    ...data.subscription,
    events: ["regressionFailed", "revisionUploaded"],
    webhooks: ["operations"],
  });
  expect(
    data.writes.every((w) => w.token === synthetic.context.requestToken),
  ).toBe(true);
  expect(JSON.stringify(data.writes)).not.toContain("secret");
});

test("notification read and explicit destination re-enable have separate requests", async ({
  page,
}) => {
  const data = await fixture(page);
  await page.goto(projectPath(synthetic.project.scope) + "/automation");
  await page.getByRole("button", { name: "Mark read", exact: true }).click();
  await expect.poll(() => data.writes.length).toBe(1);
  expect(data.writes[0]).toMatchObject({
    path: base + "/notifications/" + notificationId + "/read",
    body: {},
  });
  await page
    .getByRole("button", { name: "Re-enable destination", exact: true })
    .click();
  await expect.poll(() => data.writes.length).toBe(2);
  expect(data.writes[1].body).toEqual({ destinationId: "webhook:operations" });
});

test("viewers can choose subscriptions but cannot alter policy or inspect deliveries", async ({
  page,
}) => {
  await fixture(page, false);
  await page.goto(projectPath(synthetic.project.scope) + "/automation");
  await expect(
    page.getByRole("button", { name: "Save policy", exact: true }),
  ).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "Save subscriptions", exact: true }),
  ).toBeEnabled();
  await expect(page.getByRole("heading", { name: "Delivery log" })).toHaveCount(
    0,
  );
});

test("mapping list status is for the selected exact revision and switch applies to future uploads", async ({
  page,
}) => {
  const data = await fixture(page);
  await page.goto(mappingPath(synthetic.mapping.scope));
  await expect(
    page.getByText("1/3 passed · 0 failed · 2 pending").first(),
  ).toBeVisible();
  await page.getByRole("button", { name: "Turn off", exact: true }).click();
  await expect.poll(() => data.writes.length).toBe(1);
  expect(data.writes[0]).toMatchObject({
    path:
      base + "/mappings/" + synthetic.mapping.scope.mappingId + "/automation",
    body: { enabled: false },
  });
  await expect(
    page.getByText(
      "This applies to future revisions; retained results are unchanged.",
    ),
  ).toBeVisible();
});

test("organization inbox includes recipient-only invitations and marks them read without a project", async ({
  page,
}, info) => {
  const data = await fixture(page);
  await page.goto("/notifications");
  await expect(
    page.getByRole("heading", { name: "Notification centre", exact: true }),
  ).toBeVisible();
  const invitation = page.getByRole("listitem").filter({
    has: page.getByRole("heading", { name: "Invitation", exact: true }),
  });
  await expect(invitation).toContainText("Organization invitation");
  await invitation
    .getByRole("button", { name: "Mark read", exact: true })
    .click();
  await expect(
    invitation.getByRole("button", { name: "Mark read", exact: true }),
  ).toHaveCount(0);
  expect(data.writes[0].path).toBe(
    organizationBase +
      "/notifications/50000000-0000-4000-8000-000000000002/read",
  );
  expect(data.writes[0].body).toEqual({});
  await page.getByRole("heading", { level: 1 }).click();
  await page.screenshot({
    path: info.outputPath("notification-centre-desktop.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: info.outputPath("notification-centre-mobile.png"),
    fullPage: true,
  });
});

test("same-owner refresh and unavailable reads preserve unsent automation settings", async ({
  page,
}, info) => {
  await fixture(page);
  let unavailable = false;
  await page.route("**/api/v1/catalog/context", (route) =>
    unavailable
      ? route.fulfill({ status: 503, json: { code: "unavailable" } })
      : route.fallback(),
  );
  const path = projectPath(synthetic.project.scope) + "/automation";
  await page.goto(path);
  await expect(
    page.getByLabel("Require passing exact-revision regression"),
  ).toBeChecked();
  await page.getByRole("heading", { level: 1 }).click();
  await page.screenshot({
    path: info.outputPath("automation-current-desktop.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: info.outputPath("automation-current-mobile.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.getByLabel("Require passing exact-revision regression").uncheck();
  await page
    .getByRole("button", {
      name: "Refresh saved settings and inbox",
      exact: true,
    })
    .click();
  await expect(
    page.getByLabel("Require passing exact-revision regression"),
  ).not.toBeChecked();
  unavailable = true;
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(
    page.getByRole("button", { name: "Retry", exact: true }),
  ).toBeVisible();
  unavailable = false;
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(
    page.getByLabel("Require passing exact-revision regression"),
  ).not.toBeChecked();
  page.once("dialog", (dialog) => dialog.dismiss());
  await page.getByRole("link", { name: "Help", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(path + "$"));
});
