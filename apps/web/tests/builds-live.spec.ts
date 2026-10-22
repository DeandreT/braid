import { test, expect, type Page, type Response } from "@playwright/test";
import { isAbsolute, join } from "node:path";
import { confirmAction } from "./confirmation.fixture";
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
  page.getByRole("region", { name: "Prepare this version", exact: true });
function revisionPath(): string {
  const scope = owner.manifest.revision;
  return `${owner.binding.readOrigin}/catalog/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}`;
}
async function open(page: Page) {
  await page.goto(revisionPath());
  await page.getByRole("tab", { name: "Prepare", exact: true }).click();
  await panel(page)
    .getByRole("button", { name: "Load preparation controls", exact: true })
    .click();
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
  await confirmAction(page);
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
    panel(page).getByRole("status").filter({
      hasText: "Result matches the saved expectation, including formatting",
    }),
  ).toBeVisible();
  const evidenceDetails = panel(page).locator(".builds-evidence-reference");
  await evidenceDetails.locator("summary").click();
  await expect(evidenceDetails.locator("code")).toHaveText(
    owner.manifest.evidenceId,
  );
  await evidenceDetails.locator("summary").click();
  const response = await mutation(page, /\/build-approvals\/[^/]+$/, () =>
    panel(page)
      .getByRole("button", { name: "Approve preparation", exact: true })
      .click(),
  );
  const body = await response.json();
  expect(body.approval.revision).toEqual(owner.manifest.revision);
  expect(body.approval.regressionEvidenceId).toBe(owner.manifest.evidenceId);
  await expect(
    panel(page).getByRole("button", {
      name: "Prepare approved version",
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
        .getByRole("button", { name: "Prepare approved version", exact: true })
        .click(),
  );
  const value = await response.json();
  expect(value.run.buildId).toBe(buildId);
  await showTechnicalDetails(page);
  await expect(
    panel(page).locator(".builds-result").getByText(buildId, { exact: true }),
  ).toBeVisible();
  await panel(page).locator(".builds-result-details > summary").click();
}
async function showTechnicalDetails(page: Page): Promise<void> {
  const details = panel(page).locator(".builds-result-details");
  if (
    !(await details.evaluate((element) => (element as HTMLDetailsElement).open))
  )
    await details.locator(":scope > summary").click();
}
async function openHistory(page: Page, buildId: string): Promise<void> {
  await panel(page)
    .getByRole("button", { name: "Load preparation history", exact: true })
    .click();
  const row = panel(page)
    .locator(".wb-build-table tbody tr")
    .filter({ hasText: buildId });
  await row
    .getByRole("button", { name: "Open preparation result", exact: true })
    .click();
}
async function openApproval(page: Page, approvalId: string): Promise<void> {
  await panel(page)
    .getByRole("button", { name: "Load your approvals", exact: true })
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
      name: "Preparation complete · Attempt 1 of 3",
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
  await showTechnicalDetails(page);
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toBeVisible();
  if (process.env.WEFT_WORKBENCH_CAPTURE_DIR) {
    expect(isAbsolute(process.env.WEFT_WORKBENCH_CAPTURE_DIR)).toBe(true);
    await panel(page).locator(".builds-result-details > summary").click();
    await panel(page).locator(".builds-result").scrollIntoViewIfNeeded();
    await page.screenshot({
      path: join(
        process.env.WEFT_WORKBENCH_CAPTURE_DIR,
        "workbench-real-builds.png",
      ),
    });
  }
  await expect(
    panel(page).getByRole("button", { name: /download/i }),
  ).toHaveCount(0);
  await owner.control("restart");
  await open(page);
  await openHistory(page, first.buildId);
  await showTechnicalDetails(page);
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toBeVisible();
  const second = await approve(page);
  expect(second.buildId).not.toBe(first.buildId);
  await submit(page, second.buildId);
  await expect(
    panel(page).getByRole("heading", {
      name: "Preparation complete · Attempt 1 of 3",
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
      .getByRole("button", { name: "Cancel preparation", exact: true })
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
    panel(page).getByRole("combobox", {
      name: "Environment version",
      exact: true,
    }),
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
      name: "Prepare approved version",
      exact: true,
    }),
  ).toBeDisabled();
  await panel(page)
    .getByRole("button", { name: "Check previous preparation", exact: true })
    .click();
  await showTechnicalDetails(page);
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toBeVisible();
  await mutation(page, /\/build-approvals\/[^/]+\/revoke$/, () =>
    panel(page)
      .getByRole("button", { name: "Withdraw your approval", exact: true })
      .click(),
  );
  await expect(
    panel(page).getByRole("button", {
      name: "Prepare approved version",
      exact: true,
    }),
  ).toBeDisabled();
  expect((await owner.control("checkpoint")).capturedBuilds).toBe(3);
  await owner.control("revoke-publisher");
  await panel(page)
    .getByRole("button", { name: "Refresh preparation result", exact: true })
    .click();
  await expect(
    panel(page).getByText(originalDigest, { exact: true }),
  ).toHaveCount(0);
  await expect(
    panel(page).getByRole("button", {
      name: "Prepare approved version",
      exact: true,
    }),
  ).toHaveCount(0);
  await owner.control("restore-publisher");
  await owner.control("restart-profile", { profile: "viewer" });
  await page.goto(revisionPath());
  await page.getByRole("tab", { name: "Prepare", exact: true }).click();
  await panel(page)
    .getByRole("button", { name: "Load preparation controls", exact: true })
    .click();
  await expect(panel(page).getByRole("alert")).toContainText("access changed");
  expect((await owner.control("checkpoint")).capturedBuilds).toBe(3);
});
