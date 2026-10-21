import { test, expect, type Page } from "@playwright/test";
import { buildFixture, type BuildFixture } from "./builds.fixture";

const panel = (page: Page) =>
  page.getByRole("region", { name: "Approved builds", exact: true });
async function loaded(page: Page) {
  await panel(page)
    .getByRole("button", { name: "Load build controls", exact: true })
    .click();
  await expect(
    panel(page).getByLabel("Exact retained environment version"),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("button", {
      name: "Refresh environments",
      exact: true,
    }),
  ).toBeEnabled();
}
async function approved(page: Page, state: BuildFixture) {
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Load saved regression runs", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", {
      name: "Approve immutable build",
      exact: true,
    }),
  ).toBeEnabled();
  await panel(page)
    .getByRole("button", { name: "Approve immutable build", exact: true })
    .click();
  await expect.poll(() => state.approval?.approvalId).toBeTruthy();
  await expect(
    panel(page).getByRole("button", {
      name: "Queue approved build",
      exact: true,
    }),
  ).toBeEnabled();
}
async function settledOriginal(page: Page, state: BuildFixture): Promise<void> {
  await expect.poll(() => state.writes.length).toBe(1);
  await expect(
    panel(page).getByRole("button", {
      name: "Retry original request",
      exact: true,
    }),
  ).toBeEnabled();
}

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
    .getByRole("button", { name: "Queue approved build", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", { name: "Cancel build", exact: true }),
  ).toBeEnabled();
  state.cancelWinsAsSuccess = true;
  await panel(page)
    .getByRole("button", { name: "Cancel build", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Succeeded · Attempt 1 of 3",
      exact: true,
    }),
  ).toBeVisible();
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
    .getByRole("button", { name: "Load saved regression runs", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  state.nextWriteFailure = "abort";
  await panel(page)
    .getByRole("button", { name: "Approve immutable build", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
      exact: true,
    }),
  ).toBeVisible();
  await settledOriginal(page, state);
  const original = state.writes[0];
  await panel(page)
    .getByRole("button", { name: "Retry original request", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", {
      name: "Queue approved build",
      exact: true,
    }),
  ).toBeEnabled();
  expect(state.writes).toHaveLength(2);
  expect(state.writes[1].url).toBe(original.url);
  expect(state.writes[1].body).toBe(original.body);
});

test("a catalog metadata refresh replaces the child without discarding its uncertain original request", async ({
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
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
      exact: true,
    }),
  ).toBeVisible();
  await settledOriginal(page, state);
  const original = state.writes[0];
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  await loaded(page);
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
      exact: true,
    }),
  ).toBeVisible();
  await panel(page)
    .getByRole("button", { name: "Retry original request", exact: true })
    .click();
  await expect.poll(() => state.writes.length).toBe(2);
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(2);
  expect(state.writes[1].url).toBe(original.url);
  expect(state.writes[1].body).toBe(original.body);
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
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
      exact: true,
    }),
  ).toBeVisible();
  await settledOriginal(page, state);
  state.role = "publisher";
  await panel(page)
    .getByRole("button", { name: "Retry original request", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
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
  await expect(
    panel(page).getByRole("button", {
      name: "Load build controls",
      exact: true,
    }),
  ).toBeVisible();
  const original = state.writes[0];
  await loaded(page);
  await panel(page)
    .getByRole("button", { name: "Read original result", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
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
    .getByRole("button", { name: "Queue approved build", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", { name: "Cancel build", exact: true }),
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
    .getByRole("button", { name: "Refresh build result", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Uncertain · Attempt 3 of 3",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    panel(page).getByText("Not confirmed as successful.", { exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("button", { name: "Cancel build", exact: true }),
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
  await settledOriginal(page, state);
  await expect(panel(page).getByRole("alert")).toContainText(
    "could not be verified",
  );
  await panel(page)
    .getByRole("button", { name: "Read original result", exact: true })
    .click();
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
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
  await settledOriginal(page, state);
  state.actorPrincipalId = "70000000-0000-4000-8000-000000000099";
  await panel(page)
    .getByRole("button", { name: "Retry original request", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  await expect(
    panel(page).getByRole("heading", {
      name: "Original request recovery",
      exact: true,
    }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
});

test("viewer admission does not dispatch a private build request", async ({
  page,
}) => {
  const state = await buildFixture(page);
  state.role = "viewer";
  await panel(page)
    .getByRole("button", { name: "Load build controls", exact: true })
    .click();
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
  const state = await buildFixture(page);
  state.readOwnMetadata = false;
  await panel(page)
    .getByRole("button", { name: "Load build controls", exact: true })
    .click();
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
    .getByRole("button", { name: "Queue approved build", exact: true })
    .click();
  await expect(
    panel(page).getByRole("button", { name: "Cancel build", exact: true }),
  ).toBeEnabled();
  state.forgeRun = true;
  await panel(page)
    .getByRole("button", { name: "Refresh build result", exact: true })
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
  await buildFixture(page);
  await page.setViewportSize({ width: 360, height: 780 });
  const load = panel(page).getByRole("button", {
    name: "Load build controls",
    exact: true,
  });
  await load.focus();
  await page.keyboard.press("Enter");
  await expect(
    panel(page).getByLabel("Exact retained environment version"),
  ).toBeVisible();
  await panel(page).getByLabel("Exact retained environment version").focus();
  await expect(
    panel(page).getByLabel("Exact retained environment version"),
  ).toBeFocused();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});
