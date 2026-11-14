import { test, expect, type Page } from "@playwright/test";
import {
  buildFixture,
  environmentId,
  openPrepare,
  scope,
  type BuildFixture,
} from "./builds.fixture";
import { confirmAction } from "./confirmation.fixture";
import { synthetic } from "./catalog.fixture";

const panel = (page: Page) =>
  page.getByRole("region", { name: "Prepare this version", exact: true });
async function loaded(page: Page) {
  await expect(
    panel(page).getByRole("combobox", {
      name: "Environment version",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("button", {
      name: "Refresh environments",
      exact: true,
    }),
  ).toBeEnabled();
}

test("document compiler environments use the explicitly offered profile", async ({
  page,
}) => {
  const state = await buildFixture(page, false);
  state.documentProfiles = true;
  await openPrepare(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  await panel(page)
    .getByRole("combobox", { name: "Compiler profile", exact: true })
    .selectOption({ label: "Declared document formats" });
  if (process.env.WEFT_EDI_SCREENSHOT_ROOT)
    await panel(page).screenshot({
      path: `${process.env.WEFT_EDI_SCREENSHOT_ROOT}/profile-single.png`,
    });
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await expect.poll(() => state.writes.length).toBe(1);
  expect(JSON.parse(state.writes[0].body!)).toEqual({
    profileId: "weft.ferrule-csharp-document.v1",
    profileVersion: "1",
  });
});
test("X12 compiler environments bind the explicitly offered interchange profile", async ({
  page,
}) => {
  const state = await buildFixture(page, false);
  state.documentProfiles = true;
  state.interchangeProfiles = true;
  await openPrepare(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  await panel(page)
    .getByRole("combobox", { name: "Compiler profile", exact: true })
    .selectOption({ label: "X12 MapForce interchanges" });
  if (process.env.WEFT_EDI_SCREENSHOT_ROOT)
    await panel(page).screenshot({
      path: `${process.env.WEFT_EDI_SCREENSHOT_ROOT}/profile-interchanges.png`,
    });
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await expect.poll(() => state.writes.length).toBe(1);
  expect(JSON.parse(state.writes[0].body!)).toEqual({
    profileId: "weft.ferrule-csharp-x12-interchanges.v1",
    profileVersion: "1",
  });
});
test("the Prepare tab reads its records once when shown and nothing while hidden", async ({
  page,
}) => {
  const reads: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    const name = /\/(build-[a-z-]+|regression-runs)$/.exec(path)?.[1];
    if (request.method() === "GET" && name) reads.push(name);
  });
  await buildFixture(page, false);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    synthetic.mapping.name,
  );
  await expect(page.locator(".wb-version-number")).toHaveText("Version 1");
  await page.waitForTimeout(500);
  expect(reads).toEqual([]);
  await openPrepare(page);
  await loaded(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Verify this evidence",
      exact: true,
    }),
  ).toBeVisible();
  await page.getByRole("tab", { name: "Data mapping", exact: true }).click();
  await openPrepare(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Verify this evidence",
      exact: true,
    }),
  ).toBeVisible();
  expect(reads).toEqual([
    "build-capabilities",
    "build-environments",
    "build-approvals",
    "build-runs",
    "regression-runs",
  ]);
});
async function approved(page: Page, state: BuildFixture) {
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", {
      name: "Approve preparation",
      exact: true,
    }),
  ).toBeEnabled();
  await panel(page)
    .getByRole("button", { name: "Approve preparation", exact: true })
    .click();
  await confirmAction(page);
  await expect.poll(() => state.approval?.approvalId).toBeTruthy();
  await expect(
    panel(page).getByRole("button", {
      name: "Prepare approved version",
      exact: true,
    }),
  ).toBeEnabled();
}
async function settledOriginal(page: Page, state: BuildFixture): Promise<void> {
  await expect.poll(() => state.writes.length).toBe(1);
  await expect(
    panel(page).getByRole("button", {
      name: "Resend the same request",
      exact: true,
    }),
  ).toBeEnabled();
}

test("approval confirmation names the exact version and environment and Escape sends nothing", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  const approve = panel(page).getByRole("button", {
    name: "Approve preparation",
    exact: true,
  });
  await approve.click();
  const dialog = page.getByRole("dialog", {
    name: "Approve preparation of version 1",
    exact: true,
  });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(scope.revisionId);
  await expect(dialog).toContainText(environmentId);
  await expect(dialog).toContainText(
    "does not start preparation or change the version in use",
  );
  await expect(
    dialog.getByRole("button", { name: "Not now", exact: true }),
  ).toBeFocused();
  expect(state.writes).toHaveLength(0);
  await page.keyboard.press("Escape");
  await expect(dialog).not.toBeVisible();
  await expect(approve).toBeFocused();
  expect(state.writes).toHaveLength(0);
  await approve.click();
  const reference = (
    await dialog.locator('dt:has-text("Request reference") + dd').innerText()
  ).trim();
  await confirmAction(page);
  await expect.poll(() => state.writes.length).toBe(1);
  expect(state.writes[0].url).toMatch(
    new RegExp("/build-approvals/" + reference + "$"),
  );
  expect(JSON.parse(state.writes[0].body!)).toMatchObject({
    revisionId: scope.revisionId,
    environmentId,
  });
});

test("confirming after access changes cannot send the captured mutation", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toBeVisible();
  expect(state.writes).toHaveLength(0);
  state.role = "publisher";
  await confirmAction(page);
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  expect(state.writes).toHaveLength(0);
});

test("leaving an unconfirmed dialog does not submit or retain a pending operation", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.reload();
  await page.getByRole("tab", { name: "Prepare", exact: true }).click();
  await expect(page.getByRole("dialog")).not.toBeVisible();
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(0);
});

test("stopping tracking is explicit and never sends server cancellation", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  state.nextWriteFailure = "abort";
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await settledOriginal(page, state);
  await panel(page)
    .getByRole("button", { name: "Stop tracking this request", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toContainText(
    "does not cancel the request",
  );
  await page.keyboard.press("Escape");
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toBeVisible();
  await panel(page)
    .getByRole("button", { name: "Stop tracking this request", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
});

test("approval, explicit queue and a terminal cancellation race preserve actual result metadata", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await approved(page, state);
  expect(state.writes).toHaveLength(1);
  expect(state.writes[0].media).toContain(
    "application/vnd.weft.build-approval.v1+json",
  );
  expect(state.writes[0].token).toBe("synthetic-ui-only-not-a-credential");
  await panel(page)
    .getByRole("button", { name: "Prepare approved version", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Cancel preparation",
      exact: true,
    }),
  ).toBeEnabled();
  state.cancelWinsAsSuccess = true;
  await panel(page)
    .getByRole("button", { name: "Cancel preparation", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Preparation complete · Attempt 1 of 3",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    panel(page).getByText("Private artifact digest", { exact: true }),
  ).not.toBeVisible();
  await panel(page).locator(".builds-result-details > summary").click();
  await expect(
    panel(page).getByText("Private artifact digest", { exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("button", { name: /download/i }),
  ).toHaveCount(0);
});

test("lost approval reply retries the original URL and exact body without creating replacement IDs", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  state.nextWriteFailure = "abort";
  await panel(page)
    .getByRole("button", { name: "Approve preparation", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toBeVisible();
  await settledOriginal(page, state);
  const original = state.writes[0];
  await panel(page)
    .getByRole("button", { name: "Resend the same request", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", {
      name: "Prepare approved version",
      exact: true,
    }),
  ).toBeEnabled();
  expect(state.writes).toHaveLength(2);
  expect(state.writes[1].url).toBe(original.url);
  expect(state.writes[1].body).toBe(original.body);
});

test("a same-owner catalog refresh keeps the panel and its uncertain original request", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  state.nextWriteFailure = "abort";
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toBeVisible();
  await settledOriginal(page, state);
  const original = state.writes[0];
  const refresh = page.getByRole("button", {
    name: "Refresh catalog",
    exact: true,
  });
  const revision = page.waitForResponse(
    (response) =>
      response.request().method() === "GET" &&
      new URL(response.url()).pathname ===
        `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}`,
  );
  await refresh.click();
  // Wait for the page's own re-read to finish, not only the access check.
  expect((await revision).status()).toBe(200);
  await expect(page.locator("weft-workbench-frame")).not.toHaveAttribute(
    "aria-busy",
    "true",
  );
  // The re-read leaves the loaded panel, and the request it holds, on screen.
  await expect(
    panel(page).getByRole("button", {
      name: "Load preparation controls",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toBeVisible();
  await panel(page)
    .getByRole("button", { name: "Resend the same request", exact: true })
    .click();
  await expect.poll(() => state.writes.length).toBe(2);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(2);
  expect(state.writes[1].url).toBe(original.url);
  expect(state.writes[1].body).toBe(original.body);
});

const workspaceAccess = (page: Page) =>
  page.getByRole("alert").filter({
    has: page.getByRole("heading", {
      name: "Workspace access unavailable",
      exact: true,
    }),
  });

/** A focus refresh whose session check fails, then the shell and page Retry controls. */
async function failSessionCheck(page: Page) {
  await page.route(
    "**/api/auth/session*",
    (route) => route.fulfill({ status: 503, json: { code: "unavailable" } }),
    { times: 1 },
  );
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(workspaceAccess(page)).toBeVisible();
  await expect(panel(page)).toHaveCount(0);
  await workspaceAccess(page)
    .getByRole("button", { name: "Retry", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Catalog unavailable", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await page.getByRole("tab", { name: "Prepare", exact: true }).click();
}

async function lostRegistration(page: Page, state: BuildFixture) {
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  state.nextWriteFailure = "abort";
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await settledOriginal(page, state);
}

test("controls requested during a routine refresh load after it without disturbing the page", async ({
  page,
}) => {
  await buildFixture(page, false);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    synthetic.mapping.name,
  );
  await expect(page.locator(".wb-version-number")).toHaveText("Version 1");
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let arrive!: () => void;
  const arrived = new Promise<void>((resolve) => {
    arrive = resolve;
  });
  await page.route(
    "**/api/v1/catalog/context",
    async (route) => {
      arrive();
      await gate;
      await route.fulfill({
        json: {
          ...synthetic.context,
          features: { ...synthetic.context.features, uploadRevisions: false },
        },
      });
    },
    { times: 1 },
  );
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  await arrived;
  try {
    // Showing the panel starts its read, which waits for the pending context read instead of being refused.
    await openPrepare(page);
  } finally {
    release();
  }
  await expect(
    panel(page).getByRole("combobox", {
      name: "Environment version",
      exact: true,
    }),
  ).toBeVisible();
  // The page's own re-read was not refused by the panel's later admission check.
  await expect(page.locator("weft-workbench-frame")).not.toHaveAttribute(
    "aria-busy",
    "true",
  );
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    synthetic.mapping.name,
  );
  await expect(page.locator(".wb-version-number")).toHaveText("Version 1");
});

test("a failed session check keeps the uncertain original request", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await lostRegistration(page, state);
  const original = state.writes[0];
  await failSessionCheck(page);
  await loaded(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toBeVisible();
  await panel(page)
    .getByRole("button", { name: "Resend the same request", exact: true })
    .click();
  await expect.poll(() => state.writes.length).toBe(2);
  expect(state.writes[1].url).toBe(original.url);
  expect(state.writes[1].body).toBe(original.body);
});

test("a different actor after a failed session check clears the original request", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await lostRegistration(page, state);
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({
      json: {
        ...synthetic.context,
        actorPrincipalId: "70000000-0000-4000-8000-000000000099",
        features: { ...synthetic.context.features, uploadRevisions: false },
      },
    }),
  );
  await failSessionCheck(page);
  await loaded(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
});

test("fresh administrator-to-publisher change clears the old operation before mutation dispatch", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  state.nextWriteFailure = "abort";
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toBeVisible();
  await settledOriginal(page, state);
  state.role = "publisher";
  await panel(page)
    .getByRole("button", { name: "Resend the same request", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
  // A refused request is not retried automatically under the same admission.
  await panel(page)
    .getByRole("button", { name: "Load preparation controls", exact: true })
    .click();
  await loaded(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Environment administration",
      exact: true,
    }),
  ).toHaveCount(0);
});

test("an opaque post-commit 404 hides metadata without replacing a still-owned original request", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  state.nextWriteFailure = "opaque404";
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Load preparation controls",
      exact: true,
    }),
  ).toBeVisible();
  const original = state.writes[0];
  await panel(page)
    .getByRole("button", { name: "Load preparation controls", exact: true })
    .click();
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Check previous result", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
  expect(original.body).not.toContain("runtimeVersion");
});

test("uncertain is terminal metadata, never a successful build or an automatic replacement", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await approved(page, state);
  await panel(page)
    .getByRole("button", { name: "Prepare approved version", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Cancel preparation",
      exact: true,
    }),
  ).toBeEnabled();
  Object.assign(state.run!, {
    state: "uncertain",
    attempt: 3,
    outcome: null,
    artifact: {
      sha256: "b".repeat(64),
      byteCount: 2048,
      format: "dotnet-library",
    },
  });
  await panel(page)
    .getByRole("button", { name: "Refresh preparation result", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Result not confirmed — check the previous request · Attempt 3 of 3",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    panel(page).getByText("Not confirmed as successful.", { exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("button", {
      name: "Cancel preparation",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(2);
});

test("an incompatible receipt preserves the original request for a read-only recovery", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  state.nextWriteFailure = "badReceipt";
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await settledOriginal(page, state);
  await expect(panel(page).getByRole("alert")).toContainText(
    "could not be verified",
  );
  await panel(page)
    .getByRole("button", { name: "Check previous result", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
});

test("a changed actor clears private recovery before another write", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  state.nextWriteFailure = "abort";
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  await confirmAction(page);
  await settledOriginal(page, state);
  state.actorPrincipalId = "70000000-0000-4000-8000-000000000099";
  await panel(page)
    .getByRole("button", { name: "Resend the same request", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  await expect(
    panel(page).getByRole("heading", {
      name: "Previous request",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
});

test("viewer admission does not dispatch a private build request", async ({
  page,
}) => {
  const state = await buildFixture(page, false);
  state.role = "viewer";
  await openPrepare(page);
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  await expect(
    panel(page).getByRole("heading", {
      name: "Environment version",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(0);
});

test("an explicit metadata capability refusal does not display private build controls", async ({
  page,
}) => {
  const state = await buildFixture(page, false);
  state.readOwnMetadata = false;
  await openPrepare(page);
  await expect(panel(page).getByRole("alert")).toContainText(
    "cannot read private build metadata",
  );
  await expect(
    panel(page).getByRole("heading", {
      name: "Environment version",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(0);
});

test("foreign run scope is refused without replacing the displayed original result", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await approved(page, state);
  await panel(page)
    .getByRole("button", { name: "Prepare approved version", exact: true })
    .click();
  await confirmAction(page);
  await expect(
    panel(page).getByRole("button", {
      name: "Cancel preparation",
      exact: true,
    }),
  ).toBeEnabled();
  state.forgeRun = true;
  await panel(page)
    .getByRole("button", { name: "Refresh preparation result", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText(
    "could not be verified",
  );
  await expect(
    panel(page).getByRole("heading", {
      name: "Queued · Attempt 0 of 3",
      exact: true,
    }),
  ).toBeVisible();
});

test("narrow keyboard interaction keeps the build panel within the viewport", async ({
  page,
}) => {
  await buildFixture(page, false);
  await page.setViewportSize({ width: 360, height: 780 });
  const tab = page.getByRole("tab", { name: "Prepare", exact: true });
  await tab.focus();
  await page.keyboard.press("Enter");
  await expect(
    panel(page).getByRole("combobox", {
      name: "Environment version",
      exact: true,
    }),
  ).toBeVisible();
  // The select is visible before the remaining preparation records finish loading.
  // Native focus() is a no-op while that loading state keeps it disabled.
  await expect(
    panel(page).getByRole("combobox", {
      name: "Environment version",
      exact: true,
    }),
  ).toBeEnabled();
  await panel(page)
    .getByRole("combobox", { name: "Environment version", exact: true })
    .focus();
  await expect(
    panel(page).getByRole("combobox", {
      name: "Environment version",
      exact: true,
    }),
  ).toBeFocused();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});

test("a scaled narrow confirmation keeps exact references readable and defaults to the safe action", async ({
  page,
}) => {
  const state = await buildFixture(page);
  await loaded(page);
  await page.setViewportSize({ width: 360, height: 780 });
  await page.addStyleTag({ content: ":root { font-size: 24px !important; }" });
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Register new environment", exact: true })
    .click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText(scope.revisionId);
  await expect(
    dialog.getByRole("button", { name: "Not now", exact: true }),
  ).toBeFocused();
  expect((await dialog.boundingBox())!.width).toBeLessThanOrEqual(360);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.keyboard.press("Enter");
  await expect(dialog).not.toBeVisible();
  expect(state.writes).toHaveLength(0);
});
