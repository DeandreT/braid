import type {
  APIResponse,
  Page,
  Request,
  Route,
  Response as BrowserResponse,
} from "@playwright/test";
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { dirname, isAbsolute, join } from "node:path";
import { isDeepStrictEqual } from "node:util";
import type { components } from "../src/api/generated";
import { isSessionStatusRequest } from "./browser-session-status.fixture";
import {
  test,
  expect,
  canonicalId,
  requireAppend,
  appendPath,
  catalogApi,
  mappingPath,
  revisionPath,
  projectPath,
  type NativeRevisionAppendOwner,
  type AppendCounts,
  type Sample,
  type Profile,
  type Receipt,
  type Revision,
  type AppendResult,
} from "./native-revision-append.fixture";

// A browser session never receives the bearer arm.
type Context = Exclude<
  components["schemas"]["CatalogContextV1"],
  { mode: "bearer" }
>;
type Mapping = components["schemas"]["CatalogMappingV1"];
type FixedSample = {
  readonly sample: Sample;
  readonly profile: "administrator" | "publisher";
  readonly filename: string;
  readonly source: string;
  readonly byteCount: number;
  readonly digest: string;
  readonly admission: "valid" | "decodedInvalid";
  readonly conflict: boolean;
};
const valid = {
  source: "versioned-exact.json",
  byteCount: 1566,
  digest: "b9ac8045a2b3fba94520043cdb5f1a0be7dde34aa2ecd577fafce9168dd440e7",
  admission: "valid" as const,
};
const fixed: Readonly<Record<Sample, FixedSample>> = {
  valid: {
    sample: "valid",
    profile: "publisher",
    filename: "append + % 精確😀.native.json",
    ...valid,
    conflict: false,
  },
  decodedInvalid: {
    sample: "decodedInvalid",
    profile: "administrator",
    filename: "append-invalid.native.json",
    source: "invalid-function.json",
    byteCount: 988,
    digest: "646595d5def08a5c9f2a1e009d811361730eaa6024326b915ae14e97016fc38e",
    admission: "decodedInvalid",
    conflict: false,
  },
  winner: {
    sample: "winner",
    profile: "publisher",
    filename: "winner.native.json",
    ...valid,
    conflict: false,
  },
  loser: {
    sample: "loser",
    profile: "publisher",
    filename: "loser.native.json",
    ...valid,
    conflict: true,
  },
  renewed: {
    sample: "renewed",
    profile: "publisher",
    filename: "reviewed-new.native.json",
    ...valid,
    conflict: false,
  },
  lost: {
    sample: "lost",
    profile: "publisher",
    filename: "lost-ack.native.json",
    ...valid,
    conflict: false,
  },
  restored: {
    sample: "restored",
    profile: "publisher",
    filename: "restored-original.native.json",
    ...valid,
    conflict: false,
  },
};
const button = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const heading = (page: Page, name: string) =>
  page.getByRole("heading", { name, exact: true });
const sha = (bytes: Buffer) => createHash("sha256").update(bytes).digest("hex");
/** One genuine HTTP entity, owned by one wire and keyed only by its browser Request. */
class MediatedAppendRead {
  readonly deadline = performance.now() + 8000;
  readonly ready: Promise<Buffer>;
  private resolve!: (bytes: Buffer) => void;
  private reject!: (error: Error) => void;
  private readonly timer: ReturnType<typeof setTimeout>;
  private bytes: Buffer | null = null;
  private status: number | null = null;
  private fulfilled = false;
  private responseSeen = false;
  private failure: Error | null = null;
  private ownedTeardown = false;
  constructor(
    readonly request: Request,
    private readonly report: (error: Error) => void,
  ) {
    this.ready = new Promise<Buffer>((resolve, reject) => {
      this.resolve = resolve;
      this.reject = reject;
    });
    void this.ready.catch(() => undefined);
    this.timer = setTimeout(
      () => this.fail("actual GET exceeded the existing read budget"),
      8000,
    );
  }
  assertLive(): void {
    if (!this.failure && performance.now() >= this.deadline)
      this.fail("actual GET exceeded the existing read budget");
    if (!this.failure && this.request.failure())
      this.fail("actual GET request failed");
    if (this.failure) throw this.failure;
  }
  remaining(): number {
    this.assertLive();
    return Math.max(1, Math.floor(this.deadline - performance.now()));
  }
  fail(category: string): void {
    if (!this.failure) {
      this.failure = new Error(
        "Native revision append browser fixture: " + category,
      );
      if (!this.ownedTeardown) this.report(this.failure);
    }
    clearTimeout(this.timer);
    this.reject(this.failure);
  }
  candidate(bytes: Buffer, status: number): void {
    this.assertLive();
    this.bytes = bytes;
    this.status = status;
  }
  response(reply: BrowserResponse): void {
    try {
      this.assertLive();
      requireAppend(
        reply.request() === this.request &&
          reply.url() === this.request.url() &&
          reply.status() === this.status,
        "actual GET browser response identity/status differs",
      );
      this.responseSeen = true;
      this.complete();
    } catch {
      this.fail("actual GET browser response could not be admitted");
    }
  }
  delivered(): void {
    this.assertLive();
    this.fulfilled = true;
    this.complete();
  }
  private complete(): void {
    if (this.failure || !this.fulfilled || !this.responseSeen || !this.bytes)
      return;
    clearTimeout(this.timer);
    this.resolve(this.bytes);
  }
  isOwnedTeardownFailure(error: unknown): boolean {
    return this.ownedTeardown && error === this.failure;
  }
  close(): void {
    if (!this.failure && (!this.fulfilled || !this.responseSeen)) {
      if (performance.now() >= this.deadline)
        this.fail("actual GET exceeded the existing read budget");
      else if (this.request.failure()) this.fail("actual GET request failed");
      else {
        this.ownedTeardown = true;
        this.fail("actual GET closed before delivery");
      }
    }
    clearTimeout(this.timer);
  }
  async body(reply: BrowserResponse): Promise<Buffer> {
    const bytes = await this.ready;
    if (!this.failure && this.request.failure())
      this.fail("actual GET delivery failed");
    if (this.failure) throw this.failure;
    requireAppend(
      reply.request() === this.request &&
        reply.status() === this.status &&
        this.fulfilled &&
        this.responseSeen,
      "actual GET delivery is incomplete",
    );
    return bytes;
  }
  diagnostic() {
    const failure = this.request.failure();
    return {
      status: this.status,
      candidatePresent: this.bytes !== null,
      fulfilled: this.fulfilled,
      responseSeen: this.responseSeen,
      failed: this.failure !== null,
      ownedTeardown: this.ownedTeardown,
      requestFailure:
        failure === null
          ? null
          : failure.errorText === "net::ERR_ABORTED"
            ? "aborted"
            : failure.errorText === "net::ERR_FAILED"
              ? "failed"
              : "other",
    };
  }
}
const actualGetReads = new WeakMap<Request, MediatedAppendRead>();
async function actualBody(reply: BrowserResponse): Promise<Buffer> {
  if (reply.request().method() === "GET") {
    const captured = actualGetReads.get(reply.request());
    requireAppend(captured, "actual GET response has no exact request capture");
    return captured.body(reply);
  }
  return reply.body();
}
async function sampleBytes(sample: FixedSample): Promise<Buffer> {
  let bytes: Buffer;
  try {
    bytes = await readFile(
      new URL(
        `../../../engines/ferrule-host/tests/fixtures/${sample.source}`,
        import.meta.url,
      ),
    );
  } catch {
    throw new Error(
      "Native revision append browser fixture: exact source sample could not be read",
    );
  }
  requireAppend(
    bytes.length === sample.byteCount && sha(bytes) === sample.digest,
    "fixed sample bytes differ",
  );
  return bytes;
}
async function bodyDone(reply: BrowserResponse): Promise<void> {
  let nonempty: boolean;
  try {
    nonempty = (await actualBody(reply)).length > 0;
  } catch {
    throw new Error(
      "Native revision append browser fixture: expected response body read failed",
    );
  }
  requireAppend(nonempty, "expected response body was empty");
}
async function privateJson(reply: APIResponse): Promise<unknown> {
  try {
    return await reply.json();
  } catch {
    throw new Error(
      "Native revision append browser fixture: private API response JSON failed",
    );
  }
}
const appendWireOwners = new WeakMap<Page, AppendWire>();
function waitResponse(
  page: Page,
  origin: string,
  method: string,
  path: string,
  status: number,
): Promise<BrowserResponse> {
  const owner = appendWireOwners.get(page);
  requireAppend(owner, "actual response waiter has no owning wire");
  const waiting = (async () => {
    let complete = Promise.resolve(false);
    const freshRequests = new WeakSet<Request>();
    const observeRequest = (request: Request) => freshRequests.add(request);
    page.on("request", observeRequest);
    try {
      return await page.waitForResponse(
        (reply) => {
          const url = new URL(reply.url());
          const matches =
            url.origin === origin &&
            url.pathname === path &&
            !url.search &&
            reply.request().method() === method &&
            reply.status() === status;
          if (!matches) return false;
          if (method === "GET" && !freshRequests.has(reply.request()))
            return false;
          if (method !== "GET" || status !== 200) return true;
          complete = complete.then(async () => {
            let nonempty: boolean;
            try {
              nonempty = (await actualBody(reply)).length > 0;
            } catch {
              if (reply.request().failure()?.errorText === "net::ERR_ABORTED")
                return false;
              throw new Error(
                "Native revision append browser fixture: expected matching GET200 response body read failed",
              );
            }
            requireAppend(nonempty, "expected response body was empty");
            return true;
          });
          return complete;
        },
        { timeout: 45000 },
      );
    } finally {
      page.off("request", observeRequest);
    }
  })();
  owner.trackWaiter(waiting);
  return waiting;
}
async function visible(page: Page, name: string): Promise<void> {
  await expect
    .poll(() => heading(page, name).isVisible(), {
      message: "Expected append lifecycle heading did not settle",
      timeout: 45000,
    })
    .toBe(true);
}
function sameCounts(actual: AppendCounts, expected: AppendCounts): void {
  requireAppend(
    isDeepStrictEqual(actual, expected),
    "exact append counts differ",
  );
}
function receiptPath(receipt: Receipt): string {
  return (
    catalogApi(projectPath(receipt.project)) +
    "/revision-ingestions/" +
    receipt.operationId
  );
}
type Original = {
  readonly sample: FixedSample;
  readonly body: Buffer;
  readonly reviewed: Mapping["head"];
  url: string;
  key: string;
  receipt: Receipt | null;
  revision: Revision | null;
};
/** Shared two-context ledger advances only after an independently validated exact mutation delta. */
class AppendLedger {
  readonly originals = new Map<Sample, Original>();
  private readonly captured = new Set<Sample>();
  private expected: AppendCounts;
  constructor(
    readonly owner: NativeRevisionAppendOwner,
    initial: AppendCounts,
  ) {
    this.expected = { ...initial };
  }
  private appendDelta(committed: boolean): AppendCounts {
    return {
      ...this.expected,
      revisions: this.expected.revisions + (committed ? 1 : 0),
      ingestions: this.expected.ingestions + 1,
      ingestionAttempts: this.expected.ingestionAttempts + 1,
      nativePublications: this.expected.nativePublications + 1,
      workersCreated: this.expected.workersCreated + 1,
    };
  }
  async capture(original: Original): Promise<void> {
    const receipt = original.receipt;
    requireAppend(receipt, "capture requires own terminal receipt");
    if (this.captured.has(original.sample.sample)) {
      await this.checkpoint();
      return;
    }
    const ack = await this.owner.control({
      action: "capture-append",
      sample: original.sample.sample,
      operationId: receipt.operationId,
    });
    requireAppend(
      ack.action === "capture-append",
      "expected exact persistence capture",
    );
    const expected = this.appendDelta(!original.sample.conflict);
    sameCounts(ack.counts, expected);
    this.expected = expected;
    this.captured.add(original.sample.sample);
  }
  async metadataEdit(): Promise<void> {
    const ack = await this.owner.control({ action: "metadata-edit" });
    requireAppend(
      ack.action === "metadata-edit",
      "expected genuine metadata edit",
    );
    sameCounts(ack.counts, this.expected);
  }
  async advanceHead(): Promise<
    Pick<Revision, "scope" | "sequence" | "admission">
  > {
    const ack = await this.owner.control({ action: "advance-head" });
    requireAppend(
      ack.action === "advance-head",
      "expected genuine later head append",
    );
    const expected = this.appendDelta(true);
    sameCounts(ack.counts, expected);
    this.expected = expected;
    return ack.head;
  }
  async checkpoint(): Promise<AppendCounts> {
    const counts = await this.owner.checkpoint();
    sameCounts(counts, this.expected);
    return counts;
  }
  receipt(sample: Sample): Receipt {
    const receipt = this.originals.get(sample)?.receipt;
    requireAppend(receipt, "expected learned actual own receipt");
    return receipt;
  }
  complete(): void {
    requireAppend(
      this.captured.size === 7,
      "incomplete exact browser append capture census",
    );
  }
}
type Plan = {
  readonly original: Original;
  readonly replay: boolean;
  readonly delivery: "normal" | "lose" | "hold-forward";
  readonly contexts: number;
  readonly mappings: number;
  readonly entered: () => void;
  readonly wait: Promise<void>;
};
type Mutation = {
  readonly original: Original;
  readonly done: Promise<void>;
  readonly finish: () => void;
  status: number | null;
};
/** Real network observation only. No synthetic response body and no request material in matcher output. */
class AppendWire {
  readonly mappingApi: string;
  readonly appendApi: string;
  private readonly contexts: Request[] = [];
  private readonly mappings: Request[] = [];
  private readonly contextValues = new Map<Request, Context>();
  private readonly mappingValues = new Map<Request, Mapping>();
  private readonly reads: { path: string; status: number; value: unknown }[] =
    [];
  private readonly tasks: Promise<void>[] = [];
  private readonly readJobs: Promise<void>[] = [];
  private readonly pendingReadJobs = new Set<Promise<void>>();
  private readonly waiters: Promise<BrowserResponse>[] = [];
  private readonly pendingWaiters = new Set<Promise<BrowserResponse>>();
  private readonly captures = new Set<MediatedAppendRead>();
  private diagnosticAttempted = false;
  private closingReads = false;
  private readonly failedRead = (request: Request) => {
    const capture = actualGetReads.get(request);
    if (capture) capture.fail("actual GET browser request failed");
    else if (request.method() === "GET") {
      const url = new URL(request.url());
      if (
        url.origin === this.ledger.owner.manifest.readOrigin &&
        this.allowedGet(request)
      )
        this.remember(null, "actual GET failed without its capture");
    }
  };
  private readonly mutations: Mutation[] = [];
  private readonly releases: (() => void)[] = [];
  private plan: Plan | null = null;
  private failure: Error | null = null;
  private exactReceiptReads = 0;
  readonly apiEvidence = {
    contexts: 0,
    hiddenGets: 0,
    terminalGets: 0,
    terminalResumes: 0,
  };
  constructor(
    readonly page: Page,
    readonly ledger: AppendLedger,
  ) {
    this.mappingApi = catalogApi(
      mappingPath(ledger.owner.manifest.mapping.scope),
    );
    this.appendApi = this.mappingApi + "/revisions";
    appendWireOwners.set(page, this);
  }
  get postCount(): number {
    return this.mutations.length;
  }
  get contextCount(): number {
    return this.contexts.length;
  }
  get mappingCount(): number {
    return this.mappings.length;
  }
  get receiptReadCount(): number {
    return this.exactReceiptReads;
  }
  private remember(error: unknown, category: string): void {
    this.failure ??=
      error instanceof Error &&
      error.message.startsWith("Native revision append browser fixture:")
        ? error
        : new Error("Native revision append browser fixture: " + category);
  }
  trackWaiter(waiting: Promise<BrowserResponse>): void {
    this.waiters.push(waiting);
    this.pendingWaiters.add(waiting);
    void waiting.then(
      () => {
        this.pendingWaiters.delete(waiting);
      },
      () => {
        this.pendingWaiters.delete(waiting);
      },
    );
  }
  async settle(): Promise<void> {
    let reads = 0,
      jobs = 0;
    while (reads < this.tasks.length || jobs < this.readJobs.length) {
      const nextReads = this.tasks.slice(reads),
        nextJobs = this.readJobs.slice(jobs);
      reads += nextReads.length;
      jobs += nextJobs.length;
      const settled = await Promise.allSettled([...nextReads, ...nextJobs]);
      for (const result of settled)
        if (result.status === "rejected")
          this.remember(result.reason, "actual GET settlement failed");
    }
    if (this.failure) throw this.failure;
  }
  async install(): Promise<void> {
    const origin = this.ledger.owner.manifest.readOrigin;
    this.page.on("requestfailed", this.failedRead);
    this.page.on("response", (reply) => {
      const request = reply.request(),
        url = new URL(reply.url());
      if (
        request.method() !== "GET" ||
        url.origin !== origin ||
        !url.pathname.startsWith("/api/")
      )
        return;
      this.tasks.push(
        (async () => {
          const capture = actualGetReads.get(request);
          try {
            requireAppend(
              capture,
              "actual GET JSON has no exact request capture",
            );
            capture.response(reply);
            const bytes = await capture.body(reply);
            requireAppend(bytes.length > 0, "actual GET body was empty");
            const value: unknown = JSON.parse(bytes.toString("utf8"));
            this.reads.push({
              path: url.pathname,
              status: reply.status(),
              value,
            });
            if (
              url.pathname === "/api/v1/catalog/context" &&
              reply.status() === 200
            )
              this.contextValues.set(request, value as Context);
            if (url.pathname === this.mappingApi && reply.status() === 200)
              this.mappingValues.set(request, value as Mapping);
            for (const original of this.ledger.originals.values()) {
              const receipt = original.receipt;
              if (
                receipt &&
                url.pathname === receiptPath(receipt) &&
                reply.status() === 200
              )
                requireAppend(
                  isDeepStrictEqual(value, receipt),
                  "terminal own receipt changed",
                );
              if (
                receipt?.phase === "committed" &&
                url.pathname ===
                  catalogApi(revisionPath(receipt.futureRevision)) &&
                reply.status() === 200
              ) {
                this.verifyRevision(value, original);
                if (original.revision)
                  requireAppend(
                    isDeepStrictEqual(value, original.revision),
                    "ordinary immutable revision changed",
                  );
                original.revision = value as Revision;
              }
            }
          } catch (error) {
            if (!capture?.isOwnedTeardownFailure(error))
              this.remember(
                error,
                "actual GET JSON or admission observation failed",
              );
          }
        })(),
      );
    });
    await this.page.route("**/api/**", (route) => this.observe(route));
  }
  private allowedGet(request: Request): boolean {
    const url = new URL(request.url());
    const fixture = this.ledger.owner.manifest,
      paths = new Set([
        "/api/v1/catalog/context",
        catalogApi(projectPath(fixture.project.scope)),
        this.mappingApi,
        catalogApi(mappingPath(fixture.protected.revision)),
        catalogApi(revisionPath(fixture.mapping.initialRevision.scope)),
        catalogApi(revisionPath(fixture.protected.revision)),
      ]);
    for (const original of this.ledger.originals.values())
      if (original.receipt) {
        paths.add(receiptPath(original.receipt));
        if (original.receipt.phase === "committed")
          paths.add(catalogApi(revisionPath(original.receipt.futureRevision)));
      }
    return (
      isSessionStatusRequest(request, fixture.readOrigin) ||
      (!url.search && paths.has(url.pathname))
    );
  }
  private diagnosticPathKind(request: Request): string {
    const url = new URL(request.url()),
      fixture = this.ledger.owner.manifest;
    if (url.origin !== fixture.readOrigin || url.search) return "other";
    if (url.pathname === "/api/v1/catalog/context") return "context";
    if (url.pathname === catalogApi(projectPath(fixture.project.scope)))
      return "project";
    if (url.pathname === this.mappingApi) return "primaryMapping";
    if (url.pathname === catalogApi(mappingPath(fixture.protected.revision)))
      return "protectedMapping";
    if (
      url.pathname ===
      catalogApi(revisionPath(fixture.mapping.initialRevision.scope))
    )
      return "initialRevision";
    if (url.pathname === catalogApi(revisionPath(fixture.protected.revision)))
      return "protectedRevision";
    for (const original of this.ledger.originals.values())
      if (original.receipt) {
        if (url.pathname === receiptPath(original.receipt)) return "ownReceipt";
        if (
          original.receipt.phase === "committed" &&
          url.pathname ===
            catalogApi(revisionPath(original.receipt.futureRevision))
        )
          return "committedRevision";
      }
    return "other";
  }
  private diagnosticSnapshot(limit: number) {
    const captures = [...this.captures];
    return {
      counts: {
        contexts: this.contexts.length,
        mappings: this.mappings.length,
        receiptReads: this.exactReceiptReads,
        mutations: this.mutations.length,
        readTasks: this.tasks.length,
        routeJobs: this.readJobs.length,
        waiters: this.waiters.length,
        captures: captures.length,
      },
      pendingJobs: this.pendingReadJobs.size,
      pendingWaiters: this.pendingWaiters.size,
      rememberedFailure: this.failure !== null,
      capturesOmitted: Math.max(0, captures.length - limit),
      captures: captures.slice(-limit).map((capture) => ({
        pathKind: this.diagnosticPathKind(capture.request),
        ...capture.diagnostic(),
      })),
    };
  }
  async diagnoseFailure(other: AppendWire | null): Promise<void> {
    if (this.diagnosticAttempted) return;
    this.diagnosticAttempted = true;
    try {
      const config =
        process.env.BRAID_NATIVE_REVISION_APPEND_BROWSER_OPERATOR_CONFIG;
      if (!config || !isAbsolute(config)) return;
      const limit = other ? 16 : 32;
      const serialized = JSON.stringify({
        schemaVersion: "braid.native-append-get-diagnostic.v1",
        diagnosticOnly: true,
        primary: this.diagnosticSnapshot(limit),
        secondary: other ? other.diagnosticSnapshot(limit) : null,
      });
      if (Buffer.byteLength(serialized, "utf8") > 8192) return;
      const controller = new AbortController();
      let timer: ReturnType<typeof setTimeout> | undefined;
      const writing = writeFile(
        join(dirname(config), "native-revision-append-get-diagnostic.json"),
        serialized,
        { flag: "wx", mode: 0o600, signal: controller.signal },
      );
      void writing.catch(() => undefined);
      try {
        await Promise.race([
          writing,
          new Promise<void>((resolve) => {
            timer = setTimeout(() => {
              controller.abort();
              resolve();
            }, 1000);
          }),
        ]);
      } finally {
        if (timer) clearTimeout(timer);
      }
    } catch {
      /* A missing diagnostic must never replace the original failure. */
    }
  }
  private async observe(route: Route): Promise<void> {
    const request = route.request(),
      url = new URL(request.url()),
      headers = request.headers();
    let mutation: Mutation | null = null;
    try {
      requireAppend(
        url.origin === this.ledger.owner.manifest.readOrigin,
        "browser API escaped packaged origin",
      );
      if (request.method() === "GET") {
        requireAppend(
          this.allowedGet(request),
          "unexpected catalog or operation GET",
        );
        requireAppend(
          ![
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "idempotency-key",
            "x-xsrf-token",
          ].some((key) => key in headers),
          "ordinary GET carried mutation or conditional header",
        );
        if (url.pathname === "/api/v1/catalog/context")
          this.contexts.push(request);
        if (url.pathname === this.mappingApi) this.mappings.push(request);
        if (url.pathname.includes("/revision-ingestions/"))
          this.exactReceiptReads++;
        requireAppend(
          !this.closingReads,
          "actual GET arrived during owned teardown",
        );
        const capture = new MediatedAppendRead(request, (error) =>
          this.remember(error, "actual GET capture failed"),
        );
        actualGetReads.set(request, capture);
        this.captures.add(capture);
        const original = [...this.ledger.originals.values()].find(
          (original) =>
            original.receipt?.phase === "committed" &&
            url.pathname ===
              catalogApi(revisionPath(original.receipt.futureRevision)),
        );
        const job = this.mediateGet(route, capture, original);
        this.readJobs.push(job);
        this.pendingReadJobs.add(job);
        void job.then(
          () => {
            this.pendingReadJobs.delete(job);
          },
          (error) => {
            this.pendingReadJobs.delete(job);
            this.remember(error, "actual GET route job rejected");
          },
        );
        await job;
        return;
      }
      const plan = this.plan;
      this.plan = null;
      requireAppend(
        plan && request.method() === "POST",
        "unexpected browser mutation",
      );
      let finish!: () => void;
      const done = new Promise<void>((resolve) => {
        finish = resolve;
      });
      mutation = { original: plan.original, done, finish, status: null };
      this.mutations.push(mutation);
      requireAppend(
        this.contexts.length === plan.contexts + 1 &&
          this.mappings.length === plan.mappings + 1,
        "deliberate append did not fresh-admit exactly one context/mapping",
      );
      await this.settle();
      const context = this.contextValues.get(this.contexts[plan.contexts]),
        mapping = this.mappingValues.get(this.mappings[plan.mappings]),
        original = plan.original,
        fixture = this.ledger.owner.manifest;
      requireAppend(
        context?.mode === "development" &&
          context.actorPrincipalId ===
            fixture.profiles[original.sample.profile].actorPrincipalId &&
          context.allowedOrganizationId === fixture.organizationId &&
          context.features.uploadRevisions === true &&
          mapping?.access.actions.uploadRevisions === true &&
          mapping.access.actions.createMappings === false &&
          isDeepStrictEqual(mapping.scope, fixture.mapping.scope) &&
          mapping.backendId === "ferrule" &&
          typeof context.requestToken === "string" &&
          context.requestToken.length > 0 &&
          headers["x-xsrf-token"] === context.requestToken,
        "append lacked exact fresh owner/mapping capability/token",
      );
      if (!plan.replay)
        requireAppend(
          isDeepStrictEqual(mapping.head, original.reviewed),
          "first original did not use reviewed head",
        );
      const encode = (value: string) =>
        encodeURIComponent(value).replace(
          /[!'()*]/g,
          (c) => "%" + c.charCodeAt(0).toString(16).toUpperCase(),
        );
      const query =
        "?head=equal&expectedHeadRevisionId=" +
        original.reviewed.scope.revisionId +
        "&originalFilename=" +
        encode(original.sample.filename);
      requireAppend(
        url.pathname === this.appendApi &&
          url.search === query &&
          canonicalId(headers["idempotency-key"]),
        "actual append target or equal-head query/key differs",
      );
      if (!original.key) {
        requireAppend(!plan.replay, "replay cannot allocate first key");
        original.key = headers["idempotency-key"];
        original.url = url.href;
      }
      requireAppend(
        url.href === original.url &&
          headers["idempotency-key"] === original.key &&
          headers["content-type"] === "application/octet-stream" &&
          request.postDataBuffer()?.equals(original.body) &&
          headers["origin"] === fixture.readOrigin &&
          ![
            "content-encoding",
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "if-range",
          ].some((key) => key in headers),
        "original raw bytes/key/query/media/envelope changed",
      );
      if (plan.delivery === "hold-forward") {
        plan.entered();
        await plan.wait;
      }
      const reply = await route.fetch({
        maxRetries: 0,
        maxRedirects: 0,
        timeout: 30000,
      });
      try {
        requireAppend(
          reply.headers()["location"] === undefined &&
            reply.headers()["cache-control"] === "no-store",
          "actual terminal append response headers differ",
        );
        const bytes = await reply.body();
        let result: unknown;
        try {
          result = JSON.parse(bytes.toString("utf8"));
        } catch {
          throw new Error(
            "Native revision append browser fixture: actual append response JSON failed",
          );
        }
        mutation.status = reply.status();
        if (reply.status() === 404) {
          requireAppend(
            result &&
              typeof result === "object" &&
              !Array.isArray(result) &&
              Object.keys(result).join("|") === "code" &&
              (result as { code: unknown }).code === "notFound",
            "final revoked call was not opaque404",
          );
          await this.ledger.checkpoint();
        } else {
          const receipt = this.verifyResult(result, original, reply.status());
          if (original.receipt)
            requireAppend(
              isDeepStrictEqual(receipt, original.receipt),
              "replay changed original terminal receipt",
            );
          else original.receipt = receipt;
          await this.ledger.capture(original);
        }
        // Real completed200 and independently captured persistence precede intentional delivery loss.
        if (plan.delivery === "lose") {
          requireAppend(
            reply.status() === 200,
            "delivery loss requires actual completed200",
          );
          await route.abort("failed");
        } else await route.fulfill({ response: reply });
      } finally {
        await reply.dispose();
      }
      if (plan.delivery !== "hold-forward") plan.entered();
    } catch (error) {
      this.remember(error, "actual append or delivery observation failed");
      try {
        await route.abort("failed");
      } catch {
        /* preserve the first closed failure */
      }
    } finally {
      mutation?.finish();
    }
  }
  private async mediateGet(
    route: Route,
    capture: MediatedAppendRead,
    original: Original | undefined,
  ): Promise<void> {
    let reply: APIResponse | undefined;
    try {
      reply = await route.fetch({
        maxRetries: 0,
        maxRedirects: 0,
        timeout: capture.remaining(),
      });
      capture.assertLive();
      const bytes = await reply.body(),
        headers = reply.headers();
      capture.assertLive();
      requireAppend(
        bytes.length > 0 && bytes.length <= 65536,
        "actual GET exceeded the metadata entity bound",
      );
      requireAppend(
        reply.url() === capture.request.url() &&
          (headers["content-encoding"] === undefined ||
            headers["content-encoding"] === "identity"),
        "actual GET representation identity differs",
      );
      const length = headers["content-length"];
      requireAppend(
        length === undefined ||
          (/^(0|[1-9][0-9]*)$/.test(length) &&
            BigInt(length) === BigInt(bytes.length)),
        "actual GET received content length differs",
      );
      let value: unknown;
      try {
        value = JSON.parse(bytes.toString("utf8"));
      } catch {
        throw new Error(
          "Native revision append browser fixture: actual GET entity is not JSON",
        );
      }
      if (original && reply.status() === 200) {
        this.verifyRevision(value, original);
        if (original.revision)
          requireAppend(
            isDeepStrictEqual(value, original.revision),
            "ordinary immutable revision changed",
          );
      }
      capture.candidate(bytes, reply.status());
      if (original) {
        const receipt = original.receipt;
        requireAppend(
          receipt?.phase === "committed",
          "actual saved revision lost its committed receipt",
        );
        const append = appendPath(receipt.futureMapping),
          revision = revisionPath(receipt.futureRevision);
        await expect
          .poll(
            async () => {
              capture.assertLive();
              const path = new URL(this.page.url()).pathname;
              if (path === append)
                return this.page
                  .locator(".native-append")
                  .evaluateAll((roots) => {
                    if (
                      roots.length !== 1 ||
                      roots[0].getAttribute("aria-busy") !== "true"
                    )
                      return false;
                    return !Array.from(roots[0].querySelectorAll("a")).some(
                      (link) => {
                        if (link.textContent?.trim() !== "View saved revision")
                          return false;
                        const box = link.getBoundingClientRect(),
                          style = getComputedStyle(link);
                        return (
                          box.width > 0 &&
                          box.height > 0 &&
                          style.visibility !== "hidden" &&
                          style.display !== "none"
                        );
                      },
                    );
                  });
              return (
                path === revision &&
                (await this.page.locator(".native-append").count()) === 0
              );
            },
            {
              message:
                "Actual saved revision did not reach its fresh cleared UI boundary",
              timeout: Math.min(10000, capture.remaining()),
            },
          )
          .toBe(true);
      }
      capture.assertLive();
      await route.fulfill({ response: reply, body: bytes });
      capture.delivered();
    } catch (error) {
      if (!capture.isOwnedTeardownFailure(error))
        this.remember(error, "actual GET route job failed");
      capture.fail("actual GET could not be delivered");
      try {
        await route.abort("failed");
      } catch {
        /* A failed or canceled delivery is never positive. */
      }
    } finally {
      if (reply)
        try {
          await reply.dispose();
        } catch {
          this.remember(null, "actual GET response disposal failed");
        }
    }
  }
  private verifyResult(
    value: unknown,
    original: Original,
    status: number,
  ): Receipt {
    requireAppend(
      value && typeof value === "object" && !Array.isArray(value),
      "actual result is not an object",
    );
    const result = value as AppendResult,
      sample = original.sample;
    requireAppend(
      Object.keys(result).every((key) =>
        [
          "outcome",
          "receipt",
          "failure",
          "rejection",
          "committedRevisionUrl",
        ].includes(key),
      ) &&
        result.receipt &&
        (result.failure ?? null) === null &&
        (result.rejection ?? null) === null &&
        status === (sample.conflict ? 409 : 200) &&
        result.outcome ===
          (sample.conflict
            ? "headConflict"
            : sample.admission === "decodedInvalid"
              ? "committedDecodedInvalid"
              : "committedValid"),
      "terminal result status/classification differs",
    );
    const receipt = result.receipt,
      fixture = this.ledger.owner.manifest;
    requireAppend(
      Object.keys(receipt).sort().join("|") ===
        [
          "operationId",
          "project",
          "futureMapping",
          "futureRevision",
          "phase",
          "claimCount",
          "reservedAtUtc",
          "completedAtUtc",
          "originalFilename",
          "committedSequence",
          "committedAdmission",
          "latestFailure",
        ]
          .sort()
          .join("|") &&
        canonicalId(receipt.operationId) &&
        isDeepStrictEqual(receipt.project, fixture.project.scope) &&
        isDeepStrictEqual(receipt.futureMapping, fixture.mapping.scope) &&
        isDeepStrictEqual(receipt.futureMapping, {
          organizationId: receipt.futureRevision.organizationId,
          projectId: receipt.futureRevision.projectId,
          mappingId: receipt.futureRevision.mappingId,
        }) &&
        canonicalId(receipt.futureRevision.revisionId) &&
        receipt.originalFilename === sample.filename &&
        receipt.claimCount === 1 &&
        receipt.phase === (sample.conflict ? "headConflict" : "committed") &&
        receipt.committedSequence ===
          (sample.conflict
            ? null
            : String(BigInt(original.reviewed.sequence) + 1n)) &&
        receipt.committedAdmission ===
          (sample.conflict ? null : sample.admission) &&
        receipt.latestFailure === null &&
        typeof receipt.reservedAtUtc === "string" &&
        Number.isFinite(Date.parse(receipt.reservedAtUtc)) &&
        typeof receipt.completedAtUtc === "string" &&
        Date.parse(receipt.completedAtUtc) >= Date.parse(receipt.reservedAtUtc),
      "actual own receipt scope/head/sequence/null rules differ",
    );
    requireAppend(
      (result.committedRevisionUrl ?? null) ===
        (sample.conflict
          ? null
          : catalogApi(revisionPath(receipt.futureRevision))),
      "actual immutable committed URL differs",
    );
    return receipt;
  }
  private verifyRevision(value: unknown, original: Original): void {
    const receipt = original.receipt,
      revision = value as Revision,
      sample = original.sample;
    requireAppend(
      receipt?.phase === "committed" &&
        revision &&
        isDeepStrictEqual(revision.scope, receipt.futureRevision) &&
        revision.sequence === String(BigInt(original.reviewed.sequence) + 1n) &&
        revision.admission === sample.admission &&
        revision.creatorPrincipalId ===
          this.ledger.owner.manifest.profiles[sample.profile]
            .actorPrincipalId &&
        revision.backendId === "ferrule" &&
        revision.logicalDocument === "entry.native.json" &&
        revision.originalFilename === sample.filename &&
        revision.nativeByteCount === sample.byteCount &&
        revision.nativeDigest?.algorithm === "sha256" &&
        revision.nativeDigest.value === sample.digest,
      "ordinary immutable saved revision differs from original append",
    );
  }
  /** Honest foreground/body/action observation, never a future pipeline-drain guarantee. */
  async focusAdmission(action: string): Promise<void> {
    await this.page.bringToFront();
    await expect
      .poll(
        () =>
          this.page.evaluate(
            () => document.hasFocus() && document.visibilityState === "visible",
          ),
        {
          message: "Actual append page did not become visibly focused",
          timeout: 10000,
        },
      )
      .toBe(true);
    await expect(button(this.page, "Refresh access")).toBeEnabled();
    const origin = this.ledger.owner.manifest.readOrigin;
    const context = waitResponse(
        this.page,
        origin,
        "GET",
        "/api/v1/catalog/context",
        200,
      ),
      mapping = waitResponse(this.page, origin, "GET", this.mappingApi, 200);
    await this.page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await Promise.all([context.then(bodyDone), mapping.then(bodyDone)]);
    await this.settle();
    await expect(button(this.page, "Refresh access")).toBeEnabled();
    await expect(button(this.page, action)).toBeEnabled();
  }
  async arm(
    sample: FixedSample,
    delivery: Plan["delivery"] = "normal",
    replay = false,
  ): Promise<{ entered: () => boolean; release: () => void }> {
    await this.settle();
    requireAppend(!this.plan, "overlapping actual append delivery plan");
    const bytes = await sampleBytes(sample);
    await this.focusAdmission(
      replay ? "Resend original append" : "Append revision",
    );
    let original = this.ledger.originals.get(sample.sample);
    if (!original) {
      requireAppend(!replay, "replay cannot introduce an original");
      const current = this.mappingValues.get(this.mappings.at(-1)!);
      requireAppend(current, "expected exact current mapping admission");
      original = {
        sample,
        body: bytes,
        reviewed: structuredClone(current.head),
        key: "",
        url: "",
        receipt: null,
        revision: null,
      };
      this.ledger.originals.set(sample.sample, original);
    } else
      requireAppend(replay, "original sample cannot be submitted twice as new");
    let entered = false,
      release!: () => void;
    const wait = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.releases.push(release);
    this.plan = {
      original,
      replay,
      delivery,
      contexts: this.contexts.length,
      mappings: this.mappings.length,
      entered: () => {
        entered = true;
      },
      wait,
    };
    return { entered: () => entered, release };
  }
  async complete(index: number, status: number): Promise<void> {
    await expect
      .poll(() => this.mutations.length > index, {
        message: "Expected deliberate native append was not observed",
        timeout: 10000,
      })
      .toBe(true);
    const mutation = this.mutations[index];
    requireAppend(mutation, "missing actual mutation record");
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        mutation.done,
        new Promise<never>((_, reject) => {
          timer = setTimeout(
            () =>
              reject(
                new Error(
                  "Native revision append browser fixture: actual append did not settle",
                ),
              ),
            45000,
          );
        }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
    await this.settle();
    requireAppend(
      mutation.status === status,
      "actual deliberate append status differs",
    );
  }
  async revision(sample: Sample): Promise<Revision> {
    await expect
      .poll(
        async () => {
          await this.settle();
          return !!this.ledger.originals.get(sample)?.revision;
        },
        {
          message:
            "Expected exact ordinary revision observation did not settle",
          timeout: 10000,
        },
      )
      .toBe(true);
    const revision = this.ledger.originals.get(sample)?.revision;
    requireAppend(revision, "missing actual ordinary revision record");
    return revision;
  }
  async profile(profile: Profile): Promise<void> {
    await this.settle();
    const context = this.contextValues.get(this.contexts.at(-1)!);
    requireAppend(
      context?.actorPrincipalId ===
        this.ledger.owner.manifest.profiles[profile].actorPrincipalId,
      "actual fresh context identity differs",
    );
  }
  async oldRevision(scope: Revision["scope"]): Promise<Revision> {
    const path = catalogApi(revisionPath(scope));
    await expect
      .poll(
        async () => {
          await this.settle();
          return this.reads.some(
            (row) => row.path === path && row.status === 200,
          );
        },
        {
          message: "Expected original revision read was not observed",
          timeout: 10000,
        },
      )
      .toBe(true);
    return this.reads
      .filter((row) => row.path === path && row.status === 200)
      .at(-1)!.value as Revision;
  }
  /** API-only authority/terminal-resume evidence; these do not exercise a nonexistent terminal UI action. */
  async ownApi(
    sample: Sample,
    profile: Profile,
    operation: "hidden-get" | "terminal-get" | "terminal-resume",
  ): Promise<void> {
    try {
      await this.performOwnApi(sample, profile, operation);
    } catch {
      throw new Error(
        "Native revision append browser fixture: explicit private API evidence failed",
      );
    }
  }
  private async performOwnApi(
    sample: Sample,
    profile: Profile,
    operation: "hidden-get" | "terminal-get" | "terminal-resume",
  ): Promise<void> {
    const original = this.ledger.originals.get(sample);
    requireAppend(
      original?.receipt,
      "API evidence requires learned actual receipt",
    );
    this.apiEvidence.contexts++;
    const origin = this.ledger.owner.manifest.readOrigin,
      contextReply = await this.page.request.get(
        origin + "/api/v1/catalog/context",
        { maxRedirects: 0, timeout: 8000 },
      );
    let token = "";
    try {
      const context = (await privateJson(contextReply)) as Context;
      requireAppend(
        contextReply.status() === 200 &&
          context.actorPrincipalId ===
            this.ledger.owner.manifest.profiles[profile].actorPrincipalId &&
          context.allowedOrganizationId ===
            this.ledger.owner.manifest.organizationId &&
          typeof context.requestToken === "string" &&
          context.requestToken.length > 0,
        "private API context identity differs",
      );
      token = context.requestToken;
    } finally {
      await contextReply.dispose();
    }
    if (operation === "hidden-get") this.apiEvidence.hiddenGets++;
    else if (operation === "terminal-get") this.apiEvidence.terminalGets++;
    else this.apiEvidence.terminalResumes++;
    const reply =
      operation !== "terminal-resume"
        ? await this.page.request.get(origin + receiptPath(original.receipt), {
            maxRedirects: 0,
            timeout: 8000,
          })
        : await this.page.request.post(
            origin + receiptPath(original.receipt) + "/resume",
            {
              headers: { Origin: origin, "X-XSRF-TOKEN": token },
              maxRedirects: 0,
              timeout: 30000,
            },
          );
    try {
      requireAppend(
        reply.headers()["location"] === undefined &&
          reply.headers()["cache-control"] === "no-store",
        "explicit API evidence response headers differ",
      );
      const result = await privateJson(reply);
      if (operation === "hidden-get")
        requireAppend(
          reply.status() === 404 &&
            result &&
            typeof result === "object" &&
            !Array.isArray(result) &&
            Object.keys(result).join("|") === "code" &&
            (result as { code: unknown }).code === "notFound",
          "nonowner own-receipt GET was not opaque404",
        );
      else if (operation === "terminal-get")
        requireAppend(
          reply.status() === 200 && isDeepStrictEqual(result, original.receipt),
          "own terminal receipt GET changed after new intent",
        );
      else {
        const receipt = this.verifyResult(result, original, reply.status());
        requireAppend(
          isDeepStrictEqual(receipt, original.receipt),
          "terminal source-free API resume changed immutable receipt",
        );
      }
    } finally {
      await reply.dispose();
    }
    await this.ledger.checkpoint();
  }
  release(): void {
    for (const release of this.releases) release();
  }
  async disposeReads(): Promise<void> {
    this.closingReads = true;
    for (const capture of this.captures) capture.close();
    if (
      (this.pendingReadJobs.size > 0 || this.pendingWaiters.size > 0) &&
      !this.page.isClosed()
    ) {
      try {
        await this.page.close({ runBeforeUnload: false });
      } catch {
        this.remember(null, "owned append page could not settle read teardown");
      }
    }
    try {
      try {
        await this.settle();
      } catch (error) {
        this.remember(error, "actual GET read settlement failed");
      }
      for (const result of await Promise.allSettled(this.waiters))
        if (result.status === "rejected")
          this.remember(null, "owned response waiter failed");
    } finally {
      this.page.off("requestfailed", this.failedRead);
      if (appendWireOwners.get(this.page) === this)
        appendWireOwners.delete(this.page);
    }
    if (this.failure) throw this.failure;
  }
  async closed(): Promise<void> {
    await Promise.all(this.mutations.map((row) => row.done));
    await this.settle();
    requireAppend(!this.plan, "unused actual append delivery plan");
  }
}
async function ready(page: Page): Promise<void> {
  await visible(page, "Upload revision");
  await expect
    .poll(
      () => page.getByLabel("Saved filename", { exact: true }).isEditable(),
      { message: "Actual append editor was not admitted", timeout: 10000 },
    )
    .toBe(true);
}
async function navigate(
  page: Page,
  wire: AppendWire,
  admitted: boolean,
): Promise<void> {
  const fixture = wire.ledger.owner.manifest,
    context = waitResponse(
      page,
      fixture.readOrigin,
      "GET",
      "/api/v1/catalog/context",
      200,
    ),
    mapping = waitResponse(
      page,
      fixture.readOrigin,
      "GET",
      wire.mappingApi,
      200,
    );
  await page.goto(appendPath(fixture.mapping.scope));
  await Promise.all([context.then(bodyDone), mapping.then(bodyDone)]);
  await wire.settle();
  if (admitted) await ready(page);
  else {
    await visible(page, "Append form unavailable");
    requireAppend(
      (await button(page, "Append revision").count()) === 0 &&
        (await page.getByLabel("Saved filename", { exact: true }).count()) ===
          0,
      "refused direct route exposed append editor",
    );
  }
}
async function refresh(
  page: Page,
  wire: AppendWire,
  mappingExpected = true,
): Promise<void> {
  const origin = wire.ledger.owner.manifest.readOrigin,
    before = wire.contextCount,
    context = waitResponse(page, origin, "GET", "/api/v1/catalog/context", 200),
    mapping = mappingExpected
      ? waitResponse(page, origin, "GET", wire.mappingApi, 200)
      : null;
  await button(page, "Refresh access").click();
  await bodyDone(await context);
  if (mapping) await bodyDone(await mapping);
  await wire.settle();
  await expect(button(page, "Refresh access")).toBeEnabled();
  requireAppend(
    wire.contextCount === before + 1,
    "explicit refresh did not use one fresh context",
  );
}
async function choose(page: Page, sample: FixedSample): Promise<void> {
  const bytes = await sampleBytes(sample);
  try {
    await page
      .getByLabel("Native mapping file", { exact: true })
      .setInputFiles({
        name: sample.filename,
        mimeType: "application/json",
        buffer: bytes,
      });
  } catch {
    throw new Error(
      "Native revision append browser fixture: exact file selection action failed",
    );
  }
  try {
    await page
      .getByLabel("Saved filename", { exact: true })
      .fill(sample.filename);
  } catch {
    throw new Error(
      "Native revision append browser fixture: saved filename entry action failed",
    );
  }
  await expect(button(page, "Append revision")).toBeEnabled();
  requireAppend(
    (await page.getByLabel("Saved filename", { exact: true }).inputValue()) ===
      sample.filename &&
      (await page
        .getByLabel("Native mapping file", { exact: true })
        .inputValue()) === "",
    "literal filename or cleared file input differs",
  );
}
async function saved(
  page: Page,
  wire: AppendWire,
  sample: FixedSample,
): Promise<void> {
  await visible(
    page,
    sample.admission === "decodedInvalid"
      ? "Revision saved with validation issues"
      : "Revision saved",
  );
  await expect
    .poll(
      async () =>
        (await page
          .getByRole("link", { name: "View saved revision", exact: true })
          .isVisible()) &&
        (await page
          .locator(".native-append")
          .evaluateAll(
            (roots) =>
              roots.length === 1 &&
              roots[0].getAttribute("aria-busy") === "false",
          )),
      {
        message:
          "Acknowledged append did not settle exact ordinary revision access",
        timeout: 10000,
      },
    )
    .toBe(true);
  const revision = await wire.revision(sample.sample);
  requireAppend(
    (await page
      .getByRole("link", { name: "View saved revision", exact: true })
      .getAttribute("href")) === revisionPath(revision.scope),
    "saved link changed immutable scope",
  );
}
async function startNew(page: Page): Promise<void> {
  await button(page, "Start a new append").click();
  await ready(page);
  requireAppend(
    (await heading(page, "Revision saved").count()) === 0 &&
      (await heading(page, "Append was not saved: head changed").count()) === 0,
    "explicit new attempt retained old terminal presentation",
  );
}

test.describe.configure({ mode: "serial" });
test("real packaged equal-head append: exact bytes, competing intents, historical recovery and current authority", async ({
  page,
  browser,
  nativeRevisionAppendOwner: owner,
}) => {
  test.setTimeout(720000);
  const initial = await owner.checkpoint(),
    ledger = new AppendLedger(owner, initial),
    wire = new AppendWire(page, ledger),
    fixture = owner.manifest;
  requireAppend(
    initial.workersCreated === 4 &&
      initial.mappings === 2 &&
      initial.revisions === 2 &&
      initial.reportResults === 2,
    "genuine native/report baseline differs",
  );
  await wire.install();
  let otherWire: AppendWire | null = null,
    secondContext: Awaited<ReturnType<typeof browser.newContext>> | null = null;
  const secondResources: {
    wire: AppendWire | null;
    context: Awaited<ReturnType<typeof browser.newContext>> | null;
  } = { wire: null, context: null };
  let oldPrimary: Revision | null = null,
    oldSibling: Revision | null = null;
  let primaryFailure = false;
  try {
    await page.setViewportSize({ width: 390, height: 844 });
    await test.step("actual Viewer Runner and feature-disabled direct routes cannot append or create native work", async () => {
      for (const profile of ["viewer", "runner"] as const) {
        await owner.control({
          action: "restart-profile",
          profile,
          mode: "append",
        });
        await navigate(page, wire, false);
        await wire.profile(profile);
        requireAppend(
          wire.postCount === 0,
          "read-only direct route sent native mutation",
        );
        sameCounts(await ledger.checkpoint(), initial);
      }
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "featureOff",
      });
      await navigate(page, wire, false);
      await wire.profile("publisher");
      requireAppend(
        wire.postCount === 0,
        "disabled route sent native mutation",
      );
      sameCounts(await ledger.checkpoint(), initial);
    });
    await test.step("Publisher appends exact versioned bytes after genuine independent D metadata edit; Administrator saves decoded-invalid next sequence", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "append",
      });
      for (const scope of [
        fixture.mapping.initialRevision.scope,
        fixture.protected.revision,
      ]) {
        const reply = waitResponse(
          page,
          fixture.readOrigin,
          "GET",
          catalogApi(revisionPath(scope)),
          200,
        );
        await page.goto(revisionPath(scope));
        await bodyDone(await reply);
        await expect(heading(page, "Revision 1")).toBeVisible();
        await expect(heading(page, "Current mapping metadata")).toBeVisible();
        const revision = await wire.oldRevision(scope);
        if (scope.mappingId === fixture.mapping.scope.mappingId)
          oldPrimary = revision;
        else oldSibling = revision;
      }
      await navigate(page, wire, true);
      await choose(page, fixed.valid);
      const formDimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      requireAppend(
        formDimensions.scroll <= formDimensions.width + 1,
        "actual mobile append editor overflows",
      );
      const reviewedBefore = await page
        .locator("section[aria-labelledby='append-review']")
        .textContent();
      await ledger.metadataEdit();
      requireAppend(
        (await page
          .locator("section[aria-labelledby='append-review']")
          .textContent()) === reviewedBefore,
        "metadata edit silently changed reviewed native head",
      );
      await wire.arm(fixed.valid);
      const index = wire.postCount;
      await button(page, "Append revision").dblclick();
      await wire.complete(index, 200);
      await saved(page, wire, fixed.valid);
      requireAppend(
        wire.postCount === index + 1,
        "double activation sent extra native append",
      );
      await ledger.checkpoint();
      requireAppend(
        (
          await page
            .locator("section[aria-labelledby='append-current-mapping']")
            .textContent()
        )?.includes("F metadata independence 😀"),
        "fresh current mapping lost genuine metadata edit",
      );
      await startNew(page);
      await owner.control({
        action: "restart-profile",
        profile: "administrator",
        mode: "append",
      });
      await refresh(page, wire);
      await navigate(page, wire, true);
      await choose(page, fixed.decodedInvalid);
      await wire.arm(fixed.decodedInvalid);
      const invalidIndex = wire.postCount;
      await button(page, "Append revision").click();
      await wire.complete(invalidIndex, 200);
      await saved(page, wire, fixed.decodedInvalid);
      await ledger.checkpoint();
    });
    await test.step("two actual browser contexts freeze one reviewed head; held contender loses actual409 then deliberate new-head new-key append succeeds", async () => {
      await startNew(page);
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "append",
      });
      await refresh(page, wire);
      await navigate(page, wire, true);
      secondContext = secondResources.context = await browser.newContext({
        baseURL: fixture.readOrigin,
        viewport: { width: 390, height: 844 },
        serviceWorkers: "block",
      });
      const otherPage = await secondContext.newPage();
      otherWire = secondResources.wire = new AppendWire(otherPage, ledger);
      await otherWire.install();
      await navigate(otherPage, otherWire, true);
      await choose(page, fixed.winner);
      await choose(otherPage, fixed.loser);
      const held = await otherWire.arm(fixed.loser, "hold-forward"),
        loserIndex = otherWire.postCount;
      await button(otherPage, "Append revision").click();
      await expect
        .poll(held.entered, {
          message: "Second actual contender was not held before forwarding",
          timeout: 10000,
        })
        .toBe(true);
      await wire.arm(fixed.winner);
      const winnerIndex = wire.postCount;
      await button(page, "Append revision").click();
      await wire.complete(winnerIndex, 200);
      await saved(page, wire, fixed.winner);
      requireAppend(
        isDeepStrictEqual(
          ledger.originals.get("winner")!.reviewed,
          ledger.originals.get("loser")!.reviewed,
        ) &&
          ledger.originals.get("winner")!.key !==
            ledger.originals.get("loser")!.key,
        "competing intents did not freeze same head with distinct keys",
      );
      const winnerCounts = await ledger.checkpoint();
      held.release();
      await otherWire.complete(loserIndex, 409);
      await visible(otherPage, "Append was not saved: head changed");
      requireAppend(
        (await otherPage
          .getByRole("link", { name: "View saved revision", exact: true })
          .count()) === 0 && otherWire.postCount === loserIndex + 1,
        "head conflict invented saved revision or automatic fallback",
      );
      const conflict = structuredClone(ledger.receipt("loser"));
      const afterConflict = await ledger.checkpoint();
      requireAppend(
        afterConflict.revisions === winnerCounts.revisions &&
          afterConflict.workersCreated === winnerCounts.workersCreated + 1,
        "losing real attempt did not preserve exact publication delta",
      );
      const reviewContext = waitResponse(
          otherPage,
          fixture.readOrigin,
          "GET",
          "/api/v1/catalog/context",
          200,
        ),
        reviewMapping = waitResponse(
          otherPage,
          fixture.readOrigin,
          "GET",
          otherWire.mappingApi,
          200,
        );
      await button(otherPage, "Review current head").click();
      await Promise.all([
        reviewContext.then(bodyDone),
        reviewMapping.then(bodyDone),
      ]);
      await otherWire.settle();
      await expect(button(otherPage, "Start a new append")).toBeEnabled();
      requireAppend(
        otherWire.postCount === loserIndex + 1,
        "conflict head review submitted another append",
      );
      await startNew(otherPage);
      await choose(otherPage, fixed.renewed);
      await otherWire.arm(fixed.renewed);
      const renewedIndex = otherWire.postCount;
      await button(otherPage, "Append revision").click();
      await otherWire.complete(renewedIndex, 200);
      await saved(otherPage, otherWire, fixed.renewed);
      requireAppend(
        ledger.originals.get("renewed")!.key !==
          ledger.originals.get("loser")!.key &&
          ledger.originals.get("renewed")!.reviewed.scope.revisionId ===
            ledger.receipt("winner").futureRevision.revisionId &&
          isDeepStrictEqual(ledger.receipt("loser"), conflict),
        "new deliberate intent rewrote original conflict or reused key/head",
      );
      await otherWire.ownApi("loser", "publisher", "terminal-get");
    });
    await test.step("completed200 delivery loss remains unknown; later genuine head advance plus normal same-PG restart replays historical original with no extra work", async () => {
      // The first context still holds its winner acknowledgement; retire it deliberately.
      await startNew(page);
      await choose(page, fixed.lost);
      await wire.arm(fixed.lost, "lose");
      const index = wire.postCount,
        receipts = wire.receiptReadCount;
      await button(page, "Append revision").click();
      await wire.complete(index, 200);
      await visible(page, "Append outcome is unknown");
      requireAppend(
        (await heading(page, "Revision saved").count()) === 0 &&
          wire.postCount === index + 1 &&
          wire.receiptReadCount === receipts,
        "lost delivery was adopted or searched/retried automatically",
      );
      requireAppend(
        await page.evaluate(
          () => localStorage.length === 0 && sessionStorage.length === 0,
        ),
        "native recovery was written to browser persistence",
      );
      const originalReceipt = structuredClone(ledger.receipt("lost")),
        advanced = await ledger.advanceHead();
      requireAppend(
        advanced.scope.revisionId !== originalReceipt.futureRevision.revisionId,
        "later genuine append did not advance beyond lost acknowledgement",
      );
      const trusted = await ledger.checkpoint();
      await owner.control({
        action: "restart-profile",
        profile: "administrator",
        mode: "append",
      });
      await refresh(page, wire);
      await visible(page, "An original append is retained privately");
      const hiddenFilename = page.getByLabel("Saved filename", { exact: true }),
        privateText = await page.locator(".native-append").textContent();
      requireAppend(
        ((await hiddenFilename.count()) === 0 ||
          (await hiddenFilename.inputValue()) === "") &&
          !privateText?.includes(fixed.lost.filename) &&
          !privateText?.includes(originalReceipt.futureRevision.revisionId) &&
          !privateText?.includes(originalReceipt.operationId) &&
          !privateText?.includes(fixed.lost.digest) &&
          !privateText?.includes(ledger.originals.get("lost")!.key) &&
          (await button(page, "Resend original append").count()) === 0 &&
          wire.postCount === index + 1,
        "foreign actor exposed or replayed original source",
      );
      await wire.ownApi("lost", "administrator", "hidden-get");
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "append",
      });
      await refresh(page, wire);
      await visible(page, "Append outcome is unknown");
      await owner.control({ action: "pg-normal-restart" });
      sameCounts(await ledger.checkpoint(), trusted);
      await wire.arm(fixed.lost, "normal", true);
      const resendRevision = waitResponse(
        page,
        fixture.readOrigin,
        "GET",
        catalogApi(revisionPath(originalReceipt.futureRevision)),
        200,
      );
      await button(page, "Resend original append").click();
      await wire.complete(index + 1, 200);
      await bodyDone(await resendRevision);
      await saved(page, wire, fixed.lost);
      requireAppend(
        isDeepStrictEqual(ledger.receipt("lost"), originalReceipt) &&
          wire.postCount === index + 2 &&
          (await wire.revision("lost")).scope.revisionId !==
            advanced.scope.revisionId,
        "original recovery adopted newer head or changed receipt",
      );
      sameCounts(await ledger.checkpoint(), trusted);
      await wire.focusAdmission("Check append status");
      const receiptReply = waitResponse(
          page,
          fixture.readOrigin,
          "GET",
          receiptPath(originalReceipt),
          200,
        ),
        revisionReply = waitResponse(
          page,
          fixture.readOrigin,
          "GET",
          catalogApi(revisionPath(originalReceipt.futureRevision)),
          200,
        ),
        posts = wire.postCount;
      await button(page, "Check append status").click();
      await Promise.all([
        receiptReply.then(bodyDone),
        revisionReply.then(bodyDone),
      ]);
      await saved(page, wire, fixed.lost);
      requireAppend(
        wire.postCount === posts,
        "explicit own status GET sent native mutation",
      );
      await wire.ownApi("lost", "publisher", "terminal-resume");
      sameCounts(await ledger.checkpoint(), trusted);
    });
    await test.step("same-actor Viewer keeps ordinary saved revision reads while own receipt is hidden and access retry remains GET-only", async () => {
      await wire.focusAdmission("Check append status");
      const posts = wire.postCount,
        trusted = await ledger.checkpoint();
      await owner.control({ action: "revoke-publisher" });
      const read = waitResponse(
        page,
        fixture.readOrigin,
        "GET",
        catalogApi(revisionPath(ledger.receipt("lost").futureRevision)),
        200,
      );
      await refresh(page, wire);
      await bodyDone(await read);
      await saved(page, wire, fixed.lost);
      await visible(page, "Append form unavailable");
      requireAppend(
        (await button(page, "Check append status").count()) === 0,
        "Viewer retained own upload-status control",
      );
      const retry = waitResponse(
        page,
        fixture.readOrigin,
        "GET",
        catalogApi(revisionPath(ledger.receipt("lost").futureRevision)),
        200,
      );
      await button(page, "Retry revision access").click();
      await bodyDone(await retry);
      await saved(page, wire, fixed.lost);
      await wire.ownApi("lost", "publisher", "hidden-get");
      requireAppend(
        wire.postCount === posts,
        "Viewer saved access retry sent append",
      );
      sameCounts(await ledger.checkpoint(), trusted);
      await owner.control({ action: "restore-publisher" });
      const restoredRevision = waitResponse(
        page,
        fixture.readOrigin,
        "GET",
        catalogApi(revisionPath(ledger.receipt("lost").futureRevision)),
        200,
      );
      await refresh(page, wire);
      await bodyDone(await restoredRevision);
      await saved(page, wire, fixed.lost);
      requireAppend(
        wire.postCount === posts,
        "restored upload authority automatically sent append",
      );
      sameCounts(await ledger.checkpoint(), trusted);
    });
    await test.step("fresh advisory and held final revocation create zero native work; restoration resends one exact original intent", async () => {
      await startNew(page);
      await choose(page, fixed.restored);
      await wire.focusAdmission("Append revision");
      const index = wire.postCount,
        trusted = await ledger.checkpoint();
      await owner.control({ action: "revoke-publisher" });
      const denied = waitResponse(
        page,
        fixture.readOrigin,
        "GET",
        wire.mappingApi,
        200,
      );
      await button(page, "Append revision").click();
      await bodyDone(await denied);
      await visible(page, "Append form unavailable");
      requireAppend(
        wire.postCount === index,
        "fresh read-only admission sent native append",
      );
      sameCounts(await ledger.checkpoint(), trusted);
      await owner.control({ action: "restore-publisher" });
      await refresh(page, wire);
      await ready(page);
      const held = await wire.arm(fixed.restored, "hold-forward");
      await button(page, "Append revision").click();
      await expect
        .poll(held.entered, {
          message: "Final-boundary native append was not held",
          timeout: 10000,
        })
        .toBe(true);
      await owner.control({ action: "revoke-publisher" });
      held.release();
      await wire.complete(index, 404);
      sameCounts(await ledger.checkpoint(), trusted);
      await owner.control({ action: "restore-publisher" });
      await refresh(page, wire);
      await expect(button(page, "Resend original append")).toBeEnabled();
      requireAppend(
        wire.postCount === index + 1,
        "restoring authority automatically replayed original append",
      );
      await wire.arm(fixed.restored, "normal", true);
      await button(page, "Resend original append").click();
      await wire.complete(index + 1, 200);
      await saved(page, wire, fixed.restored);
      requireAppend(
        wire.postCount === index + 2,
        "restored original was not one deliberate unchanged resend",
      );
      await ledger.checkpoint();
    });
    await test.step("all seven real browser captures preserve old immutable native revisions and saved reports across normal restarts", async () => {
      const savedDimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      requireAppend(
        savedDimensions.scroll <= savedDimensions.width + 1,
        "actual mobile append acknowledgement overflows",
      );
      ledger.complete();
      const expected = {
        ...initial,
        revisions: initial.revisions + 7,
        ingestions: initial.ingestions + 8,
        ingestionAttempts: initial.ingestionAttempts + 8,
        nativePublications: initial.nativePublications + 8,
        workersCreated: initial.workersCreated + 8,
      };
      sameCounts(await ledger.checkpoint(), expected);
      await owner.control({ action: "pg-normal-restart" });
      sameCounts(await ledger.checkpoint(), expected);
      const posts = wire.postCount;
      for (const [scope, original] of [
        [fixture.mapping.initialRevision.scope, oldPrimary],
        [fixture.protected.revision, oldSibling],
      ] as const) {
        const reply = waitResponse(
          page,
          fixture.readOrigin,
          "GET",
          catalogApi(revisionPath(scope)),
          200,
        );
        await page.goto(revisionPath(scope));
        await bodyDone(await reply);
        await expect(heading(page, "Revision 1")).toBeVisible();
        await expect(heading(page, "Current mapping metadata")).toBeVisible();
        requireAppend(
          original &&
            isDeepStrictEqual(await wire.oldRevision(scope), original),
          "old ordinary immutable native revision changed",
        );
      }
      const dimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      requireAppend(
        dimensions.scroll <= dimensions.width + 1,
        "actual mobile saved revision layout overflows",
      );
      requireAppend(
        otherWire &&
          posts === wire.postCount &&
          wire.postCount + otherWire.postCount === 9 &&
          wire.receiptReadCount + otherWire.receiptReadCount === 1,
        "actual browser POST/own-receipt census differs",
      );
      requireAppend(
        wire.apiEvidence.contexts + otherWire.apiEvidence.contexts === 4 &&
          wire.apiEvidence.hiddenGets + otherWire.apiEvidence.hiddenGets ===
            2 &&
          wire.apiEvidence.terminalGets + otherWire.apiEvidence.terminalGets ===
            1 &&
          wire.apiEvidence.terminalResumes +
            otherWire.apiEvidence.terminalResumes ===
            1,
        "explicit API-only operation census differs",
      );
      await wire.closed();
      await otherWire.closed();
      sameCounts(await ledger.checkpoint(), expected);
    });
  } catch (error) {
    primaryFailure = true;
    await wire.diagnoseFailure(secondResources.wire);
    throw error;
  } finally {
    let cleanupFailed = false,
      cleanupFailure: unknown;
    const attempt = async (action: () => unknown | Promise<unknown>) => {
      try {
        await action();
      } catch (error) {
        if (!cleanupFailed) {
          cleanupFailed = true;
          cleanupFailure = error;
        }
      }
    };
    await attempt(() => wire.release());
    await attempt(() => secondResources.wire?.release());
    const secondWire = secondResources.wire,
      ownedSecondContext = secondResources.context;
    const firstDisposal = attempt(() => wire.disposeReads()),
      secondDisposal = attempt(() => secondWire?.disposeReads());
    await Promise.all([firstDisposal, secondDisposal]);
    if (ownedSecondContext) await attempt(() => ownedSecondContext.close());
    if (!primaryFailure && cleanupFailed) throw cleanupFailure;
  }
});
