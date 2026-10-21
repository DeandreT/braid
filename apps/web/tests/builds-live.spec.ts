import { test, expect, type Page, type Response } from "@playwright/test";
import {
  ExecutionOwner,
  type ExecutionManifest,
} from "./browser-execution-owner";

interface Manifest extends ExecutionManifest {
  evidenceId: string;
  caseId: string;
}
const owner = new ExecutionOwner<Manifest>(
  "build",
  () => process.env.WEFT_BUILD_BINDINGS,
);
const panel = (page: Page) =>
  page.getByRole("region", { name: "Approved builds", exact: true });
function revisionPath(): string {
  const scope = owner.manifest.revision;
  return `${owner.binding.readOrigin}/catalog/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}`;
}
async function open(page: Page) {
  await page.goto(revisionPath());
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
async function mutation(
  page: Page,
  suffix: RegExp,
  action: () => Promise<unknown>,
): Promise<Response> {
  const received = page.waitForResponse(
    (response) => {
      const url = new URL(response.url());
      return (
        url.origin === owner.binding.readOrigin &&
        suffix.test(url.pathname) &&
        ["PUT", "POST"].includes(response.request().method())
      );
    },
    { timeout: 120000 },
  );
  await action();
  const response = await received;
  expect([200, 201]).toContain(response.status());
  expect(response.headers()["cache-control"]).toContain("no-store");
  return response;
}
async function approve(
  page: Page,
): Promise<{ approvalId: string; buildId: string }> {
  await panel(page)
    .getByRole("button", { name: "Load saved regression runs", exact: true })
    .click();
  await panel(page)
    .getByRole("button", { name: "Verify this evidence", exact: true })
    .click();
  await expect(
    panel(page)
      .getByRole("status")
      .filter({ hasText: "Exact-byte comparison passed" }),
  ).toContainText(owner.manifest.evidenceId);
  const response = await mutation(page, /\/build-approvals\/[^/]+$/, () =>
    panel(page)
      .getByRole("button", { name: "Approve immutable build", exact: true })
      .click(),
  );
  const body = await response.json();
  expect(body.approval.revision).toEqual(owner.manifest.revision);
  expect(body.approval.regressionEvidenceId).toBe(owner.manifest.evidenceId);
  await expect(
    panel(page).getByRole("button", {
      name: "Queue approved build",
      exact: true,
    }),
  ).toBeEnabled();
  return {
    approvalId: body.approval.approvalId,
    buildId: body.approval.buildId,
  };
}
async function submit(page: Page, buildId: string): Promise<void> {
  const response = await mutation(
    page,
    new RegExp("/build-runs/" + buildId + "$"),
    () =>
      panel(page)
        .getByRole("button", { name: "Queue approved build", exact: true })
        .click(),
  );
  const value = await response.json();
  expect(value.run.buildId).toBe(buildId);
  await expect(
    panel(page).locator(".builds-result").getByText(buildId, { exact: true }),
  ).toBeVisible();
}
async function openHistory(page: Page, buildId: string): Promise<void> {
  await panel(page)
    .getByRole("button", { name: "Load own build history", exact: true })
    .click();
  const row = panel(page)
    .locator(".builds-list li")
    .filter({ hasText: buildId });
  await row
    .getByRole("button", { name: "Open build result", exact: true })
    .click();
}
async function openApproval(page: Page, approvalId: string): Promise<void> {
  await panel(page)
    .getByRole("button", { name: "Load own approvals", exact: true })
    .click();
  const row = panel(page)
    .locator(".builds-list li")
    .filter({ hasText: approvalId });
  await row.getByRole("button", { name: "Open approval", exact: true }).click();
}

test.beforeAll(async () => {
  test.setTimeout(300000);
  await owner.start();
});
test.afterAll(async () => {
  test.setTimeout(390000);
  await owner.stop();
});

test("real environment approval compilation restart and revocation preserve exact own builds", async ({
  page,
}) => {
  await open(page);
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  const environment = await mutation(
    page,
    /\/build-environments\/[^/]+\/versions\/1$/,
    () =>
      panel(page)
        .getByRole("button", { name: "Register new environment", exact: true })
        .click(),
  );
  const environmentId = (await environment.json()).environment.reference
    .environmentId;
  expect(Object.keys(environment.request().postDataJSON()).sort()).toEqual([
    "profileId",
    "profileVersion",
  ]);
  await owner.control("restart-profile", { profile: "publisher" });
  await open(page);
  const first = await approve(page);
  await submit(page, first.buildId);
  await expect(
    panel(page).getByRole("heading", {
      name: "Succeeded · Attempt 1 of 3",
      exact: true,
    }),
  ).toBeVisible({ timeout: 120000 });
  const original = await owner.control("capture-build", {
    buildId: first.buildId,
    sample: "succeeded",
  });
  expect(original.state).toBe("succeeded");
  expect(original.artifactDigest).toMatch(/^[a-f0-9]{64}$/);
  const originalDigest = String(original.artifactDigest);
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toBeVisible();
  await expect(
    panel(page).getByRole("button", { name: /download/i }),
  ).toHaveCount(0);
  await owner.control("restart");
  await open(page);
  await openHistory(page, first.buildId);
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toBeVisible();
  const second = await approve(page);
  expect(second.buildId).not.toBe(first.buildId);
  await submit(page, second.buildId);
  await expect(
    panel(page).getByRole("heading", {
      name: "Succeeded · Attempt 1 of 3",
      exact: true,
    }),
  ).toBeVisible({ timeout: 120000 });
  await owner.control("capture-build", {
    buildId: second.buildId,
    sample: "succeeded",
  });
  await owner.control("pause-worker");
  const canceled = await approve(page);
  await submit(page, canceled.buildId);
  await expect(
    panel(page).getByRole("heading", {
      name: "Queued · Attempt 0 of 3",
      exact: true,
    }),
  ).toBeVisible();
  await mutation(page, /\/build-runs\/[^/]+\/cancel$/, () =>
    panel(page)
      .getByRole("button", { name: "Cancel build", exact: true })
      .click(),
  );
  await expect(
    panel(page).getByRole("heading", {
      name: "Canceled · Attempt 0 of 3",
      exact: true,
    }),
  ).toBeVisible();
  await owner.control("capture-build", {
    buildId: canceled.buildId,
    sample: "canceled",
  });
  await owner.control("resume-worker");
  await owner.control("restart-profile", { profile: "administrator" });
  await open(page);
  await expect(
    panel(page).getByLabel("Exact retained environment version"),
  ).toHaveValue(environmentId + "/1");
  await panel(page)
    .getByRole("button", { name: "Environment administration", exact: true })
    .click();
  await mutation(page, /\/build-environments\/[^/]+\/versions\/2$/, () =>
    panel(page)
      .getByRole("button", { name: "Register next version", exact: true })
      .click(),
  );
  await owner.control("restart-profile", { profile: "publisher" });
  await open(page);
  await openApproval(page, first.approvalId);
  await expect(
    panel(page).getByRole("button", {
      name: "Queue approved build",
      exact: true,
    }),
  ).toBeDisabled();
  await panel(page)
    .getByRole("button", { name: "Recover original build", exact: true })
    .click();
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toBeVisible();
  await mutation(page, /\/build-approvals\/[^/]+\/revoke$/, () =>
    panel(page)
      .getByRole("button", { name: "Revoke own approval", exact: true })
      .click(),
  );
  await expect(
    panel(page).getByRole("button", {
      name: "Queue approved build",
      exact: true,
    }),
  ).toBeDisabled();
  expect((await owner.control("checkpoint")).capturedBuilds).toBe(3);
  await owner.control("revoke-publisher");
  await panel(page)
    .getByRole("button", { name: "Refresh build result", exact: true })
    .click();
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toHaveCount(0);
  await expect(
    panel(page).getByRole("button", {
      name: "Queue approved build",
      exact: true,
    }),
  ).toHaveCount(0);
  await owner.control("restore-publisher");
  await owner.control("restart-profile", { profile: "viewer" });
  await page.goto(revisionPath());
  await panel(page)
    .getByRole("button", { name: "Load build controls", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  expect((await owner.control("checkpoint")).capturedBuilds).toBe(3);
});
