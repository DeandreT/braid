import { test, expect, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";

test("capture authorized operations in the shared shell", async ({ page }) => {
  const directory = process.env.BRAID_UI_CAPTURE_DIR;
  test.skip(!directory, "Optional sanitized UI captures");
  await fixture(page);
  await mkdir(directory!, { recursive: true });
  for (const width of [1440, 1024, 720, 360]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(
      page.getByRole("heading", { name: "Operations", exact: true }),
    ).toBeVisible();
    expect(
      await page
        .locator("braid-operations-page dt")
        .evaluateAll((terms) =>
          terms.every((term) => term.scrollWidth <= term.clientWidth),
        ),
    ).toBe(true);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: `${directory}/operations-${width}.png`,
      fullPage: true,
    });
  }
});
import {
  synthetic,
  syntheticCatalog,
  mappingPath,
  catalogApi,
} from "./catalog.fixture";

const target = "70000000-0000-4000-8000-000000000001";
const environment = "70000000-0000-4000-8000-000000000002";
const base = catalogApi(mappingPath(synthetic.mapping.scope)) + "/operations";

/** Synthetic browser boundary only. Real release admission and storage have PostgreSQL/HTTP tests. */
async function fixture(page: Page, session = false) {
  const state = {
    role: "administrator",
    version: 1,
    enabled: true,
    operationsEnabled: true,
    effects: 0,
    applied: new Set<string>(),
    failure: "" as "" | "lost" | "badReceipt" | "stale",
    writes: [] as { url: string; body: string; etag: string; token: string }[],
    reads: [] as string[],
    identity: `v1.${"A".repeat(43)}`,
    actor: synthetic.actor,
    sessionStatus: 200,
    replyGate: null as Promise<void> | null,
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
  if (session)
    await page.route("**/api/auth/session", (route) =>
      route.fulfill(
        state.sessionStatus === 200
          ? {
              json: {
                version: 1,
                mode: "session",
                state: "authenticated",
                authActionRequestToken: "YXV0aC1h",
                requestToken: "ZXBvY2gtYQ",
                identityContext: state.identity,
                displayName: null,
              },
            }
          : { status: state.sessionStatus, body: "" },
      ),
    );
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (session && url.pathname === "/api/v1/catalog/context")
      return route.fulfill({
        json: {
          ...synthetic.context,
          mode: "session",
          identityContext: state.identity,
          actorPrincipalId: state.actor,
          requestToken: "ZXBvY2gtYQ",
        },
      });
    if (
      url.pathname ===
      catalogApi(mappingPath(synthetic.mapping.scope)).split("/mappings/")[0]
    ) {
      await route.fulfill({
        json: {
          ...synthetic.project,
          access: { ...synthetic.project.access, role: state.role },
        },
      });
      return;
    }
    if (!url.pathname.startsWith(base)) return route.fallback();
    if (request.method() === "POST") {
      state.writes.push({
        url: request.url(),
        body: request.postData()!,
        etag: request.headers()["if-match"]!,
        token: request.headers()["x-xsrf-token"]!,
      });
      if (state.replyGate) await state.replyGate;
      if (!state.operationsEnabled)
        return route.fulfill({ status: 404, json: { code: "notFound" } });
      const failure = state.failure;
      state.failure = "";
      if (failure === "stale")
        return route.fulfill({
          status: 412,
          json: { code: "preconditionFailed" },
        });
      const operationId = url.pathname.split("/").at(-1)!;
      if (!state.applied.has(operationId)) {
        state.applied.add(operationId);
        state.effects++;
        state.version++;
        state.enabled = false;
      }
      if (failure === "lost") return route.abort("connectionreset");
      await route.fulfill({
        json: {
          operationId: failure === "badReceipt" ? target : operationId,
          outcome: "applied",
        },
      });
      return;
    }
    state.reads.push(request.url());
    if (url.pathname === base || url.pathname.endsWith("/targets")) {
      await route.fulfill({
        json: {
          items: [
            {
              kind: "target",
              target_id: target,
              environment_id: environment,
              environment_version: "1",
              enabled: state.enabled,
              active_release_id: null,
              runtime_image: "sha256:" + "f".repeat(64),
              profile: {
                id: "synthetic",
                version: 1,
                runtime: "dotnet",
                runtimeVersion: "10.0.12",
                artifactFormat: "dotnet-library",
                maximumArtifactBytes: 1024,
              },
              version: String(state.version),
              active: null,
            },
          ],
          nextOffset: null,
        },
      });
    } else if (url.pathname.endsWith("/summary")) {
      await route.fulfill({
        json: {
          count: 3,
          failed: 1,
          running: 0,
          retryable: 0,
          failuresByCategory: { invalidInput: 1 },
          meanLatencyMs: 20,
          p95LatencyMs: 30,
        },
      });
    } else {
      await route.fulfill({
        json: {
          items: [],
          nextOffset:
            url.pathname.endsWith("/invocations") &&
            url.searchParams.get("offset") === "0"
              ? 20
              : null,
        },
      });
    }
  });
  await page.goto(mappingPath(synthetic.mapping.scope) + "/operations");
  await expect(
    page.getByRole("heading", { name: "Operations", exact: true }),
  ).toBeVisible({ timeout: 20_000 });
  await expect(
    page.getByRole("button", { name: "Operate this target" }),
  ).toBeVisible();
  return state;
}

test("temporary feature disablement cannot erase a committed uncertain operation", async ({
  page,
}) => {
  const state = await fixture(page);
  await select(page);
  state.failure = "lost";
  await disable(page);
  const recover = page.getByRole("button", {
    name: "Recover original operation",
  });
  await expect(recover).toBeEnabled();
  expect(state.effects).toBe(1);
  state.operationsEnabled = false;
  const refused = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      response.url().includes(`${base}/${target}/actions/`) &&
      response.status() === 404,
  );
  await recover.click();
  await (await refused).finished();
  await expect(recover).toBeEnabled();
  await expect(page.getByRole("button", { name: "Apply action" })).toHaveCount(
    0,
  );
  expect(state.writes).toHaveLength(2);
  expect(state.writes[1]).toEqual(state.writes[0]);
  state.operationsEnabled = true;
  await recover.click();
  await expect(
    page.getByText(
      "Operation recorded. Refresh the target before another change.",
      { exact: true },
    ),
  ).toBeVisible();
  expect(state.writes).toHaveLength(3);
  expect(state.writes[2]).toEqual(state.writes[0]);
  expect(state.effects).toBe(1);
  expect(state.version).toBe(2);
});

test("a definitive first-attempt feature refusal does not retain an uncertain request", async ({
  page,
}) => {
  const state = await fixture(page);
  await select(page);
  state.operationsEnabled = false;
  await disable(page);
  await expect(
    page.getByText("The operation was refused.", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Recover original operation" }),
  ).toHaveCount(0);
  expect(state.effects).toBe(0);
});

async function select(page: Page) {
  await page.getByRole("button", { name: "Operate this target" }).click();
  await expect(
    page.getByRole("button", { name: "Refresh", exact: true }),
  ).toBeEnabled();
}
async function disable(page: Page) {
  await page
    .getByRole("combobox", { name: "Action", exact: true })
    .selectOption("disable");
  await page.getByLabel("I reviewed this target, action and version.").check();
  await page.getByRole("button", { name: "Apply action" }).click();
}

test("invocation filters, paging, summary and caller administration are reachable", async ({
  page,
}) => {
  const state = await fixture(page);
  await select(page);
  await page
    .getByRole("combobox", { name: "View", exact: true })
    .selectOption("invocations");
  await page.getByText("Invocation filters", { exact: true }).click();
  await page.getByLabel("Since (ISO UTC)").fill("2026-10-24T00:00:00Z");
  await page
    .getByLabel("Until (exclusive, ISO UTC)")
    .fill("2026-10-25T00:00:00Z");
  await page.getByLabel("Caller client ID").fill(target);
  await page.getByLabel("Outcome", { exact: true }).fill("invalidInput");
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await page.getByRole("button", { name: "Next page", exact: true }).click();
  await expect
    .poll(() => new URL(state.reads.at(-1)!).searchParams.get("offset"))
    .toBe("20");
  const query = new URL(state.reads.at(-1)!).searchParams;
  expect(query.get("caller")).toBe(target);
  expect(query.get("outcome")).toBe("invalidInput");
  expect(query.get("since")).toBe("2026-10-24T00:00:00Z");
  await page
    .getByRole("combobox", { name: "View", exact: true })
    .selectOption("summary");
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await expect(page.locator("dt", { hasText: "p95LatencyMs" })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "caller administration", exact: true }),
  ).toHaveAttribute("href", "/administration");
});

for (const failure of ["lost", "badReceipt"] as const) {
  test(`${failure} response keeps the original conditional recovery request`, async ({
    page,
  }) => {
    const state = await fixture(page);
    await select(page);
    state.failure = failure;
    await disable(page);
    await expect(
      page.getByRole("button", { name: "Recover original operation" }),
    ).toBeEnabled();
    await page
      .getByRole("button", { name: "Recover original operation" })
      .click();
    await expect(
      page.getByText(
        "Operation recorded. Refresh the target before another change.",
        { exact: true },
      ),
    ).toBeVisible();
    expect(state.writes).toHaveLength(2);
    expect(state.writes[1]).toEqual(state.writes[0]);
    expect(state.writes[0].etag).toBe('"1"');
    expect(state.writes[0].token).toBe("synthetic-ui-only-not-a-credential");
    expect(state.version).toBe(2);
  });
}

test("fresh project authority, not the global principal context, gates dispatch", async ({
  page,
}) => {
  const state = await fixture(page);
  await select(page);
  state.role = "viewer";
  await disable(page);
  await expect(
    page.getByRole("button", { name: "Recover original operation" }),
  ).toBeEnabled();
  expect(state.writes).toHaveLength(0);
});

test("a stale target requires review instead of silently retrying a new version", async ({
  page,
}) => {
  const state = await fixture(page);
  await select(page);
  state.failure = "stale";
  await disable(page);
  await expect(
    page.getByText(
      "The target version changed. Refresh and review before trying again.",
      { exact: true },
    ),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Recover original operation" }),
  ).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
});

const warning = (page: Page) => page.locator("#sign-in-navigation-warning");

async function account(page: Page) {
  const trigger = page.locator("#account-trigger");
  if ((await trigger.getAttribute("aria-expanded")) !== "true")
    await trigger.click();
  await expect(trigger).toHaveAttribute("aria-expanded", "true");
  const controls = page.getByRole("region", {
    name: "Sign-in and session",
    exact: true,
  });
  await expect(controls).toBeVisible();
  return controls;
}

async function closeAccount(page: Page) {
  const trigger = page.locator("#account-trigger");
  await trigger.click();
  // Click completion precedes the zoneless render; do not reuse its old open state.
  await expect(trigger).toHaveAttribute("aria-expanded", "false");
}

async function checkSession(page: Page) {
  const controls = await account(page);
  const response = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/auth/session",
  );
  await controls
    .getByRole("button", { name: "Check session", exact: true })
    .click();
  await (await response).finished();
  await expect(
    controls.getByRole("button", { name: "Check session", exact: true }),
  ).toBeEnabled();
  await closeAccount(page);
}

async function expectWarning(page: Page, present: boolean) {
  await account(page);
  if (present) await expect(warning(page)).toBeVisible();
  else await expect(warning(page)).toHaveCount(0);
  await closeAccount(page);
}

test("account warning tracks action edits, not loaded targets or read filters", async ({
  page,
}, info) => {
  await fixture(page, true);
  await account(page);
  await expectWarning(page, false);
  await select(page);
  await expectWarning(page, false);
  await page.getByText("Invocation filters", { exact: true }).click();
  await page.getByLabel("Caller client ID").fill(target);
  await expectWarning(page, false);
  await account(page);
  await page.screenshot({
    path: info.outputPath("operations-before.png"),
    fullPage: true,
  });
  await closeAccount(page);
  await page
    .getByRole("combobox", { name: "Action", exact: true })
    .selectOption("disable");
  await expectWarning(page, true);
  await expect(
    (await account(page)).getByRole("button", {
      name: "Sign in again",
      exact: true,
    }),
  ).toHaveAttribute("aria-describedby", "sign-in-navigation-warning");
  await page.screenshot({
    path: info.outputPath("operations-after.png"),
    fullPage: true,
  });
  await closeAccount(page);
  await page
    .getByRole("combobox", { name: "Action", exact: true })
    .selectOption("activate");
  await expectWarning(page, false);
  await page.getByLabel("Qualification ID", { exact: true }).fill(target);
  await expectWarning(page, true);
  await page.getByLabel("Qualification ID", { exact: true }).fill("");
  await expectWarning(page, false);
  await page.getByLabel("I reviewed this target, action and version.").check();
  await expectWarning(page, true);
  await page
    .getByLabel("I reviewed this target, action and version.")
    .uncheck();
  await expectWarning(page, false);
  await page.getByLabel("Target ID", { exact: true }).fill(environment);
  await expectWarning(page, true);
  await page.getByLabel("Target ID", { exact: true }).fill(target);
  await expectWarning(page, false);
});

test("acknowledged operations clear the warning and later edits start new work", async ({
  page,
}) => {
  const state = await fixture(page, true);
  await select(page);
  await disable(page);
  await expect(
    page.getByText(
      "Operation recorded. Refresh the target before another change.",
      { exact: true },
    ),
  ).toBeVisible();
  await expectWarning(page, false);
  expect(state.effects).toBe(1);
  await page.getByLabel("Target ID", { exact: true }).fill(environment);
  await expectWarning(page, true);
});

test("a pending request warns and acknowledgement preserves newer edits", async ({
  page,
}) => {
  const state = await fixture(page, true);
  await select(page);
  let release!: () => void;
  state.replyGate = new Promise<void>((resolve) => {
    release = resolve;
  });
  try {
    await disable(page);
    await expect.poll(() => state.writes.length).toBe(1);
    await expectWarning(page, true);
    await page.getByLabel("Target ID", { exact: true }).fill(environment);
    release();
    await expect(
      page.getByText(
        "Operation recorded. Refresh the target before another change.",
        { exact: true },
      ),
    ).toBeVisible();
    await expectWarning(page, true);
    expect(state.effects).toBe(1);
  } finally {
    release();
  }
});

test("failed session checks retain action drafts for the same owner but never another owner", async ({
  page,
}) => {
  const state = await fixture(page, true);
  await select(page);
  await page
    .getByRole("combobox", { name: "Action", exact: true })
    .selectOption("rollback");
  await page.getByLabel("Qualification ID", { exact: true }).fill(target);
  await page
    .getByLabel("Previous release ID", { exact: true })
    .fill(environment);
  await page.getByLabel("Target ID", { exact: true }).fill("unfinished-target");
  state.sessionStatus = 503;
  await checkSession(page);
  await expect(
    page.getByLabel("Previous release ID", { exact: true }),
  ).not.toBeVisible();
  state.sessionStatus = 200;
  await checkSession(page);
  await expectWarning(page, true);
  await page
    .getByRole("button", { name: "Refresh operations", exact: true })
    .click();
  await expect(
    page.getByLabel("Previous release ID", { exact: true }),
  ).toHaveValue(environment);
  await expect(
    page.getByLabel("Qualification ID", { exact: true }),
  ).toHaveValue(target);
  await expect(page.getByLabel("Target ID", { exact: true })).toHaveValue(
    "unfinished-target",
  );
  state.identity = `v1.${"B".repeat(42)}A`;
  state.actor = "10000000-0000-4000-8000-000000000003";
  await checkSession(page);
  await page
    .getByRole("button", { name: "Refresh operations", exact: true })
    .click();
  await expectWarning(page, false);
  await expect(
    page.getByRole("button", { name: "Operate this target" }),
  ).toBeVisible();
  await expect(
    page.getByLabel("Previous release ID", { exact: true }),
  ).toHaveCount(0);
  await expect(page.getByLabel("Target ID", { exact: true })).toHaveValue("");
  // The saved baseline is already empty here. Clearing another owner's choice
  // must still notify the tracker when both baseline strings stay unchanged.
  await page
    .getByRole("combobox", { name: "Action", exact: true })
    .selectOption("disable");
  await expectWarning(page, true);
  state.identity = `v1.${"C".repeat(42)}A`;
  state.actor = "10000000-0000-4000-8000-000000000004";
  await checkSession(page);
  await page
    .getByRole("button", { name: "Refresh operations", exact: true })
    .click();
  await expectWarning(page, false);
  await expect(
    page.getByRole("combobox", { name: "Action", exact: true }),
  ).toHaveValue("activate");
  expect(state.writes).toHaveLength(0);
});

test("an uncertain original operation survives unavailable checks and same-owner recovery", async ({
  page,
}) => {
  const state = await fixture(page, true);
  await select(page);
  state.failure = "lost";
  await disable(page);
  await expect(
    page.getByRole("button", { name: "Recover original operation" }),
  ).toBeEnabled();
  state.sessionStatus = 503;
  await checkSession(page);
  await expect(
    page.getByRole("button", { name: "Recover original operation" }),
  ).not.toBeVisible();
  state.sessionStatus = 200;
  await checkSession(page);
  await expectWarning(page, true);
  await page
    .getByRole("button", { name: "Refresh operations", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Recover original operation" })
    .click();
  await expect(
    page.getByText(
      "Operation recorded. Refresh the target before another change.",
      { exact: true },
    ),
  ).toBeVisible();
  await expectWarning(page, false);
  expect(state.writes).toHaveLength(2);
  expect(state.writes[1]).toEqual(state.writes[0]);
  expect(state.effects).toBe(1);
});
