import { expect, test, type Page, type Route } from "@playwright/test";
import { createHash } from "node:crypto";
import type { components } from "../src/api/generated";
import {
  catalogApi,
  mappingPath,
  revisionPath,
  synthetic,
  syntheticCatalog,
} from "./catalog.fixture";

type Run = components["schemas"]["CatalogPreviewRunV1"];
type Submission = components["schemas"]["CatalogPreviewSubmissionV1"];

const scope = synthetic.revision.scope;
const RUN = "80000000-0000-4000-8000-000000000001";
const submitPath = `${catalogApi(revisionPath(scope))}/previews`;
const runPath = `/api/v1/organizations/${scope.organizationId}/previews/${RUN}`;
const output = '{"orders":[{"id":"SO-1","qty":9007199254740993}]}';
const outputDigest = createHash("sha256").update(output).digest("hex");

function run(state: Run["state"], change: Partial<Run> = {}): Run {
  const terminal = state === "completed" || state === "canceled";
  return {
    runId: RUN,
    revision: scope,
    requestFingerprint: "b".repeat(64),
    currentDateTime: "2026-10-20T08:30:00Z",
    state,
    attempt: state === "queued" ? 0 : 1,
    maxAttempts: 3,
    cancelRequested: false,
    outcome: state === "canceled" ? "canceled" : null,
    failure: null,
    provenance: null,
    recordsWritten: null,
    outputs: [],
    createdAtUtc: "2026-10-20T08:30:00Z",
    startedAtUtc: state === "queued" ? null : "2026-10-20T08:30:01Z",
    finishedAtUtc: terminal ? "2026-10-20T08:30:02Z" : null,
    expiresAtUtc: "2026-10-21T08:30:00Z",
    ...change,
  };
}

const succeeded = run("completed", {
  outcome: "succeeded",
  recordsWritten: "1",
  provenance: {
    confirmed: true,
    engineRevision: "577359a991a7dd44172a3c4af91fdcab7592457c",
    backendBuild: "sha256:" + "c".repeat(64),
    adapterBuild: "weft.ferrule-preview-adapter.v1",
    profile: "weft.ferrule-json-preview.v1",
  },
  outputs: [
    {
      ordinal: 0,
      boundaryId: "orders",
      targetName: "",
      targetNameTruncated: false,
      mediaType: "application/json",
      recordsWritten: "1",
      sha256: outputDigest,
      byteCount: Buffer.byteLength(output),
    },
  ],
});

/** A controlled preview API: each GET of the run returns the next scripted state. */
class Previews {
  readonly submissions: { key: string; body: Submission }[] = [];
  cancels = 0;
  reads = 0;
  submitReply: { status: number; json: unknown } | null = null;
  states: Run[] = [run("running"), succeeded];
  token = synthetic.context.requestToken;

  constructor(private readonly page: Page) {}

  async install(role = "publisher", allowed = true, enabled = true) {
    const context = structuredClone(synthetic.context);
    context.features.preview = enabled;
    const mapping = structuredClone(synthetic.mapping);
    mapping.access = {
      role: role as typeof mapping.access.role,
      actions: { ...mapping.access.actions, preview: allowed },
    };
    await syntheticCatalog(this.page, {
      "/api/v1/catalog/context": context,
      [catalogApi(mappingPath(mapping.scope))]: mapping,
    });
    await this.page.route(`**${submitPath}`, (route) => this.submit(route));
    await this.page.route(`**${runPath}**`, (route) => this.run(route));
  }

  private async submit(route: Route) {
    const request = route.request();
    const headers = request.headers();
    expect(request.method()).toBe("POST");
    expect(headers["content-type"]).toContain("application/json");
    expect(headers["x-xsrf-token"]).toBe(this.token);
    const key = headers["idempotency-key"];
    expect(key).toMatch(/^[0-9a-f-]{36}$/);
    this.submissions.push({ key, body: request.postDataJSON() as Submission });
    if (this.submitReply) {
      await route.fulfill(this.submitReply);
      return;
    }
    const replay =
      this.submissions.filter((item) => item.key === key).length > 1;
    await route.fulfill({
      status: replay ? 200 : 201,
      headers: replay ? {} : { Location: runPath },
      json: { outcome: replay ? "replayed" : "created", run: run("queued") },
    });
  }

  private async run(route: Route) {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === "POST" && path === `${runPath}/cancel`) {
      this.cancels++;
      this.states = [run("canceled")];
      await route.fulfill({
        json: { outcome: "canceled", run: run("canceled") },
      });
      return;
    }
    if (request.method() === "GET" && path === `${runPath}/outputs/0`) {
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: output,
      });
      return;
    }
    if (request.method() === "GET" && path === runPath) {
      this.reads++;
      const state =
        this.states.length > 1 ? this.states.shift()! : this.states[0];
      await route.fulfill({ json: state });
      return;
    }
    await route.fulfill({ status: 404, json: { code: "notFound" } });
  }
}

const panel = (page: Page) =>
  page.getByRole("region", { name: "Sample preview", exact: true });

async function open(page: Page) {
  await page.goto(revisionPath(scope));
  await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Version 1");
}

test("a preview runs, shows its outputs and downloads their exact bytes", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill('{"id":"SO-1"}');
  await region.getByRole("button", { name: "Add named input" }).click();
  await region.getByLabel("Source name").fill("rates");
  await region.getByLabel("Named input 1 (JSON)").fill('{"currency":"EUR"}');
  await region.getByRole("button", { name: "Add parameter" }).click();
  await region.getByLabel("Name", { exact: true }).fill("region");
  await region.getByLabel("Value", { exact: true }).fill("west");
  await region.getByRole("button", { name: "Run preview" }).click();

  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  expect(previews.submissions).toHaveLength(1);
  const body = previews.submissions[0].body;
  expect(body.target).toEqual({ kind: "all", name: null });
  expect(body.parameters).toEqual([
    { name: "region", kind: "string", value: "west" },
  ]);
  expect(
    body.inputs.map((input) => [
      input.name,
      Buffer.from(input.content, "base64").toString(),
    ]),
  ).toEqual([
    [null, '{"id":"SO-1"}'],
    ["rates", '{"currency":"EUR"}'],
  ]);
  const outputs = region.getByRole("region", { name: "Preview outputs" });
  await expect(outputs).toContainText(outputDigest);
  await expect(outputs).toContainText(String(Buffer.byteLength(output)));

  await region.getByRole("button", { name: "Show Primary output" }).click();
  await expect(region.locator("pre")).toHaveText(output);
  const download = page.waitForEvent("download");
  await region.getByRole("button", { name: "Download Primary output" }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe("preview-0-primary.json");
  const stream = await file.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(chunk as Buffer);
  expect(Buffer.concat(chunks).toString()).toBe(output);
});

test("invalid input and unsupported mappings show bounded failures", async ({
  page,
}) => {
  const previews = new Previews(page);
  previews.states = [
    run("completed", {
      outcome: "invalidInput",
      failure: {
        code: "preview.invalidInput",
        message: "Input is not JSON.",
        messageTruncated: false,
        subject: "rates",
        subjectTruncated: false,
      },
    }),
  ];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(region.getByRole("alert")).toContainText(
    "Enter or choose the primary JSON document.",
  );
  expect(previews.submissions).toHaveLength(0);

  await region.getByLabel("Primary input (JSON)").fill("not json");
  await region.getByRole("button", { name: "Run preview" }).click();
  const failure = region.locator(".preview-failure");
  await expect(failure).toContainText(
    "An input is not valid JSON for this mapping",
  );
  await expect(failure).toContainText("rates");
  await expect(failure).toContainText("Input is not JSON.");

  previews.states = [
    run("completed", {
      outcome: "unsupportedCapability",
      failure: {
        code: "preview.unsupportedCapability",
        message: "Database sources are not supported.",
        messageTruncated: false,
        subject: null,
        subjectTruncated: false,
      },
    }),
  ];
  await region.getByRole("button", { name: "Start a new preview" }).click();
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(region.locator(".preview-failure")).toContainText(
    "The mapping uses features that preview does not support",
  );
  expect(new Set(previews.submissions.map((item) => item.key)).size).toBe(2);
});

test("a running preview can be canceled", async ({ page }) => {
  const previews = new Previews(page);
  previews.states = [run("running")];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Running" }),
  ).toBeVisible();
  await region.getByRole("button", { name: "Cancel preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Canceled" }),
  ).toBeVisible();
  expect(previews.cancels).toBe(1);
  await expect(
    region.getByRole("button", { name: "Cancel preview" }),
  ).toHaveCount(0);
});

/** Holds the next request matching `pattern` until released. */
async function hold(page: Page, pattern: string, reply: () => unknown) {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let arrive!: () => void;
  const arrived = new Promise<void>((resolve) => {
    arrive = resolve;
  });
  await page.route(
    pattern,
    async (route) => {
      arrive();
      await gate;
      await route.fulfill({ json: reply() });
    },
    { times: 1 },
  );
  return { release, arrived };
}

test("a preview submitted during a routine refresh waits and sends with the fresh token", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  const fresh = "synthetic-ui-only-fresh-token";
  const context = structuredClone(synthetic.context);
  context.features.preview = true;
  const held = await hold(page, "**/api/v1/catalog/context", () => ({
    ...context,
    requestToken: fresh,
  }));
  await page
    .getByRole("button", { name: "Refresh catalog", exact: true })
    .click();
  await held.arrived;
  try {
    // The form stays as it was; the submission waits for the fresh context.
    await region.getByRole("button", { name: "Run preview" }).click();
    await expect(region.getByLabel("Primary input (JSON)")).toHaveValue("{}");
    await expect(
      region.getByRole("button", { name: "Submitting…" }),
    ).toBeVisible();
    expect(previews.submissions).toHaveLength(0);
    previews.token = fresh;
  } finally {
    held.release();
  }
  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  expect(previews.submissions).toHaveLength(1);
});

test("status polling waits for a routine check instead of counting it as a failure", async ({
  page,
}) => {
  const previews = new Previews(page);
  previews.states = [run("running")];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Running" }),
  ).toBeVisible();
  await expect.poll(() => previews.reads).toBeGreaterThan(0);
  const held = await hold(page, "**/api/auth/session", () => ({
    version: 1,
    mode: "development",
  }));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await held.arrived;
  try {
    // Several poll intervals pass while the check is pending; no status read is sent.
    const reads = previews.reads;
    await page.waitForTimeout(3000);
    expect(previews.reads).toBe(reads);
    previews.states = [succeeded];
  } finally {
    held.release();
  }
  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  await expect(region.getByRole("alert")).toHaveCount(0);
});

test("a reload recovers the submitted run and an unconfirmed key is reused", async ({
  page,
}) => {
  const previews = new Previews(page);
  previews.states = [succeeded];
  await previews.install();
  await open(page);
  const region = panel(page);
  await region.getByLabel("Primary input (JSON)").fill("{}");
  await region.getByRole("button", { name: "Run preview" }).click();
  await expect(
    region.getByRole("status").filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();

  await page.reload();
  await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  await expect(
    panel(page).getByText("Showing your last preview of this revision"),
  ).toBeVisible();
  await expect(
    panel(page)
      .getByRole("status")
      .filter({ hasText: "Sample ran successfully" }),
  ).toBeVisible();
  expect(previews.submissions).toHaveLength(1);

  // A submission whose answer was lost keeps its key for the next attempt after reload.
  await panel(page)
    .getByRole("button", { name: "Start a new preview" })
    .click();
  previews.submitReply = { status: 503, json: { code: "unavailable" } };
  await panel(page).getByLabel("Primary input (JSON)").fill('{"again":true}');
  await panel(page).getByRole("button", { name: "Run preview" }).click();
  await expect(panel(page).getByRole("alert")).toContainText(
    "Previews are unavailable right now.",
  );
  await page.reload();
  await page.getByRole("tab", { name: "Try sample", exact: true }).click();
  await expect(
    panel(page).getByText(
      "An earlier submission from this tab was not confirmed",
    ),
  ).toBeVisible();
  previews.submitReply = null;
  await panel(page).getByLabel("Primary input (JSON)").fill('{"again":true}');
  await panel(page).getByRole("button", { name: "Run preview" }).click();
  await expect(
    panel(page).getByText("This preview was already submitted"),
  ).toBeVisible();
  expect(previews.submissions[2].key).toBe(previews.submissions[1].key);
});

test("viewers cannot run previews and a disabled feature shows no form", async ({
  page,
}) => {
  const previews = new Previews(page);
  await previews.install("viewer", false);
  await open(page);
  await expect(panel(page)).toContainText(
    "Your project role (viewer) cannot run previews.",
  );
  await expect(
    panel(page).getByRole("button", { name: "Run preview" }),
  ).toHaveCount(0);

  await page.unrouteAll({ behavior: "ignoreErrors" });
  await new Previews(page).install("publisher", true, false);
  await open(page);
  await expect(panel(page)).toHaveCount(0);
});
