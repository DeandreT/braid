import type { Page, Request, Route, Response } from "@playwright/test";
import { createHash } from "node:crypto";
import type { components } from "../src/api/generated";
import {
  test,
  expect,
  synthetic,
  projectsPath,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
  uploadPath,
} from "./native-mapping-upload.fixture";

// External source candidate. These controlled responses qualify browser behavior,
// never SQL races, worker/storage admission or actual native-format acceptance.
// The shared E fixture owns the host. All API requests below are controlled.
// Global config/runner must disable DOM capture and trace/video/screenshots.
// No per-file test.use, ARIA snapshots, raw material assertions or attachments.
type Context = components["schemas"]["CatalogContextV1"];
type Project = components["schemas"]["CatalogProjectV1"];
type Receipt = components["schemas"]["CatalogRevisionIngestionReceiptV1"];
type Revision = components["schemas"]["CatalogRevisionV1"];
type Reply = {
  status?: number;
  json?: unknown;
  headers?: Record<string, string>;
  abort?: boolean;
};
type Plan = Reply | (() => Reply | Promise<Reply>);
type Kind =
  "context" | "project" | "create" | "receipt" | "resume" | "revision";
type Wire = {
  exactBytes: boolean;
  exactQuery: boolean;
  queryKeyCount: boolean;
  queryDistinctKeys: boolean;
  queryAllowedKeys: boolean;
  queryName: boolean;
  queryDescription: boolean;
  queryFilename: boolean;
  queryBackend: boolean;
  queryAscii: boolean;
  queryLineWithinLimit: boolean;
  correctHeaders: boolean;
  freshToken: boolean;
  sameOriginalBytes: boolean;
  sameOriginalQuery: boolean;
  sameOriginalKey: boolean;
};
const clone = <T>(value: T): T => structuredClone(value);
const contextPath = "/api/v1/catalog/context";
const otherActor = "10000000-0000-4000-8000-000000000084";
const otherOrganization = "10000000-0000-4000-8000-000000000085";
const otherProjectId = "20000000-0000-4000-8000-000000000084";
const originalBytes = () =>
  Buffer.from([0x7b, 0x0d, 0x0a, 0x00, 0xc3, 0xa9, 0x7d, 0xff]);
const replacementBytes = () =>
  Buffer.from([0xff, 0x00, 0x0a, 0x0d, 0x22, 0x2b, 0x7d]);
const codeReply = (status: number, code: string): Reply => ({
  status,
  json: { code },
});
const canonicalId = (value: string): boolean =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
    value,
  ) && value !== "00000000-0000-0000-0000-000000000000";

class ControlledUpload {
  readonly context: Context = {
    ...clone(synthetic.context),
    features: { ...synthetic.context.features, createMappings: true },
  };
  readonly project: Project = {
    ...clone(synthetic.project),
    access: {
      role: "publisher",
      actions: { ...synthetic.project.access.actions, createMappings: true },
    },
  };
  readonly wires: Wire[] = [];
  contextReads = 0;
  projectReads = 0;
  receiptReads = 0;
  revisionReads = 0;
  resumeCount = 0;
  unexpectedRequests = 0;
  contextReply: Plan | null = null;
  projectReply: Plan | null = null;
  receiptReply: Plan | null = null;
  revisionReply: Plan | null = null;
  postReply: Plan = { abort: true };
  resumeReply: Plan = { abort: true };
  #bytes = originalBytes();
  #input = {
    name: "Controlled native mapping",
    description: "",
    filename: "original.native.json",
  };
  #admission: Revision["admission"] = "valid";
  #operation = 1;
  #lastToken = "";
  #lastMutationToken: string | null = null;
  #original: { body: Buffer; query: string; key: string } | null = null;
  #resumeValid = true;
  #released = false;
  #releases: (() => void)[] = [];
  constructor(readonly page: Page) {}
  get createCount(): number {
    return this.wires.length;
  }
  get scope() {
    return this.project.scope;
  }
  get otherScope() {
    return { ...this.scope, projectId: otherProjectId };
  }
  get futureScope() {
    return {
      ...this.scope,
      mappingId: `30000000-0000-4000-8000-${String(this.#operation).padStart(12, "0")}`,
      revisionId: `40000000-0000-4000-8000-${String(this.#operation).padStart(12, "0")}`,
    };
  }
  get operationPath(): string {
    return (
      catalogApi(projectPath(this.scope)) +
      `/revision-ingestions/50000000-0000-4000-8000-${String(this.#operation).padStart(12, "0")}`
    );
  }
  get resumeValid(): boolean {
    return this.#resumeValid;
  }
  get lastKeyDiffers(): boolean {
    return this.wires.at(-1)?.sameOriginalKey === false;
  }
  draft(
    acceptedName: string,
    description = "",
    filename = "original.native.json",
  ): void {
    this.#input = { name: acceptedName, description, filename };
  }
  file(
    name = "original.native.json",
    bytes = this.#bytes,
    mimeType = "application/json",
  ) {
    this.#bytes = Buffer.from(bytes);
    return { name, mimeType, buffer: Buffer.from(bytes) };
  }
  nextOperation(): void {
    ++this.#operation;
  }
  receipt(
    phase: Receipt["phase"] = "reserved",
    count = 1,
    failure: Receipt["latestFailure"] = null,
  ): Receipt {
    const committed = phase === "committed";
    const terminal =
      committed ||
      phase === "rejected" ||
      phase === "attemptExhausted" ||
      phase === "headConflict";
    const future = this.futureScope;
    return {
      operationId: this.operationPath.split("/").at(-1)!,
      project: clone(this.scope),
      futureMapping: {
        organizationId: future.organizationId,
        projectId: future.projectId,
        mappingId: future.mappingId,
      },
      futureRevision: future,
      phase,
      claimCount: count,
      reservedAtUtc: "2026-10-15T12:00:00.1234567Z",
      completedAtUtc: terminal ? "2026-10-15T12:00:01.1234567Z" : null,
      originalFilename: this.#input.filename,
      committedSequence: committed ? "1" : null,
      committedAdmission: committed ? this.#admission : null,
      latestFailure: failure,
    };
  }
  committed(admission: Revision["admission"] = "valid", count = 1): Reply {
    this.#admission = admission;
    return {
      status: 200,
      json: {
        outcome:
          admission === "valid" ? "committedValid" : "committedDecodedInvalid",
        receipt: this.receipt("committed", count),
        rejection: null,
        failure: null,
        committedRevisionUrl: catalogApi(revisionPath(this.futureScope)),
      },
    };
  }
  pending(phase: Receipt["phase"] = "reserved", count = 1): Reply {
    return {
      status: 202,
      json: { outcome: "inProgress", receipt: this.receipt(phase, count) },
      headers: { Location: this.operationPath },
    };
  }
  needsSource(count = 2): Reply {
    return {
      status: 409,
      json: {
        outcome: "needsSource",
        receipt: this.receipt("staged", count, "needsSource"),
        failure: "needsSource",
      },
    };
  }
  rejected(
    rejection:
      "nativeDecode" | "unsupportedArtifact" | "resourceLimit" = "nativeDecode",
    count = 1,
  ): Reply {
    return {
      status: 422,
      json: {
        outcome: "rejected",
        receipt: this.receipt("rejected", count),
        rejection,
      },
    };
  }
  revision(): Revision {
    return {
      ...clone(synthetic.revision),
      scope: this.futureScope,
      sequence: "1",
      admission: this.#admission,
      committedAtUtc: "2026-10-15T12:00:01.1234567Z",
      creatorPrincipalId: synthetic.actor,
      originalFilename: this.#input.filename,
      nativeByteCount: this.#bytes.length,
      nativeDigest: {
        algorithm: "sha256",
        value: createHash("sha256").update(this.#bytes).digest("hex"),
      },
    };
  }
  hold(kind: Kind, reply: Reply) {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let entered = false;
    let canceled = false;
    let request: Request | null = null;
    this.page.on("requestfailed", (failed) => {
      if (failed === request) canceled = true;
    });
    const plan = async () => {
      entered = true;
      await gate;
      return reply;
    };
    this.#held = {
      kind,
      capture: (value) => {
        request = value;
      },
    };
    if (kind === "create") this.postReply = plan;
    else if (kind === "resume") this.resumeReply = plan;
    else if (kind === "receipt") this.receiptReply = plan;
    else if (kind === "context") this.contextReply = plan;
    else if (kind === "project") this.projectReply = plan;
    else this.revisionReply = plan;
    this.#releases.push(release);
    return { release, entered: () => entered, canceled: () => canceled };
  }
  #held: { kind: Kind; capture: (request: Request) => void } | null = null;
  response(kind: Kind, status: number): Promise<Response> {
    const origin = new URL(this.page.url()).origin;
    const path =
      kind === "context"
        ? contextPath
        : kind === "project"
          ? catalogApi(projectPath(this.scope))
          : kind === "revision"
            ? catalogApi(revisionPath(this.futureScope))
            : kind === "create"
              ? catalogApi(projectPath(this.scope)) + "/mappings"
              : this.operationPath + (kind === "resume" ? "/resume" : "");
    return this.page.waitForResponse((response) => {
      const request = response.request(),
        url = new URL(response.url());
      return (
        url.origin === origin &&
        request.method() ===
          (kind === "create" || kind === "resume" ? "POST" : "GET") &&
        url.pathname === path &&
        (kind === "create" || url.search === "") &&
        response.status() === status
      );
    });
  }
  failedCreate(): Promise<Request> {
    return this.page.waitForEvent("requestfailed", (request) => {
      const url = new URL(request.url());
      return (
        request.method() === "POST" &&
        url.pathname === catalogApi(projectPath(this.scope)) + "/mappings"
      );
    });
  }
  async install(): Promise<void> {
    await this.page.route("**/api/**", async (route: Route) => {
      const request = route.request(),
        url = new URL(request.url()),
        method = request.method();
      // Explicit bootstrap does not consume any existing context/resource/write plan.
      if (url.pathname === "/api/auth/session") {
        const headers = request.headers();
        const admitted =
          url.origin === new URL(this.page.url()).origin &&
          method === "GET" &&
          !url.search &&
          (request.postDataBuffer()?.length ?? 0) === 0 &&
          headers["x-weft-session"] === "1" &&
          ![
            "content-type",
            "authorization",
            "x-xsrf-token",
            "x-weft-auth-token",
            "idempotency-key",
            "if-match",
            "if-none-match",
            "identitycontext",
            "identity-context",
            "x-weft-identity-context",
            "x-weft-actor",
            "x-weft-role",
            "x-weft-login",
            "x-weft-logout",
            "x-weft-forget-local",
          ].some((name) => name in headers);
        if (!admitted) ++this.unexpectedRequests;
        await route.fulfill(
          admitted
            ? { json: { version: 1, mode: "development" } }
            : { status: 404, json: { code: "notFound" } },
        );
        return;
      }
      let kind: Kind | null = null;
      let plan: Plan;
      if (url.origin !== new URL(this.page.url()).origin) {
        ++this.unexpectedRequests;
        plan = codeReply(404, "notFound");
      } else if (
        method === "GET" &&
        url.pathname === contextPath &&
        !url.search
      ) {
        kind = "context";
        ++this.contextReads;
        this.#lastToken = `controlled-upload-admission-${this.contextReads}`;
        plan = this.contextReply ?? {
          json: { ...this.context, requestToken: this.#lastToken },
        };
      } else if (
        method === "GET" &&
        !url.search &&
        [
          catalogApi(projectPath(this.scope)),
          catalogApi(projectPath(this.otherScope)),
        ].includes(url.pathname)
      ) {
        kind = "project";
        ++this.projectReads;
        plan = this.projectReply ?? {
          json: {
            ...this.project,
            scope: url.pathname.endsWith(otherProjectId)
              ? this.otherScope
              : this.scope,
          },
        };
      } else if (
        method === "POST" &&
        url.pathname === catalogApi(projectPath(this.scope)) + "/mappings"
      ) {
        kind = "create";
        this.observeCreate(request, url);
        plan = this.postReply;
      } else if (
        method === "GET" &&
        url.pathname === this.operationPath &&
        !url.search
      ) {
        kind = "receipt";
        ++this.receiptReads;
        plan = this.receiptReply ?? { json: this.receipt() };
      } else if (
        method === "POST" &&
        url.pathname === this.operationPath + "/resume" &&
        !url.search
      ) {
        kind = "resume";
        ++this.resumeCount;
        const headers = request.headers();
        this.#resumeValid &&=
          (request.postDataBuffer()?.length ?? 0) === 0 &&
          ![
            "content-type",
            "idempotency-key",
            "if-match",
            "if-none-match",
            "content-encoding",
          ].some((key) => key in headers) &&
          headers["x-xsrf-token"] === this.#lastToken &&
          headers["x-xsrf-token"] !== this.#lastMutationToken;
        this.#lastMutationToken = headers["x-xsrf-token"] ?? null;
        plan = this.resumeReply;
      } else if (
        method === "GET" &&
        url.pathname === catalogApi(revisionPath(this.futureScope)) &&
        !url.search
      ) {
        kind = "revision";
        ++this.revisionReads;
        plan = this.revisionReply ?? { json: this.revision() };
      } else if (
        method === "GET" &&
        url.pathname === catalogApi(mappingPath(this.futureScope)) &&
        !url.search
      ) {
        plan = {
          json: {
            ...clone(synthetic.mapping),
            scope: this.receipt().futureMapping,
            head: {
              scope: this.futureScope,
              sequence: "1",
              admission: this.#admission,
            },
          },
        };
      } else if (
        method === "GET" &&
        url.pathname === catalogApi(projectsPath(synthetic.org)) &&
        url.search === "?limit=10"
      ) {
        plan = { json: { projects: [this.project], nextCursor: null } };
      } else if (
        method === "GET" &&
        [
          catalogApi(projectPath(this.scope)) + "/mappings",
          catalogApi(projectPath(this.otherScope)) + "/mappings",
        ].includes(url.pathname) &&
        url.search === "?limit=10"
      ) {
        plan = { json: { mappings: [], nextCursor: null } };
      } else {
        ++this.unexpectedRequests;
        plan = codeReply(404, "notFound");
      }
      if (kind && this.#held?.kind === kind) this.#held.capture(request);
      const selected = plan;
      const reply =
        typeof selected === "function" ? await selected() : selected;
      try {
        if (reply.abort) await route.abort("failed");
        else
          await route.fulfill({
            status: reply.status ?? 200,
            json: reply.json ?? {},
            headers: reply.headers,
          });
      } catch {
        if (!this.#released && !request.failure() && !this.page.isClosed())
          throw new Error(
            "A controlled upload response could not be delivered.",
          );
      }
    });
  }
  private observeCreate(request: Request, url: URL): void {
    const headers = request.headers(),
      body = request.postDataBuffer(),
      key = headers["idempotency-key"] ?? "";
    const query = url.search,
      token = headers["x-xsrf-token"] ?? "";
    const keys = [...url.searchParams.keys()];
    const exactQuery =
      keys.length === 4 &&
      new Set(keys).size === 4 &&
      keys.every((value) =>
        ["name", "backendId", "description", "originalFilename"].includes(
          value,
        ),
      ) &&
      url.searchParams.get("name") === this.#input.name &&
      url.searchParams.get("description") === this.#input.description &&
      url.searchParams.get("originalFilename") === this.#input.filename &&
      url.searchParams.get("backendId") === "ferrule" &&
      /^[\x00-\x7f]*$/.test(query) &&
      ("POST " + url.pathname + query + " HTTP/1.1\r\n").length <= 65536;
    this.#original ??= { body: Buffer.from(body ?? []), query, key };
    this.wires.push({
      exactBytes: body !== null && body.equals(this.#bytes),
      exactQuery,
      queryKeyCount: keys.length === 4,
      queryDistinctKeys: new Set(keys).size === 4,
      queryAllowedKeys: keys.every((value) =>
        ["name", "backendId", "description", "originalFilename"].includes(
          value,
        ),
      ),
      queryName: url.searchParams.get("name") === this.#input.name,
      queryDescription:
        url.searchParams.get("description") === this.#input.description,
      queryFilename:
        url.searchParams.get("originalFilename") === this.#input.filename,
      queryBackend: url.searchParams.get("backendId") === "ferrule",
      queryAscii: /^[\x00-\x7f]*$/.test(query),
      queryLineWithinLimit:
        ("POST " + url.pathname + query + " HTTP/1.1\r\n").length <= 65536,
      correctHeaders:
        headers["content-type"] === "application/octet-stream" &&
        canonicalId(key) &&
        token === this.#lastToken &&
        ![
          "if-match",
          "if-none-match",
          "authorization",
          "content-encoding",
          "x-weft-actor",
          "x-weft-role",
        ].some((name) => name in headers),
      freshToken:
        this.#lastMutationToken === null || token !== this.#lastMutationToken,
      sameOriginalBytes: body !== null && body.equals(this.#original.body),
      sameOriginalQuery: query === this.#original.query,
      sameOriginalKey: key === this.#original.key,
    });
    this.#lastMutationToken = token;
  }
  release(): void {
    this.#released = true;
    for (const release of this.#releases) release();
    this.#bytes = Buffer.alloc(0);
    this.#original = null;
  }
}

const button = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const heading = (page: Page, name: string) =>
  page.getByRole("heading", { name, exact: true });
const nameField = (page: Page) =>
  page.getByRole("textbox", { name: "Name", exact: true });
const descriptionField = (page: Page) =>
  page.getByRole("textbox", { name: "Description (optional)", exact: true });
const filenameField = (page: Page) =>
  page.getByRole("textbox", { name: "Saved filename", exact: true });
const fileField = (page: Page) =>
  page.getByLabel("Native mapping file", { exact: true });
async function privateInput(action: () => Promise<unknown>): Promise<void> {
  try {
    await action();
  } catch {
    throw new Error("A controlled upload input action did not complete.");
  }
}
async function finish(response: Promise<Response>): Promise<void> {
  let nonempty: boolean;
  try {
    nonempty = (await (await response).body()).length > 0;
  } catch {
    throw new Error("A controlled upload response body could not be consumed.");
  }
  expect(nonempty, "A controlled upload response body was empty.").toBe(true);
}
async function ready(
  page: Page,
  scope = synthetic.project.scope,
): Promise<void> {
  await page.goto(uploadPath(scope));
  await expect(heading(page, "Upload mapping")).toBeVisible();
  await expect(fileField(page)).toBeEnabled();
}
async function prepare(
  page: Page,
  fixture: ControlledUpload,
  input = {
    name: "Controlled native mapping",
    description: "",
    filename: "original.native.json",
    acceptedName: "Controlled native mapping",
  },
): Promise<void> {
  fixture.draft(input.acceptedName, input.description, input.filename);
  await privateInput(() => nameField(page).fill(input.name));
  await privateInput(() => descriptionField(page).fill(input.description));
  await privateInput(() => fileField(page).setInputFiles(fixture.file()));
  await privateInput(() => filenameField(page).fill(input.filename));
  await expect(button(page, "Upload mapping")).toBeEnabled();
}
function wires(fixture: ControlledUpload, count: number): void {
  expect(fixture.createCount).toBe(count);
  expect(
    fixture.wires.every((wire) => wire.exactBytes),
    "Native byte identity differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryKeyCount),
    "Reviewed query key count differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryDistinctKeys),
    "Reviewed query key uniqueness differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryAllowedKeys),
    "Reviewed query contains an unexpected field.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryName),
    "Reviewed query name differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryDescription),
    "Reviewed query description differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryFilename),
    "Reviewed query filename differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryBackend),
    "Reviewed query backend differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryAscii),
    "Reviewed query encoding differs.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.queryLineWithinLimit),
    "Reviewed upload request line exceeds its limit.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.exactQuery),
    "Reviewed query fields differ.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.correctHeaders),
    "Native transport headers differ.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.freshToken),
    "A deliberate send did not use fresh admission.",
  ).toBe(true);
}
function sameOriginal(fixture: ControlledUpload): void {
  expect(
    fixture.wires.every((wire) => wire.sameOriginalBytes),
    "Recovery changed the original native bytes.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.sameOriginalQuery),
    "Recovery changed the original encoded query.",
  ).toBe(true);
  expect(
    fixture.wires.every((wire) => wire.sameOriginalKey),
    "Recovery changed the original request key.",
  ).toBe(true);
}
async function unknown(page: Page): Promise<void> {
  await expect(heading(page, "Upload outcome is unknown")).toBeVisible();
}
async function saved(page: Page, invalid = false): Promise<void> {
  await expect(
    heading(
      page,
      invalid ? "Mapping saved with validation issues" : "Mapping saved",
    ),
  ).toBeVisible();
  await expect(button(page, "Retry revision access")).toBeEnabled();
}
async function unloadWarning(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const event = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  });
}
async function fieldMatches(
  page: Page,
  field: "name" | "description" | "filename",
  value: string,
): Promise<boolean> {
  const locator =
    field === "name"
      ? nameField(page)
      : field === "description"
        ? descriptionField(page)
        : filenameField(page);
  return locator.evaluate(
    (element, expected) => (element as HTMLInputElement).value === expected,
    value,
  );
}
async function controlled(
  page: Page,
  run: (fixture: ControlledUpload) => Promise<void>,
): Promise<void> {
  const fixture = new ControlledUpload(page);
  await fixture.install();
  try {
    await run(fixture);
  } finally {
    fixture.release();
    expect(fixture.unexpectedRequests).toBe(0);
  }
}
async function isolated(
  parent: Page,
  run: (page: Page, fixture: ControlledUpload) => Promise<void>,
): Promise<void> {
  const page = await parent.context().newPage();
  try {
    await controlled(page, (fixture) => run(page, fixture));
  } finally {
    await page.close();
  }
}
async function loseOriginal(
  page: Page,
  fixture: ControlledUpload,
): Promise<void> {
  fixture.postReply = { abort: true };
  const failed = fixture.failedCreate();
  await button(page, "Upload mapping").click();
  await failed;
  await unknown(page);
  await expect(button(page, "Resend original upload")).toBeEnabled();
}
async function clickCreate(
  page: Page,
  fixture: ControlledUpload,
  status: number,
  label = "Upload mapping",
): Promise<void> {
  const response = fixture.response("create", status);
  await button(page, label).click();
  await finish(response);
}
async function refresh(
  page: Page,
  fixture: ControlledUpload,
  status = 200,
): Promise<void> {
  const response = fixture.response("context", status);
  await button(page, "Refresh access").click();
  await finish(response);
  await expect(button(page, "Refresh access")).toBeEnabled();
}

test("controlled native upload: malformed ancestry and foreign admission never expose authority or send", async ({
  page,
}) => {
  const invalid = [
    uploadPath({
      ...synthetic.project.scope,
      organizationId: "not-an-organization",
    }),
    uploadPath({
      ...synthetic.project.scope,
      projectId: "00000000-0000-0000-0000-000000000000",
    }),
    uploadPath({
      ...synthetic.project.scope,
      organizationId: "A0000000-0000-4000-8000-000000000001",
    }),
    uploadPath(synthetic.project.scope) + "?operationId=forbidden",
  ];
  for (const path of invalid)
    await isolated(page, async (current, fixture) => {
      await current.goto(path);
      await expect(heading(current, "Upload form unavailable")).toBeVisible();
      expect(fixture.contextReads).toBe(0);
      expect(fixture.projectReads).toBe(0);
      expect(fixture.createCount).toBe(0);
      await expect(fileField(current)).toHaveCount(0);
    });
  await isolated(page, async (current, fixture) => {
    fixture.context.allowedOrganizationId = otherOrganization;
    await current.goto(uploadPath(fixture.scope));
    await expect(heading(current, "Upload form unavailable")).toBeVisible();
    await expect(button(current, "Refresh access")).toBeEnabled();
    expect(fixture.projectReads).toBe(0);
    expect(fixture.createCount).toBe(0);
  });
  await controlled(page, async (fixture) => {
    await ready(page);
    const reads = fixture.contextReads;
    await page.evaluate((path) => {
      history.pushState(null, "", path);
      window.dispatchEvent(new PopStateEvent("popstate"));
    }, invalid[0]);
    await expect(heading(page, "Upload form unavailable")).toBeVisible();
    await expect(page.locator(".catalog-context")).toHaveCount(0);
    expect(fixture.contextReads).toBe(reads);
    expect(fixture.createCount).toBe(0);
  });
});

test("controlled native upload: current feature, scoped action and fresh usable identity gate each send", async ({
  page,
}) => {
  for (const role of ["viewer", "runner"] as const)
    await isolated(page, async (current, fixture) => {
      fixture.project.access.role = role;
      fixture.project.access.actions.createMappings = false;
      await current.goto(uploadPath(fixture.scope));
      await expect(heading(current, "Upload form unavailable")).toBeVisible();
      await expect(button(current, "Refresh access")).toBeEnabled();
      await expect(fileField(current)).toHaveCount(0);
      expect(fixture.createCount).toBe(0);
    });
  const gates: ((fixture: ControlledUpload) => void)[] = [
    (fixture) => {
      fixture.context.features.createMappings = false;
    },
    (fixture) => {
      fixture.project.access.actions.createMappings = false;
    },
    (fixture) => {
      fixture.context.actorPrincipalId = otherActor;
    },
    (fixture) => {
      fixture.projectReply = {
        json: { ...fixture.project, scope: fixture.otherScope },
      };
    },
    (fixture) => {
      fixture.contextReply = { json: { ...fixture.context, requestToken: "" } };
    },
  ];
  for (const change of gates)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      change(fixture);
      const admission = fixture.response("context", 200);
      await button(current, "Upload mapping").click();
      await finish(admission);
      await expect(heading(current, "Upload form unavailable")).toBeVisible();
      await expect(button(current, "Refresh access")).toBeEnabled();
      expect(fixture.createCount).toBe(0);
      expect(fixture.resumeCount).toBe(0);
    });
});

test("controlled native upload: Unicode scalar, safe filename and native size errors stay local", async ({
  page,
}) => {
  const invalid = [
    { field: "name", value: "\u0085 \u0085" },
    { field: "name", value: "😀".repeat(201) },
    { field: "name", value: "bad\0name" },
    { field: "name", value: "bad\ud800", exactUtf16: true },
    { field: "description", value: "😀".repeat(4001) },
    { field: "description", value: "bad\udc00", exactUtf16: true },
    { field: "filename", value: ".." },
    { field: "filename", value: "folder/file.native.json" },
    { field: "filename", value: "folder\\file.native.json" },
    { field: "filename", value: "bad\u0085name" },
    { field: "filename", value: "😀".repeat(64) },
  ] as const;
  for (const vector of invalid)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      const field =
        vector.field === "name"
          ? nameField(current)
          : vector.field === "description"
            ? descriptionField(current)
            : filenameField(current);
      const assignExactUtf16 = "exactUtf16" in vector;
      const codeUnits = vector.value
        .split("")
        .map((unit) => unit.charCodeAt(0));
      let delivered = false;
      await privateInput(async () => {
        if (!assignExactUtf16) await field.fill(vector.value);
        delivered = await field.evaluate(
          (element, input) => {
            if (
              !(element instanceof HTMLInputElement) &&
              !(element instanceof HTMLTextAreaElement)
            )
              return false;
            if (input.assign) {
              element.value = String.fromCharCode(...input.codeUnits);
              element.dispatchEvent(new Event("input", { bubbles: true }));
            }
            const actual = element.value;
            return (
              actual.length === input.codeUnits.length &&
              input.codeUnits.every(
                (unit, index) => actual.charCodeAt(index) === unit,
              )
            );
          },
          { assign: assignExactUtf16, codeUnits },
        );
      });
      expect(
        delivered,
        "An invalid upload field was altered before validation.",
      ).toBe(true);
      await button(current, "Upload mapping").click();
      await expect(
        current.getByRole("alert", {
          name: "Upload fields need attention",
          exact: true,
        }),
      ).toBeVisible();
      expect((await field.getAttribute("aria-invalid")) === "true").toBe(true);
      expect(fixture.createCount).toBe(0);
    });
  for (const bytes of [Buffer.alloc(0), Buffer.alloc(4_194_305)])
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await privateInput(() =>
        fileField(current).setInputFiles(
          fixture.file("bounded.native.json", bytes),
        ),
      );
      await expect(
        current.getByRole("alert", {
          name: "Upload fields need attention",
          exact: true,
        }),
      ).toBeVisible();
      await expect(button(current, "Upload mapping")).toBeDisabled();
      expect(fixture.createCount).toBe(0);
    });
});

test("controlled native upload: actual Blob bytes and encoded reviewed metadata are sent without text conversion", async ({
  page,
}) => {
  const inputs = [
    {
      name: "\u0085 \uFEFF \u0085",
      acceptedName: "\uFEFF",
      description: "\u0085\uFEFF\n",
      filename: "original.native.json",
    },
    {
      name: " \u0085" + "😀".repeat(200) + "\u0085 ",
      acceptedName: "😀".repeat(200),
      description: "😀".repeat(4000),
      filename: "😀".repeat(63) + "abc",
    },
    {
      name: " \u0085A + % & ? # / = '()!*\u0085 ",
      acceptedName: "A + % & ? # / = '()!*",
      description: "\n<literal> +%&?#/= \uFEFF",
      filename: "reviewed +%&'().native.json",
    },
  ];
  for (const input of inputs)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture, input);
      fixture.postReply = fixture.committed();
      await clickCreate(current, fixture, 200);
      await saved(current);
      await expect(
        current.getByRole("link", { name: "View revision", exact: true }),
      ).toBeVisible();
      wires(fixture, 1);
      expect(fixture.resumeCount).toBe(0);
      expect(await unloadWarning(current)).toBe(false);
    });
  await isolated(page, async (current, fixture) => {
    await ready(current);
    await privateInput(() =>
      nameField(current).fill("Controlled maximum source"),
    );
    fixture.draft("Controlled maximum source");
    await privateInput(() =>
      fileField(current).setInputFiles(
        fixture.file(
          "original.native.json",
          Buffer.alloc(4_194_304, 0x7b),
          "text/xml",
        ),
      ),
    );
    await expect(button(current, "Upload mapping")).toBeEnabled();
    fixture.postReply = fixture.pending();
    await clickCreate(current, fixture, 202);
    await expect(
      heading(current, "Upload is not confirmed saved yet"),
    ).toBeVisible();
    wires(fixture, 1);
  });
});

test("controlled native upload: superseded or failed file preparation cannot publish stale material", async ({
  page,
}) => {
  await isolated(page, async (current, fixture) => {
    await current.addInitScript(() => {
      const original = Blob.prototype.arrayBuffer;
      let first = true;
      Blob.prototype.arrayBuffer = async function () {
        if (first) {
          first = false;
          await new Promise<void>((resolve) =>
            window.addEventListener(
              "controlled-upload-release",
              () => resolve(),
              { once: true },
            ),
          );
        }
        return original.call(this);
      };
    });
    await ready(current);
    await privateInput(() =>
      nameField(current).fill("Controlled replacement source"),
    );
    const filenameAtChange = await fileField(current).evaluateHandle(
      (input) => {
        if (!(input instanceof HTMLInputElement))
          throw new Error("The controlled file input was unavailable.");
        let selectedName: string | null = null;
        let observed = false;
        let synchronous = false;
        const capture = (event: Event) => {
          if (event.target === input)
            selectedName = input.files?.item(0)?.name ?? null;
        };
        const inspect = (event: Event) => {
          if (event.target !== input) return;
          observed = true;
          const filename = document.getElementById("upload-filename");
          synchronous =
            selectedName !== null &&
            filename instanceof HTMLInputElement &&
            filename.value === selectedName;
          selectedName = null;
          document.removeEventListener("input", capture, true);
          document.removeEventListener("change", inspect);
        };
        // Keep the selected name before Angular clears FileList; inspect after its handler.
        document.addEventListener("input", capture, {
          capture: true,
          passive: true,
        });
        document.addEventListener("change", inspect, { passive: true });
        return () => observed && synchronous;
      },
    );
    await privateInput(() => fileField(current).setInputFiles(fixture.file()));
    expect(
      await filenameAtChange.evaluate((read) => read()),
      "The selected filename was not reflected before the file change event completed.",
    ).toBe(true);
    await filenameAtChange.dispose();
    await expect(
      current.getByText("Preparing the selected file…", { exact: false }),
    ).toBeVisible();
    fixture.draft(
      "Controlled replacement source",
      "",
      "replacement.native.json",
    );
    await privateInput(() =>
      fileField(current).setInputFiles(
        fixture.file("replacement.native.json", replacementBytes()),
      ),
    );
    await current.evaluate(() =>
      window.dispatchEvent(new Event("controlled-upload-release")),
    );
    await expect(button(current, "Upload mapping")).toBeEnabled();
    fixture.postReply = fixture.pending();
    await clickCreate(current, fixture, 202);
    await expect(
      heading(current, "Upload is not confirmed saved yet"),
    ).toBeVisible();
    wires(fixture, 1);
  });
  for (const failure of ["read", "hash"] as const)
    await isolated(page, async (current, fixture) => {
      await current.addInitScript((kind) => {
        if (kind === "read")
          Blob.prototype.arrayBuffer = async function () {
            throw new Error("controlled-private-source-detail");
          };
        else
          Object.defineProperty(SubtleCrypto.prototype, "digest", {
            configurable: true,
            value: async function () {
              throw new Error("controlled-private-source-detail");
            },
          });
      }, failure);
      await ready(current);
      await privateInput(() =>
        fileField(current).setInputFiles(fixture.file()),
      );
      await expect(
        current.getByRole("alert", {
          name: "Upload fields need attention",
          exact: true,
        }),
      ).toBeVisible();
      await expect(button(current, "Upload mapping")).toBeDisabled();
      expect(
        await current
          .locator("body")
          .evaluate(
            (element) =>
              !element.textContent?.includes(
                "controlled-private-source-detail",
              ),
          ),
      ).toBe(true);
      expect(fixture.createCount).toBe(0);
    });
  await isolated(page, async (current, fixture) => {
    await current.addInitScript(() => {
      const original = Blob.prototype.arrayBuffer;
      Blob.prototype.arrayBuffer = async function () {
        await new Promise<void>((resolve) =>
          window.addEventListener(
            "controlled-upload-release",
            () => resolve(),
            { once: true },
          ),
        );
        return original.call(this);
      };
    });
    await ready(current);
    await privateInput(() => fileField(current).setInputFiles(fixture.file()));
    await expect(button(current, "Stop waiting")).toBeVisible();
    await button(current, "Stop waiting").click();
    await current.evaluate(() =>
      window.dispatchEvent(new Event("controlled-upload-release")),
    );
    await expect(
      current.getByRole("alert", {
        name: "Upload fields need attention",
        exact: true,
      }),
    ).toBeVisible();
    await expect(button(current, "Upload mapping")).toBeDisabled();
    expect(fixture.createCount).toBe(0);
  });
});

test("controlled native upload: nullable and omitted result members preserve their actual meaning", async ({
  page,
}) => {
  for (const mode of ["omitted", "null", "decodedInvalid"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      const reply = fixture.committed(
        mode === "decodedInvalid" ? "decodedInvalid" : "valid",
      );
      if (mode === "omitted") {
        const json = reply.json as Record<string, unknown>;
        delete json["failure"];
        delete json["rejection"];
      }
      fixture.postReply = reply;
      await clickCreate(current, fixture, 200);
      await saved(current, mode === "decodedInvalid");
      wires(fixture, 1);
    });
  for (const mode of ["omitted", "null"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      fixture.postReply = {
        status: 422,
        json:
          mode === "omitted"
            ? { outcome: "rejected", rejection: "unregisteredBackend" }
            : {
                outcome: "rejected",
                rejection: "unregisteredBackend",
                receipt: null,
                failure: null,
                committedRevisionUrl: null,
              },
      };
      await clickCreate(current, fixture, 422);
      await expect(heading(current, "Upload call refused")).toBeVisible();
      await expect(button(current, "Resend original upload")).toBeEnabled();
      await expect(heading(current, "Mapping saved")).toHaveCount(0);
      expect(await unloadWarning(current)).toBe(true);
      wires(fixture, 1);
    });
  for (const [status, rejection] of [
    [400, "emptyNative"],
    [413, "nativeLimit"],
  ] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      fixture.postReply = {
        status,
        json: { outcome: "rejected", rejection, receipt: null },
      };
      await clickCreate(current, fixture, status);
      await expect(heading(current, "Upload call refused")).toBeVisible();
      await expect(button(current, "Resend original upload")).toBeEnabled();
      expect(await unloadWarning(current)).toBe(true);
      wires(fixture, 1);
    });
  await isolated(page, async (current, fixture) => {
    await ready(current);
    await prepare(current, fixture);
    fixture.postReply = {
      status: 503,
      json: {
        outcome: "retryableFailure",
        receipt: fixture.receipt("staged", 1, "backendUnavailable"),
        failure: "backendUnavailable",
      },
    };
    await clickCreate(current, fixture, 503);
    await expect(
      heading(current, "Upload is not confirmed saved yet"),
    ).toBeVisible();
    await expect(button(current, "Resume upload")).toBeEnabled();
    wires(fixture, 1);
  });
  await isolated(page, async (current, fixture) => {
    await ready(current);
    await prepare(current, fixture);
    fixture.postReply = {
      status: 422,
      json: {
        outcome: "rejected",
        receipt: fixture.receipt("rejected", 1, "evidenceLimit"),
        rejection: "resourceLimit",
        failure: "evidenceLimit",
      },
    };
    await clickCreate(current, fixture, 422);
    await expect(
      heading(current, "Upload ended without a saved revision"),
    ).toBeVisible();
    expect(await unloadWarning(current)).toBe(false);
    wires(fixture, 1);
  });
});

test("controlled native upload: incompatible status, scope, receipt and URL replies never acknowledge a saved mapping", async ({
  page,
}) => {
  const variants: {
    label: string;
    change: (reply: Reply, fixture: ControlledUpload) => void;
  }[] = [
    {
      label: "unexpected success status",
      change: (reply) => {
        reply.status = 201;
      },
    },
    {
      label: "missing committed receipt",
      change: (reply) => {
        delete (reply.json as Record<string, unknown>)["receipt"];
      },
    },
    {
      label: "missing committed URL",
      change: (reply) => {
        delete (reply.json as Record<string, unknown>)["committedRevisionUrl"];
      },
    },
    {
      label: "foreign committed URL",
      change: (reply) => {
        (reply.json as Record<string, unknown>)["committedRevisionUrl"] =
          "https://invalid.example/foreign";
      },
    },
    {
      label: "extra result member",
      change: (reply) => {
        (reply.json as Record<string, unknown>)["extra"] = true;
      },
    },
    {
      label: "foreign project ancestry",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.project.projectId =
          otherProjectId;
      },
    },
    {
      label: "inconsistent future mapping",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.futureRevision.mappingId =
          "30000000-0000-4000-8000-000000000099";
      },
    },
    {
      label: "different saved filename",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.originalFilename =
          "another.native.json";
      },
    },
    {
      label: "append sequence in a create response",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.committedSequence =
          "9007199254740993";
      },
    },
    {
      label: "append head conflict in a create response",
      change: (reply, fixture) => {
        reply.status = 409;
        reply.json = {
          outcome: "headConflict",
          receipt: fixture.receipt("headConflict"),
        };
      },
    },
    {
      label: "claim budget exceeded",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.claimCount = 9;
      },
    },
    {
      label: "committed receipt without completion",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.completedAtUtc = null;
      },
    },
    {
      label: "validity contradicts the outcome",
      change: (reply) => {
        (reply.json as { receipt: Receipt }).receipt.committedAdmission =
          "decodedInvalid";
      },
    },
    {
      label: "pending receipt with missing Location",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.pending());
        delete reply.headers;
      },
    },
    {
      label: "pending receipt with foreign Location",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.pending());
        reply.headers = { Location: "/api/v1/foreign" };
      },
    },
    {
      label: "source requirement without its failure",
      change: (reply, fixture) => {
        Object.assign(reply, fixture.needsSource());
        (reply.json as Record<string, unknown>)["failure"] = null;
      },
    },
    {
      label: "unsupported rejection without terminal receipt",
      change: (reply) => {
        reply.status = 422;
        reply.json = {
          outcome: "rejected",
          rejection: "unsupportedArtifact",
          receipt: null,
        };
      },
    },
    {
      label: "closed error body extension",
      change: (reply) => {
        reply.status = 500;
        reply.json = { code: "internalFailure", extra: true };
      },
    },
  ];
  for (const variant of variants)
    await test.step(variant.label, () =>
      isolated(page, async (current, fixture) => {
        await ready(current);
        await prepare(current, fixture);
        const reply = fixture.committed();
        variant.change(reply, fixture);
        fixture.postReply = reply;
        await clickCreate(current, fixture, reply.status!);
        await unknown(current);
        await expect(button(current, "Resend original upload")).toBeEnabled();
        await expect(heading(current, "Mapping saved")).toHaveCount(0);
        await expect(button(current, "Check upload status")).toHaveCount(0);
        expect(fixture.revisionReads).toBe(0);
        expect(await unloadWarning(current)).toBe(true);
        wires(fixture, 1);
      }),
    );
});

test("controlled native upload: known pending status is GET-only and missing source resends the original bytes", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page);
    await prepare(page, fixture);
    fixture.postReply = fixture.pending("staged");
    await clickCreate(page, fixture, 202);
    await expect(
      heading(page, "Upload is not confirmed saved yet"),
    ).toBeVisible();
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    expect(fixture.revisionReads).toBe(0);
    expect(await unloadWarning(page)).toBe(true);
    expect(
      await nameField(page).evaluate(
        (element) => (element as HTMLInputElement).readOnly,
      ),
    ).toBe(true);
    await expect(fileField(page)).toBeDisabled();
    fixture.receiptReply = { json: fixture.receipt("staged") };
    const checked = fixture.response("receipt", 200);
    await button(page, "Check upload status").click();
    await finish(checked);
    await expect(button(page, "Resume upload")).toBeEnabled();
    expect(fixture.receiptReads).toBe(1);
    expect(fixture.resumeCount).toBe(0);
    wires(fixture, 1);
    fixture.resumeReply = fixture.needsSource(2);
    const resumed = fixture.response("resume", 409);
    await button(page, "Resume upload").click();
    await finish(resumed);
    await expect(button(page, "Resend original file")).toBeEnabled();
    expect(fixture.resumeValid).toBe(true);
    expect(fixture.resumeCount).toBe(1);
    fixture.postReply = fixture.committed("valid", 2);
    await clickCreate(page, fixture, 200, "Resend original file");
    await saved(page);
    wires(fixture, 2);
    sameOriginal(fixture);
    expect(fixture.receiptReads).toBe(1);
    expect(await unloadWarning(page)).toBe(false);
  });
});

test("controlled native upload: null-receipt recovery replies retain the learned operation and earlier uncertainty", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page);
    await prepare(page, fixture);
    fixture.postReply = fixture.pending("staged", 2);
    await clickCreate(page, fixture, 202);
    await expect(button(page, "Resume upload")).toBeEnabled();
    const failed = page.waitForEvent(
      "requestfailed",
      (request) =>
        request.method() === "POST" &&
        new URL(request.url()).pathname === fixture.operationPath + "/resume",
    );
    fixture.resumeReply = { abort: true };
    await button(page, "Resume upload").click();
    await failed;
    await unknown(page);
    fixture.resumeReply = codeReply(503, "ingestionAdmissionBusy");
    const busy = fixture.response("resume", 503);
    await button(page, "Resume upload").click();
    await finish(busy);
    await expect(page.getByRole("alert")).toHaveText(
      "Upload admission is busy. No automatic retry was made.",
    );
    await unknown(page);
    fixture.resumeReply = {
      status: 409,
      json: {
        outcome: "claimLost",
        receipt: null,
        failure: null,
        rejection: null,
        committedRevisionUrl: null,
      },
    };
    const lost = fixture.response("resume", 409);
    await button(page, "Resume upload").click();
    await finish(lost);
    await expect(heading(page, "Upload claim lost")).toBeVisible();
    await unknown(page);
    await expect(button(page, "Check upload status")).toBeEnabled();
    await expect(button(page, "Resend original upload")).toHaveCount(0);
    fixture.resumeReply = {
      status: 404,
      json: { outcome: "notAuthorizedOrNotFound", receipt: null },
    };
    const hidden = fixture.response("resume", 404);
    await button(page, "Resume upload").click();
    await finish(hidden);
    await expect(page.getByRole("alert")).toHaveText(
      "This project or upload is unavailable to your current identity. Private upload information is hidden.",
    );
    await unknown(page);
    await expect(button(page, "Check upload status")).toHaveCount(0);
    await refresh(page, fixture);
    await expect(button(page, "Check upload status")).toBeEnabled();
    fixture.receiptReply = { json: fixture.receipt("committed", 2) };
    const checked = fixture.response("receipt", 200);
    await button(page, "Check upload status").click();
    await finish(checked);
    await saved(page);
    await expect(heading(page, "Upload outcome is unknown")).toHaveCount(0);
    expect(fixture.resumeCount).toBe(4);
    expect(fixture.resumeValid).toBe(true);
    expect(fixture.receiptReads).toBe(1);
    wires(fixture, 1);
    expect(await unloadWarning(page)).toBe(false);
  });
  await isolated(page, async (current, fixture) => {
    await ready(current);
    await prepare(current, fixture);
    await loseOriginal(current, fixture);
    fixture.postReply = fixture.needsSource(2);
    await clickCreate(current, fixture, 409, "Resend original upload");
    await expect(button(current, "Resend original file")).toBeEnabled();
    await expect(
      heading(current, "Upload is not confirmed saved yet"),
    ).toBeVisible();
    await unknown(current);
    wires(fixture, 2);
    sameOriginal(fixture);
    fixture.postReply = {
      status: 409,
      json: {
        outcome: "idempotencyConflict",
        receipt: null,
        rejection: null,
        failure: null,
        committedRevisionUrl: null,
      },
    };
    await clickCreate(current, fixture, 409, "Resend original file");
    await expect(heading(current, "Original upload conflicts")).toBeVisible();
    await expect(
      heading(current, "Upload is not confirmed saved yet"),
    ).toBeVisible();
    await expect(button(current, "Check upload status")).toBeEnabled();
    await expect(button(current, "Resume upload")).toBeEnabled();
    await expect(button(current, "Resend original file")).toBeEnabled();
    await unknown(current);
    await expect(heading(current, "Mapping saved")).toHaveCount(0);
    await expect(
      current.getByRole("link", { name: "View revision", exact: true }),
    ).toHaveCount(0);
    expect(fixture.revisionReads).toBe(0);
    expect(await unloadWarning(current)).toBe(true);
    wires(fixture, 3);
    sameOriginal(fixture);
    fixture.receiptReply = {
      json: fixture.receipt("staged", 2, "needsSource"),
    };
    const checked = fixture.response("receipt", 200);
    await button(current, "Check upload status").click();
    await finish(checked);
    await expect(heading(current, "Original upload conflicts")).toHaveCount(0);
    await expect(button(current, "Resend original file")).toBeEnabled();
    await unknown(current);
    await clickCreate(current, fixture, 409, "Resend original file");
    await expect(heading(current, "Original upload conflicts")).toBeVisible();
    await expect(
      heading(current, "Upload is not confirmed saved yet"),
    ).toBeVisible();
    await unknown(current);
    await expect(heading(current, "Mapping saved")).toHaveCount(0);
    expect(fixture.receiptReads).toBe(1);
    expect(fixture.resumeCount).toBe(0);
    expect(fixture.revisionReads).toBe(0);
    expect(await unloadWarning(current)).toBe(true);
    wires(fixture, 4);
    sameOriginal(fixture);
  });
});

test("controlled native upload: only exact terminal own proof resolves uncertainty and enables a fresh file", async ({
  page,
}) => {
  for (const phase of ["committed", "rejected", "attemptExhausted"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      await loseOriginal(current, fixture);
      fixture.postReply = fixture.pending("staged", 2);
      await clickCreate(current, fixture, 202, "Resend original upload");
      await expect(button(current, "Check upload status")).toBeEnabled();
      await unknown(current);
      fixture.receiptReply = {
        json: fixture.receipt(phase, phase === "attemptExhausted" ? 8 : 2),
      };
      const proof = fixture.response("receipt", 200);
      await button(current, "Check upload status").click();
      await finish(proof);
      if (phase === "committed") await saved(current);
      else
        await expect(
          heading(current, "Upload ended without a saved revision"),
        ).toBeVisible();
      await expect(heading(current, "Upload outcome is unknown")).toHaveCount(
        0,
      );
      await expect(button(current, "Resend original upload")).toHaveCount(0);
      await expect(button(current, "Resend original file")).toHaveCount(0);
      expect(await unloadWarning(current)).toBe(false);
      fixture.resumeReply =
        phase === "committed"
          ? fixture.committed("valid", 2)
          : phase === "rejected"
            ? fixture.rejected("nativeDecode", 2)
            : {
                status: 409,
                json: {
                  outcome: "attemptExhausted",
                  receipt: fixture.receipt("attemptExhausted", 8),
                },
              };
      const replay = fixture.response(
        "resume",
        phase === "committed" ? 200 : phase === "rejected" ? 422 : 409,
      );
      await button(current, "Replay upload result").click();
      await finish(replay);
      await expect(button(current, "Upload another mapping")).toBeEnabled();
      expect(fixture.resumeCount).toBe(1);
      expect(fixture.resumeValid).toBe(true);
      wires(fixture, 2);
      await button(current, "Upload another mapping").click();
      await expect(fileField(current)).toBeEnabled();
      await expect(button(current, "Upload mapping")).toBeDisabled();
      expect(await fieldMatches(current, "name", "")).toBe(true);
      expect(await fieldMatches(current, "filename", "")).toBe(true);
      await expect(button(current, "Replay upload result")).toHaveCount(0);
      expect(await unloadWarning(current)).toBe(false);
      fixture.nextOperation();
      fixture.draft(
        "Controlled distinct request",
        "",
        "replacement.native.json",
      );
      await privateInput(() =>
        nameField(current).fill("Controlled distinct request"),
      );
      await privateInput(() =>
        fileField(current).setInputFiles(
          fixture.file("replacement.native.json", replacementBytes()),
        ),
      );
      await expect(button(current, "Upload mapping")).toBeEnabled();
      fixture.postReply = fixture.pending();
      await clickCreate(current, fixture, 202);
      await expect(button(current, "Check upload status")).toBeEnabled();
      wires(fixture, 3);
      expect(fixture.lastKeyDiffers).toBe(true);
    });
});

test("controlled native upload: lost acknowledgement recovers only by explicit identical original sends", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page);
    await prepare(page, fixture, {
      name: " \u0085Original + % mapping\u0085 ",
      acceptedName: "Original + % mapping",
      description: "\uFEFF\nExact +%& text",
      filename: "original +%.native.json",
    });
    await loseOriginal(page, fixture);
    expect(await unloadWarning(page)).toBe(true);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.revisionReads).toBe(0);
    const refusals = [
      [
        400,
        "invalidRequest",
        "The service refused this upload request as invalid.",
      ],
      [413, "bodyLimit", "The service refused the upload body size."],
      [
        415,
        "unsupportedMediaType",
        "The service did not accept this upload format.",
      ],
      [
        503,
        "ingestionAdmissionBusy",
        "Upload admission is busy. No automatic retry was made.",
      ],
      [
        500,
        "persistenceFailure",
        "The service could not confirm this upload call.",
      ],
      [
        404,
        "notFound",
        "This project or upload is unavailable to your current identity. Private upload information is hidden.",
      ],
    ] as const;
    for (const [index, [status, code, message]] of refusals.entries()) {
      fixture.postReply = codeReply(status, code);
      await clickCreate(page, fixture, status, "Resend original upload");
      await expect(page.getByRole("alert")).toHaveText(message);
      await unknown(page);
      if (status === 404) await refresh(page, fixture);
      await expect(button(page, "Resend original upload")).toBeEnabled();
      wires(fixture, index + 2);
      sameOriginal(fixture);
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
    }
    fixture.postReply = fixture.committed();
    await clickCreate(page, fixture, 200, "Resend original upload");
    await saved(page);
    await expect(heading(page, "Upload outcome is unknown")).toHaveCount(0);
    wires(fixture, refusals.length + 2);
    sameOriginal(fixture);
    expect(await unloadWarning(page)).toBe(false);
  });
});

test("controlled native upload: learned operation identity and terminal facts cannot be replaced by later reads", async ({
  page,
}) => {
  const changes: ((receipt: Receipt) => void)[] = [
    (receipt) => {
      receipt.operationId = "50000000-0000-4000-8000-000000000099";
    },
    (receipt) => {
      receipt.futureMapping.mappingId = "30000000-0000-4000-8000-000000000099";
      receipt.futureRevision.mappingId = receipt.futureMapping.mappingId;
    },
    (receipt) => {
      receipt.futureRevision.revisionId =
        "40000000-0000-4000-8000-000000000099";
    },
    (receipt) => {
      receipt.reservedAtUtc = "2026-10-15T12:00:00.1234568Z";
    },
    (receipt) => {
      receipt.claimCount = 1;
    },
  ];
  for (const change of changes)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      fixture.postReply = fixture.pending("staged", 2);
      await clickCreate(current, fixture, 202);
      await expect(button(current, "Check upload status")).toBeEnabled();
      const reply = fixture.receipt("staged", 2);
      change(reply);
      fixture.receiptReply = { json: reply };
      const checked = fixture.response("receipt", 200);
      await button(current, "Check upload status").click();
      await finish(checked);
      await expect(current.getByRole("alert")).toHaveText(
        "The reply could not be verified as the original upload operation.",
      );
      await expect(button(current, "Check upload status")).toBeEnabled();
      await expect(button(current, "Resume upload")).toBeEnabled();
      await expect(heading(current, "Mapping saved")).toHaveCount(0);
      expect(await unloadWarning(current)).toBe(true);
      wires(fixture, 1);
    });
  await isolated(page, async (current, fixture) => {
    await ready(current);
    await prepare(current, fixture);
    fixture.postReply = fixture.committed();
    await clickCreate(current, fixture, 200);
    await saved(current);
    fixture.receiptReply = { json: fixture.receipt("committed", 2) };
    const checked = fixture.response("receipt", 200);
    await button(current, "Check upload status").click();
    await finish(checked);
    await expect(current.getByRole("alert")).toHaveText(
      "The reply could not be verified as the original upload operation.",
    );
    await saved(current);
    expect(await unloadWarning(current)).toBe(false);
    wires(fixture, 1);
  });
});

test("controlled native upload: saved acknowledgement survives read failure and view-only retry never writes", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page);
    await prepare(page, fixture);
    fixture.revisionReply = codeReply(503, "unavailable");
    fixture.postReply = fixture.committed();
    await clickCreate(page, fixture, 200);
    await saved(page);
    await expect(
      page.getByRole("link", { name: "View revision", exact: true }),
    ).toHaveCount(0);
    expect(await unloadWarning(page)).toBe(false);
    fixture.context.features.createMappings = false;
    fixture.project.access.role = "viewer";
    fixture.project.access.actions.createMappings = false;
    fixture.revisionReply = null;
    const read = fixture.response("revision", 200);
    await button(page, "Retry revision access").click();
    await finish(read);
    await saved(page);
    await expect(
      page.getByRole("link", { name: "View revision", exact: true }),
    ).toBeVisible();
    await expect(button(page, "Replay upload result")).toHaveCount(0);
    await expect(button(page, "Upload another mapping")).toHaveCount(0);
    wires(fixture, 1);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    fixture.contextReply = codeReply(503, "unavailable");
    await refresh(page, fixture, 503);
    await saved(page);
    await expect(
      page.getByRole("link", { name: "View revision", exact: true }),
    ).toHaveCount(0);
    fixture.contextReply = null;
    fixture.context.actorPrincipalId = otherActor;
    await refresh(page, fixture);
    await expect(
      heading(page, "An original upload is retained privately"),
    ).toBeVisible();
    await expect(heading(page, "Mapping saved")).toHaveCount(0);
    fixture.contextReply = codeReply(503, "unavailable");
    await refresh(page, fixture, 503);
    await expect(heading(page, "Mapping saved")).toHaveCount(0);
    await expect(button(page, "Retry revision access")).toHaveCount(0);
    fixture.contextReply = null;
    fixture.context.actorPrincipalId = synthetic.actor;
    await refresh(page, fixture);
    await saved(page);
    await expect(
      page.getByRole("link", { name: "View revision", exact: true }),
    ).toBeVisible();
    wires(fixture, 1);
  });
  await isolated(page, async (current, fixture) => {
    await ready(current);
    await prepare(current, fixture);
    fixture.revisionReply = {
      json: {
        ...fixture.revision(),
        nativeDigest: { algorithm: "sha256", value: "0".repeat(64) },
      },
    };
    fixture.postReply = fixture.committed();
    await clickCreate(current, fixture, 200);
    await saved(current);
    await expect(
      current.getByRole("link", { name: "View revision", exact: true }),
    ).toHaveCount(0);
    fixture.revisionReply = null;
    const read = fixture.response("revision", 200);
    await button(current, "Retry revision access").click();
    await finish(read);
    await saved(current);
    await expect(
      current.getByRole("link", { name: "View revision", exact: true }),
    ).toBeVisible();
    wires(fixture, 1);
    expect(fixture.resumeCount).toBe(0);
    expect(await unloadWarning(current)).toBe(false);
  });
});

test("controlled native upload: canceled late replies cannot cross actor or full project scope", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page);
    await prepare(page, fixture);
    const held = fixture.hold("create", fixture.committed());
    await button(page, "Upload mapping").click();
    await expect.poll(held.entered).toBe(true);
    const failed = fixture.failedCreate();
    await page
      .getByRole("link", { name: "Back to project", exact: true })
      .click();
    await expect(
      heading(page, "Leave and keep upload recovery?"),
    ).toBeVisible();
    await button(page, "Keep recovery information and leave").click();
    await failed;
    await expect(heading(page, "No mappings")).toBeVisible();
    fixture.context.actorPrincipalId = otherActor;
    await page.goBack();
    await expect(
      heading(page, "An original upload is retained privately"),
    ).toBeVisible();
    await expect(nameField(page)).toHaveCount(0);
    await expect(button(page, "Resend original upload")).toHaveCount(0);
    held.release();
    expect(held.canceled()).toBe(true);
    await expect(heading(page, "Mapping saved")).toHaveCount(0);
    fixture.contextReply = codeReply(503, "unavailable");
    await refresh(page, fixture, 503);
    await expect(heading(page, "Mapping saved")).toHaveCount(0);
    fixture.contextReply = null;
    fixture.context.actorPrincipalId = synthetic.actor;
    await refresh(page, fixture);
    await unknown(page);
    await expect(button(page, "Resend original upload")).toBeEnabled();
    await page
      .getByRole("link", { name: "Back to project", exact: true })
      .click();
    await button(page, "Keep recovery information and leave").click();
    await expect(heading(page, "No mappings")).toBeVisible();
    await page.evaluate((path) => {
      history.pushState(null, "", path);
      window.dispatchEvent(new PopStateEvent("popstate"));
    }, uploadPath(fixture.otherScope));
    await expect(
      heading(page, "An original upload is retained privately"),
    ).toBeVisible();
    await expect(nameField(page)).toHaveCount(0);
    await expect(button(page, "Resend original upload")).toHaveCount(0);
    expect(fixture.createCount).toBe(1);
    await page.goBack();
    await expect(
      heading(page, "Leave and keep upload recovery?"),
    ).toBeVisible();
    await button(page, "Keep recovery information and leave").click();
    await expect(heading(page, "No mappings")).toBeVisible();
    await page.goBack();
    await unknown(page);
    await expect(button(page, "Resend original upload")).toBeEnabled();
    fixture.postReply = fixture.committed();
    await clickCreate(page, fixture, 200, "Resend original upload");
    await saved(page);
    wires(fixture, 2);
    sameOriginal(fixture);
  });
});

test("controlled native upload: focus preserves raw drafts and duplicate submit events send once", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    const input = {
      name: " \u0085A raw draft\u0085 ",
      acceptedName: "A raw draft",
      description: "\uFEFF\nExact draft",
      filename: "reviewed.native.json",
    };
    await ready(page);
    await prepare(page, fixture, input);
    const focused = fixture.response("context", 200);
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await finish(focused);
    await expect(fileField(page)).toBeEnabled();
    expect(await fieldMatches(page, "name", input.name)).toBe(true);
    expect(await fieldMatches(page, "description", input.description)).toBe(
      true,
    );
    expect(await fieldMatches(page, "filename", input.filename)).toBe(true);
    expect(fixture.createCount).toBe(0);
    expect(fixture.receiptReads).toBe(0);
    expect(fixture.resumeCount).toBe(0);
    const held = fixture.hold("create", fixture.pending());
    const response = fixture.response("create", 202);
    await button(page, "Upload mapping").evaluate((element) => {
      (element as HTMLButtonElement).click();
      (element as HTMLButtonElement).click();
    });
    await expect.poll(held.entered).toBe(true);
    wires(fixture, 1);
    await page.evaluate(() => {
      window.dispatchEvent(new Event("focus"));
      document
        .querySelector(".upload-form")
        ?.dispatchEvent(
          new Event("submit", { bubbles: true, cancelable: true }),
        );
    });
    await expect(button(page, "Refresh access")).toBeDisabled();
    wires(fixture, 1);
    held.release();
    await finish(response);
    await expect(button(page, "Check upload status")).toBeEnabled();
    wires(fixture, 1);
    expect(fixture.resumeCount).toBe(0);
  });
});

test("controlled native upload: bounded timeout and stop waiting retain original recovery without automatic sends", async ({
  page,
}) => {
  for (const action of ["timeout", "stop"] as const)
    await isolated(page, async (current, fixture) => {
      await ready(current);
      await prepare(current, fixture);
      if (action === "timeout") await current.clock.install();
      const held = fixture.hold("create", fixture.committed());
      await button(current, "Upload mapping").click();
      await expect.poll(held.entered).toBe(true);
      const failed = fixture.failedCreate();
      if (action === "timeout") await current.clock.fastForward(30001);
      else await button(current, "Stop waiting").click();
      await failed;
      await unknown(current);
      await expect(button(current, "Resend original upload")).toBeEnabled();
      expect(await unloadWarning(current)).toBe(true);
      wires(fixture, 1);
      expect(fixture.receiptReads).toBe(0);
      expect(fixture.resumeCount).toBe(0);
      held.release();
      fixture.postReply = fixture.committed();
      await clickCreate(current, fixture, 200, "Resend original upload");
      await saved(current);
      wires(fixture, 2);
      sameOriginal(fixture);
      expect(await unloadWarning(current)).toBe(false);
    });
});

test("controlled native upload: leave and explicit abandonment distinguish unsent draft from uncertain server work", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await ready(page);
    await prepare(page, fixture);
    expect(await unloadWarning(page)).toBe(true);
    await page
      .getByRole("link", { name: "Back to project", exact: true })
      .click();
    await expect(heading(page, "Leave this upload draft?")).toBeVisible();
    await expect(heading(page, "Leave this upload draft?")).toBeFocused();
    await button(page, "Stay").click();
    await expect(heading(page, "Upload mapping")).toBeFocused();
    expect(await fieldMatches(page, "name", "Controlled native mapping")).toBe(
      true,
    );
    expect(fixture.createCount).toBe(0);
    await page
      .getByRole("link", { name: "Back to project", exact: true })
      .click();
    await button(page, "Discard draft and leave").click();
    await expect(heading(page, "No mappings")).toBeVisible();
    expect(await unloadWarning(page)).toBe(false);
    await page.goBack();
    await expect(fileField(page)).toBeEnabled();
    await expect(button(page, "Upload mapping")).toBeDisabled();
    expect(await fieldMatches(page, "name", "")).toBe(true);
    await prepare(page, fixture);
    await loseOriginal(page, fixture);
    await button(page, "Abandon original upload").click();
    await expect(
      heading(page, "Forget this upload's recovery information?"),
    ).toBeVisible();
    await expect(button(page, "Abandon and start a new upload")).toBeDisabled();
    await button(page, "Keep original upload").click();
    await unknown(page);
    wires(fixture, 1);
    await button(page, "Abandon original upload").click();
    await page
      .getByRole("checkbox", {
        name: "I understand a mapping may already exist.",
        exact: true,
      })
      .check();
    await button(page, "Abandon and start a new upload").click();
    await expect(fileField(page)).toBeEnabled();
    await expect(button(page, "Upload mapping")).toBeDisabled();
    await expect(button(page, "Resend original upload")).toHaveCount(0);
    expect(await unloadWarning(page)).toBe(false);
    wires(fixture, 1);
    fixture.nextOperation();
    await prepare(page, fixture);
    fixture.postReply = fixture.pending();
    await clickCreate(page, fixture, 202);
    await expect(button(page, "Check upload status")).toBeEnabled();
    wires(fixture, 2);
    expect(fixture.lastKeyDiffers).toBe(true);
  });
});

test("controlled native upload: keyboard mobile flow renders literal filenames and keeps recovery out of browser persistence", async ({
  page,
}) => {
  await controlled(page, async (fixture) => {
    await page.addInitScript(() => {
      const counts = { storageWrites: 0, databaseOpens: 0, cacheOpens: 0 };
      Object.defineProperty(window, "__uploadBoundaryPrivacy", {
        value: counts,
      });
      Object.defineProperty(Storage.prototype, "setItem", {
        configurable: true,
        value: function () {
          ++counts.storageWrites;
        },
      });
      Object.defineProperty(IDBFactory.prototype, "open", {
        configurable: true,
        value: function () {
          ++counts.databaseOpens;
          throw new Error("Controlled persistence is unavailable.");
        },
      });
      if (typeof CacheStorage !== "undefined")
        Object.defineProperty(CacheStorage.prototype, "open", {
          configurable: true,
          value: function () {
            ++counts.cacheOpens;
            return Promise.reject(
              new Error("Controlled persistence is unavailable."),
            );
          },
        });
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await ready(page);
    await expect(nameField(page)).toBeFocused();
    await nameField(page).press("Tab");
    await expect(descriptionField(page)).toBeFocused();
    await descriptionField(page).press("Tab");
    await expect(fileField(page)).toBeFocused();
    const chooser = page.waitForEvent("filechooser");
    await fileField(page).press("Enter");
    const filename = "<img onerror=1>.native.json";
    const selected = await chooser;
    await privateInput(() => selected.setFiles(fixture.file(filename)));
    await expect(button(page, "Upload mapping")).toBeEnabled();
    await nameField(page).focus();
    await nameField(page).press("Enter");
    await expect(
      page.getByRole("alert", {
        name: "Upload fields need attention",
        exact: true,
      }),
    ).toBeFocused();
    await button(page, "Enter a mapping name.").press("Enter");
    await expect(nameField(page)).toBeFocused();
    const literal =
      "<img src=x onerror=document.documentElement.dataset.uploadMarkup=1>";
    fixture.draft(literal, "\uFEFF\nLiteral description", filename);
    await privateInput(() => nameField(page).fill(literal));
    await privateInput(() =>
      descriptionField(page).fill("\uFEFF\nLiteral description"),
    );
    fixture.postReply = fixture.committed();
    const response = fixture.response("create", 200);
    await filenameField(page).press("Enter");
    await finish(response);
    await saved(page);
    await expect(heading(page, "Mapping saved")).toBeFocused();
    const literalMatches = await page
      .locator('section[aria-labelledby="upload-saved"] .upload-literal')
      .evaluate(
        (element, expected) => element.textContent === expected,
        filename,
      );
    expect(literalMatches).toBe(true);
    expect(await page.locator(".native-upload img").count()).toBe(0);
    expect(
      (await page.locator("html").getAttribute("data-upload-markup")) === null,
    ).toBe(true);
    const bounds = await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth + 1,
    );
    expect(bounds).toBe(true);
    const persistence = await page.evaluate(() => {
      const counts = (
        window as unknown as {
          __uploadBoundaryPrivacy: {
            storageWrites: number;
            databaseOpens: number;
            cacheOpens: number;
          };
        }
      ).__uploadBoundaryPrivacy;
      return {
        ...counts,
        local: localStorage.length,
        session: sessionStorage.length,
        noQuery: location.search === "",
      };
    });
    expect(persistence.storageWrites).toBe(0);
    expect(persistence.databaseOpens).toBe(0);
    expect(persistence.cacheOpens).toBe(0);
    expect(persistence.local).toBe(0);
    expect(persistence.session).toBe(0);
    expect(persistence.noQuery).toBe(true);
    wires(fixture, 1);
    expect(fixture.resumeCount).toBe(0);
  });
});
