import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import {
  synthetic,
  syntheticCatalog,
  revisionPath,
  mappingPath,
  catalogApi,
} from "./catalog.fixture";
import type { components } from "../src/api/generated";

test("real edit wasm: keyboard mapping, exact values, autosave, undo and accessible layout", async ({
  page,
}, info) => {
  test.setTimeout(90_000);
  const native = await readFile(
    new URL(
      "../../../engines/ferrule-host/tests/fixtures/valid-basic.json",
      import.meta.url,
    ),
  );
  const digest = createHash("sha256").update(native).digest("hex");
  const revision = {
    ...synthetic.revision,
    nativeByteCount: native.length,
    nativeDigest: { algorithm: "sha256", value: digest },
  };
  const actions = { ...synthetic.mapping.access.actions, edit: true };
  const mapping = {
    ...synthetic.mapping,
    access: { role: "publisher" as const, actions },
  };
  const context = {
    ...synthetic.context,
    features: { ...synthetic.context.features, edit: true },
  };
  const scope = revision.scope;
  const base = catalogApi(mappingPath(mapping.scope)) + "/drafts";
  const id = "50000000-0000-4000-8000-000000000001";
  let operations: components["schemas"]["EditScriptV1"] = {
    format: "ferrule.edit.v1",
    operations: [],
  };
  let draft: components["schemas"]["MappingDraftV1"] = {
    id,
    mapping: mapping.scope,
    authorPrincipalId: synthetic.actor,
    base: {
      revision: scope,
      sequence: "1",
      nativeDigest: revision.nativeDigest,
      nativeByteCount: native.length,
    },
    state: "open",
    version: "0",
    operationCount: 0,
    operationsDigest: null,
    createdAtUtc: "2026-10-25T00:00:00Z",
    changedAtUtc: "2026-10-25T00:00:00Z",
    expiresAtUtc: "2026-11-24T00:00:00Z",
    checkpoint: null,
    savingOperationId: null,
    savedRevisionId: null,
  };
  let writes = 0;
  await syntheticCatalog(page, {
    "/api/v1/catalog/context": context,
    [catalogApi(mappingPath(mapping.scope))]: mapping,
    [catalogApi(revisionPath(scope))]: revision,
  });
  await page.route("**/drafts**", async (route) => {
    const request = route.request(),
      url = new URL(request.url());
    if (!url.pathname.startsWith(base)) return route.fallback();
    const endpoint = url.pathname.slice(base.length);
    const headers = {
      "Cache-Control": "no-store",
      ETag: `"draft-${id}-${draft.version}"`,
    };
    if (!endpoint && request.method() === "GET")
      return route.fulfill({ json: { drafts: [draft] }, headers });
    if (endpoint === `/${id}`) return route.fulfill({ json: draft, headers });
    if (endpoint === `/${id}/base`)
      return route.fulfill({
        body: native,
        contentType: "application/octet-stream",
        headers,
      });
    if (endpoint === `/${id}/operations`) {
      if (request.method() === "PUT") {
        expect(request.headers()["if-match"]).toBe(
          `"draft-${id}-${draft.version}"`,
        );
        expect(request.headers()["content-type"]).toContain("application/json");
        operations = request.postDataJSON() as typeof operations;
        expect(operations.format).toBe("ferrule.edit.v1");
        expect(request.postData()).not.toContain('"graph"');
        ++writes;
        draft = {
          ...draft,
          version: String(writes),
          operationCount: operations.operations.length,
        };
        return route.fulfill({
          json: { outcome: "succeeded", draft },
          headers,
        });
      }
      return route.fulfill({ json: operations, headers });
    }
    return route.fulfill({ status: 404, json: { code: "notFound" }, headers });
  });
  await page.goto(revisionPath(scope));
  if (process.env.WEFT_EDIT_SHELL_DLL) {
    expect(
      await page.evaluate(async () => {
        try {
          await WebAssembly.compile(
            new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]),
          );
          return false;
        } catch {
          return true;
        }
      }),
    ).toBe(true);
  }
  await page.getByRole("button", { name: "Edit mapping", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: `Editing ${mapping.name}` }),
  ).toBeVisible();
  const field = page.getByRole("button", { name: /Inspect field.*code/ });
  await expect(field).toBeVisible();
  await page.screenshot({
    path: info.outputPath("mapping-before.png"),
    fullPage: true,
    animations: "disabled",
  });
  await field.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByLabel("Map from", { exact: true })).toBeFocused();
  await page.getByLabel("Value type", { exact: true }).selectOption("int");
  await page
    .getByLabel("Exact value", { exact: true })
    .fill("9007199254740993");
  await page
    .getByRole("button", { name: "Apply mapping", exact: true })
    .click();
  await expect(page.getByRole("status")).toContainText("Not yet autosaved");
  await expect(field).toBeFocused();
  await expect.poll(() => writes).toBe(1);
  expect(JSON.stringify(operations)).toContain("9007199254740993");
  await page.screenshot({
    path: info.outputPath("mapping-edited.png"),
    fullPage: true,
    animations: "disabled",
  });
  await page.keyboard.press("Control+z");
  await expect.poll(() => writes).toBe(2);
  expect(operations.operations).toEqual([]);
  await page.keyboard.press("Control+Shift+z");
  await expect.poll(() => writes).toBe(3);
  expect(JSON.stringify(operations)).toContain("9007199254740993");
  const accessibility = await new AxeBuilder({ page })
    .include("weft-mapping-editor")
    .analyze();
  expect(accessibility.violations).toEqual([]);
  // A hung dedicated worker is terminated, and a restart replays only retained intent.
  await page.getByRole("button", { name: "Back to saved version" }).click();
  let hang = true;
  await page.route("**/edit/worker.js", async (route) => {
    if (!hang) return route.fallback();
    hang = false;
    await route.fulfill({
      contentType: "text/javascript",
      body: "self.onmessage = () => {};",
    });
  });
  await page.getByRole("button", { name: "Edit mapping", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText(
    "stopped after 10 seconds",
    { timeout: 15_000 },
  );
  await page
    .getByRole("button", { name: "Restart editor from retained draft" })
    .click();
  await expect(field).toBeVisible();
  await page.unroute("**/edit/worker.js");
  // Integrity errors never fall back to compiling an unverified module.
  await page.getByRole("button", { name: "Back to saved version" }).click();
  let corrupt = true;
  await page.route("**/edit/core.wasm", async (route) => {
    if (!corrupt) return route.fallback();
    corrupt = false;
    const response = await route.fetch();
    const bytes = Buffer.from(await response.body());
    bytes[8] = bytes[8]! ^ 1;
    await route.fulfill({ response, body: bytes });
  });
  await page.getByRole("button", { name: "Edit mapping", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Restart editor from retained draft" }),
  ).toBeVisible();
  await expect(field).toHaveCount(0);
  await page
    .getByRole("button", { name: "Restart editor from retained draft" })
    .click();
  await expect(field).toBeVisible();
  await page.unroute("**/edit/core.wasm");
  expect(writes).toBe(3);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    page.getByRole("button", { name: "Save new version", exact: true }),
  ).toBeVisible();
  await page.evaluate(() => (document.documentElement.style.fontSize = "200%"));
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});
