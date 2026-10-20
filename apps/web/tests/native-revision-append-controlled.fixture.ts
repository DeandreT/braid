import type { Page, Request, Route, Response } from "@playwright/test";
import { test, expect } from "./native-revision-append.fixture";
import { createHash } from "node:crypto";
import type { components } from "../src/api/generated";
// Pure exports only: importing these does not request the catalog automatic owner.
// The test export uses F's own automatic owner, without any earlier C/E owner.
import {
  synthetic,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
} from "./catalog.fixture";

// Deliberately synthetic responses. These tests establish browser behavior only,
// never actual persistence, restricted workers, native admission or DB concurrency.
// F's own automatic fixture supplies the ordinary packaged host for both test files.
// Root must keep global trace/media/DOM capture disabled.
export {
  test,
  expect,
  synthetic,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
};
export type Context = components["schemas"]["CatalogContextV1"];
export type Mapping = components["schemas"]["CatalogMappingV1"];
export type MappingScope = components["schemas"]["CatalogMappingScopeV1"];
export type Revision = components["schemas"]["CatalogRevisionV1"];
export type Receipt =
  components["schemas"]["CatalogRevisionIngestionReceiptV1"];
export type Reply = {
  status?: number;
  json?: unknown;
  headers?: Record<string, string>;
  abort?: boolean;
};
export type Plan = Reply | (() => Reply | Promise<Reply>);
export type Kind =
  "context" | "mapping" | "append" | "receipt" | "resume" | "revision";
export type Wire = Readonly<{
  exactBytes: boolean;
  exactQuery: boolean;
  equalHeadOnly: boolean;
  correctHeaders: boolean;
  freshToken: boolean;
  sameOriginalBytes: boolean;
  sameOriginalQuery: boolean;
  sameOriginalKey: boolean;
  keyDiffersFromPreviousAttempt: boolean;
}>;
export const clone = <T>(value: T): T => structuredClone(value);
export const contextPath = "/api/v1/catalog/context";
export const appendPath = (scope: MappingScope): string =>
  `${mappingPath(scope)}/revisions/new`;
export const otherActor = "10000000-0000-4000-8000-000000000088";
export const otherOrganization = "10000000-0000-4000-8000-000000000089";
export const otherProjectId = "20000000-0000-4000-8000-000000000088";
export const otherMappingId = "30000000-0000-4000-8000-000000000088";
export const originalBytes = (): Buffer =>
  Buffer.from([0x7b, 0x0d, 0x0a, 0x00, 0xc3, 0xa9, 0x7d, 0xff]);
export const replacementBytes = (): Buffer =>
  Buffer.from([0xff, 0x00, 0x0a, 0x0d, 0x22, 0x2b, 0x7d]);
export const codeReply = (status: number, code: string): Reply => ({
  status,
  json: { code },
});
const canonicalId = (value: string): boolean =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
    value,
  ) && value !== "00000000-0000-0000-0000-000000000000";

/** Private request material never escapes as assertion output or fixture metadata. */
export class ControlledAppend {
  readonly context: Context = {
    ...clone(synthetic.context),
    features: { ...synthetic.context.features, uploadRevisions: true },
  };
  readonly mapping: Mapping = {
    ...clone(synthetic.mapping),
    access: {
      role: "publisher",
      actions: { ...synthetic.mapping.access.actions, uploadRevisions: true },
    },
    head: { ...clone(synthetic.mapping.head), sequence: "9007199254740993" },
  };
  readonly wires: Wire[] = [];
  contextReads = 0;
  mappingReads = 0;
  receiptReads = 0;
  revisionReads = 0;
  resumeCount = 0;
  unexpectedRequests = 0;
  contextReply: Plan | null = null;
  mappingReply: Plan | null = null;
  receiptReply: Plan | null = null;
  revisionReply: Plan | null = null;
  appendReply: Plan = { abort: true };
  resumeReply: Plan = { abort: true };
  #bytes = originalBytes();
  #filename = "original.native.json";
  #reviewed = clone(this.mapping.head);
  #admission: Revision["admission"] = "valid";
  #operation = 1;
  #lastToken = "";
  #lastMutationToken: string | null = null;
  #previousAttemptKey: string | null = null;
  #original: { body: Buffer; query: string; key: string } | null = null;
  #resumeValid = true;
  #receiptValid = true;
  #released = false;
  #releases: (() => void)[] = [];
  #held: { kind: Kind; capture: (request: Request) => void } | null = null;
  constructor(readonly page: Page) {}
  get appendCount(): number {
    return this.wires.length;
  }
  get scope(): MappingScope {
    return this.mapping.scope;
  }
  get otherScope(): MappingScope {
    return { ...this.scope, mappingId: otherMappingId };
  }
  get otherProjectScope(): MappingScope {
    return { ...this.scope, projectId: otherProjectId };
  }
  get futureScope(): Revision["scope"] {
    return {
      ...this.scope,
      revisionId: `40000000-0000-4000-8000-${String(88_000_000 + this.#operation).padStart(12, "0")}`,
    };
  }
  get operationPath(): string {
    return (
      catalogApi(projectPath(this.scope)) +
      `/revision-ingestions/50000000-0000-4000-8000-${String(88_000_000 + this.#operation).padStart(12, "0")}`
    );
  }
  get resumeValid(): boolean {
    return this.#resumeValid;
  }
  get receiptValid(): boolean {
    return this.#receiptValid;
  }
  get lastKeyDiffers(): boolean {
    return this.wires.at(-1)?.keyDiffersFromPreviousAttempt === true;
  }
  draft(filename = "original.native.json"): void {
    this.#filename = filename;
  }
  file(
    name = "original.native.json",
    bytes = this.#bytes,
    mimeType = "application/json",
  ) {
    this.#bytes = Buffer.from(bytes);
    return { name, mimeType, buffer: Buffer.from(bytes) };
  }
  /** Deliberate test setup. A current-head read alone never changes this expectation. */
  acceptCurrentHead(): void {
    this.#reviewed = clone(this.mapping.head);
  }
  advanceHead(
    sequence = (BigInt(this.mapping.head.sequence) + 1n).toString(),
  ): void {
    this.mapping.head = {
      ...clone(this.mapping.head),
      scope: {
        ...this.scope,
        revisionId: `40000000-0000-4000-8000-${String(89_000_000 + this.#operation).padStart(12, "0")}`,
      },
      sequence,
    };
  }
  nextAttempt(): void {
    ++this.#operation;
    this.#previousAttemptKey = this.#original?.key ?? null;
    this.#original = null;
    this.acceptCurrentHead();
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
    return {
      operationId: this.operationPath.split("/").at(-1)!,
      project: {
        organizationId: this.scope.organizationId,
        projectId: this.scope.projectId,
      },
      futureMapping: clone(this.scope),
      futureRevision: this.futureScope,
      phase,
      claimCount: count,
      reservedAtUtc: "2026-10-15T12:00:00.1234567Z",
      completedAtUtc: terminal ? "2026-10-15T12:00:01.1234567Z" : null,
      originalFilename: this.#filename,
      committedSequence: committed
        ? (BigInt(this.#reviewed.sequence) + 1n).toString()
        : null,
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
  conflict(count = 1): Reply {
    return {
      status: 409,
      json: {
        outcome: "headConflict",
        receipt: this.receipt("headConflict", count),
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
      sequence: (BigInt(this.#reviewed.sequence) + 1n).toString(),
      admission: this.#admission,
      committedAtUtc: "2026-10-15T12:00:01.1234567Z",
      creatorPrincipalId: synthetic.actor,
      originalFilename: this.#filename,
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
    let entered = false,
      canceled = false;
    let held: Request | null = null;
    this.page.on("requestfailed", (request) => {
      if (request === held) canceled = true;
    });
    const plan = async () => {
      entered = true;
      await gate;
      return reply;
    };
    this.#held = {
      kind,
      capture: (request) => {
        held = request;
      },
    };
    if (kind === "append") this.appendReply = plan;
    else if (kind === "resume") this.resumeReply = plan;
    else if (kind === "receipt") this.receiptReply = plan;
    else if (kind === "context") this.contextReply = plan;
    else if (kind === "mapping") this.mappingReply = plan;
    else this.revisionReply = plan;
    this.#releases.push(release);
    return { release, entered: () => entered, canceled: () => canceled };
  }
  response(kind: Kind, status: number): Promise<Response> {
    const origin = new URL(this.page.url()).origin;
    const path =
      kind === "context"
        ? contextPath
        : kind === "mapping"
          ? catalogApi(mappingPath(this.scope))
          : kind === "revision"
            ? catalogApi(revisionPath(this.futureScope))
            : kind === "append"
              ? catalogApi(mappingPath(this.scope)) + "/revisions"
              : this.operationPath + (kind === "resume" ? "/resume" : "");
    return this.page.waitForResponse((response) => {
      const request = response.request(),
        url = new URL(response.url());
      return (
        url.origin === origin &&
        url.pathname === path &&
        request.method() ===
          (kind === "append" || kind === "resume" ? "POST" : "GET") &&
        (kind === "append" || url.search === "") &&
        response.status() === status
      );
    });
  }
  failedWrite(kind: "append" | "resume" = "append"): Promise<Request> {
    const path =
      kind === "append"
        ? catalogApi(mappingPath(this.scope)) + "/revisions"
        : this.operationPath + "/resume";
    return this.page.waitForEvent(
      "requestfailed",
      (request) =>
        request.method() === "POST" && new URL(request.url()).pathname === path,
    );
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
        this.#lastToken = `controlled-append-admission-${this.contextReads}`;
        plan = this.contextReply ?? {
          json: { ...this.context, requestToken: this.#lastToken },
        };
      } else if (
        method === "GET" &&
        !url.search &&
        [this.scope, this.otherScope, this.otherProjectScope].some(
          (scope) => catalogApi(mappingPath(scope)) === url.pathname,
        )
      ) {
        kind = "mapping";
        ++this.mappingReads;
        const scope = [
          this.scope,
          this.otherScope,
          this.otherProjectScope,
        ].find(
          (candidate) => catalogApi(mappingPath(candidate)) === url.pathname,
        )!;
        const primary =
          scope.mappingId === this.scope.mappingId &&
          scope.projectId === this.scope.projectId;
        plan = this.mappingReply ?? {
          json: primary
            ? clone(this.mapping)
            : {
                ...clone(this.mapping),
                scope,
                head: {
                  ...clone(this.mapping.head),
                  scope: {
                    ...scope,
                    revisionId: this.mapping.head.scope.revisionId,
                  },
                },
              },
        };
      } else if (
        method === "POST" &&
        url.pathname === catalogApi(mappingPath(this.scope)) + "/revisions"
      ) {
        kind = "append";
        this.observeAppend(request, url);
        plan = this.appendReply;
      } else if (
        method === "GET" &&
        url.pathname === this.operationPath &&
        !url.search
      ) {
        kind = "receipt";
        ++this.receiptReads;
        const headers = request.headers();
        this.#receiptValid &&=
          (request.postDataBuffer()?.length ?? 0) === 0 &&
          ![
            "x-xsrf-token",
            "idempotency-key",
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "if-range",
            "content-type",
            "content-encoding",
          ].some((name) => name in headers);
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
            "if-modified-since",
            "if-unmodified-since",
            "if-range",
            "content-encoding",
          ].some((name) => name in headers) &&
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
        !url.search &&
        [this.scope, this.otherProjectScope].some(
          (scope) => catalogApi(projectPath(scope)) === url.pathname,
        )
      ) {
        const scope =
          url.pathname === catalogApi(projectPath(this.scope))
            ? this.scope
            : this.otherProjectScope;
        plan = {
          json: {
            ...clone(synthetic.project),
            scope: {
              organizationId: scope.organizationId,
              projectId: scope.projectId,
            },
          },
        };
      } else if (
        method === "GET" &&
        url.search === "?limit=10" &&
        [this.scope, this.otherScope, this.otherProjectScope].some(
          (scope) =>
            catalogApi(mappingPath(scope)) + "/revisions" === url.pathname,
        )
      ) {
        plan = { json: { revisions: [], nextCursor: null } };
      } else {
        ++this.unexpectedRequests;
        plan = codeReply(404, "notFound");
      }
      if (kind && this.#held?.kind === kind) this.#held.capture(request);
      const reply = typeof plan === "function" ? await plan() : plan;
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
            "A controlled append response could not be delivered.",
          );
      }
    });
  }
  private observeAppend(request: Request, url: URL): void {
    const headers = request.headers(),
      body = request.postDataBuffer();
    const key = headers["idempotency-key"] ?? "",
      token = headers["x-xsrf-token"] ?? "",
      query = url.search;
    const expectedQuery = `?head=equal&expectedHeadRevisionId=${this.#reviewed.scope.revisionId}&originalFilename=${encodeURIComponent(this.#filename).replace(/[!'()*]/g, (value) => "%" + value.charCodeAt(0).toString(16).toUpperCase())}`;
    const keys = [...url.searchParams.keys()];
    this.#original ??= { body: Buffer.from(body ?? []), query, key };
    this.wires.push({
      exactBytes: body !== null && body.equals(this.#bytes),
      exactQuery:
        query === expectedQuery &&
        /^[\x00-\x7f]*$/.test(query) &&
        query.length <= 65536 &&
        ("POST " + url.pathname + query + " HTTP/1.1\r\n").length <= 65536,
      equalHeadOnly:
        keys.length === 3 &&
        new Set(keys).size === 3 &&
        keys.every((name) =>
          ["head", "expectedHeadRevisionId", "originalFilename"].includes(name),
        ) &&
        url.searchParams.get("head") === "equal" &&
        url.searchParams.get("expectedHeadRevisionId") ===
          this.#reviewed.scope.revisionId &&
        url.searchParams.get("originalFilename") === this.#filename,
      correctHeaders:
        headers["content-type"] === "application/octet-stream" &&
        canonicalId(key) &&
        token === this.#lastToken &&
        ![
          "if-match",
          "if-none-match",
          "if-modified-since",
          "if-unmodified-since",
          "if-range",
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
      keyDiffersFromPreviousAttempt:
        this.#previousAttemptKey !== null && key !== this.#previousAttemptKey,
    });
    this.#lastMutationToken = token;
  }
  release(): void {
    this.#released = true;
    for (const release of this.#releases) release();
    this.#bytes = Buffer.alloc(0);
    this.#original = null;
    this.#previousAttemptKey = null;
    this.#lastToken = "";
    this.#lastMutationToken = null;
  }
}
