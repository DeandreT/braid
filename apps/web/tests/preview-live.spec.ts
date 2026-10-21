import { expect, test, type Page } from "@playwright/test";
import { createHash, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import type { components } from "../src/api/generated";

/**
 * The preview panel against the actual API, PostgreSQL, Wolverine worker host and Ferrule
 * worker image owned by tools/preview-browser/run.mjs. Nothing here scripts an API reply;
 * the one routed request only drops a real answer to recreate a lost response.
 */
type Scope = components["schemas"]["CatalogRevisionScopeV1"];
type Run = components["schemas"]["CatalogPreviewRunV1"];
interface Manifest {
  readonly schemaVersion: "weft.preview-browser-manifest.v1";
  readonly readOrigin: string;
  readonly organizationId: string;
  readonly profiles: { readonly publisher: string; readonly viewer: string };
  readonly orders: Scope;
  readonly unsupported: Scope;
}
interface RunRow {
  readonly state: number;
  readonly attempt: number;
  readonly outcome: string | null;
  readonly submitter: string;
  readonly outputs: number;
}

const manifest = JSON.parse(
  readFileSync(process.env.WEFT_PREVIEW_BROWSER_MANIFEST!, "utf8"),
) as Manifest;
if (manifest.schemaVersion !== "weft.preview-browser-manifest.v1")
  throw new Error("The preview browser manifest is not the expected version.");
const order =
  '{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}';
const rates = '{"currency":"EUR"}';
const sha256 = (bytes: Buffer) =>
  createHash("sha256").update(bytes).digest("hex");
const revisionPage = (s: Scope) =>
  `/catalog/organizations/${s.organizationId}/projects/${s.projectId}/mappings/${s.mappingId}/revisions/${s.revisionId}`;
const submitApi = (s: Scope) =>
  `/api/v1/organizations/${s.organizationId}/projects/${s.projectId}/mappings/${s.mappingId}/revisions/${s.revisionId}/previews`;
const runApi = (runId: string) =>
  `/api/v1/organizations/${manifest.organizationId}/previews/${runId}`;

async function control<T>(body: Record<string, unknown>): Promise<T> {
  const response = await fetch(process.env.WEFT_PREVIEW_BROWSER_CONTROL!, {
    method: "POST",
    headers: {
      Authorization: "Bearer " + process.env.WEFT_PREVIEW_BROWSER_CONTROL_TOKEN,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });
  const value = (await response.json()) as T & { error?: string };
  if (!response.ok) throw new Error("Fixture control failed: " + value.error);
  return value;
}

const panel = (page: Page) =>
  page.getByRole("region", { name: "Sample preview", exact: true });
const status = (page: Page) => panel(page).locator(".preview-status");

async function open(page: Page, scope: Scope) {
  await page.goto(revisionPage(scope));
  await expect(panel(page)).toBeVisible();
}

/** The preview-orders mapping needs its rates source and both parameters. */
async function fill(page: Page, primary = order, complete = true) {
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill(primary);
  if (!complete) return;
  await region.getByRole("button", { name: "Add named input" }).click();
  await region.getByLabel("Source name").fill("rates");
  await region.getByLabel("Named input 1 (JSON)").fill(rates);
  for (const [index, [name, kind, value]] of [
    ["region", "string", "west"],
    ["express", "boolean", "true"],
  ].entries()) {
    await region.getByRole("button", { name: "Add parameter" }).click();
    const row = region.locator(".preview-parameter").nth(index);
    await row.getByLabel("Name").fill(name);
    await row.getByLabel("Type").selectOption(kind);
    await row.getByLabel("Value").fill(value);
  }
}

/** Submits through the real button and returns the server's actual answer. */
async function submit(page: Page) {
  const answer = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      new URL(response.url()).pathname.endsWith("/previews"),
  );
  await panel(page).getByRole("button", { name: "Run preview" }).click();
  const response = await answer;
  return {
    status: response.status(),
    body: (await response.json()) as { outcome: string; run: Run },
  };
}

let publisherRun = "";

test.describe.configure({ mode: "serial" });

test("a publisher previews through the actual worker and downloads exact output bytes", async ({
  page,
}) => {
  await open(page, manifest.orders);
  await fill(page);
  const created = await submit(page);
  expect(created.status).toBe(201);
  expect(created.body.outcome).toBe("created");
  publisherRun = created.body.run.runId;
  await expect(status(page)).toHaveText(/Completed\s*·\s*Succeeded/, {
    timeout: 180_000,
  });

  const run = (await (
    await page.request.get(runApi(publisherRun))
  ).json()) as Run;
  expect(run.outputs.map((output) => output.boundaryId)).toEqual([
    "primary-output",
    "named-output:0",
  ]);
  expect(run.provenance?.confirmed).toBe(true);
  const region = panel(page);
  const table = region.getByRole("region", { name: "Preview outputs" });
  const bytes: Buffer[] = [];
  for (const output of run.outputs) {
    const reply = await page.request.get(
      `${runApi(publisherRun)}/outputs/${output.ordinal}`,
    );
    expect(reply.status()).toBe(200);
    const body = await reply.body();
    expect(body.length).toBe(output.byteCount);
    expect(sha256(body)).toBe(output.sha256);
    await expect(table).toContainText(output.sha256);
    bytes.push(body);
  }
  const primary = JSON.parse(bytes[0].toString("utf8")) as Record<
    string,
    unknown
  >;
  expect(primary["orderId"]).toBe("SO-1");
  expect(primary["region"]).toBe("west");
  expect(bytes[0].toString("utf8")).toContain("9007199254740993");

  const label = run.outputs[0].targetName || "Primary output";
  await region.getByRole("button", { name: "Show " + label }).click();
  await expect(region.locator("pre")).toHaveText(bytes[0].toString("utf8"));
  const download = page.waitForEvent("download");
  await region.getByRole("button", { name: "Download " + label }).click();
  const file = await download;
  const chunks: Buffer[] = [];
  for await (const chunk of await file.createReadStream())
    chunks.push(chunk as Buffer);
  expect(Buffer.concat(chunks).equals(bytes[0])).toBe(true);

  expect(await control<RunRow>({ action: "run", runId: publisherRun })).toEqual(
    {
      state: 3,
      attempt: 1,
      outcome: "succeeded",
      submitter: manifest.profiles.publisher,
      outputs: 2,
    },
  );
});

test("invalid input and an unsupported mapping are refused by the actual worker", async ({
  page,
}) => {
  await open(page, manifest.orders);
  await fill(page, '{"id":');
  const invalid = await submit(page);
  expect(invalid.status).toBe(201);
  await expect(status(page)).toHaveText(
    /Completed\s*·\s*An input is not valid JSON for this mapping/,
    { timeout: 180_000 },
  );
  const failure = panel(page).locator(".preview-failure");
  await expect(failure).toContainText("primary");
  await expect(failure).toContainText("ferrule.invalidInput");
  expect(
    await control<RunRow>({ action: "run", runId: invalid.body.run.runId }),
  ).toMatchObject({ state: 3, outcome: "invalidInput", outputs: 0 });

  await open(page, manifest.unsupported);
  await fill(page, order, false);
  const unsupported = await submit(page);
  expect(unsupported.status).toBe(201);
  await expect(status(page)).toHaveText(
    /Completed\s*·\s*The mapping uses features that preview does not support/,
    { timeout: 180_000 },
  );
  await expect(panel(page).locator(".preview-failure")).toContainText(
    "ferrule.unsupportedCapability",
  );
  expect(
    await control<RunRow>({ action: "run", runId: unsupported.body.run.runId }),
  ).toMatchObject({ state: 3, outcome: "unsupportedCapability", outputs: 0 });
});

test("a queued preview is canceled while the worker is stopped", async ({
  page,
}) => {
  await control({ action: "stop-worker" });
  try {
    await open(page, manifest.orders);
    await fill(page);
    const queued = await submit(page);
    expect(queued.status).toBe(201);
    await expect(status(page)).toHaveText(/Queued/);
    await panel(page).getByRole("button", { name: "Cancel preview" }).click();
    await expect(status(page)).toHaveText(/Canceled/);
    await expect(
      panel(page).getByRole("button", { name: "Cancel preview" }),
    ).toHaveCount(0);
    expect(
      await control<RunRow>({ action: "run", runId: queued.body.run.runId }),
    ).toEqual({
      state: 4,
      attempt: 0,
      outcome: "canceled",
      submitter: manifest.profiles.publisher,
      outputs: 0,
    });
  } finally {
    await control({ action: "start-worker" });
  }
});

test("a reload recovers the run and a lost submission answer reuses its key", async ({
  page,
}) => {
  await open(page, manifest.orders);
  await fill(page);
  const first = await submit(page);
  await expect(status(page)).toHaveText(/Completed\s*·\s*Succeeded/, {
    timeout: 180_000,
  });
  await page.reload();
  await expect(
    panel(page).getByText("Showing your last preview of this revision"),
  ).toBeVisible();
  await expect(status(page)).toHaveText(/Completed\s*·\s*Succeeded/);
  await expect(panel(page)).toContainText(first.body.run.runId);

  await panel(page)
    .getByRole("button", { name: "Start a new preview" })
    .click();
  const before = await control<{ count: number }>({ action: "runs" });
  // The server records the run; only its answer is dropped on the way back.
  let lost = "";
  await page.route("**/previews", async (route) => {
    if (route.request().method() !== "POST") return route.continue();
    const response = await route.fetch();
    lost = ((await response.json()) as { run: Run }).run.runId;
    await route.abort("connectionreset");
  });
  const second = '{"id":"SO-2","lines":[{"sku":"C","qty":1}]}';
  await fill(page, second);
  await panel(page).getByRole("button", { name: "Run preview" }).click();
  await expect(panel(page).locator(".preview-error")).toBeVisible();
  expect(lost).toMatch(/^[0-9a-f-]{36}$/);
  await page.unroute("**/previews");

  await page.reload();
  await expect(
    panel(page).getByText(
      "An earlier submission from this tab was not confirmed",
    ),
  ).toBeVisible();
  await fill(page, second);
  const replayed = await submit(page);
  expect(replayed.status).toBe(200);
  expect(replayed.body.outcome).toBe("replayed");
  expect(replayed.body.run.runId).toBe(lost);
  await expect(
    panel(page).getByText("This preview was already submitted"),
  ).toBeVisible();
  await expect(status(page)).toHaveText(/Completed\s*·\s*Succeeded/, {
    timeout: 180_000,
  });
  expect((await control<{ count: number }>({ action: "runs" })).count).toBe(
    before.count + 1,
  );
});

test("a viewer cannot run previews or read another member's run", async ({
  page,
}) => {
  await control({ action: "restart-server", profile: "viewer" });
  try {
    await open(page, manifest.orders);
    await expect(panel(page)).toContainText(
      "Your project role (viewer) cannot run previews.",
    );
    await expect(
      panel(page).getByRole("button", { name: "Run preview" }),
    ).toHaveCount(0);

    const context = (await (
      await page.request.get("/api/v1/catalog/context")
    ).json()) as { actorPrincipalId: string; requestToken: string };
    expect(context.actorPrincipalId).toBe(manifest.profiles.viewer);
    const refused = await page.request.post(submitApi(manifest.orders), {
      headers: {
        Origin: manifest.readOrigin,
        "X-XSRF-TOKEN": context.requestToken,
        "Idempotency-Key": randomUUID(),
        "Content-Type": "application/json",
      },
      data: {
        target: { kind: "all", name: null },
        parameters: [],
        inputs: [
          { name: null, content: Buffer.from(order).toString("base64") },
        ],
      },
    });
    expect(refused.status()).toBe(404);
    expect((await page.request.get(runApi(publisherRun))).status()).toBe(404);
  } finally {
    await control({ action: "restart-server", profile: "publisher" });
  }
});
