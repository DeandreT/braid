import { expect, test } from "@playwright/test";
import { createHash } from "node:crypto";
import {
  synthetic,
  syntheticCatalog,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
} from "./catalog.fixture";

test.setTimeout(90000);

test("account warning tracks unsent MFD work but not a recoverable server review", async ({
  page,
}, testInfo) => {
  const data = structuredClone(synthetic);
  const identityContext = "v1." + "a".repeat(42) + "A";
  const context = {
    ...data.context,
    mode: "session",
    identityContext,
    requestToken: "dG9rZW4",
    features: { ...data.context.features, createMappings: true },
  };
  data.project.access.actions.createMappings = true;
  data.project.access.role = "publisher";
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": context,
    [catalogApi(projectPath(data.project.scope))]: data.project,
    "/api/v1/catalog/interchange": { enabled: true },
  });
  await page.route("**/api/auth/session*", (route) =>
    route.fulfill({
      json: {
        version: 1,
        mode: "session",
        state: "authenticated",
        authActionRequestToken: "YXV0aA",
        requestToken: "dG9rZW4",
        identityContext,
        displayName: null,
      },
    }),
  );
  let sent = false;
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/mfd-imports/review?*", async (route) => {
    sent = true;
    await pending;
    await route.fulfill({ json: privateReview() });
  });
  try {
    await page.goto(projectPath(data.project.scope) + "/mappings/new");
    const panel = page.locator("braid-mfd-import");
    const warning = page.locator("#sign-in-navigation-warning");
    await expect(panel.getByLabel("Mapping name")).toBeVisible();
    await page.locator("#account-trigger").click();
    await expect(warning).toHaveCount(0);
    await page.screenshot({
      path: testInfo.outputPath("mfd-account-clean.png"),
    });
    await page.locator("#account-trigger").click();
    await panel.getByLabel("Mapping name").fill("Unsent MFD name");
    await page.locator("#account-trigger").click();
    await expect(warning).toBeVisible();
    await page.screenshot({
      path: testInfo.outputPath("mfd-account-unsent.png"),
    });
    await page.locator("#account-trigger").click();
    await panel.getByLabel("Design and schema files").setInputFiles({
      name: "design.mfd",
      mimeType: "application/xml",
      buffer: Buffer.from("<self-authored/>"),
    });
    await panel.getByRole("button", { name: "Review conversion" }).click();
    await expect.poll(() => sent).toBe(true);
    await page.locator("#account-trigger").click();
    await expect(warning).toBeVisible();
    release();
    await expect(
      panel.getByText("Private rejected conversion summary", { exact: true }),
    ).toBeVisible();
    await expect(warning).toHaveCount(0);
    await page.locator("#account-trigger").click();
    await page
      .getByRole("button", { name: "Refresh access", exact: true })
      .click();
    await expect(
      panel.getByText("Private rejected conversion summary", { exact: true }),
    ).toBeVisible();
  } finally {
    release();
  }
});

for (const append of [false, true])
  test(`unavailable session hides and restores only the same owner's MFD ${append ? "append" : "create"} package`, async ({
    page,
  }, testInfo) => {
    const data = structuredClone(synthetic);
    const context = {
      ...data.context,
      mode: "session",
      identityContext: "v1." + "a".repeat(42) + "A",
      requestToken: "dG9rZW4",
      features: {
        ...data.context.features,
        createMappings: true,
        uploadRevisions: true,
      },
    };
    data.project.access.actions.createMappings = true;
    data.project.access.actions.uploadRevisions = true;
    data.project.access.role = "publisher";
    data.mapping.access.actions.uploadRevisions = true;
    data.mapping.access.role = "publisher";
    await syntheticCatalog(page, {
      [catalogApi(projectPath(data.project.scope))]: data.project,
      [catalogApi(mappingPath(data.mapping.scope))]: data.mapping,
      "/api/v1/catalog/interchange": { enabled: true },
    });
    await page.route("**/api/v1/catalog/context", (route) =>
      route.fulfill({ json: context }),
    );
    let unavailable = false;
    await page.route("**/api/auth/session*", (route) =>
      route.fulfill(
        unavailable
          ? { status: 503, json: { code: "unavailable" } }
          : {
              json: {
                version: 1,
                mode: "session",
                state: "authenticated",
                authActionRequestToken: "YXV0aA",
                requestToken: "dG9rZW4",
                identityContext: context.identityContext,
                displayName: null,
              },
            },
      ),
    );
    await page.goto(
      append
        ? mappingPath(data.mapping.scope) + "/revisions/new"
        : projectPath(data.project.scope) + "/mappings/new",
    );
    const panel = page.locator("braid-mfd-import");
    if (!append) {
      await panel.getByLabel("Mapping name").fill("Retained unavailable name");
      await panel
        .getByLabel("Description")
        .fill("Retained unavailable description");
    }
    await panel.getByLabel("Design and schema files").setInputFiles([
      {
        name: "design.mfd",
        mimeType: "application/xml",
        buffer: Buffer.from("<retained-design/>"),
      },
      {
        name: "schema.json",
        mimeType: "application/json",
        buffer: Buffer.from('{"retained":true}'),
      },
    ]);
    await page.screenshot({
      path: testInfo.outputPath("mfd-retained-before.png"),
    });
    const check = async () => {
      if (!(await page.locator("#account-menu").isVisible()))
        await page.locator("#account-trigger").click();
      await page
        .getByRole("button", { name: "Check session", exact: true })
        .click();
      if (await page.locator("#account-menu").isVisible())
        await page.locator("#account-trigger").click();
    };
    unavailable = true;
    await check();
    await expect(page.getByLabel("Design and schema files")).toHaveCount(0);
    expect(
      await page.evaluate(() => {
        const event = new Event("beforeunload", { cancelable: true });
        window.dispatchEvent(event);
        return event.defaultPrevented;
      }),
    ).toBe(true);
    const selectedUrl = page.url();
    await page.locator('a[href="/catalog"]').first().click();
    await expect(page).toHaveURL(selectedUrl);
    let releaseAdmission = () => {};
    let denyProject = !append;
    let capabilities = 0;
    if (!append) {
      const admission = new Promise<void>((resolve) => {
        releaseAdmission = resolve;
      });
      await page.route(
        "**" + catalogApi(projectPath(data.project.scope)),
        async (route) => {
          if (denyProject) {
            await admission;
            await route.fulfill({ status: 404, json: { code: "notFound" } });
          } else await route.fulfill({ json: data.project });
        },
      );
      await page.route("**/api/v1/catalog/interchange", async (route) => {
        capabilities++;
        await route.fulfill({ json: { enabled: true } });
      });
    }
    unavailable = false;
    await check();
    await page
      .getByRole("button", { name: "Refresh access", exact: true })
      .click();
    if (!append) {
      try {
        await expect.poll(() => capabilities).toBeGreaterThan(0);
        await expect(
          panel.getByRole("heading", { name: "Import MapForce design" }),
        ).toBeVisible();
        await expect(
          panel.getByRole("list", { name: "Selected package files" }),
        ).toHaveCount(0);
        await expect(panel.getByLabel("Mapping name")).toHaveValue("");
      } finally {
        releaseAdmission();
      }
      await expect(
        panel.getByRole("heading", { name: "Import MapForce design" }),
      ).toHaveCount(0);
      denyProject = false;
      await page
        .getByRole("button", { name: "Refresh access", exact: true })
        .click();
    }
    await expect(
      panel
        .getByRole("list", { name: "Selected package files" })
        .getByRole("listitem"),
    ).toHaveText(["design.mfd", "schema.json"]);
    if (!append) {
      await expect(panel.getByLabel("Mapping name")).toHaveValue(
        "Retained unavailable name",
      );
      await expect(panel.getByLabel("Description")).toHaveValue(
        "Retained unavailable description",
      );
    }
    await page.screenshot({
      path: testInfo.outputPath("mfd-retained-after.png"),
    });
    let sent = false;
    await page.route("**/mfd-imports/review?*", async (route) => {
      const body = route.request().postDataBuffer()!;
      expect(body.includes(Buffer.from("<retained-design/>"))).toBe(true);
      expect(body.includes(Buffer.from('{"retained":true}'))).toBe(true);
      sent = true;
      await route.fulfill({ status: 503, json: { code: "unavailable" } });
    });
    await panel.getByRole("button", { name: "Review conversion" }).click();
    await expect.poll(() => sent).toBe(true);
    await expect(
      panel.getByRole("button", { name: "Review conversion" }),
    ).toBeEnabled();
    context.actorPrincipalId = "10000000-0000-4000-8000-000000000099";
    context.identityContext = "v1." + "b".repeat(42) + "A";
    await check();
    await page
      .getByRole("button", { name: "Refresh access", exact: true })
      .click();
    await expect(
      panel.getByRole("list", { name: "Selected package files" }),
    ).toHaveCount(0);
    if (!append) await expect(panel.getByLabel("Mapping name")).toHaveValue("");
  });

test("uncertain confirmation cannot become a new draft when recovery reads fail", async ({
  page,
}) => {
  const data = structuredClone(synthetic);
  const identityContext = "v1." + "a".repeat(42) + "A";
  const context = {
    ...data.context,
    mode: "session",
    identityContext,
    requestToken: "dG9rZW4",
    features: { ...data.context.features, createMappings: true },
  };
  data.project.access.actions.createMappings = true;
  data.project.access.role = "publisher";
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": context,
    [catalogApi(projectPath(data.project.scope))]: data.project,
    "/api/v1/catalog/interchange": { enabled: true },
  });
  let unavailable = false;
  await page.route("**/api/auth/session*", (route) =>
    route.fulfill(
      unavailable
        ? { status: 503, json: { code: "unavailable" } }
        : {
            json: {
              version: 1,
              mode: "session",
              state: "authenticated",
              authActionRequestToken: "YXV0aA",
              requestToken: "dG9rZW4",
              identityContext,
              displayName: null,
            },
          },
    ),
  );
  let reviews = 0;
  const review = privateReview();
  await page.route("**/mfd-imports/review?*", (route) => {
    reviews++;
    return route.fulfill({ json: review });
  });
  let release!: () => void;
  const confirmation = new Promise<void>((resolve) => {
    release = resolve;
  });
  let confirmations = 0;
  let finished = false;
  await page.route("**/mfd-imports/*/confirm", async (route) => {
    confirmations++;
    await confirmation;
    try {
      await route.fulfill({ status: 503, json: { code: "unavailable" } });
    } catch {
      /* The old fenced request may already be canceled. */
    } finally {
      finished = true;
    }
  });
  let restores = 0;
  await page.route(`**/mfd-imports/${review.reviewId}`, (route) => {
    restores++;
    return route.fulfill({ status: 503, json: { code: "unavailable" } });
  });
  const check = async () => {
    await page.evaluate(
      () =>
        new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
    );
    if (!(await page.locator("#account-menu").isVisible()))
      await page.locator("#account-trigger").click();
    await page
      .getByRole("button", { name: "Check session", exact: true })
      .click();
    if (await page.locator("#account-menu").isVisible())
      await page.locator("#account-trigger").click();
  };
  try {
    await page.goto(projectPath(data.project.scope) + "/mappings/new");
    const panel = page.locator("braid-mfd-import");
    await panel.getByLabel("Mapping name").fill(review.mappingName);
    await panel.getByLabel("Design and schema files").setInputFiles({
      name: "design.mfd",
      mimeType: "application/xml",
      buffer: Buffer.from("<self-authored/>"),
    });
    await panel.getByRole("button", { name: "Review conversion" }).click();
    await panel.getByRole("checkbox").check();
    await panel
      .getByRole("button", { name: "Confirm or recover import" })
      .click();
    await expect.poll(() => confirmations).toBe(1);
    unavailable = true;
    await check();
    await expect(panel.getByText(review.summary, { exact: true })).toHaveCount(
      0,
    );
    await expect(
      page.getByRole("button", {
        name: "Discard retained MFD draft",
        includeHidden: true,
      }),
    ).toBeDisabled();
    unavailable = false;
    await check();
    await page
      .getByRole("button", { name: "Refresh access", exact: true })
      .click();
    await expect(
      panel.getByText(review.summary, { exact: true }),
    ).toBeVisible();
    await expect(
      panel.getByRole("button", { name: "Review conversion" }),
    ).toHaveCount(0);
    release();
    await expect.poll(() => finished).toBe(true);
    await page.reload();
    await expect(
      panel.getByRole("button", { name: "Retry original review recovery" }),
    ).toBeEnabled();
    await expect(panel.getByLabel("Mapping name")).toHaveCount(0);
    await expect(
      panel.getByRole("button", { name: "Review conversion" }),
    ).toHaveCount(0);
    await panel
      .getByRole("button", { name: "Retry original review recovery" })
      .click();
    await expect.poll(() => restores).toBeGreaterThan(1);
    expect(reviews).toBe(1);
    expect(confirmations).toBe(1);
    await panel.getByRole("button", { name: "Clear this import" }).click();
    await expect(
      panel.getByRole("button", { name: "Forget review", exact: true }),
    ).toBeDisabled();
  } finally {
    release();
  }
});

test("same-owner access and focus checks preserve unsent MFD fields and files", async ({
  page,
}) => {
  const data = structuredClone(synthetic);
  data.context.features.createMappings = true;
  data.project.access.actions.createMappings = true;
  data.project.access.role = "publisher";
  await syntheticCatalog(page, {
    [catalogApi(projectPath(data.project.scope))]: data.project,
    "/api/v1/catalog/interchange": { enabled: true },
  });
  let held = false,
    waiting = 0;
  let release: (() => void) | undefined;
  await page.route("**/api/v1/catalog/context", async (route) => {
    if (held) {
      waiting++;
      await new Promise<void>((resolve) => {
        release = resolve;
      });
    }
    await route.fulfill({ json: data.context });
  });
  await page.goto(projectPath(data.project.scope) + "/mappings/new");
  const panel = page.locator("braid-mfd-import");
  await panel.getByLabel("Mapping name").fill("Retained private name");
  await panel.getByLabel("Description").fill("Retained private notes");
  await panel.getByLabel("Design and schema files").setInputFiles([
    {
      name: "design.mfd",
      mimeType: "application/xml",
      buffer: Buffer.from("<self-authored/>"),
    },
    {
      name: "schema.json",
      mimeType: "application/json",
      buffer: Buffer.from('{"selfAuthored":true}'),
    },
  ]);
  const original = await panel.elementHandle();
  for (const focus of [false, true]) {
    held = true;
    const next = waiting + 1;
    if (focus)
      await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    else
      await page
        .getByRole("button", { name: "Refresh access", exact: true })
        .click();
    await expect.poll(() => waiting).toBe(next);
    expect(
      await original!.evaluate(
        (element) => element === document.querySelector("braid-mfd-import"),
      ),
    ).toBe(true);
    await expect(panel.getByLabel("Mapping name")).toHaveValue(
      "Retained private name",
    );
    await expect(panel.getByLabel("Description")).toHaveValue(
      "Retained private notes",
    );
    expect(
      await panel
        .getByLabel("Design and schema files")
        .evaluate((input) =>
          Array.from(
            (input as HTMLInputElement).files ?? [],
            (file) => file.name,
          ),
        ),
    ).toEqual(["design.mfd", "schema.json"]);
    held = false;
    release!();
    await expect(
      panel.getByRole("button", { name: "Review conversion" }),
    ).toBeEnabled();
  }
  let uploaded = false;
  await page.route("**/mfd-imports/review?*", async (route) => {
    const request = route.request();
    expect(new URL(request.url()).searchParams.get("name")).toBe(
      "Retained private name",
    );
    expect(new URL(request.url()).searchParams.get("description")).toBe(
      "Retained private notes",
    );
    expect(
      request.postDataBuffer()!.includes(Buffer.from("<self-authored/>")),
    ).toBe(true);
    expect(
      request.postDataBuffer()!.includes(Buffer.from('{"selfAuthored":true}')),
    ).toBe(true);
    uploaded = true;
    await route.fulfill({ status: 503, json: { code: "unavailable" } });
  });
  await panel.getByRole("button", { name: "Review conversion" }).click();
  await expect.poll(() => uploaded).toBe(true);
});

for (const operation of ["confirm", "download"])
  for (const status of [401, 403]) {
    test(`MFD ${operation} ${status} clears private review and rechecks the original session`, async ({
      page,
    }) => {
      const data = structuredClone(synthetic);
      const identityContext = "v1." + "a".repeat(42) + "A";
      const context = {
        ...data.context,
        mode: "session",
        identityContext,
        requestToken: "dG9rZW4",
        features: { ...data.context.features, createMappings: true },
      };
      data.project.access.actions.createMappings = true;
      data.project.access.role = "publisher";
      await syntheticCatalog(page, {
        "/api/v1/catalog/context": context,
        [catalogApi(projectPath(data.project.scope))]: data.project,
        "/api/v1/catalog/interchange": { enabled: true },
      });
      let refused = false,
        reads = 0;
      await page.route("**/api/auth/session*", (route) => {
        reads++;
        return route.fulfill({
          json: {
            version: 1,
            mode: "session",
            state: refused ? "anonymous" : "authenticated",
            authActionRequestToken: "YXV0aA",
            requestToken: refused ? null : "dG9rZW4",
            identityContext: refused ? null : identityContext,
            displayName: null,
          },
        });
      });
      const review = privateReview();
      await page.route(
        operation === "confirm"
          ? "**/mfd-imports/review?*"
          : "**/mfd-exports/review",
        (route) => route.fulfill({ json: review }),
      );
      await page.route(
        operation === "confirm"
          ? "**/mfd-imports/*/confirm"
          : "**/mfd-exports/*/download",
        (route) => {
          refused = true;
          return route.fulfill({ status, json: { code: "identityRejected" } });
        },
      );
      await page.goto(
        operation === "confirm"
          ? projectPath(data.project.scope) + "/mappings/new"
          : revisionPath(data.revision.scope),
      );
      const panel = page.locator(
        operation === "confirm" ? "braid-mfd-import" : "braid-mfd-export",
      );
      if (operation === "confirm") {
        await panel.getByLabel("Mapping name").fill("Private rejected import");
        await panel.getByLabel("Design and schema files").setInputFiles({
          name: "design.mfd",
          mimeType: "application/xml",
          buffer: Buffer.from("<self-authored/>"),
        });
        await panel.getByRole("button", { name: "Review conversion" }).click();
      } else
        await panel.getByRole("button", { name: "Review .mfd export" }).click();
      await expect(
        panel
          .getByRole("listitem")
          .filter({ hasText: "Private rejected conversion warning" }),
      ).toBeVisible();
      await panel.getByRole("checkbox").check();
      const before = reads;
      await panel
        .getByRole("button", {
          name:
            operation === "confirm"
              ? "Confirm or recover import"
              : "Download reviewed export",
        })
        .click();
      await expect.poll(() => reads).toBeGreaterThan(before);
      await expect(
        page
          .getByRole("listitem")
          .filter({ hasText: "Private rejected conversion warning" }),
      ).toHaveCount(0);
      await expect(
        page.getByText("Private rejected conversion summary", { exact: true }),
      ).toHaveCount(0);
    });
  }

function privateReview() {
  return {
    reviewId: "99999999-1111-4111-8111-111111111111",
    reviewDigest: "1".repeat(64),
    packageDigest: "2".repeat(64),
    nativeDigest: "3".repeat(64),
    engine: {
      backendId: "ferrule",
      engineRevision: "self-authored-fixture",
      backendBuild: "fixture-build",
      adapterBuild: "fixture-adapter",
    },
    summary: "Private rejected conversion summary",
    warnings: [
      { code: "unsupported", message: "Private rejected conversion warning" },
    ],
    expiresAt: "2099-01-01T00:00:00Z",
    targetMappingId: null,
    expectedHeadRevisionId: null,
    mappingName: "Private rejected import",
  };
}

test("metadata-only import drafts are guarded and cleared when identity changes", async ({
  page,
}) => {
  const data = structuredClone(synthetic);
  data.context.features.createMappings = true;
  data.project.access.actions.createMappings = true;
  data.project.access.role = "publisher";
  await syntheticCatalog(page, {
    [catalogApi(projectPath(data.project.scope))]: data.project,
    "/api/v1/catalog/interchange": { enabled: true },
  });
  await page.route("**/api/v1/catalog/context", (route) =>
    route.fulfill({ json: data.context }),
  );
  await page.goto(projectPath(data.project.scope) + "/mappings/new");
  const panel = page.locator("braid-mfd-import");
  await panel.getByLabel("Mapping name").fill("Private draft name");
  await panel.getByLabel("Description").fill("Private draft description");
  const prevented = await page.evaluate(() => {
    const event = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  });
  expect(prevented).toBe(true);
  data.context.actorPrincipalId = "10000000-0000-4000-8000-000000000099";
  await page.getByRole("button", { name: /refresh.*access/i }).click();
  await expect(panel.getByLabel("Mapping name")).toHaveValue("");
  await expect(panel.getByLabel("Description")).toHaveValue("");
});

// UI-only deterministic protocol fixtures. Actual scoped PostgreSQL publication is
// exercised separately by CatalogInterchangeHttpTests; these are not engine proofs.
for (const append of [false, true]) {
  test(`MFD ${append ? "append" : "create"} reviews warnings and retries only the same server review`, async ({
    page,
  }) => {
    const data = structuredClone(synthetic);
    data.context.features.createMappings = true;
    data.context.features.uploadRevisions = true;
    data.project.access.actions.createMappings = true;
    data.project.access.actions.uploadRevisions = true;
    data.project.access.role = "publisher";
    data.mapping.access.actions.uploadRevisions = true;
    data.mapping.access.role = "publisher";
    const review = {
      reviewId: "99999999-1111-4111-8111-111111111111",
      reviewDigest: "1".repeat(64),
      packageDigest: "2".repeat(64),
      nativeDigest: "3".repeat(64),
      engine: {
        backendId: "ferrule",
        engineRevision: "self-authored-fixture",
        backendBuild: "fixture-build",
        adapterBuild: "fixture-adapter",
      },
      summary: "Synthetic mapping review",
      warnings: [
        {
          code: "unsupported",
          message: "Synthetic construct needs manual review",
        },
      ],
      expiresAt: "2099-01-01T00:00:00Z",
      targetMappingId: append ? data.mapping.scope.mappingId : null,
      expectedHeadRevisionId: append
        ? data.mapping.head.scope.revisionId
        : null,
      mappingName: append ? null : "Imported fixture",
    };
    await syntheticCatalog(page, {
      "/api/v1/catalog/context": data.context,
      [catalogApi(projectPath(data.project.scope))]: data.project,
      [catalogApi(mappingPath(data.mapping.scope))]: data.mapping,
      "/api/v1/catalog/interchange": { enabled: true },
    });
    let reviews = 0,
      confirmations = 0;
    const keys: string[] = [];
    await page.route("**/mfd-imports/review?*", async (route) => {
      reviews++;
      expect(route.request().headers()["content-type"]).toBe(
        "application/vnd.braid.mfd-package.v1",
      );
      expect(route.request().postDataBuffer()!.length).toBeGreaterThan(20);
      await route.fulfill({ json: review });
    });
    await page.route("**/mfd-imports/*/confirm", async (route) => {
      confirmations++;
      keys.push(route.request().headers()["idempotency-key"]!);
      expect(route.request().postDataJSON()).toEqual({
        reviewDigest: review.reviewDigest,
        acknowledgeWarnings: true,
      });
      if (confirmations === 1) await route.abort();
      else
        await route.fulfill({ status: 409, json: { outcome: "headConflict" } });
    });
    await page.route(`**/mfd-imports/${review.reviewId}`, (route) =>
      route.fulfill({ json: review }),
    );
    await page.goto(
      append
        ? mappingPath(data.mapping.scope) + "/revisions/new"
        : projectPath(data.project.scope) + "/mappings/new",
    );
    const panel = page.locator("braid-mfd-import");
    await expect(
      panel.getByRole("heading", { name: "Import MapForce design" }),
    ).toBeVisible();
    if (!append)
      await panel.getByLabel("Mapping name").fill("Imported fixture");
    await panel.getByLabel("Design and schema files").setInputFiles({
      name: "design.mfd",
      mimeType: "application/xml",
      buffer: Buffer.from("<self-authored/>"),
    });
    await panel.getByRole("button", { name: "Review conversion" }).click();
    await expect(
      panel.getByText("Synthetic construct needs manual review"),
    ).toBeVisible();
    const confirm = panel.getByRole("button", {
      name: "Confirm or recover import",
    });
    await expect(confirm).toBeDisabled();
    await panel.getByRole("checkbox").check();
    if (process.env.BRAID_MFD_SCREENSHOT_DIR)
      await page.screenshot({
        path: `${process.env.BRAID_MFD_SCREENSHOT_DIR}/mfd-${append ? "append" : "create"}-review.png`,
        fullPage: true,
      });
    await confirm.click();
    await expect(panel.getByRole("status")).toContainText("unknown");
    await page.reload();
    await expect(
      panel.getByText("Synthetic construct needs manual review"),
    ).toBeVisible();
    await expect(confirm).toBeDisabled();
    await panel.getByRole("checkbox").check();
    await confirm.click();
    await expect(panel.getByRole("status")).toContainText("headConflict");
    expect(reviews).toBe(1);
    expect(keys).toEqual([review.reviewId, review.reviewId]);
    await panel.getByRole("button", { name: "Clear this import" }).click();
    await expect(
      panel.getByRole("button", { name: "Forget review", exact: true }),
    ).toBeDisabled();
    await panel.getByRole("button", { name: "Keep recovery" }).click();
  });
}

test("export displays warnings before enabling the verified download", async ({
  page,
}) => {
  const review = {
    reviewId: "99999999-1111-4111-8111-111111111111",
    reviewDigest: "1".repeat(64),
    packageDigest: "2".repeat(64),
    nativeDigest: "3".repeat(64),
    engine: {
      backendId: "ferrule",
      engineRevision: "self-authored-fixture",
      backendBuild: "fixture-build",
      adapterBuild: "fixture-adapter",
    },
    summary: "Synthetic export review",
    warnings: [{ code: "lossy", message: "Synthetic export warning" }],
    expiresAt: "2099-01-01T00:00:00Z",
    targetMappingId: null,
    expectedHeadRevisionId: null,
    mappingName: null,
  };
  await syntheticCatalog(page, {
    "/api/v1/catalog/interchange": { enabled: true },
  });
  await page.route("**/mfd-exports/review", (route) =>
    route.fulfill({ json: review }),
  );
  const bytes = Buffer.from("<self-authored/>");
  await page.route("**/mfd-exports/*/download", (route) =>
    route.fulfill({
      body: bytes,
      contentType: "application/xml",
      headers: {
        "X-Content-Sha256": createHash("sha256").update(bytes).digest("hex"),
      },
    }),
  );
  await page.goto(revisionPath(synthetic.revision.scope));
  const panel = page.locator("braid-mfd-export");
  await panel.getByRole("button", { name: "Review .mfd export" }).click();
  await expect(panel.getByText("Synthetic export warning")).toBeVisible();
  const button = panel.getByRole("button", {
    name: "Download reviewed export",
  });
  await expect(button).toBeDisabled();
  await panel.getByRole("checkbox").check();
  if (process.env.BRAID_MFD_SCREENSHOT_DIR)
    await page.screenshot({
      path: `${process.env.BRAID_MFD_SCREENSHOT_DIR}/mfd-export-review.png`,
      fullPage: true,
    });
  const downloading = page.waitForEvent("download");
  await button.click();
  expect((await downloading).suggestedFilename()).toBe("mapping.mfd");
});
