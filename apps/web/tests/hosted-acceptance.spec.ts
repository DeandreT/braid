import {
  expect,
  test,
  type Browser,
  type BrowserContext,
  type Locator,
  type Page,
  type Response,
} from "@playwright/test";
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { confirmAction } from "./confirmation.fixture";
import { confirmHostedMutation } from "./hosted-mutation.fixture";
import { readHostedState } from "./hosted-read.fixture";
import { expectSignedInAccount } from "./hosted-account.fixture";
import {
  chooseSavedConversation,
  historyTrigger,
  openHistory,
  refreshChat,
} from "./chat-ui.fixture";

/**
 * P8 local acceptance on the packaged Session stack owned by deploy/compose/acceptance-test.sh:
 * one signed-in browser drives the main workflows through the Angular application, then the
 * server restarts and the Ferrule service scales while work is queued or running. Nothing here
 * scripts an API reply. Database reads confirm the outcomes; the payload-policy case also
 * prepares and revokes its explicit test-owned grant.
 */
function setting(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Hosted acceptance requires ${name}.`);
  return value;
}
const organization = setting("BRAID_SESSION_ORGANIZATION_ID");
const work = setting("BRAID_ACCEPTANCE_WORK");
const fixtures = setting("BRAID_ACCEPTANCE_FIXTURES");

/** One docker compose command against the run's own project; output is never secret. */
function stack(...args: string[]): string {
  const files = setting("BRAID_ACCEPTANCE_COMPOSE_FILES")
    .split(":")
    .flatMap((file) => ["--file", file]);
  return execFileSync(
    "docker",
    [
      "compose",
      "--project-name",
      setting("BRAID_ACCEPTANCE_COMPOSE_PROJECT"),
      ...files,
      "--profile",
      "operator",
      ...args,
    ],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: 600_000 },
  );
}

function sql(query: string): string[] {
  return stack(
    "exec",
    "-T",
    "postgres",
    "psql",
    "-U",
    "postgres",
    "-d",
    "braid",
    "-At",
    "-c",
    query,
  )
    .split("\n")
    .filter((line) => line.length > 0);
}

/** Starts the server replica again; its edge and relay share its network namespace. */
function startServer(): void {
  stack(
    "up",
    "--detach",
    "--wait",
    "--wait-timeout",
    "180",
    "--no-deps",
    "server",
    "edge",
    "server-relay",
  );
}

function scaleService(replicas: number): void {
  stack(
    "up",
    "--detach",
    "--wait",
    "--wait-timeout",
    "180",
    "--no-deps",
    "--no-recreate",
    "--timeout",
    "60",
    "--scale",
    `ferrule=${replicas}`,
    "ferrule",
  );
  expect(stack("ps", "--quiet", "ferrule").trim().split("\n")).toHaveLength(
    replicas,
  );
}

interface Scope {
  projectId: string;
  mappingId: string;
  revisionId: string;
}
const scopes: { first?: Scope; second?: Scope } = {};
let projectId = "";
let previewRunId = "";
// Unique per run so a repeated run against a kept stack never matches an earlier project.
const projectName = `Acceptance orders ${Date.now().toString(36)}`;
const order =
  '{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}';
const rates = '{"currency":"EUR"}';

interface NativeMapping {
  target: { kind: { children: { name: string }[] } };
  root: { bindings: { target_field: string }[] };
  extra_sources: { options?: unknown }[];
  extra_targets: { options?: unknown }[];
  source_options?: unknown;
  target_options?: unknown;
}

/**
 * The preview-orders fixture with every input and output declared a JSON document, which
 * builds require of each boundary; `change` derives a later revision. Returns the written file.
 */
function nativeMapping(change?: (native: NativeMapping) => void): string {
  const native = JSON.parse(
    readFileSync(`${fixtures}/preview-orders.json`, "utf8"),
  ) as NativeMapping;
  native.source_options = { json_document: true };
  native.target_options = { json_document: true };
  for (const extra of [...native.extra_sources, ...native.extra_targets])
    extra.options = { json_document: true };
  change?.(native);
  const file = `${work}/orders-to-shipments-${change ? 2 : 1}.json`;
  writeFileSync(file, JSON.stringify(native, null, 2) + "\n");
  return file;
}

const revisionPath = (scope: Scope) =>
  `/catalog/organizations/${organization}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}`;
const ids = (href: string): Scope => {
  const match =
    /\/projects\/([0-9a-f-]{36})\/mappings\/([0-9a-f-]{36})\/revisions\/([0-9a-f-]{36})$/.exec(
      href,
    );
  if (!match) throw new Error("Unexpected revision link.");
  return { projectId: match[1], mappingId: match[2], revisionId: match[3] };
};
const signInStatus = (page: Page) =>
  page
    .getByRole("region", { name: "Sign in to braid" })
    .getByRole("status")
    .first();
const workflow = (page: Page, name: string) =>
  page.getByRole("tab", { name, exact: true });

/**
 * Each use of the application's own recovery control after a timed-out or unavailable read
 * (a cold server on a loaded host), so a run reports them instead of hiding them.
 */
const recoveries: string[] = [];
let chatAttempts = 0;

/**
 * A same-origin read in the signed-in browser, which is the only client of the public host.
 * A 503 (a dependency timed out on a loaded host) is read again a bounded number of times.
 */
async function read<T>(target: Page, path: string): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    const [status, body] = await target.evaluate(async (url) => {
      const response = await fetch(url, {
        headers: { Accept: "application/json", "X-Braid-Session": "1" },
      });
      return [response.status, await response.text()] as const;
    }, path);
    if (status === 200) return JSON.parse(body) as T;
    if (status !== 503 || attempt === 4)
      throw new Error(`GET ${path} returned ${status}`);
    recoveries.push("api read");
    await target.waitForTimeout(2_000);
  }
}

/** Opens a catalog page and waits for `ready`, using its Retry control while the read is unavailable. */
async function openCatalog(
  path: string,
  ready: (target: Page) => Locator,
  target: Page = page,
): Promise<void> {
  await target.goto(path);
  const unavailable = target
    .getByRole("alert")
    .filter({ hasText: "Catalog unavailable" });
  for (let attempt = 1; ; attempt++) {
    await expect(ready(target).or(unavailable).first()).toBeVisible();
    if (!(await unavailable.isVisible())) return;
    expect(attempt).toBeLessThan(4);
    recoveries.push("catalog read");
    await unavailable.getByRole("button", { name: "Retry" }).click();
  }
}

const projectsReady = (target: Page) =>
  target.getByRole("link", { name: "Create project" });
const projectReady = (target: Page) =>
  target.getByRole("link", { name: "Upload mapping" });
const revisionReady = (target: Page) =>
  target.getByRole("tablist", { name: "Version workflows" });

/** Restore admission only through the visible read-recovery controls. */
async function recoverReadAdmission(target: Page): Promise<boolean> {
  const workspace = target
    .getByRole("alert")
    .filter({ hasText: "Workspace access unavailable" })
    .first();
  const catalog = target
    .getByRole("alert")
    .filter({ hasText: "Catalog unavailable" })
    .first();
  const changed = target
    .getByRole("alert")
    .filter({ hasText: "Your access changed" })
    .first();
  if (await workspace.isVisible())
    await workspace.getByRole("button", { name: "Retry", exact: true }).click();
  else if (await catalog.isVisible())
    await catalog.getByRole("button", { name: "Retry", exact: true }).click();
  else if (await changed.isVisible())
    await target
      .getByRole("button", { name: "Refresh catalog", exact: true })
      .click();
  else return false;
  await expect(revisionReady(target)).toBeVisible();
  return true;
}
const appendReady = (target: Page) =>
  target.getByRole("region", { name: "Reviewed head for this append" });
const comparisonReady = (target: Page) =>
  target.getByRole("region", { name: "Choose revisions" });

/**
 * Waits for a saved upload or append. When the service could not confirm the call, the page
 * offers an idempotent resend of the original request, which is used a bounded number of times.
 */
async function awaitSaved(
  saved: Locator,
  unknown: Locator,
  resend: RegExp,
): Promise<void> {
  const retry = page.getByRole("button", { name: resend });
  for (let attempt = 1; ; attempt++) {
    await expect(saved.or(unknown).first()).toBeVisible({ timeout: 180_000 });
    if (await saved.isVisible()) return;
    // While the page still waits on the original call, that call can confirm the save; the
    // resend is offered only once waiting has ended.
    await expect(saved.or(retry).first()).toBeVisible({ timeout: 180_000 });
    if (await saved.isVisible()) return;
    expect(attempt).toBeLessThan(4);
    recoveries.push("unconfirmed upload");
    await retry.click();
  }
}

let context: BrowserContext;
let page: Page;
const pageErrors: string[] = [];

async function openBrowser(browser: Browser): Promise<void> {
  context = await browser.newContext({
    baseURL: setting("BRAID_ACCEPTANCE_BASE_URL"),
    ignoreHTTPSErrors: false,
    serviceWorkers: "block",
  });
  context.setDefaultTimeout(60_000);
  page = await context.newPage();
  page.on("pageerror", (error) => pageErrors.push(error.message));
}

test.describe.configure({ mode: "serial" });
test.beforeAll(async ({ browser }) => openBrowser(browser));
test.afterAll(async () => {
  await context?.close();
  // The harness prints these facts; they distinguish documented user recovery from failure.
  writeFileSync(
    `${work}/acceptance-facts.json`,
    JSON.stringify({
      recoveries,
      chatAttempts,
      buildAttempt,
      modelFixture: process.env.BRAID_ACCEPTANCE_MODEL_FIXTURE === "true",
    }),
  );
  expect(pageErrors).toEqual([]);
});

test("signs in through the Session host's Microsoft sign-in", async () => {
  const projects = `/catalog/organizations/${organization}/projects`;
  // Opening a workspace page without a session goes to the sign-in page, which keeps the path.
  await page.goto(projects);
  await page.waitForURL(
    (url) =>
      url.pathname === "/sign-in" &&
      url.searchParams.get("returnTo") === projects,
  );
  await expect(signInStatus(page)).toHaveText(
    "You are signed out. Sign in to open your workspace.",
  );
  let returnTo = projects;
  for (let attempt = 1; ; attempt++) {
    await page
      .getByRole("button", { name: "Sign in with Microsoft", exact: true })
      .click();
    await page.waitForURL(/^https:\/\/login\.microsoftonline\.com\//);
    await page
      .getByRole("button", { name: "Sign in as the acceptance user" })
      .click();
    await page.waitForURL(/^https:\/\/braid\.example\.test\//);
    // The callback page offers "Return to Braid" when sign-in was temporarily unavailable.
    const unavailable = page.getByRole("heading", {
      name: "Sign-in is temporarily unavailable",
    });
    await expect(
      page
        .getByRole("region", { name: "Catalog workspace" })
        .or(unavailable)
        .first(),
    ).toBeVisible();
    if (!(await unavailable.isVisible())) break;
    expect(attempt).toBeLessThan(3);
    recoveries.push("sign-in");
    await page.getByRole("link", { name: "Return to Braid" }).click();
    // "/" opens the catalog, which sends a signed-out browser back to sign-in.
    await page.waitForURL((url) => url.pathname === "/sign-in");
    returnTo = "/catalog";
  }
  // The callback returned to the requested page.
  expect(new URL(page.url()).pathname).toBe(returnTo);
  await expectSignedInAccount(page);
  const status = await read<{ mode: string; state: string }>(
    page,
    "/api/auth/session",
  );
  expect(status).toMatchObject({ mode: "session", state: "authenticated" });
});

test("creates a project", async () => {
  await openCatalog(
    `/catalog/organizations/${organization}/projects`,
    projectsReady,
  );
  await page.getByRole("link", { name: "Create project" }).click();
  await page.getByRole("textbox", { name: "Name" }).fill(projectName);
  await page
    .getByRole("textbox", { name: "Description (optional)" })
    .fill("Packaged Session stack acceptance");
  const created = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      new URL(response.url()).pathname ===
        `/api/v1/organizations/${organization}/projects`,
  );
  await page.getByRole("button", { name: "Create project" }).click();
  const response = await created;
  expect(response.status()).toBe(201);
  await expect(
    page.getByRole("heading", { name: "Project creation confirmed" }),
  ).toBeVisible();
  projectId = (await page
    .locator(".creation-facts code")
    .first()
    .textContent())!.trim();
  expect(projectId).toMatch(/^[a-f0-9-]{36}$/);
  writeFileSync(`${work}/project-id`, projectId);
  await openCatalog(
    `/catalog/organizations/${organization}/projects/${projectId}`,
    projectReady,
  );
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(projectName);
});

test("uploads a native mapping through the Ferrule service into Blob storage", async () => {
  await page.getByRole("link", { name: "Upload mapping" }).click();
  await page
    .getByRole("textbox", { name: "Name", exact: true })
    .fill("Orders to shipments");
  await page.getByLabel("Native mapping file").setInputFiles(nativeMapping());
  await page.getByRole("button", { name: "Upload mapping" }).click();
  const saved = page.getByRole("region", { name: "Mapping saved" });
  await awaitSaved(
    saved,
    page.getByRole("heading", { name: "Upload outcome is unknown" }),
    /^Resend original (upload|file)$/,
  );
  scopes.first = ids(
    (await saved
      .getByRole("link", { name: "View revision" })
      .getAttribute("href"))!,
  );
  // The native object was published to the Blob store: its registration carries the
  // fixed Blob profile identity rather than a filesystem boot ID.
  expect(
    sql(
      `select object_class, linux_boot_id from braid.private_object_publications where project_id = '${projectId}'`,
    ),
  ).toEqual(["native|42726169-6442-6c6f-6200-010000000001"]);
});

/** The report controls sit in a disclosure that closes again when the reports re-render. */
async function reportAction(reports: Locator, name: string): Promise<void> {
  const button = reports.getByRole("button", { name }).first();
  if (!(await button.isVisible()))
    await reports.getByText("Report controls and evidence").click();
  await button.click();
}

async function settleReports(reports: Locator): Promise<void> {
  for (let attempt = 1; ; attempt++) {
    for (const heading of await reports
      .getByRole("heading", { level: 3 })
      .all())
      await expect(heading).not.toHaveText(/Reading|Requesting|Waiting/, {
        timeout: 120_000,
      });
    const timedOut = reports
      .getByRole("alert")
      .filter({ hasText: "The request timed out" });
    if ((await timedOut.count()) === 0) return;
    expect(attempt).toBeLessThan(4);
    recoveries.push("report read");
    await reportAction(reports, "Read explanation again");
    await reportAction(reports, "Read validation again");
  }
}

/**
 * Generates one report and waits for its saved result. A failed attempt (for example a
 * database statement timeout on a loaded host) is derived again through the page's own
 * "Try derivation again" once the server's retry delay has passed, a bounded number of times.
 */
async function generate(
  target: Page,
  scope: Scope,
  name: "explanation" | "validation",
  done: Locator,
): Promise<void> {
  const reports = target.getByRole("region", { name: "Saved reports" });
  await settleReports(reports);
  if (await done.isVisible()) return;
  await reportAction(reports, `Generate ${name}`);
  const failed = reports.getByRole("heading", {
    name: `${name === "explanation" ? "Explanation" : "Validation"}: Attempt failed`,
  });
  const operation = name === "explanation" ? "projection" : "validation";
  for (let attempt = 1; ; attempt++) {
    await settleReports(reports);
    await expect(done.or(failed)).toBeVisible({ timeout: 180_000 });
    if (await done.isVisible()) return;
    expect(attempt).toBeLessThan(4);
    recoveries.push(`${name} derivation`);
    const report = await read<{ nextAttemptAtUtc: string | null }>(
      target,
      `/api/v1/organizations/${organization}/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}/reports/${operation}`,
    );
    const delay = report.nextAttemptAtUtc
      ? Date.parse(report.nextAttemptAtUtc) - Date.now()
      : 0;
    expect(delay).toBeLessThan(300_000);
    if (delay > 0) await target.waitForTimeout(delay + 1_000);
    await reportAction(reports, "Try derivation again");
  }
}

async function deriveReports(
  target: Page,
  scope: Scope,
  validation: boolean,
): Promise<void> {
  await generate(
    target,
    scope,
    "explanation",
    target.getByRole("table", { name: "Mapping field connections" }),
  );
  if (!validation) return;
  await generate(
    target,
    scope,
    "validation",
    target
      .getByRole("region", { name: "Saved reports" })
      .getByRole("heading", { name: "Validation: Native mapping is valid" }),
  );
}

test("derives and shows the revision's explanation and validation", async () => {
  await openCatalog(revisionPath(scopes.first!), revisionReady);
  await deriveReports(page, scopes.first!, true);
  const table = page.getByRole("table", { name: "Mapping field connections" });
  await expect(table).toContainText('["orderId"]');
  await expect(table).toContainText('["items"][*]["quantity"]');
});

test("answers a workspace chat question with a catalog tool and verified mapping citations", async () => {
  test.setTimeout(900_000);
  const chat = page.getByRole("complementary", { name: "Workspace chat" });
  if (
    (await page
      .locator("#workspace-chat-toggle")
      .getAttribute("aria-expanded")) === "false"
  )
    await page.locator("#workspace-chat-toggle").click();
  const question = chat.getByRole("textbox", { name: "Question" });
  const questions = chat
    .getByRole("list", { name: "Questions" })
    .getByRole("listitem");
  // A small local model does not always call the tool, or calls it invalidly (the server then
  // stops the turn without running it), and a tool read can exceed its deadline on a loaded
  // host. Each attempt is a new conversation, so an earlier answer never shapes the next one.
  for (let attempt = 1; ; attempt++) {
    await chat.getByRole("button", { name: "New conversation" }).click();
    await expect(questions).toHaveCount(0);
    await question.fill(
      'Call search_catalog with {"query": "order"} and summarize the mapping names with citations.',
    );
    await chat.getByRole("button", { name: "Send question" }).click();
    await expect(questions).toHaveCount(1);
    const selected = chat.getByRole("region", { name: "Selected answer" });
    await expect(selected.getByRole("status")).toHaveText(
      /^Answer (complete|failed|interrupted|cancelled)/,
      { timeout: 300_000 },
    );
    const citations = chat.getByRole("list", { name: "Answer citations" });
    if (
      (await selected.getByRole("status").textContent()) ===
        "Answer complete." &&
      (await citations.getByRole("listitem").count()) > 0
    ) {
      await expect(citations).toContainText(/mapping/i);
      chatAttempts = attempt;
      break;
    }
    expect(attempt).toBeLessThan(6);
  }
});

const preview = (target: Page) =>
  target.getByRole("region", { name: "Sample preview", exact: true });

/** Fills the preview-orders inputs: its static rates source and both typed parameters. */
async function fillPreview(target: Page): Promise<void> {
  const region = preview(target);
  await region.getByLabel("Primary input (JSON)").fill(order);
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

/** Submits through the actual button and returns the recorded run ID. */
async function submitPreview(target: Page): Promise<string> {
  const answer = target.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      new URL(response.url()).pathname.endsWith("/previews"),
  );
  await preview(target).getByRole("button", { name: "Run preview" }).click();
  const response = await answer;
  expect(response.status()).toBe(201);
  const id = preview(target)
    .locator(".preview-run .catalog-facts code")
    .first();
  await expect(id).toBeVisible();
  const runId = (await id.textContent())!.trim();
  expect(runId).toMatch(/^[a-f0-9-]{36}$/);
  return runId;
}

const shipment = (generatedAt: string) =>
  `{\n  "orderId": "SO-1",\n  "region": "west",\n  "generatedAt": "${generatedAt}",\n  "currency": "EUR",\n  "express": true,\n  "items": [\n    {\n      "sku": "A",\n      "quantity": 2\n    },\n    {\n      "sku": "B",\n      "quantity": 9007199254740993\n    }\n  ]\n}\n`;

test("runs a preview of sample JSON through the worker and the Ferrule service", async () => {
  await openCatalog(revisionPath(scopes.first!), revisionReady);
  await workflow(page, "Try sample").click();
  await fillPreview(page);
  const runId = await submitPreview(page);
  previewRunId = runId;
  const region = preview(page);
  await expect(region.locator(".preview-status")).toContainText(
    "Sample ran successfully",
    { timeout: 240_000 },
  );
  await expect(region).toContainText(runId);
  await expect(region).toContainText("1 of 3");
  await region.getByRole("button", { name: "Show Shipment" }).click();
  const text = (await page
    .getByRole("tabpanel", { name: "Result", exact: true })
    .getByRole("region", { name: "Document bytes", exact: true })
    .locator("pre")
    .textContent())!;
  const generatedAt = /"generatedAt": "([^"]+)"/.exec(text)![1];
  expect(text).toBe(shipment(generatedAt));
});

test("standalone workspace chat gates payload bytes by both profile and fresh project permission", async () => {
  test.skip(
    process.env.BRAID_ACCEPTANCE_MODEL_FIXTURE !== "true",
    "Exact policy assertions use the owned deterministic provider; live model coverage is separate.",
  );
  test.setTimeout(900_000);
  expect(previewRunId).toMatch(/^[0-9a-f-]{36}$/);
  await openCatalog("/chat", (target) =>
    target.getByRole("complementary", { name: "Workspace chat" }),
  );
  const chat = page.getByRole("complementary", { name: "Workspace chat" });
  const actor = await read<{ actorPrincipalId: string }>(
    page,
    "/api/v1/catalog/context",
  );
  expect(actor.actorPrincipalId).toMatch(/^[0-9a-f-]{36}$/);
  const grant = `organization_id='${organization}' and project_id='${projectId}' and principal_id='${actor.actorPrincipalId}'`;
  expect(
    sql(
      `select count(*) from braid.project_payload_read_grants where ${grant}`,
    ),
  ).toEqual(["0"]);
  const observations: Record<string, unknown>[] = [];
  function recordPolicyEvidence() {
    const provider = stack("logs", "--no-color", "model")
      .split("\n")
      .filter((line) => line.includes("fixture-policy "))
      .map((line) =>
        JSON.parse(line.slice(line.indexOf("fixture-policy ") + 15)),
      );
    writeFileSync(
      `${process.env.BRAID_ACCEPTANCE_LOG_DIRECTORY ?? work}/payload-policy.json`,
      JSON.stringify({ observations, provider }, null, 2),
    );
  }
  async function ask(profile: string, shared: boolean) {
    const available = await read<{
      profiles: { profile: { profileId: string }; payloadsAllowed: boolean }[];
    }>(page, `/api/v1/organizations/${organization}/chat`);
    const advertised = available.profiles.find(
      (item) => item.profile.profileId === profile,
    );
    const proof: Record<string, unknown> = {
      requestedProfile: profile,
      expectShared: shared,
      advertisedPayloadsAllowed: advertised?.payloadsAllowed,
      grantActive: sql(
        `select active from braid.project_payload_read_grants where ${grant}`,
      ),
    };
    observations.push(proof);
    try {
      expect(advertised?.payloadsAllowed).toBe(
        profile === "local-acceptance-payloads",
      );
      const created = page
        .waitForResponse(
          (response) =>
            response.request().method() === "POST" &&
            new URL(response.url()).pathname ===
              `/api/v1/organizations/${organization}/chat/conversations`,
        )
        .then((response) => ({
          status: response.status(),
          conversation: response.request().postDataJSON(),
        }));
      await chat
        .getByRole("button", { name: "New conversation", exact: true })
        .click();
      const { status, conversation } = await created;
      expect(status).toBe(201);
      // The library updates its history entry on creation. Verify the durable
      // server identity rather than retaining a Chromium response-body handle.
      const saved = await read<{ conversation: { conversationId: string } }>(
        page,
        `/api/v1/organizations/${organization}/chat/conversations/${conversation.conversationId}`,
      );
      expect(saved.conversation.conversationId).toBe(
        conversation.conversationId,
      );
      await expect(historyTrigger(chat)).toHaveAttribute(
        "data-conversation-id",
        conversation.conversationId,
      );
      const selector = chat.getByRole("combobox", {
        name: "Model profile",
        exact: true,
      });
      await selector.selectOption(profile);
      await expect(selector).toHaveValue(profile);
      proof.selectedProfile = await selector.inputValue();
      await chat
        .getByRole("textbox", { name: "Question", exact: true })
        .fill(`Read preview input ${previewRunId}.`);
      const accepted = page.waitForResponse(
        (response) =>
          response.request().method() === "POST" &&
          new URL(response.url()).pathname.startsWith(
            `/api/v1/organizations/${organization}/chat/conversations/`,
          ) &&
          new URL(response.url()).pathname.endsWith("/turns"),
      );
      await chat
        .getByRole("button", { name: "Send question", exact: true })
        .click();
      const response = await accepted;
      proof.responseStatus = response.status();
      const sent = response.request().postDataJSON();
      proof.sentProfile = sent.profileId;
      expect(response.ok()).toBe(true);
      const detail = await read<{
        turns: { turnId: string; profile: { profileId: string } }[];
      }>(
        page,
        `/api/v1/organizations/${organization}/chat/conversations/${conversation.conversationId}`,
      );
      const receipt = detail.turns.find((turn) => turn.turnId === sent.turnId);
      expect(receipt).toBeDefined();
      proof.acceptedProfile = receipt!.profile.profileId;
      expect(proof.sentProfile).toBe(profile);
      expect(proof.acceptedProfile).toBe(profile);
      const answer = chat.getByRole("region", { name: "Selected answer" });
      await expect(answer.getByRole("status")).toHaveText("Answer complete.", {
        timeout: 240_000,
      });
      proof.dataSharedMarkers = await chat
        .getByText(/Data shared with/)
        .count();
      await expect(answer).toContainText(
        shared
          ? `Received ${Buffer.byteLength(order)} sample bytes`
          : "Payload tools were not offered.",
      );
      await expect(chat.getByText(/Data shared with/)).toHaveCount(
        shared ? 1 : 0,
      );
    } finally {
      recordPolicyEvidence();
    }
  }
  await ask("local-acceptance", false);
  await ask("local-acceptance-payloads", false);
  sql(`insert into braid.project_payload_read_grants(organization_id,project_id,principal_id)
       values('${organization}','${projectId}','${actor.actorPrincipalId}')`);
  try {
    await ask("local-acceptance", false);
    await ask("local-acceptance-payloads", true);
    expect(
      sql(`select byte_count from braid.chat_payload_audit where creator_id='${actor.actorPrincipalId}'
      and project_id='${projectId}' and subject_id='${previewRunId}' and profile_id='local-acceptance-payloads'`),
    ).toEqual([String(Buffer.byteLength(order))]);
  } finally {
    sql(
      `update braid.project_payload_read_grants set active=false,version=version+1 where ${grant} and active`,
    );
  }
  await ask("local-acceptance-payloads", false);
});

test("compares two revisions of the mapping", async () => {
  // Revision 2 renames one output field; nothing else changes.
  const file = nativeMapping((native) => {
    native.target.kind.children.find((c) => c.name === "orderId")!.name =
      "orderNumber";
    native.root.bindings.find(
      (b) => b.target_field === "orderId",
    )!.target_field = "orderNumber";
  });
  const first = scopes.first!;
  await openCatalog(
    `/catalog/organizations/${organization}/projects/${first.projectId}/mappings/${first.mappingId}/revisions/new`,
    appendReady,
  );
  await page.getByLabel("Native mapping file").setInputFiles(file);
  await page.getByRole("button", { name: "Append revision" }).click();
  const saved = page.getByRole("region", { name: "Revision saved" });
  await awaitSaved(
    saved,
    page.getByRole("heading", { name: "Append outcome is unknown" }),
    /^Resend original (append|file)$/,
  );
  scopes.second = ids(
    (await saved
      .getByRole("link", { name: "View saved revision" })
      .getAttribute("href"))!,
  );
  await openCatalog(revisionPath(scopes.second), revisionReady);
  await deriveReports(page, scopes.second, false);
  await expect(
    page.getByRole("table", { name: "Mapping field connections" }),
  ).toContainText('["orderNumber"]');

  await openCatalog(
    `/catalog/organizations/${organization}/projects/${first.projectId}/mappings/${first.mappingId}/comparisons?left=${first.revisionId}&right=${scopes.second.revisionId}`,
    comparisonReady,
  );
  const results = page.getByRole("region", { name: "Comparison", exact: true });
  await expect(results.locator(".comparison-headline")).toBeVisible({
    timeout: 120_000,
  });
  await expect(results).toContainText("orderNumber");
  await expect(results).toContainText("orderId");
  for (const name of ["Left revision", "Right revision"])
    await expect(
      page.getByRole("region", { name, exact: true }),
    ).not.toContainText("busy");
});

const tests = (target: Page) =>
  target.getByRole("region", { name: "Regression tests", exact: true });

async function openTests(target: Page): Promise<void> {
  await target.goto(revisionPath(scopes.first!));
  await workflow(target, "Tests").click();
  await expect(
    tests(target).getByRole("button", { name: "Refresh cases" }),
  ).toBeEnabled();
}

/** Runs the open case version and waits for its settled comparison. */
async function runCase(target: Page): Promise<string> {
  const receipt = target.waitForResponse(
    (response) =>
      response.request().method() === "PUT" &&
      /\/regression-runs\/[a-f0-9-]{36}$/.test(
        new URL(response.url()).pathname,
      ),
  );
  await tests(target)
    .getByRole("button", { name: "Run saved version" })
    .click();
  const response = await receipt;
  expect(response.status()).toBe(201);
  // Keep the original command identity even if Chromium has retired its response
  // body from the debugging cache. The UI still has to show this exact run passing.
  const runId = /\/regression-runs\/([a-f0-9-]{36})$/.exec(
    new URL(response.request().url()).pathname,
  )![1];
  const result = tests(target).getByRole("region", {
    name: "Regression run result",
  });
  await expect(result).toContainText(runId);
  await expect(result).toContainText("Outcome: passed", { timeout: 300_000 });
  await expect(result).toContainText("Attempt 1 of 3");
  return runId;
}

let caseRuns: string[] = [];
let buildId = "";
interface BuildRunView {
  state: string;
  outcome: string | null;
  attempt: number;
  artifact: { sha256: string; byteCount: number; format: string } | null;
}
interface BuildApprovalView {
  approvalId: string;
  buildId: string;
  backend: { engineBuild: string };
  profile: { artifactFormat: string };
}
let buildArtifact: BuildRunView["artifact"] = null;
/** The attempt that completed the build; a retry within the attempt budget is designed behaviour. */
let buildAttempt = 0;
const buildAttemptBudget = 3;
const activeBuildStates = ["queued", "running"];

/**
 * The trusted service image's identities, measured by the harness with the image's own
 * `identity` command before the stack started.
 */
function measuredCompiler(): {
  native: string;
  toolchain: string;
  build: string;
} {
  return JSON.parse(readFileSync(`${work}/compiler-identity.json`, "utf8"));
}

/** Enters once; failed checks recover the retained editor without reloading or rebuilding it. */
async function enterCaseFields(region: Locator): Promise<void> {
  const entered: [Locator, string][] = [];
  let recoveriesUsed = 0;
  const assertEntered = async () => {
    for (const [field, value] of entered)
      await expect(field).toHaveValue(value);
  };
  const recoverEditor = async (): Promise<boolean> => {
    const workspace = page
      .getByRole("alert")
      .filter({ hasText: "Workspace access unavailable" });
    const catalog = page
      .getByRole("alert")
      .filter({ hasText: "Catalog unavailable" });
    if (!(await workspace.isVisible()) && !(await catalog.isVisible()))
      return false;
    expect(++recoveriesUsed).toBeLessThanOrEqual(3);
    recoveries.push("retained regression editor");
    if (await workspace.isVisible())
      await workspace
        .getByRole("button", { name: "Retry", exact: true })
        .click();
    await expect(revisionReady(page).or(catalog).first()).toBeVisible();
    if (await catalog.isVisible())
      await catalog.getByRole("button", { name: "Retry", exact: true }).click();
    await expect(revisionReady(page)).toBeVisible();
    await workflow(page, "Tests").click();
    const load = region.getByRole("button", {
      name: "Load regression cases",
      exact: true,
    });
    if (await load.isVisible()) await load.click();
    await expect(
      region.getByRole("button", { name: "New test case", exact: true }),
    ).toBeVisible();
    await assertEntered();
    return true;
  };
  const step = async (
    action: () => Promise<unknown>,
    completed: () => Promise<boolean>,
  ) => {
    for (;;) {
      try {
        await action();
        return;
      } catch (error) {
        if (!(await recoverEditor())) throw error;
        if (await completed()) return;
      }
    }
  };
  const enter = async (field: Locator, value: string) => {
    await step(
      () => field.fill(value),
      async () =>
        (await field.isVisible()) && (await field.inputValue()) === value,
    );
    entered.push([field, value]);
  };
  await step(
    async () => {
      await region.getByRole("button", { name: "New test case" }).click();
      await expect(
        region.getByLabel("Test name", { exact: true }),
      ).toBeVisible();
    },
    () => region.getByLabel("Test name", { exact: true }).isVisible(),
  );
  await enter(
    region.getByLabel("Test name", { exact: true }),
    "SO-1 west express",
  );
  await enter(
    region.getByLabel("Test date and time (UTC or offset)", { exact: true }),
    "2026-10-20T12:00:00Z",
  );
  const documents = region.locator(".regression-document");
  await expect(documents).toHaveCount(2);
  await step(
    async () => {
      await region.getByRole("button", { name: "Add named input" }).click();
      await expect(documents).toHaveCount(3);
    },
    async () => (await documents.count()) === 3,
  );
  await expect(documents).toHaveCount(3);
  await step(
    async () => {
      await region.getByRole("button", { name: "Add expected output" }).click();
      await expect(documents).toHaveCount(4);
    },
    async () => (await documents.count()) === 4,
  );
  await expect(documents).toHaveCount(4);
  await enter(documents.nth(0).getByLabel("Input UTF-8 text"), order);
  await enter(documents.nth(1).getByLabel("Document name"), "rates");
  await enter(documents.nth(1).getByLabel("Input UTF-8 text"), rates);
  // Independently authored expectation: Ferrule's pretty JSON with a final LF and the fixed clock.
  await enter(
    documents.nth(2).getByLabel("Expected UTF-8 text"),
    shipment("2026-10-20T12:00:00Z"),
  );
  await enter(documents.nth(3).getByLabel("Document name"), "summary");
  await enter(
    documents.nth(3).getByLabel("Expected UTF-8 text"),
    '{\n  "lineCount": 2,\n  "region": "west"\n}\n',
  );
  const parameters = region.locator(".regression-parameter");
  for (const [index, [name, kind, value]] of [
    ["region", "string", "west"],
    ["express", "boolean", "true"],
  ].entries()) {
    await step(
      async () => {
        await region.getByRole("button", { name: "Add parameter" }).click();
        await expect(parameters).toHaveCount(index + 1);
      },
      async () => (await parameters.count()) === index + 1,
    );
    await expect(parameters).toHaveCount(index + 1);
    const row = parameters.nth(index);
    await enter(row.getByLabel("Parameter name"), name);
    await step(
      () => row.getByLabel("Parameter type").selectOption(kind),
      async () =>
        (await row.getByLabel("Parameter type").inputValue()) === kind,
    );
    entered.push([row.getByLabel("Parameter type"), kind]);
    await enter(row.getByLabel("Exact parameter value"), value);
  }
  try {
    await assertEntered();
  } catch (error) {
    if (!(await recoverEditor())) throw error;
  }
}

test("saves a regression case with an authored baseline and runs it", async () => {
  test.setTimeout(900_000);
  await openTests(page);
  const region = tests(page);
  await enterCaseFields(region);
  const saved = page.waitForResponse(
    (response) =>
      response.request().method() === "PUT" &&
      /\/regression-cases\/[a-f0-9-]{36}\/versions\/1$/.test(
        new URL(response.url()).pathname,
      ),
  );
  await region.getByRole("button", { name: "Save test version" }).click();
  const response = await saved;
  expect(response.status()).toBe(201);
  // The saved fixture is exactly what was entered.
  const sent = response.request().postDataJSON() as {
    namedInputs: { name: string | null }[];
    expected: { name: string | null }[];
    parameters: { name: string }[];
  };
  expect(sent.namedInputs.map((input) => input.name)).toEqual(["rates"]);
  expect(sent.expected.map((output) => output.name)).toEqual([null, "summary"]);
  expect(sent.parameters.map((parameter) => parameter.name).sort()).toEqual([
    "express",
    "region",
  ]);
  const savedCaseId = /\/regression-cases\/([a-f0-9-]{36})\/versions\/1$/.exec(
    new URL(response.request().url()).pathname,
  )![1];
  let reopen = false;
  await readHostedState(
    page,
    async () => {
      if (reopen) {
        await workflow(page, "Tests").click();
        const load = region.getByRole("button", {
          name: "Load regression cases",
          exact: true,
        });
        if (await load.isVisible()) await load.click();
        else
          await region
            .getByRole("button", { name: "Refresh test access", exact: true })
            .click();
        await region
          .getByRole("button", { name: "Open SO-1 west express", exact: true })
          .click();
      }
      await expect(
        region.getByRole("heading", { name: "SO-1 west express · Version 1" }),
      ).toBeVisible();
      await expect(
        region.getByText(savedCaseId, { exact: true }),
      ).toBeVisible();
    },
    {
      recover: async () => {
        if (!(await recoverReadAdmission(page))) return false;
        reopen = true;
        return true;
      },
      onRecovery: () => recoveries.push("saved regression read"),
    },
  );
  caseRuns = [await runCase(page)];
});

const prepare = (target: Page) =>
  target.getByRole("region", { name: "Prepare this version", exact: true });

/** Waits for one confirmed mutation response after the application's own confirmation dialog. */
async function confirmed(
  target: Page,
  suffix: RegExp,
  action: () => Promise<unknown>,
  recoverUnsentAction?: () => Promise<boolean>,
): Promise<Response> {
  const response = await confirmHostedMutation(target, suffix, action, {
    confirm: () => confirmAction(target),
    recoverUnsentAction,
    onRecovery: () => recoveries.push("unsent build action"),
  });
  expect([200, 201]).toContain(response.status());
  return response;
}

test("prepares and completes a build through the Ferrule service", async () => {
  // Each attempt may run for up to six minutes, and the run may use every attempt of its budget.
  test.setTimeout(1_500_000);
  const first = scopes.first!;
  const mapping = `/api/v1/organizations/${organization}/projects/${first.projectId}/mappings/${first.mappingId}`;
  await openCatalog(revisionPath(scopes.first!), revisionReady);
  await workflow(page, "Prepare").click();
  const panel = prepare(page);
  // A failed session check while loading clears request authority; the panel then asks for
  // a catalog refresh before loading again.
  const changed = panel
    .getByRole("alert")
    .filter({ hasText: "Your access changed" });
  const loadControls = async () => {
    for (let attempt = 1; ; attempt++) {
      const refresh = panel.getByRole("button", {
        name: "Refresh environments",
      });
      await expect(refresh.or(changed)).toBeVisible();
      if (!(await changed.isVisible())) {
        await expect(refresh).toBeEnabled();
        return;
      }
      expect(attempt).toBeLessThan(4);
      recoveries.push("build controls");
      await page.getByRole("button", { name: "Refresh catalog" }).click();
    }
  };
  await loadControls();
  const environment = panel.getByRole("combobox", {
    name: "Environment version",
  });
  await expect(environment).toBeVisible();
  // A new stack has no build environment; its project administrator registers the offered one.
  if ((await environment.locator("option").count()) === 1) {
    await panel
      .getByRole("button", { name: "Environment administration" })
      .click();
    await confirmed(
      page,
      /\/build-environments\/[^/]+\/versions\/1$/,
      async () => {
        const register = panel.getByRole("button", {
          name: "Register new environment",
        });
        await expect(register.or(changed).first()).toBeVisible({
          timeout: 120_000,
        });
        if (await changed.isVisible())
          throw new Error("Build admission changed before registration.");
        await register.click({ timeout: 5_000 });
      },
      async () => {
        if (!(await changed.isVisible())) return false;
        await page.getByRole("button", { name: "Refresh catalog" }).click();
        await loadControls();
        if ((await environment.locator("option").count()) !== 1) return false;
        await panel
          .getByRole("button", { name: "Environment administration" })
          .click();
        return true;
      },
    );
    await panel.getByRole("button", { name: "Refresh environments" }).click();
  }
  await expect(environment.locator("option")).toHaveCount(2);
  await environment.selectOption({ index: 1 });
  await panel
    .getByRole("button", { name: "Verify this evidence" })
    .first()
    .click();
  await expect(
    panel.getByRole("status").filter({
      hasText: "Result matches the saved expectation, including formatting",
    }),
  ).toBeVisible();
  const approved = await confirmed(page, /\/build-approvals\/[^/]+$/, () =>
    panel.getByRole("button", { name: "Approve preparation" }).click(),
  );
  const approval = await read<BuildApprovalView>(
    page,
    new URL(approved.url()).pathname,
  );
  buildId = approval.buildId;
  expect(buildId).toBe(
    (approved.request().postDataJSON() as { buildId: string }).buildId,
  );
  // The approval binds the combined native and toolchain identity measured from the trusted
  // service image, not the native identity alone.
  const measured = measuredCompiler();
  expect(measured.build).not.toBe(measured.native);
  expect(approval.backend.engineBuild).toBe(measured.build);
  const submitted = await confirmed(
    page,
    new RegExp(`/build-runs/${buildId}$`),
    () =>
      panel.getByRole("button", { name: "Prepare approved version" }).click(),
  );
  expect(submitted.status()).toBe(201);
  const path = `${mapping}/build-runs/${buildId}`;
  // Wait for any terminal state, so a failed, exhausted, uncertain, canceled or revoked run
  // reports its own state and outcome rather than a timeout.
  let completed: BuildRunView | undefined;
  await expect
    .poll(
      async () => {
        completed = await read<BuildRunView>(page, path);
        return activeBuildStates.includes(completed.state);
      },
      { timeout: 1_320_000, intervals: [2_000] },
    )
    .toBe(false);
  expect(completed).toMatchObject({ state: "succeeded" });
  buildAttempt = completed!.attempt;
  expect(buildAttempt).toBeGreaterThanOrEqual(1);
  expect(buildAttempt).toBeLessThanOrEqual(buildAttemptBudget);
  const artifact = completed!.artifact;
  expect(artifact).not.toBeNull();
  expect(artifact!.byteCount).toBeGreaterThan(0);
  expect(artifact!.sha256).toMatch(/^[0-9a-f]{64}$/);
  expect(artifact!.format).toBe(approval.profile.artifactFormat);
  // The published private body is exactly the artifact the run reports, for this approval.
  expect(
    sql(
      `select artifact_digest, octet_length(artifact_body), approval_id, state from braid.build_publications where build_id = '${buildId}'`,
    ),
  ).toEqual([
    `${artifact!.sha256}|${artifact!.byteCount}|${approval.approvalId}|2`,
  ]);
  buildArtifact = artifact;

  // The panel shows the same completed result after a refresh through its own control.
  await panel
    .getByRole("button", { name: "Refresh preparation result" })
    .click();
  const result = panel.locator(".builds-result");
  const heading = result.getByRole("heading", { level: 4 });
  await expect(heading).toContainText("Preparation complete");
  await expect(heading).toContainText(
    `Attempt ${buildAttempt} of ${buildAttemptBudget}`,
  );
  await result.locator(".builds-result-details > summary").click();
  await expect(
    result.locator(".builds-result-details").getByText(artifact!.sha256, {
      exact: true,
    }),
  ).toBeVisible();
  await expect(result.locator(".builds-result-details")).toContainText(
    `State: succeeded · Outcome: ${completed!.outcome ?? "not returned"}`,
  );
});

interface PreviewRunView {
  state: string;
  outcome: string | null;
  attempt: number;
  outputs: unknown[];
}

const count = (text: string, pattern: RegExp) =>
  (text.match(new RegExp(pattern, "g")) ?? []).length;

test("recovers retained work across a server restart and Ferrule service scaling", async () => {
  test.setTimeout(1_800_000);
  try {
    const first = scopes.first!;
    const mapping = `/api/v1/organizations/${organization}/projects/${first.projectId}/mappings/${first.mappingId}`;

    // Previews queue while the runs worker is stopped; nothing executes them yet.
    // Each tab of the same signed-in browser holds one preview.
    stack("stop", "--timeout", "60", "runs-relay", "runs");
    const queued: string[] = [];
    for (let index = 0; index < 3; index++) {
      const tab = await context.newPage();
      await openCatalog(revisionPath(first), revisionReady, tab);
      await workflow(tab, "Try sample").click();
      await fillPreview(tab);
      queued.push(await submitPreview(tab));
      await expect(preview(tab).locator(".preview-status")).toContainText(
        "Queued",
      );
      await tab.close();
    }
    await openCatalog(revisionPath(first), revisionReady);

    // A chat turn is in flight at the model when the server replica stops. A small model can
    // finish first; the turn's ledger phase at the stop decides, and a finished turn is asked again.
    scaleService(3);
    const tokensBefore = count(
      stack("logs", "--no-color", "issuer"),
      /issuer token/,
    );
    if (
      (await page
        .locator("#workspace-chat-toggle")
        .getAttribute("aria-expanded")) === "false"
    )
      await page.locator("#workspace-chat-toggle").click();
    const chat = page.getByRole("complementary", { name: "Workspace chat" });
    await chat.getByRole("button", { name: "New conversation" }).click();
    let turnId = "";
    let conversationId = "";
    for (let attempt = 1; ; attempt++) {
      await chat
        .getByRole("textbox", { name: "Question" })
        .fill(
          "Describe every output field of this mapping in detail, one paragraph per field.",
        );
      const turnRequest = page.waitForRequest(
        (request) =>
          request.method() === "POST" &&
          /\/chat\/conversations\/[0-9a-f-]{36}\/turns$/.test(
            new URL(request.url()).pathname,
          ),
      );
      await chat.getByRole("button", { name: "Send question" }).click();
      const turn = await turnRequest;
      turnId = (turn.postDataJSON() as { turnId: string }).turnId;
      conversationId = /\/conversations\/([0-9a-f-]{36})\/turns$/.exec(
        new URL(turn.url()).pathname,
      )![1];
      await expect(
        chat
          .getByRole("region", { name: "Selected answer" })
          .getByRole("status"),
      ).toHaveText("Reading answer…");
      // In flight at the stop: still active, or settled by the stopping replica itself.
      const stopping = sql("select now()")[0];
      // The replica stops first, as a platform restart would, then the processes in its namespace.
      stack("stop", "--timeout", "30", "server");
      stack("stop", "--timeout", "30", "edge", "server-relay");
      const inFlight =
        sql(
          `select phase = 1 or finished_at >= '${stopping}' from braid.chat_turns where turn_id = '${turnId}'`,
        )[0] === "t";
      startServer();
      if (inFlight) break;
      expect(attempt).toBeLessThan(3);
      await openCatalog(revisionPath(first), revisionReady);
      if (
        (await page
          .locator("#workspace-chat-toggle")
          .getAttribute("aria-expanded")) === "false"
      )
        await page.locator("#workspace-chat-toggle").click();
      await chooseSavedConversation(chat, conversationId);
    }
    const roundsAfterRestart = sql(
      `select count(*) from braid.chat_rounds where turn_id = '${turnId}'`,
    );
    const modelCallsAfterRestart = count(
      stack("logs", "--no-color", "model"),
      /POST\s+"\/api\/chat"/,
    );

    // The same browser session and every retained record reopen on the restarted replica.
    await openCatalog(revisionPath(first), revisionReady);
    await expectSignedInAccount(page);
    const reports = page.getByRole("region", { name: "Saved reports" });
    await settleReports(reports);
    await expect(
      page.getByRole("table", { name: "Mapping field connections" }),
    ).toContainText('["orderId"]');
    const interrupted = chat
      .getByRole("region", { name: "Selected answer" })
      .getByRole("status");
    // The turn the stopped replica was running settles once, without another provider call.
    let reopen = false;
    await readHostedState(
      page,
      async () => {
        if (
          (await page
            .locator("#workspace-chat-toggle")
            .getAttribute("aria-expanded")) === "false"
        )
          await page.locator("#workspace-chat-toggle").click();
        if (reopen) {
          await refreshChat(chat);
        }
        const conversations = await openHistory(chat);
        await expect(
          conversations.locator(
            `button[data-conversation-id="${conversationId}"]`,
          ),
        ).toHaveCount(1);
        await conversations
          .locator(`button[data-conversation-id="${conversationId}"]`)
          .click();
        await Promise.all([
          page.waitForRequest(
            (request) =>
              request.method() === "GET" &&
              new URL(request.url()).pathname.endsWith(
                `/conversations/${conversationId}/turns/${turnId}/events`,
              ),
          ),
          chat
            .getByRole("list", { name: "Conversation transcript" })
            .locator(".chat-bubble")
            .last()
            .click(),
        ]);
        await expect(interrupted).toHaveText(
          /^Answer (interrupted|failed|cancelled)\b/,
          { timeout: 120_000 },
        );
      },
      {
        recover: async () => {
          if (!(await recoverReadAdmission(page))) return false;
          reopen = true;
          return true;
        },
        onRecovery: () => recoveries.push("retained chat read"),
      },
    );
    expect(
      sql(
        `select phase <> 1 from braid.chat_turns where turn_id = '${turnId}'`,
      ),
    ).toEqual(["t"]);
    expect(
      sql(`select count(*) from braid.chat_rounds where turn_id = '${turnId}'`),
    ).toEqual(roundsAfterRestart);
    expect(
      count(stack("logs", "--no-color", "model"), /POST\s+"\/api\/chat"/),
    ).toBe(modelCallsAfterRestart);
    for (const runId of queued)
      expect(
        (
          await read<PreviewRunView>(
            page,
            `/api/v1/organizations/${organization}/previews/${runId}`,
          )
        ).state,
      ).toBe("queued");

    // The worker resumes while the service scales from three replicas to one.
    stack(
      "up",
      "--detach",
      "--wait",
      "--wait-timeout",
      "180",
      "--no-deps",
      "runs",
      "runs-relay",
    );
    scaleService(1);
    await expect
      .poll(
        async () => {
          const runs = await Promise.all(
            queued.map((runId) =>
              read<PreviewRunView>(
                page,
                `/api/v1/organizations/${organization}/previews/${runId}`,
              ),
            ),
          );
          return runs.map((run) => [
            run.state,
            run.outcome,
            run.attempt,
            run.outputs.length,
          ]);
        },
        { timeout: 600_000, intervals: [5_000] },
      )
      .toEqual(queued.map(() => ["completed", "succeeded", 1, 2]));
    // Each queued preview ran exactly once.
    expect(
      sql(
        `select count(*), min(attempt), max(attempt) from braid.preview_runs where run_id in (${queued.map((id) => `'${id}'`).join(",")})`,
      ),
    ).toEqual(["3|1|1"]);

    // Regression evidence keeps passing on one replica; the completed artifact survives restart.
    await openTests(page);
    await tests(page)
      .getByRole("button", { name: "Open SO-1 west express" })
      .first()
      .click();
    caseRuns.push(await runCase(page));
    const build = await read<BuildRunView>(
      page,
      `${mapping}/build-runs/${buildId}`,
    );
    expect(build).toMatchObject({ state: "succeeded", attempt: buildAttempt });
    expect(build.artifact).toEqual(buildArtifact);

    // The browser kept its session: the restart needed no new sign-in at the issuer.
    expect(count(stack("logs", "--no-color", "issuer"), /issuer token/)).toBe(
      tokensBefore,
    );
    expect(
      sql(
        `select count(*) from braid.regression_runs where run_id in (${caseRuns.map((id) => `'${id}'`).join(",")}) and attempt = 1`,
      ),
    ).toEqual([String(caseRuns.length)]);
  } finally {
    // Leave the stack whole for the harness's own checks, also after a failure.
    stack(
      "up",
      "--detach",
      "--wait",
      "--wait-timeout",
      "180",
      "--no-deps",
      "runs",
      "runs-relay",
    );
    scaleService(2);
  }
});
