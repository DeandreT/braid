import type {
  APIResponse,
  BrowserContext,
  Page,
  Request,
  Route,
  Response as BrowserResponse,
} from "@playwright/test";
import { errors } from "@playwright/test";
import { createHash } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { dirname, isAbsolute, join } from "node:path";
import { isDeepStrictEqual } from "node:util";
import type { components } from "../src/api/generated";
import {
  catalogApi,
  editPath,
  expect,
  mappingPath,
  requireMetadata,
  revisionPath,
  test,
  type MappingMetadataOwner,
  type Metadata,
  type Profile,
} from "./mapping-metadata.fixture";

type Context = components["schemas"]["CatalogContextV1"];
type Mapping = components["schemas"]["CatalogMappingV1"];
type Draft = {
  readonly name: string;
  readonly description: string;
  readonly acceptedName: string;
};
type Baseline = { readonly body: Metadata; readonly tag: string };
type RefreshProof = Readonly<{ kind: "completedRefresh" }>;
type Read = {
  readonly path: string;
  readonly status: number;
  readonly value: unknown;
  readonly tag?: string;
};
type Patch = {
  readonly request: Request;
  readonly body: string;
  readonly tag: string;
  readonly token: string;
  readonly context: number;
  readonly mappingRead: number;
  readonly baseline: Baseline;
  readonly draft: Draft;
  readonly done: Promise<void>;
  readonly finish: () => void;
  status?: number;
  value?: unknown;
  result?: Baseline;
  changed?: boolean;
};
type Delivery = "normal" | "lose" | "hold-forward";
type PatchPhase =
  | "observed"
  | "envelopePassed"
  | "fetchEntered"
  | "upstreamReceived"
  | "jsonParsed"
  | "authorityPassed"
  | "fieldsPassed"
  | "valueVersionTagPassed"
  | "captureAckPassed"
  | "doneResolved"
  | "fulfillEntered"
  | "fulfilled"
  | "fulfillRejected"
  | "handlerExited"
  | "browserResponse"
  | "requestFinished"
  | "requestFailed";
type PatchObservation = {
  readonly ordinal: number;
  readonly started: number;
  delivery: Delivery | null;
  readonly phases: Partial<Record<PatchPhase, number>>;
  upstreamStatus: "none" | "200" | "other";
  browserResponseSeen: boolean;
  browserStatus200: boolean | null;
  browserSameUrl: boolean | null;
  browserTagMatchesUpstream: boolean | null;
  requestFinished: boolean;
  requestFailed: boolean;
};
type GetKind =
  | "context"
  | "primaryMapping"
  | "metadata"
  | "otherMapping"
  | "revision"
  | "list"
  | "otherAllowed";
type GetFailureKind =
  | "deadline"
  | "requestFailure"
  | "browserAdmission"
  | "captureOrDelivery"
  | "ownedTeardown";
class MetadataGetCaptureError extends Error {
  constructor(
    readonly category: GetFailureKind,
    message: string,
  ) {
    super(message);
  }
}
type WaiterObservation = {
  readonly path: string;
  readonly before: ReadonlySet<Request>;
  readonly started: number;
  settledAt: number | null;
  matchingResponses: number;
  oldRequestRejected: number;
  bodyEntered: boolean;
  bodyReady: boolean;
  state: "pending" | "succeeded" | "failed";
  caught: "none" | "playwrightTimeout" | "captureFailure" | "other";
  captureFailure: GetFailureKind | null;
};
let readDiagnosticAttempted = false;
const sha = (text: string) =>
  createHash("sha256").update(text, "utf8").digest("hex");

function coherentLength(
  headers: Record<string, string>,
  bytes: Buffer,
): boolean {
  const length = headers["content-length"];
  return (
    length === undefined ||
    (/^(0|[1-9][0-9]*)$/.test(length) &&
      BigInt(length) === BigInt(bytes.length))
  );
}

/** One actual received GET entity, admitted only with its exact browser Request and delivery. */
class MetadataGetCapture {
  readonly deadline = performance.now() + 8000;
  readonly ready: Promise<Buffer>;
  private resolve!: (bytes: Buffer) => void;
  private reject!: (error: Error) => void;
  private readonly timer: ReturnType<typeof setTimeout>;
  private bytes: Buffer | null = null;
  private status: number | null = null;
  private headers: Record<string, string> | null = null;
  private fulfilled = false;
  private responseSeen = false;
  private failure: Error | null = null;
  private failureCategory: GetFailureKind | null = null;
  readonly phases = {
    fetchEntered: false,
    upstreamReceived: false,
    jsonParsed: false,
    fulfillEntered: false,
    ready: false,
  };
  ownedTeardown = false;
  constructor(
    readonly request: Request,
    private readonly onFailure: (error: Error) => void,
  ) {
    this.ready = new Promise<Buffer>((resolve, reject) => {
      this.resolve = resolve;
      this.reject = reject;
    });
    void this.ready.catch(() => undefined);
    this.timer = setTimeout(
      () =>
        this.fail(
          "actual GET capture exceeded the existing read budget",
          false,
          "deadline",
        ),
      8000,
    );
  }
  remaining(): number {
    this.assertLive();
    return Math.max(1, Math.floor(this.deadline - performance.now()));
  }
  assertLive(): void {
    if (!this.failure && performance.now() >= this.deadline)
      this.fail(
        "actual GET capture exceeded the existing read budget",
        false,
        "deadline",
      );
    if (this.failure) throw this.failure;
    requireMetadata(
      !this.request.failure(),
      "actual GET request failed before complete delivery",
    );
  }
  candidate(
    bytes: Buffer,
    status: number,
    headers: Record<string, string>,
  ): void {
    this.assertLive();
    this.bytes = bytes;
    this.status = status;
    this.headers = headers;
  }
  response(reply: BrowserResponse): void {
    try {
      this.assertLive();
      const headers = reply.headers();
      requireMetadata(
        this.bytes &&
          this.headers &&
          reply.request() === this.request &&
          reply.url() === this.request.url() &&
          reply.status() === this.status &&
          headers["etag"] === this.headers["etag"] &&
          headers["content-encoding"] === this.headers["content-encoding"] &&
          (headers["content-encoding"] === undefined ||
            headers["content-encoding"] === "identity") &&
          coherentLength(headers, this.bytes),
        "actual browser GET identity/status/validator/representation differs",
      );
      this.responseSeen = true;
      this.complete();
    } catch {
      this.fail(
        "actual browser GET delivery could not be admitted",
        false,
        "browserAdmission",
      );
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
    this.phases.ready = true;
    this.resolve(this.bytes);
  }
  fail(
    category: string,
    ownedTeardown = false,
    failureCategory: GetFailureKind = "captureOrDelivery",
  ): void {
    if (this.failure) return;
    this.failureCategory = ownedTeardown ? "ownedTeardown" : failureCategory;
    this.failure = new MetadataGetCaptureError(
      this.failureCategory,
      "Mapping metadata browser fixture: " + category,
    );
    this.ownedTeardown = ownedTeardown;
    clearTimeout(this.timer);
    if (!ownedTeardown) this.onFailure(this.failure);
    this.reject(this.failure);
  }
  close(): void {
    if (!this.fulfilled || !this.responseSeen)
      this.fail("owned GET capture closed before complete delivery", true);
    clearTimeout(this.timer);
  }
  diagnostic() {
    return {
      ...this.phases,
      fulfilled: this.fulfilled,
      responseSeen: this.responseSeen,
      bytesPresent: this.bytes !== null,
      requestFailure: this.request.failure() !== null,
      ownedTeardown: this.ownedTeardown,
      failureCategory: this.failureCategory,
    };
  }
  async body(reply: BrowserResponse): Promise<Buffer> {
    const bytes = await this.ready;
    if (this.failure) throw this.failure;
    requireMetadata(
      reply.request() === this.request &&
        reply.url() === this.request.url() &&
        reply.status() === this.status &&
        this.fulfilled &&
        this.responseSeen &&
        !this.request.failure(),
      "actual GET capture is not a complete same-request delivery",
    );
    return bytes;
  }
}
const metadataGetCaptures = new WeakMap<Request, MetadataGetCapture>();
async function capturedGetBody(reply: BrowserResponse): Promise<Buffer> {
  const capture = metadataGetCaptures.get(reply.request());
  requireMetadata(
    capture && reply.request().method() === "GET",
    "allowed GET response has no exact request capture",
  );
  return capture.body(reply);
}

/** Private envelope comparisons; every PATCH forwards the original actual browser request exactly once. */
class MetadataWire {
  private baseline: Baseline | null = null;
  private readonly reads: Read[] = [];
  private readonly tasks: Promise<void>[] = [];
  private readonly getJobs: Promise<void>[] = [];
  private readonly pendingGetJobs = new Set<Promise<void>>();
  private readonly captures = new Set<MetadataGetCapture>();
  private readonly getRequests = new Set<Request>();
  private readonly waiters: Promise<BrowserResponse>[] = [];
  private readonly pendingWaiters = new Set<Promise<BrowserResponse>>();
  private readonly refreshProofs = new WeakMap<
    RefreshProof,
    { context: Request; mapping: Request }
  >();
  private readonly waiterObservations: WaiterObservation[] = [];
  private closingReads = false;
  private responseListener: ((reply: BrowserResponse) => void) | null = null;
  private readonly observeRequest = (request: Request) => {
    const url = new URL(request.url());
    if (
      request.method() === "GET" &&
      url.origin === this.owner.manifest.readOrigin &&
      url.pathname.startsWith("/api/")
    )
      this.getRequests.add(request);
  };
  private readonly failedRead = (request: Request) => {
    const patch = this.patchObservations.get(request);
    if (patch) {
      patch.requestFailed = true;
      this.markPatch(request, "requestFailed");
    }
    const capture = metadataGetCaptures.get(request);
    if (!capture) return;
    if (this.closingReads) capture.close();
    else
      capture.fail(
        "actual browser GET request failed",
        false,
        "requestFailure",
      );
  };
  private readonly contexts: Request[] = [];
  private readonly contextValues = new Map<Request, Context>();
  private readonly mappings: Request[] = [];
  private readonly mappingValues = new Map<Request, Mapping>();
  private readonly patches: Patch[] = [];
  private readonly patchObservations = new Map<Request, PatchObservation>();
  private readonly patchResponseTags = new Map<Request, string | undefined>();
  private patchObservationUnavailable = false;
  private readonly finishedPatch = (request: Request) => {
    const patch = this.patchObservations.get(request);
    if (patch) {
      patch.requestFinished = true;
      this.markPatch(request, "requestFinished");
    }
  };
  private readonly releases: (() => void)[] = [];
  private plan: {
    draft: Draft;
    profile: Profile;
    delivery: Delivery;
    entered: () => void;
    wait: Promise<void>;
  } | null = null;
  private failed = false;
  private failure: Error | null = null;
  private unexpected = false;
  readonly metadataPath: string;
  readonly mappingApi: string;
  constructor(
    private readonly page: Page,
    private readonly owner: MappingMetadataOwner,
  ) {
    this.mappingApi = catalogApi(mappingPath(owner.manifest.primary.scope));
    this.metadataPath = this.mappingApi + "/metadata";
  }
  get patchCount(): number {
    return this.patches.length;
  }
  get contextCount(): number {
    return this.contexts.length;
  }
  get actionReadCount(): number {
    return this.mappings.length;
  }
  get metadataReadCount(): number {
    return this.reads.filter((r) => r.path === this.metadataPath).length;
  }
  private markPatch(request: Request, phase: PatchPhase): void {
    const patch = this.patchObservations.get(request);
    if (patch)
      patch.phases[phase] ??= Math.min(
        600000,
        Math.max(0, Math.round(performance.now() - patch.started)),
      );
  }
  private failGet(category: string): void {
    this.failed = true;
    this.failure ??= new Error("Mapping metadata browser fixture: " + category);
  }
  private async drainReads(): Promise<void> {
    let reads = 0,
      jobs = 0;
    while (reads < this.tasks.length || jobs < this.getJobs.length) {
      const next = [...this.tasks.slice(reads), ...this.getJobs.slice(jobs)];
      reads = this.tasks.length;
      jobs = this.getJobs.length;
      for (const result of await Promise.allSettled(next))
        if (result.status === "rejected")
          this.failGet("owned GET observation job failed");
    }
  }
  async settle(): Promise<void> {
    await this.drainReads();
    if (this.failure) throw this.failure;
    requireMetadata(
      !this.failed && !this.unexpected,
      "actual HTTP observation or closed request boundary failed",
    );
  }
  private waitForGet(
    path: string,
    before: ReadonlySet<Request> = new Set(this.getRequests),
  ): Promise<BrowserResponse> {
    const observation: WaiterObservation = {
      path,
      before,
      started: performance.now(),
      settledAt: null,
      matchingResponses: 0,
      oldRequestRejected: 0,
      bodyEntered: false,
      bodyReady: false,
      state: "pending",
      caught: "none",
      captureFailure: null,
    };
    this.waiterObservations.push(observation);
    let complete = Promise.resolve(false);
    const waiting = (async () => {
      try {
        const reply = await this.page.waitForResponse(
          (reply) => {
            const request = reply.request(),
              url = new URL(reply.url());
            if (
              request.method() !== "GET" ||
              url.origin !== this.owner.manifest.readOrigin ||
              url.pathname !== path ||
              url.search ||
              reply.status() !== 200 ||
              !this.getRequests.has(request)
            )
              return false;
            if (before.has(request)) {
              observation.oldRequestRejected++;
              return false;
            }
            observation.matchingResponses++;
            complete = complete.then(async () => {
              observation.bodyEntered = true;
              requireMetadata(
                (await capturedGetBody(reply)).length > 0,
                "fresh GET response body was empty",
              );
              observation.bodyReady = true;
              return true;
            });
            return complete;
          },
          { timeout: 10000 },
        );
        observation.state = "succeeded";
        observation.settledAt = performance.now();
        return reply;
      } catch (error) {
        observation.state = "failed";
        observation.settledAt = performance.now();
        observation.caught =
          error instanceof errors.TimeoutError
            ? "playwrightTimeout"
            : error instanceof MetadataGetCaptureError
              ? "captureFailure"
              : "other";
        if (error instanceof MetadataGetCaptureError)
          observation.captureFailure = error.category;
        throw new Error(
          "Mapping metadata browser fixture: fresh complete GET response boundary failed",
        );
      }
    })();
    this.waiters.push(waiting);
    this.pendingWaiters.add(waiting);
    void waiting.then(
      () => this.pendingWaiters.delete(waiting),
      () => this.pendingWaiters.delete(waiting),
    );
    return waiting;
  }
  mappingResponse(): Promise<BrowserResponse> {
    return this.waitForGet(this.mappingApi);
  }
  async refreshAdmission(page: Page): Promise<RefreshProof> {
    requireMetadata(
      page === this.page,
      "refresh requires its owned browser page",
    );
    const before = new Set(this.getRequests),
      context = this.waitForGet("/api/v1/catalog/context", before),
      mapping = this.waitForGet(this.mappingApi, before);
    await this.page.bringToFront();
    await expect
      .poll(() =>
        this.page.evaluate(
          () => document.hasFocus() && document.visibilityState === "visible",
        ),
      )
      .toBe(true);
    await expect(button(this.page, "Refresh admission")).toBeEnabled();
    await button(this.page, "Refresh admission").click();
    const replies = await Promise.all([context, mapping]);
    for (const reply of replies) {
      let nonempty: boolean;
      try {
        nonempty = (await capturedGetBody(reply)).length > 0;
      } catch {
        throw new Error(
          "Mapping metadata browser fixture: refresh admission response body could not be consumed",
        );
      }
      requireMetadata(nonempty, "refresh admission response body was empty");
    }
    await this.settle();
    await ready(this.page);
    const proof: RefreshProof = Object.freeze({ kind: "completedRefresh" });
    this.refreshProofs.set(proof, {
      context: replies[0]!.request(),
      mapping: replies[1]!.request(),
    });
    return proof;
  }
  consumeRefresh(proof: RefreshProof | undefined): void {
    const observed = proof ? this.refreshProofs.get(proof) : undefined;
    requireMetadata(
      proof &&
        observed &&
        this.contextValues.has(observed.context) &&
        this.mappingValues.has(observed.mapping) &&
        !observed.context.failure() &&
        !observed.mapping.failure(),
      "refresh proof is absent, foreign, reused or not a complete owned admission",
    );
    this.refreshProofs.delete(proof);
  }
  private admitted(value: unknown, tag: string | undefined): Baseline {
    requireMetadata(
      value !== null && typeof value === "object" && !Array.isArray(value),
      "actual metadata body is not an object",
    );
    const body = value as Metadata,
      scope = this.owner.manifest.primary.scope;
    requireMetadata(
      Object.keys(body).sort().join("|") === "description|name|scope|version" &&
        body.scope &&
        Object.keys(body.scope).sort().join("|") ===
          "mappingId|organizationId|projectId" &&
        isDeepStrictEqual(body.scope, scope) &&
        typeof body.name === "string" &&
        typeof body.description === "string" &&
        typeof body.version === "string" &&
        /^[1-9][0-9]*$/.test(body.version) &&
        BigInt(body.version) <= 9223372036854775807n,
      "actual metadata scope/shape/version differs",
    );
    requireMetadata(
      tag ===
        `"metadata-v1:${scope.organizationId}:${scope.projectId}:${scope.mappingId}:${body.version}"`,
      "actual strong metadata tag differs",
    );
    return { body, tag };
  }
  async install(): Promise<void> {
    const org = this.owner.manifest.organizationId,
      origin = this.owner.manifest.readOrigin,
      id = "[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}";
    const projectApi = `/api/v1/organizations/${org}/projects/${this.owner.manifest.project.scope.projectId}`;
    const exactGets = new Set([
      projectApi,
      this.mappingApi,
      catalogApi(mappingPath(this.owner.manifest.unrelated.scope)),
      this.metadataPath,
    ]);
    const listGets = new Set([
      projectApi + "/mappings",
      this.mappingApi + "/revisions",
    ]);
    const revisionGet = new RegExp(`^${this.mappingApi}/revisions/${id}$`);
    this.page.on("request", this.observeRequest);
    this.page.on("requestfailed", this.failedRead);
    this.page.on("requestfinished", this.finishedPatch);
    this.responseListener = (reply) => {
      const request = reply.request(),
        url = new URL(reply.url());
      const patch = this.patchObservations.get(request);
      if (patch) {
        try {
          patch.browserResponseSeen = true;
          patch.browserStatus200 = reply.status() === 200;
          patch.browserSameUrl = reply.url() === request.url();
          patch.browserTagMatchesUpstream = this.patchResponseTags.has(request)
            ? reply.headers()["etag"] === this.patchResponseTags.get(request)
            : null;
          this.markPatch(request, "browserResponse");
        } catch {
          this.patchObservationUnavailable = true;
        }
      }
      if (
        request.method() !== "GET" ||
        url.origin !== origin ||
        !url.pathname.startsWith("/api/")
      )
        return;
      const capture = metadataGetCaptures.get(request);
      capture?.response(reply);
      const task = (async () => {
        let body: Buffer;
        try {
          requireMetadata(
            capture,
            "allowed GET response has no exact request capture",
          );
          body = await capture.body(reply);
        } catch {
          if (!capture?.ownedTeardown)
            this.failGet("actual GET capture observation failed");
          return;
        }
        try {
          const value: unknown = JSON.parse(body.toString("utf8")),
            tag = reply.headers()["etag"];
          this.reads.push({
            path: url.pathname,
            status: reply.status(),
            value,
            tag,
          });
          if (
            url.pathname === "/api/v1/catalog/context" &&
            reply.status() === 200
          )
            this.contextValues.set(request, value as Context);
          if (url.pathname === this.mappingApi && reply.status() === 200) {
            requireMetadata(
              tag === undefined,
              "ordinary mapping GET unexpectedly supplied metadata validator",
            );
            this.mappingValues.set(request, value as Mapping);
          }
          if (url.pathname === this.metadataPath && reply.status() === 200)
            this.baseline = this.admitted(value, tag);
        } catch {
          this.failed = true;
          this.failure ??= new Error(
            "Mapping metadata browser fixture: actual GET contract admission failed",
          );
        }
      })();
      this.tasks.push(task);
    };
    this.page.on("response", this.responseListener);
    await this.page.route("**/api/**", async (route: Route) => {
      const request = route.request(),
        url = new URL(request.url()),
        headers = request.headers();
      const names = [...url.searchParams.keys()],
        list =
          names.length > 0 &&
          names.every((n) => n === "limit" || n === "cursor") &&
          url.searchParams.getAll("limit").length === 1 &&
          url.searchParams.get("limit") === "10" &&
          url.searchParams.getAll("cursor").length <= 1;
      const allowedGet =
        ((url.pathname === "/api/v1/catalog/context" ||
          exactGets.has(url.pathname) ||
          revisionGet.test(url.pathname)) &&
          names.length === 0) ||
        (listGets.has(url.pathname) && list);
      if (
        url.origin !== origin ||
        (request.method() === "GET"
          ? !allowedGet
          : request.method() !== "PATCH" ||
            url.pathname !== this.metadataPath ||
            names.length !== 0)
      ) {
        this.unexpected = true;
        this.failure ??= new Error(
          "Mapping metadata browser fixture: closed request boundary failed",
        );
        await route.abort("blockedbyclient");
        return;
      }
      if (request.method() === "GET") {
        try {
          requireMetadata(
            ![
              "if-match",
              "if-none-match",
              "if-modified-since",
              "if-unmodified-since",
            ].some((n) => n in headers),
            "metadata/catalog read unexpectedly became conditional",
          );
        } catch {
          this.failGet("metadata/catalog read unexpectedly became conditional");
          try {
            await route.abort("failed");
          } catch {
            this.failGet("conditional GET refusal could not settle");
          }
          return;
        }
        if (url.pathname === "/api/v1/catalog/context")
          this.contexts.push(request);
        if (url.pathname === this.mappingApi) this.mappings.push(request);
        const capture = new MetadataGetCapture(request, (error) => {
          this.failed = true;
          this.failure ??= error;
        });
        metadataGetCaptures.set(request, capture);
        this.captures.add(capture);
        if (this.closingReads) capture.close();
        const job = this.captureGet(route, capture);
        this.getJobs.push(job);
        this.pendingGetJobs.add(job);
        void job.then(
          () => this.pendingGetJobs.delete(job),
          () => {
            this.pendingGetJobs.delete(job);
            this.failGet("owned GET route job rejected");
          },
        );
        await job;
        return;
      }
      if (this.patchObservations.size < 9)
        this.patchObservations.set(request, {
          ordinal: this.patches.length + 1,
          started: performance.now(),
          delivery: null,
          phases: { observed: 0 },
          upstreamStatus: "none",
          browserResponseSeen: false,
          browserStatus200: null,
          browserSameUrl: null,
          browserTagMatchesUpstream: null,
          requestFinished: false,
          requestFailed: false,
        });
      else this.patchObservationUnavailable = true;
      const plan = this.plan;
      const observation = this.patchObservations.get(request);
      if (observation) observation.delivery = plan?.delivery ?? null;
      this.plan = null;
      requireMetadata(
        plan && this.baseline,
        "unplanned metadata PATCH or absent actual baseline",
      );
      const body = request.postData(),
        tag = headers["if-match"],
        token = headers["x-xsrf-token"];
      requireMetadata(
        body !== null &&
          Buffer.byteLength(body) <= 65536 &&
          tag === this.baseline.tag &&
          typeof token === "string" &&
          token.length > 0 &&
          headers["content-type"] ===
            "application/vnd.weft.mapping-metadata-edit.v1+json" &&
          headers["origin"] === origin &&
          ![
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "if-range",
            "idempotency-key",
            "content-encoding",
          ].some((n) => n in headers),
        "actual PATCH envelope differs from received validator/transport",
      );
      this.markPatch(request, "envelopePassed");
      let finish!: () => void;
      const done = new Promise<void>((resolve) => {
        finish = resolve;
      });
      const patch: Patch = {
        request,
        body,
        tag,
        token,
        context: this.contexts.length - 1,
        mappingRead: this.mappings.length - 1,
        baseline: this.baseline,
        draft: plan.draft,
        done,
        finish,
      };
      this.patches.push(patch);
      try {
        if (plan.delivery === "hold-forward") {
          plan.entered();
          await plan.wait;
        }
        this.markPatch(request, "fetchEntered");
        const reply = await route.fetch({
          maxRetries: 0,
          maxRedirects: 0,
          timeout: 30000,
        });
        patch.status = reply.status();
        if (observation)
          observation.upstreamStatus = patch.status === 200 ? "200" : "other";
        this.patchResponseTags.set(request, reply.headers()["etag"]);
        this.markPatch(request, "upstreamReceived");
        patch.value = await reply.json();
        this.markPatch(request, "jsonParsed");
        await this.settle();
        const context = this.contextValues.get(this.contexts[patch.context]),
          mapping = this.mappingValues.get(this.mappings[patch.mappingRead]);
        requireMetadata(
          context?.actorPrincipalId ===
            this.owner.manifest.profiles[plan.profile].actorPrincipalId &&
            context.allowedOrganizationId === org &&
            context.features.editMetadata &&
            context.requestToken === token &&
            mapping?.access.actions.editMetadata,
          "PATCH lacked fresh same-actor context/action/token",
        );
        this.markPatch(request, "authorityPassed");
        let raw: unknown;
        try {
          raw = JSON.parse(body);
        } catch {
          throw new Error(
            "Mapping metadata browser fixture: invalid raw PATCH JSON",
          );
        }
        const input = raw as Record<string, unknown>;
        requireMetadata(
          raw !== null &&
            typeof raw === "object" &&
            !Array.isArray(raw) &&
            Object.keys(input).sort().join("|") === "description|name" &&
            input.name === plan.draft.name &&
            input.description === plan.draft.description,
          "exact raw replacement fields changed",
        );
        this.markPatch(request, "fieldsPassed");
        if (reply.status() === 200) {
          const result = this.admitted(patch.value, reply.headers()["etag"]),
            changed =
              plan.draft.acceptedName !== patch.baseline.body.name ||
              plan.draft.description !== patch.baseline.body.description;
          requireMetadata(
            result.body.name === plan.draft.acceptedName &&
              result.body.description === plan.draft.description &&
              result.body.version ===
                (changed
                  ? (BigInt(patch.baseline.body.version) + 1n).toString()
                  : patch.baseline.body.version),
            "actual successful replacement/version differs",
          );
          this.markPatch(request, "valueVersionTagPassed");
          const ack = await this.owner.control({
            action: "capture-edit",
            previousVersion: patch.baseline.body.version,
            version: result.body.version,
            nameSha256: sha(result.body.name),
            descriptionSha256: sha(result.body.description),
          });
          requireMetadata(
            ack.action === "capture-edit" && ack.changed === changed,
            "independent captured edit delta differs",
          );
          this.markPatch(request, "captureAckPassed");
          patch.result = result;
          patch.changed = changed;
          this.baseline = result;
        } else {
          requireMetadata(
            reply.headers()["etag"] === undefined &&
              patch.value !== null &&
              typeof patch.value === "object" &&
              !Array.isArray(patch.value) &&
              Object.keys(patch.value).join("|") === "code",
            "actual refusal disclosed metadata or validator",
          );
        }
        patch.finish();
        this.markPatch(request, "doneResolved");
        if (plan.delivery !== "hold-forward") plan.entered();
        if (plan.delivery === "lose") await route.abort("failed");
        else {
          this.markPatch(request, "fulfillEntered");
          try {
            await route.fulfill({ response: reply });
            this.markPatch(request, "fulfilled");
          } catch (error) {
            this.markPatch(request, "fulfillRejected");
            throw error;
          }
        }
      } catch (error: unknown) {
        if (!request.failure()) {
          this.failed = true;
          this.failure ??=
            error instanceof Error &&
            error.message.startsWith("Mapping metadata browser fixture: ")
              ? error
              : new Error(
                  "Mapping metadata browser fixture: actual PATCH observation failed",
                );
        }
        try {
          await route.abort("failed");
        } catch {
          // The original request may already be settled or canceled; retain its first failure.
        }
        patch.finish();
        this.markPatch(request, "doneResolved");
      } finally {
        this.markPatch(request, "handlerExited");
      }
    });
  }
  private async captureGet(
    route: Route,
    capture: MetadataGetCapture,
  ): Promise<void> {
    let reply: APIResponse | undefined;
    try {
      capture.assertLive();
      capture.phases.fetchEntered = true;
      reply = await route.fetch({
        maxRetries: 0,
        maxRedirects: 0,
        timeout: capture.remaining(),
      });
      capture.phases.upstreamReceived = true;
      capture.assertLive();
      const bytes = await reply.body(),
        headers = reply.headers();
      capture.assertLive();
      requireMetadata(
        bytes.length > 0 && bytes.length <= 65536,
        "actual GET entity exceeded the metadata observation bound",
      );
      requireMetadata(
        reply.url() === capture.request.url() &&
          (headers["content-encoding"] === undefined ||
            headers["content-encoding"] === "identity") &&
          coherentLength(headers, bytes),
        "actual GET representation identity or length differs",
      );
      try {
        JSON.parse(bytes.toString("utf8"));
        capture.phases.jsonParsed = true;
      } catch {
        throw new Error(
          "Mapping metadata browser fixture: actual captured GET entity is not JSON",
        );
      }
      capture.candidate(bytes, reply.status(), headers);
      capture.phases.fulfillEntered = true;
      await route.fulfill({ response: reply, body: bytes });
      capture.delivered();
    } catch {
      capture.fail("actual GET capture or delivery failed", this.closingReads);
      try {
        await route.abort("failed");
      } catch {
        if (!this.closingReads)
          this.failGet("failed GET route could not settle");
      }
    } finally {
      if (reply) {
        try {
          await reply.dispose();
        } catch {
          this.failGet("actual GET response disposal failed");
        }
      }
    }
  }
  async arm(
    draft: Draft,
    profile: Profile = "publisher",
    delivery: Delivery = "normal",
  ): Promise<{ entered: () => boolean; release: () => void }> {
    await this.settle();
    requireMetadata(
      !this.plan && this.baseline,
      "save requires one actual metadata baseline and no pending plan",
    );
    let entered = false,
      release!: () => void;
    const wait = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.releases.push(release);
    this.plan = {
      draft,
      profile,
      delivery,
      entered: () => {
        entered = true;
      },
      wait,
    };
    return { entered: () => entered, release };
  }
  async complete(index: number, status: number): Promise<void> {
    const patch = this.patches[index];
    requireMetadata(patch, "expected deliberate PATCH");
    await Promise.race([
      patch.done,
      new Promise<never>((_, reject) => {
        const timer = setTimeout(
          () =>
            reject(
              new Error(
                "Mapping metadata browser fixture: actual PATCH observation deadline exceeded",
              ),
            ),
          50000,
        );
        void patch.done.finally(() => clearTimeout(timer));
      }),
    ]);
    await this.settle();
    requireMetadata(patch.status === status, "actual PATCH status differs");
    if (status === 412)
      requireMetadata(
        (patch.value as Record<string, unknown>).code === "preconditionFailed",
        "actual CAS refusal differs",
      );
  }
  async finalRefusal(index: number): Promise<void> {
    await this.patches[index].done;
    await this.settle();
    const patch = this.patches[index],
      code = (patch.value as Record<string, unknown>).code;
    requireMetadata(
      (patch.status === 404 &&
        (code === "notFound" || code === "unavailable")) ||
        (patch.status === 403 && code === "identityRejected"),
      "actual final authority refusal differs",
    );
  }
  async fresh(
    index: number,
    contexts: number,
    mappings: number,
  ): Promise<void> {
    await this.settle();
    const patch = this.patches[index];
    requireMetadata(
      patch.context === contexts && patch.mappingRead === mappings,
      "Save did not refresh context/action exactly once",
    );
  }
  async sameBaseline(other: MetadataWire): Promise<void> {
    await this.settle();
    await other.settle();
    requireMetadata(
      this.baseline &&
        other.baseline &&
        this.baseline.tag === other.baseline.tag &&
        isDeepStrictEqual(this.baseline.body, other.baseline.body),
      "actual metadata validator changed across independent reads",
    );
  }
  async latestMapping(): Promise<Mapping> {
    await this.settle();
    const value = this.mappingValues.get(this.mappings.at(-1)!);
    requireMetadata(value, "expected ordinary mapping view");
    return value;
  }
  async version(): Promise<string> {
    await this.settle();
    requireMetadata(this.baseline, "expected real metadata baseline");
    return this.baseline.body.version;
  }
  release(): void {
    for (const release of this.releases) release();
  }
  private getKind(path: string): GetKind {
    if (path === "/api/v1/catalog/context") return "context";
    if (path === this.mappingApi) return "primaryMapping";
    if (path === this.metadataPath) return "metadata";
    if (path === catalogApi(mappingPath(this.owner.manifest.unrelated.scope)))
      return "otherMapping";
    if (path.startsWith(this.mappingApi + "/revisions/")) return "revision";
    if (path.endsWith("/mappings") || path === this.mappingApi + "/revisions")
      return "list";
    return "otherAllowed";
  }
  private async diagnosticSnapshot() {
    const latest = this.patches.at(-1),
      accepted = latest?.result?.body,
      draft = this.plan?.draft ?? latest?.draft;
    const ui = await this.page
      .evaluate(
        ({ expectedPath, rawName, rawDescription, accepted }) => {
          const roots =
              document.querySelectorAll<HTMLElement>(".metadata-editor"),
            root = roots.length === 1 ? roots[0] : null;
          const visible = (element: Element | null): boolean => {
            if (!element) return false;
            const rect = element.getBoundingClientRect(),
              style = getComputedStyle(element);
            return (
              rect.width > 0 &&
              rect.height > 0 &&
              style.display !== "none" &&
              style.visibility !== "hidden"
            );
          };
          const headings = Array.from(root?.querySelectorAll("h1,h2") ?? []);
          const heading = (name: string) =>
            headings.some(
              (element) =>
                element.textContent?.trim() === name && visible(element),
            );
          const enabled = (name: string): boolean | null => {
            const matches = Array.from(
              root?.querySelectorAll<HTMLButtonElement>("button") ?? [],
            ).filter(
              (element) =>
                element.textContent?.trim() === name && visible(element),
            );
            return matches.length === 1 ? !matches[0]!.disabled : null;
          };
          const name =
              root?.querySelector<HTMLInputElement>("#metadata-name") ?? null,
            description =
              root?.querySelector<HTMLTextAreaElement>(
                "#metadata-description",
              ) ?? null;
          const vocabulary = {
            invalidInput: "Check the metadata fields before saving.",
            identityChanged:
              "Your identity or organization changed. The previous editor information was cleared; refresh admission to start a new edit.",
            identityRejected:
              "Your identity could not access this mapping. Private editor details are hidden.",
            featureDisabled:
              "Metadata editing is not enabled for this workspace.",
            editUnavailable:
              "You do not currently have permission to edit this mapping.",
            notFound:
              "This mapping is unavailable to your current identity. Private editor details are hidden.",
            invalidRequest:
              "The service refused this metadata request as invalid.",
            preconditionFailed:
              "The mapping metadata changed. Review latest before deciding what to save.",
            preconditionRequired:
              "A current metadata validator is required. Review latest before saving.",
            versionExhausted:
              "This metadata version cannot advance further. No different version was adopted.",
            bodyLimit: "The service refused the size of this metadata request.",
            unsupportedMediaType:
              "The service did not accept the metadata request format.",
            persistenceOrInternalFailure:
              "The service could not confirm this metadata operation.",
            timedOut:
              "Waiting timed out. A sent save may already have committed.",
            unavailable:
              "This operation could not be confirmed. No automatic save or retry was made.",
            incompatible:
              "The reply could not be verified as this mapping's metadata.",
          };
          const errors = Array.from(
            root?.querySelectorAll(".metadata-error") ?? [],
          )
            .filter(visible)
            .slice(0, 32)
            .map((element) => element.textContent?.trim() ?? "");
          return {
            expectedEditorRoute:
              location.pathname === expectedPath && location.search === "",
            exactlyOneEditor: roots.length === 1,
            editorVisible: root ? visible(root) : null,
            ariaBusy: root ? root.getAttribute("aria-busy") === "true" : null,
            confirmationVisible: heading("Metadata save confirmed"),
            unknownVisible: heading("Save outcome is unknown"),
            unavailableVisible: heading("Metadata editor unavailable"),
            conflictVisible: heading("Review before another save"),
            comparisonVisible: heading("Compare mapping metadata"),
            fieldsAttentionVisible: heading("Check the metadata fields"),
            saveEnabled: enabled("Save metadata"),
            refreshEnabled: enabled("Refresh admission"),
            namePresent: name !== null,
            descriptionPresent: description !== null,
            nameMatchesRaw:
              name && rawName !== null ? name.value === rawName : null,
            descriptionMatchesRaw:
              description && rawDescription !== null
                ? description.value === rawDescription
                : null,
            acceptedAvailable: accepted !== null,
            nameMatchesAccepted:
              name && accepted ? name.value === accepted.name : null,
            descriptionMatchesAccepted:
              description && accepted
                ? description.value === accepted.description
                : null,
            documentFocused: document.hasFocus(),
            documentVisible: document.visibilityState === "visible",
            errors: {
              ...Object.fromEntries(
                Object.entries(vocabulary).map(([kind, text]) => [
                  kind,
                  errors.includes(text),
                ]),
              ),
              otherFixedCategory: errors.some(
                (text) =>
                  text !== "" && !Object.values(vocabulary).includes(text),
              ),
            },
          };
        },
        {
          expectedPath: editPath(this.owner.manifest.primary.scope),
          rawName: draft?.name ?? null,
          rawDescription: draft?.description ?? null,
          accepted: accepted
            ? { name: accepted.name, description: accepted.description }
            : null,
        },
      )
      .catch(() => null);
    const kinds: GetKind[] = [
      "context",
      "primaryMapping",
      "metadata",
      "otherMapping",
      "revision",
      "list",
      "otherAllowed",
    ];
    const requests = [...this.getRequests],
      captures = [...this.captures];
    const selectedCaptures = captures
      .map((capture, index) => ({
        capture,
        ordinal: index + 1,
        state: capture.diagnostic(),
      }))
      .sort(
        (left, right) =>
          Number(
            right.state.failureCategory !== null && !right.state.ownedTeardown,
          ) -
            Number(
              left.state.failureCategory !== null && !left.state.ownedTeardown,
            ) ||
          Number(!right.state.ready && right.state.failureCategory === null) -
            Number(!left.state.ready && left.state.failureCategory === null) ||
          right.ordinal - left.ordinal,
      )
      .slice(0, 3);
    return {
      collection: ui === null ? "snapshotUnavailable" : "complete",
      observationUnavailable: this.patchObservationUnavailable,
      counts: {
        requests: requests.length,
        captures: captures.length,
        waiters: this.waiterObservations.length,
        pendingRouteJobs: this.pendingGetJobs.size,
        pendingWaiters: this.pendingWaiters.size,
        patches: this.patches.length,
      },
      requestsByKind: Object.fromEntries(
        kinds.map((kind) => [
          kind,
          requests.filter(
            (request) => this.getKind(new URL(request.url()).pathname) === kind,
          ).length,
        ]),
      ),
      capturesTruncated: captures.length > 3,
      captures: selectedCaptures.map(({ capture, ordinal, state }) => ({
        ordinal,
        kind: this.getKind(new URL(capture.request.url()).pathname),
        ...state,
      })),
      waitersTruncated: this.waiterObservations.length > 2,
      waiters: this.waiterObservations.slice(-2).map((observation) => ({
        kind: this.getKind(observation.path),
        entryRequests: observation.before.size,
        newRequests: requests.filter(
          (request) =>
            !observation.before.has(request) &&
            new URL(request.url()).pathname === observation.path,
        ).length,
        matchingResponses: observation.matchingResponses,
        oldRequestRejected: observation.oldRequestRejected,
        bodyEntered: observation.bodyEntered,
        bodyReady: observation.bodyReady,
        state: observation.state,
        caught: observation.caught,
        captureFailure: observation.captureFailure,
        deadlineReached:
          (observation.settledAt ?? performance.now()) - observation.started >=
          10000,
      })),
      patchesTruncated: this.patchObservations.size > 1,
      patches: Array.from(this.patchObservations.values())
        .slice(-1)
        .map((observation) => ({
          ordinal: observation.ordinal,
          delivery: observation.delivery,
          phases: observation.phases,
          upstreamStatus: observation.upstreamStatus,
          browserResponseSeen: observation.browserResponseSeen,
          browserStatus200: observation.browserStatus200,
          browserSameUrl: observation.browserSameUrl,
          browserTagMatchesUpstream: observation.browserTagMatchesUpstream,
          requestFinished: observation.requestFinished,
          requestFailed: observation.requestFailed,
        })),
      ui,
      historicalAcknowledgementClearedEstablished: false,
    };
  }
  async diagnoseFailure(
    other?: MetadataWire,
  ): Promise<"written" | "unavailable"> {
    if (readDiagnosticAttempted) return "unavailable";
    readDiagnosticAttempted = true;
    const config = process.env.WEFT_MAPPING_METADATA_BROWSER_OPERATOR_CONFIG;
    if (!config || !isAbsolute(config)) return "unavailable";
    const deadline = performance.now() + 1000;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const collection = (async (): Promise<"written" | "unavailable"> => {
      try {
        const results = await Promise.allSettled([
          this.diagnosticSnapshot(),
          ...(other ? [other.diagnosticSnapshot()] : []),
        ]);
        if (performance.now() >= deadline) return "unavailable";
        let serialized = JSON.stringify({
          schemaVersion: "weft.metadata-delivery-diagnostic.v2",
          wires: results.map((result, index) => ({
            wire: index === 0 ? "primary" : "secondary",
            ...(result.status === "fulfilled"
              ? result.value
              : { collection: "unavailable" }),
          })),
        });
        if (Buffer.byteLength(serialized, "utf8") > 8192)
          serialized = JSON.stringify({
            schemaVersion: "weft.metadata-delivery-diagnostic.v2",
            collection: "unavailable",
            category: "sizeLimit",
          });
        await writeFile(
          join(dirname(config), "metadata-patch-delivery-diagnostic.json"),
          serialized,
          { flag: "wx", mode: 0o600 },
        );
        return "written";
      } catch {
        return "unavailable";
      }
    })();
    void collection.catch(() => undefined);
    try {
      return await Promise.race([
        collection,
        new Promise<"unavailable">((resolve) => {
          timer = setTimeout(
            () => resolve("unavailable"),
            Math.max(0, Math.floor(deadline - performance.now())),
          );
        }),
      ]);
    } catch {
      return "unavailable";
    } finally {
      if (timer) clearTimeout(timer);
    }
  }
  /** The caller releases every planned PATCH hold before read cancellation can close a page. */
  async disposeReads(): Promise<void> {
    this.closingReads = true;
    for (const capture of this.captures) capture.close();
    try {
      if (
        (this.pendingGetJobs.size > 0 || this.pendingWaiters.size > 0) &&
        !this.page.isClosed()
      ) {
        try {
          await this.page.close({ runBeforeUnload: false });
        } catch {
          this.failGet("owned page could not settle GET observation teardown");
        }
      }
      await this.drainReads();
      for (const result of await Promise.allSettled(this.waiters))
        if (result.status === "rejected")
          this.failGet("owned explicit GET boundary failed");
    } finally {
      this.page.off("request", this.observeRequest);
      this.page.off("requestfailed", this.failedRead);
      this.page.off("requestfinished", this.finishedPatch);
      if (this.responseListener)
        this.page.off("response", this.responseListener);
    }
    if (this.failure) throw this.failure;
  }
  async closed(): Promise<void> {
    await this.settle();
    requireMetadata(!this.plan, "unused metadata delivery control");
  }
}

const button = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const heading = (page: Page, name: string) =>
  page.getByRole("heading", { name, exact: true });
async function ready(page: Page): Promise<void> {
  await expect(heading(page, "Edit mapping metadata")).toBeVisible();
  await expect(button(page, "Save metadata")).toBeEnabled();
}
async function fill(page: Page, draft: Draft): Promise<void> {
  await page.getByLabel("Name", { exact: true }).fill(draft.name);
  await page
    .getByLabel("Description (optional)", { exact: true })
    .fill(draft.description);
}
async function refresh(page: Page, wire: MetadataWire): Promise<RefreshProof> {
  return wire.refreshAdmission(page);
}
async function saved(page: Page): Promise<void> {
  await expect(heading(page, "Metadata save confirmed")).toBeVisible();
  await expect(button(page, "Save metadata")).toBeEnabled();
}
async function save(
  page: Page,
  wire: MetadataWire,
  draft: Draft,
  profile: Profile = "publisher",
  proof?: RefreshProof,
): Promise<number> {
  wire.consumeRefresh(proof ?? (await refresh(page, wire)));
  await fill(page, draft);
  await wire.arm(draft, profile);
  const index = wire.patchCount,
    contexts = wire.contextCount,
    mappings = wire.actionReadCount;
  await button(page, "Save metadata").click();
  await saved(page);
  await wire.complete(index, 200);
  await wire.fresh(index, contexts, mappings);
  return index;
}

test.describe.configure({ mode: "serial" });
test("real packaged mapping metadata: strong-validator CAS, preserved native history and GET-only uncertain-save reconciliation", async ({
  page,
  browser,
  mappingMetadataOwner: owner,
}) => {
  test.setTimeout(600000);
  const fixture = owner.manifest,
    path = editPath(fixture.primary.scope),
    wire = new MetadataWire(page, owner);
  let secondContext: BrowserContext | undefined,
    other: MetadataWire | undefined;
  let primaryFailure: unknown,
    hasPrimaryFailure = false;
  try {
    await wire.install();
    const initial = await owner.checkpoint();
    expect(initial.counts.workersCreated).toBeGreaterThan(0);
    await page.setViewportSize({ width: 390, height: 844 });
    await test.step("actual Viewer, Runner and feature-off direct routes cannot expose or submit the editor", async () => {
      for (const profile of ["viewer", "runner"] as const) {
        await owner.control({
          action: "restart-profile",
          profile,
          mode: "edit",
        });
        await page.goto(path);
        await expect(
          heading(page, "Metadata editor unavailable"),
        ).toBeVisible();
        await expect(page.getByLabel("Name", { exact: true })).toHaveCount(0);
        expect(wire.patchCount).toBe(0);
        expect((await owner.checkpoint()).state).toEqual(initial.state);
        await wire.settle();
      }
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "featureOff",
      });
      await page.goto(path);
      await expect(heading(page, "Metadata editor unavailable")).toBeVisible();
      await expect(page.getByLabel("Name", { exact: true })).toHaveCount(0);
      expect(wire.patchCount).toBe(0);
      await wire.settle();
    });
    await test.step("actual Administrator saves and exact trimmed no-op preserve timestamps; BOM-only remains ordinarily readable", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "administrator",
        mode: "edit",
      });
      await page.goto(path);
      await ready(page);
      const before = wire.patchCount;
      await page.getByLabel("Name", { exact: true }).fill("\u0085 \u0085");
      await page.getByLabel("Name", { exact: true }).press("Enter");
      await expect(
        page.getByRole("alert", {
          name: "Metadata fields need attention",
          exact: true,
        }),
      ).toBeVisible();
      expect(wire.patchCount).toBe(before);
      const admin: Draft = {
        name: "Administrator metadata",
        acceptedName: "Administrator metadata",
        description: " Administrator exact description. ",
      };
      await save(page, wire, admin, "administrator");
      const state = await owner.checkpoint(),
        version = await wire.version();
      await save(
        page,
        wire,
        { ...admin, name: " \u0085" + admin.name + "\u0085 " },
        "administrator",
      );
      expect(await wire.version()).toBe(version);
      expect(await owner.checkpoint()).toEqual(state);
      await save(
        page,
        wire,
        { name: "\uFEFF", acceptedName: "\uFEFF", description: "" },
        "administrator",
      );
      const mappingResponse = wire.mappingResponse();
      await page
        .getByRole("link", { name: "Back to mapping", exact: true })
        .click();
      await expect(page).toHaveURL(
        new RegExp(mappingPath(fixture.primary.scope) + "$"),
      );
      await expect(
        page.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
      ).toBeVisible();
      requireMetadata(
        (await capturedGetBody(await mappingResponse)).length > 0,
        "ordinary mapping response body was empty",
      );
      await wire.settle();
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      requireMetadata(
        (await wire.latestMapping()).name === "\uFEFF",
        "ordinary read rejected the accepted BOM-only name",
      );
    });
    const unicode: Draft = {
      name: " \u0085" + "😀".repeat(200) + "\u0085 ",
      acceptedName: "😀".repeat(200),
      description:
        " \uFEFFe\u0301\n<img src=controlled-invalid onerror=document.documentElement.dataset.metadataMarkup=1> ",
    };
    let active!: Page;
    await test.step("two actual independent Publisher sessions receive one tag, then stale unchanged CAS412 preserves raw mine until explicit review/save", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "edit",
      });
      await page.goto(path);
      await ready(page);
      secondContext = await browser.newContext({
        baseURL: fixture.readOrigin,
        viewport: { width: 390, height: 844 },
      });
      active = await secondContext.newPage();
      const contender = new MetadataWire(active, owner);
      other = contender;
      await contender.install();
      await active.goto(path);
      await ready(active);
      await wire.sameBaseline(contender);
      const mine: Draft = {
        name: " \u0085\uFEFF\u0085 ",
        acceptedName: "\uFEFF",
        description: "",
      };
      await fill(active, mine);
      await save(page, wire, unicode);
      const trusted = await owner.checkpoint();
      contender.consumeRefresh(await refresh(active, contender));
      const index = contender.patchCount,
        contexts = contender.contextCount,
        mappings = contender.actionReadCount;
      await contender.arm(mine);
      await button(active, "Save metadata").click();
      await expect(heading(active, "Review before another save")).toBeVisible();
      await contender.complete(index, 412);
      await contender.fresh(index, contexts, mappings);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          mine.name &&
          (await active
            .getByLabel("Description (optional)", { exact: true })
            .inputValue()) === mine.description,
        "actual conflict replaced the raw draft",
      );
      await expect(button(active, "Save metadata")).toBeDisabled();
      expect(await owner.checkpoint()).toEqual(trusted);
      await button(active, "Review latest").click();
      await expect(heading(active, "Compare mapping metadata")).toBeVisible();
      for (const region of ["Base metadata", "Current metadata", "My metadata"])
        await expect(
          active.getByRole("region", { name: region, exact: true }),
        ).toBeVisible();
      expect(contender.patchCount).toBe(index + 1);
      await button(active, "Keep mine").click();
      await save(active, contender, mine);
      await expect(active.locator(".metadata-editor img")).toHaveCount(0);
      expect(
        await active.locator("html").getAttribute("data-metadata-markup"),
      ).toBeNull();
    });
    requireMetadata(other, "expected second actual session");
    const editorWire = other;
    let restoredAdmission: RefreshProof | undefined;
    await test.step("dirty navigation is deliberate; a genuine native append leaves the received metadata tag valid and the next edit preserves its new head", async () => {
      const draft: Draft = {
        name: "After the native append",
        acceptedName: "After the native append",
        description: " Exact metadata survives the new native head. ",
      };
      await fill(active, draft);
      await active
        .getByRole("link", { name: "Back to mapping", exact: true })
        .click();
      await expect(heading(active, "Leave this metadata edit?")).toBeVisible();
      await button(active, "Keep editing").click();
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "leave decision discarded retained draft",
      );
      const before = await owner.checkpoint(),
        patches = editorWire.patchCount;
      expect(before.counts).toEqual(initial.counts);
      const append = await owner.control({ action: "append-primary-revision" });
      requireMetadata(
        append.action === "append-primary-revision",
        "expected genuine append",
      );
      const after = await owner.checkpoint();
      expect(after.state.version).toBe(before.state.version);
      expect(after.state.headRevisionId).toBe(
        append.appendedRevision.scope.revisionId,
      );
      expect(after.counts.revisions).toBe(before.counts.revisions + 1);
      expect(after.counts.reportAttempts).toBe(before.counts.reportAttempts);
      expect(after.counts.reportResults).toBe(before.counts.reportResults);
      expect(after.counts.workersCreated).toBe(
        before.counts.workersCreated + 1,
      );
      await page.goto(path);
      await ready(page);
      await wire.sameBaseline(editorWire);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "append reset dirty metadata",
      );
      const refreshed = await refresh(active, editorWire);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "authority refresh reset dirty metadata",
      );
      expect(editorWire.patchCount).toBe(patches);
      await save(active, editorWire, draft, "publisher", refreshed);
      const edited = await owner.checkpoint();
      expect(edited.state.headRevisionId).toBe(after.state.headRevisionId);
      expect(edited.state.headSequence).toBe(after.state.headSequence);
      expect(edited.counts).toEqual(after.counts);
    });
    await test.step("fresh Publisher revocation suppresses PATCH and the final actual server authority check refuses a previously admitted request", async () => {
      const draft: Draft = {
        name: "Retained after authority refusal",
        acceptedName: "Retained after authority refusal",
        description: " No automatic write on restoration. ",
      };
      await fill(active, draft);
      let count = editorWire.patchCount;
      await owner.control({ action: "revoke-publisher" });
      await button(active, "Save metadata").click();
      await expect(
        heading(active, "Metadata editor unavailable"),
      ).toBeVisible();
      await expect(active.getByLabel("Name", { exact: true })).toHaveCount(0);
      expect(editorWire.patchCount).toBe(count);
      await owner.checkpoint();
      await owner.control({ action: "restore-publisher" });
      const restoredForHeldSave = await refresh(active, editorWire);
      await ready(active);
      requireMetadata(
        (await active.getByLabel("Name", { exact: true }).inputValue()) ===
          draft.name,
        "same-actor restoration lost retained draft",
      );
      expect(editorWire.patchCount).toBe(count);
      editorWire.consumeRefresh(restoredForHeldSave);
      const contexts = editorWire.contextCount,
        mappings = editorWire.actionReadCount,
        held = await editorWire.arm(draft, "publisher", "hold-forward");
      await button(active, "Save metadata").click();
      await expect.poll(held.entered).toBe(true);
      await editorWire.fresh(count, contexts, mappings);
      await owner.control({ action: "revoke-publisher" });
      held.release();
      await editorWire.finalRefusal(count);
      expect(editorWire.patchCount).toBe(count + 1);
      await owner.checkpoint();
      await owner.control({ action: "restore-publisher" });
      restoredAdmission = await refresh(active, editorWire);
      await ready(active);
      expect(editorWire.patchCount).toBe(count + 1);
    });
    await test.step("a real completed200 is independently captured before delivery loss; explicit matching GET never claims that save was acknowledged", async () => {
      const draft: Draft = {
        name: "Unacknowledged actual metadata",
        acceptedName: "Unacknowledged actual metadata",
        description: " Exact lost-response description. ",
      };
      editorWire.consumeRefresh(restoredAdmission);
      restoredAdmission = undefined;
      await fill(active, draft);
      const count = editorWire.patchCount,
        contexts = editorWire.contextCount,
        mappings = editorWire.actionReadCount;
      await editorWire.arm(draft, "publisher", "lose");
      await button(active, "Save metadata").click();
      await expect
        .poll(() => editorWire.patchCount, {
          message: "Expected deliberate lost-response PATCH did not arrive",
          timeout: 10000,
        })
        .toBe(count + 1);
      await editorWire.complete(count, 200);
      await editorWire.fresh(count, contexts, mappings);
      await expect(heading(active, "Save outcome is unknown")).toBeVisible();
      await expect(heading(active, "Metadata save confirmed")).toHaveCount(0);
      const trusted = await owner.checkpoint(),
        reads = editorWire.metadataReadCount;
      await button(active, "Check current metadata").click();
      await expect(
        active.getByText("Current values match the attempted values.", {
          exact: false,
        }),
      ).toBeVisible();
      await expect(heading(active, "Save outcome is unknown")).toBeVisible();
      await expect(heading(active, "Metadata save confirmed")).toHaveCount(0);
      expect(editorWire.patchCount).toBe(count + 1);
      await editorWire.settle();
      expect(editorWire.metadataReadCount).toBe(reads + 1);
      expect(await owner.checkpoint()).toEqual(trusted);
      await button(active, "Review latest").click();
      await expect(heading(active, "Compare mapping metadata")).toBeVisible();
      await button(active, "Use current").click();
      await expect(heading(active, "Save outcome is unknown")).toHaveCount(0);
      await expect(heading(active, "Metadata save confirmed")).toHaveCount(0);
      expect(editorWire.patchCount).toBe(count + 1);
    });
    await test.step("ordinary API and same-owned PostgreSQL restart preserve current metadata plus original immutable revision and saved report facts", async () => {
      const before = await owner.checkpoint();
      const mappingResponse = editorWire.mappingResponse();
      await active
        .getByRole("link", { name: "Back to mapping", exact: true })
        .click();
      await expect(active).toHaveURL(
        new RegExp(mappingPath(fixture.primary.scope) + "$"),
      );
      await expect(
        active.getByRole("region", {
          name: "Current mapping metadata",
          exact: true,
        }),
      ).toBeVisible();
      requireMetadata(
        (await capturedGetBody(await mappingResponse)).length > 0,
        "ordinary mapping response body was empty",
      );
      await editorWire.settle();
      await active.goto(revisionPath(fixture.primary.initialRevision.scope));
      await expect(active.getByRole("heading", { level: 1 })).toHaveText(
        `Revision ${fixture.primary.initialRevision.sequence}`,
      );
      await owner.control({ action: "api-stop" });
      await owner.control({
        action: "api-start",
        profile: "publisher",
        mode: "edit",
      });
      await owner.control({ action: "pg-normal-restart" });
      await page.goto(path);
      await ready(page);
      await wire.sameBaseline(editorWire);
      await active.reload();
      await expect(active.getByRole("heading", { level: 1 })).toHaveText(
        `Revision ${fixture.primary.initialRevision.sequence}`,
      );
      const revisionId = active
        .locator("dt", { hasText: /^Revision ID$/ })
        .locator("xpath=following-sibling::dd[1]");
      await expect(revisionId).toHaveText(
        fixture.primary.initialRevision.scope.revisionId,
      );
      const digest = active
        .locator("dt", { hasText: /^Native digest$/ })
        .locator("xpath=following-sibling::dd[1]");
      requireMetadata(
        (await digest.textContent())?.includes(
          fixture.primary.initialRevision.nativeDigest.value,
        ),
        "original native digest changed after metadata edits/append/restart",
      );
      const after = await owner.checkpoint();
      expect(after).toEqual(before);
      expect(after.counts).toEqual({
        ...initial.counts,
        revisions: initial.counts.revisions + 1,
        workersCreated: initial.counts.workersCreated + 1,
      });
      expect(after.counts.reportResults).toBeGreaterThanOrEqual(2);
      const dimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.width + 1);
    });
    await wire.closed();
    await editorWire.closed();
  } catch (error) {
    primaryFailure = error;
    hasPrimaryFailure = true;
    try {
      await wire.diagnoseFailure(other);
    } catch {
      /* Preserve the same primary failure if diagnostics are unavailable. */
    }
  } finally {
    wire.release();
    other?.release();
    const cleanups = await Promise.allSettled([
      wire.disposeReads(),
      other?.disposeReads() ?? Promise.resolve(),
    ]);
    let cleanupFailure: Error | null = null;
    for (const result of cleanups)
      if (result.status === "rejected") {
        cleanupFailure ??=
          result.reason instanceof Error &&
          result.reason.message.startsWith("Mapping metadata browser fixture:")
            ? result.reason
            : new Error(
                "Mapping metadata browser fixture: owned GET cleanup failed",
              );
      }
    try {
      await secondContext?.close();
    } catch {
      cleanupFailure ??= new Error(
        "Mapping metadata browser fixture: owned second context could not close",
      );
    }
    if (!hasPrimaryFailure && cleanupFailure) throw cleanupFailure;
  }
  if (hasPrimaryFailure) throw primaryFailure;
});
