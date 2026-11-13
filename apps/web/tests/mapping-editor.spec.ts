import { test, expect, type Page, type Request } from "@playwright/test";
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

type Draft = components["schemas"]["MappingDraftV1"];
type Extra = (
  endpoint: string,
  request: Request,
  headers: Record<string, string>,
) => Promise<{ status?: number; json: unknown } | undefined>;

async function editorFixture(page: Page, extra?: Extra) {
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
    features: { ...synthetic.context.features, edit: true, preview: true },
  };
  const scope = revision.scope;
  const base = catalogApi(mappingPath(mapping.scope)) + "/drafts";
  const id = "50000000-0000-4000-8000-000000000001";
  const state: {
    operations: components["schemas"]["EditScriptV1"];
    draft: Draft;
    writes: number;
  } = {
    operations: { format: "ferrule.edit.v1", operations: [] },
    writes: 0,
    draft: {
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
    },
  };
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
      ETag: `"draft-${id}-${state.draft.version}"`,
    };
    if (!endpoint && request.method() === "GET")
      return route.fulfill({ json: { drafts: [state.draft] }, headers });
    if (endpoint === `/${id}`)
      return route.fulfill({ json: state.draft, headers });
    if (endpoint === `/${id}/base`)
      return route.fulfill({
        body: native,
        contentType: "application/octet-stream",
        headers,
      });
    if (endpoint === `/${id}/operations`) {
      if (request.method() === "PUT") {
        expect(request.headers()["if-match"]).toBe(
          `"draft-${id}-${state.draft.version}"`,
        );
        expect(request.headers()["content-type"]).toContain("application/json");
        state.operations = request.postDataJSON() as typeof state.operations;
        expect(state.operations.format).toBe("ferrule.edit.v1");
        expect(request.postData()).not.toContain('"graph"');
        ++state.writes;
        state.draft = {
          ...state.draft,
          version: String(state.writes),
          operationCount: state.operations.operations.length,
        };
        return route.fulfill({
          json: { outcome: "succeeded", draft: state.draft },
          headers,
        });
      }
      return route.fulfill({ json: state.operations, headers });
    }
    const reply = await extra?.(
      endpoint.slice(id.length + 1),
      request,
      headers,
    );
    if (reply) return route.fulfill({ ...reply, headers });
    return route.fulfill({ status: 404, json: { code: "notFound" }, headers });
  });
  return { state, native, revision, mapping, scope };
}

const editor = (page: Page) =>
  page.getByRole("region", { name: "Editable mapping" });

async function applyExactValue(page: Page) {
  const field = page.getByRole("button", { name: /Inspect field.*code/ });
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
  return field;
}

test("real edit wasm: keyboard mapping, exact values, autosave, undo and accessible layout", async ({
  page,
}, info) => {
  test.setTimeout(90_000);
  const { state, mapping, scope } = await editorFixture(page);
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
  await applyExactValue(page);
  await expect(editor(page).getByRole("status")).toContainText(
    "Not yet autosaved",
  );
  await expect(field).toBeFocused();
  await expect.poll(() => state.writes).toBe(1);
  expect(JSON.stringify(state.operations)).toContain("9007199254740993");
  await page.screenshot({
    path: info.outputPath("mapping-edited.png"),
    fullPage: true,
    animations: "disabled",
  });
  await page.keyboard.press("Control+z");
  await expect.poll(() => state.writes).toBe(2);
  expect(state.operations.operations).toEqual([]);
  await page.keyboard.press("Control+Shift+z");
  await expect.poll(() => state.writes).toBe(3);
  expect(JSON.stringify(state.operations)).toContain("9007199254740993");
  const accessibility = await new AxeBuilder({ page })
    .include("weft-mapping-editor")
    .analyze();
  expect(accessibility.violations).toEqual([]);
  // A hung dedicated worker is terminated, and a restart replays only retained intent.
  const viewport = page.viewportSize();
  expect(viewport).not.toBeNull();
  if (viewport!.width < 480) {
    const showInspector = page.getByRole("button", {
      name: "Show inspector",
      exact: true,
    });
    await expect(showInspector).toBeVisible();
    await expect(showInspector).toHaveAttribute("aria-expanded", "false");
    await expect(
      page.locator("weft-mapping-editor #wb-main-panel"),
    ).toBeVisible();
    await expect(field).toBeVisible();
    await expect(field).toBeFocused();
    const dock = page.locator("weft-mapping-editor #wb-dock-panel");
    await expect(dock).toHaveCount(1);
    await expect(dock).toBeHidden();
  } else {
    await expect(
      page.getByRole("button", { name: "Hide inspector", exact: true }),
    ).toBeVisible();
    await page
      .getByRole("button", { name: /Back to work|Hide inspector/ })
      .click();
  }
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
  await expect(
    page.getByRole("alert").filter({ hasText: "stopped after 10 seconds" }),
  ).toBeVisible({ timeout: 15_000 });
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
  expect(state.writes).toBe(3);
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

test("rebase keeps the reviewed head and an unconfirmed preview is recovered before replacement", async ({
  page,
}) => {
  test.setTimeout(90_000);
  const revisions = [2, 3].map((sequence) => ({
    ...synthetic.revision,
    scope: {
      ...synthetic.revision.scope,
      revisionId: `40000000-0000-4000-8000-00000000000${sequence}`,
    },
    sequence: String(sequence),
  }));
  const rebases: string[] = [];
  const previews: { key: string; body: string }[] = [];
  const checkpointed = (draft: Draft): Draft => ({
    ...draft,
    checkpoint: {
      isValid: true,
      nativeByteCount: draft.base.nativeByteCount,
      nativeDigest: draft.base.nativeDigest,
      totalIssueCount: 0,
      version: draft.version,
    },
  });
  const { state, mapping, scope } = await editorFixture(
    page,
    async (endpoint, request) => {
      if (endpoint === "/checkpoints") {
        state.draft = checkpointed(state.draft);
        return { json: { outcome: "succeeded", draft: state.draft } };
      }
      if (endpoint === "/save")
        return {
          json: { outcome: "accepted", receipt: { phase: "headConflict" } },
        };
      if (endpoint === "/rebase") {
        const target = (request.postDataJSON() as { targetRevisionId: string })
          .targetRevisionId;
        rebases.push(target);
        const head = revisions.find((r) => r.scope.revisionId === target)!;
        state.draft = {
          ...state.draft,
          version: String(Number(state.draft.version) + 1),
          base: {
            ...state.draft.base,
            revision: head.scope,
            sequence: head.sequence,
          },
          checkpoint: null,
        };
        return { json: { outcome: "succeeded", draft: state.draft } };
      }
      if (endpoint === "/previews") {
        previews.push({
          key: request.headers()["idempotency-key"]!,
          body: request.postData()!,
        });
        return {
          status: previews.length === 2 ? 200 : 201,
          json: {
            outcome: previews.length === 2 ? "replayed" : "created",
            run: {
              runId: `60000000-0000-4000-8000-00000000000${previews.length === 2 ? 1 : 2}`,
              state: "queued",
              outcome: null,
              outputs: [],
            },
          },
        };
      }
      return undefined;
    },
  );
  let head = synthetic.revision;
  const mappingUrl = catalogApi(mappingPath(mapping.scope));
  await page.route(
    (url) => url.pathname === mappingUrl,
    (route) =>
      route.fulfill({
        json: {
          ...mapping,
          head: {
            scope: head.scope,
            sequence: head.sequence,
            admission: head.admission,
          },
        },
      }),
  );
  for (const revision of revisions)
    await page.route(
      (url) => url.pathname === catalogApi(revisionPath(revision.scope)),
      (route) => route.fulfill({ json: revision }),
    );
  // The first preview response is lost after the request leaves the browser.
  await page.route(
    (url) => url.pathname.endsWith("/previews"),
    async (route) =>
      previews.length === 0
        ? (previews.push({
            key: route.request().headers()["idempotency-key"]!,
            body: route.request().postData()!,
          }),
          route.abort("failed"))
        : route.fallback(),
  );
  await page.goto(revisionPath(scope));
  await page.getByRole("button", { name: "Edit mapping", exact: true }).click();
  await applyExactValue(page);
  await expect.poll(() => state.writes).toBe(1);

  head = revisions[0]!;
  await page
    .getByRole("button", { name: "Save new version", exact: true })
    .click();
  const conflict = page.getByRole("region", { name: "Draft conflict" });
  await expect(conflict).toContainText("Latest observed saved version: 2");

  // Another author saves version 3 after version 2 was reviewed.
  head = revisions[1]!;
  const rebase = page.getByRole("button", {
    name: "Rebase onto latest observed version",
  });
  await rebase.click();
  await expect(editor(page).getByRole("status")).toContainText(
    "Another version was saved after the one you reviewed",
  );
  await expect(conflict).toContainText("Latest observed saved version: 3");
  expect(rebases).toEqual([]);
  await rebase.click();
  await expect(conflict).toHaveCount(0);
  expect(rebases).toEqual([revisions[1]!.scope.revisionId]);

  // The sample form lives in the investigation dock, not the inspector.
  await page.locator('[data-dock="output"]').click();
  await page.getByText("Try a sample against this checkpoint").click();
  await page.getByLabel("Primary sample (text)").fill('{"first":true}');
  await page.getByRole("button", { name: "Try sample", exact: true }).click();
  await expect(editor(page).getByRole("alert")).not.toBeEmpty();
  const recover = page.getByRole("button", {
    name: "Recover earlier sample request",
  });
  await expect(recover).toBeVisible();
  await page.getByLabel("Primary sample (text)").fill('{"second":true}');
  await recover.click();
  await expect(editor(page).getByRole("status")).toContainText(
    "Recovered the earlier sample preview request.",
  );
  expect(previews[1]).toEqual(previews[0]);
  await page.getByRole("button", { name: "Try sample", exact: true }).click();
  await expect.poll(() => previews.length).toBe(3);
  expect(previews[2]!.key).not.toBe(previews[0]!.key);
  expect(previews[2]!.body).not.toBe(previews[0]!.body);
});
