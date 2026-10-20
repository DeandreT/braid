import {
  test as base,
  expect,
  type Page,
  type Request,
  type Route,
} from "@playwright/test";
import { createHash } from "node:crypto";
import { BrowserSessionStaticOwner } from "../../../../tools/browser-session-controlled/owner.mjs";
import {
  api,
  browserPaths,
  clone,
  context,
  ids,
  mapping,
  mappingPath,
  project,
  projectPath,
  revision,
  revisionPath,
  savedReports,
  scope,
  type Context,
  type Feature,
} from "./data";

export {
  expect,
  ids,
  browserPaths,
  mappingPath,
  projectPath,
  revisionPath,
  api,
};
export type Kind =
  | "status"
  | "context"
  | "projects"
  | "project"
  | "mappings"
  | "mapping"
  | "metadata"
  | "revisions"
  | "revision"
  | "projection"
  | "validation"
  | "patch"
  | Feature
  | "receipt"
  | "resume";
export type Reply = {
  status?: number;
  json?: unknown;
  body?: string;
  headers?: Record<string, string>;
  abort?: boolean;
};
type Plan = Reply | (() => Reply | Promise<Reply>);
type Snapshot = Record<string, boolean | number | string>;
const sessionKinds = [
  "checking",
  "disabled",
  "development",
  "anonymous",
  "authenticationRequired",
  "authenticated",
  "unavailable",
] as const;
export const refused = (status: number, code = "unavailable"): Reply => ({
  status,
  json: { version: 1, code },
});
export const catalogRefused = (status: number, code: string): Reply => ({
  status,
  json: { code },
});
const check: (value: unknown) => asserts value = (value) => {
  if (!value)
    throw new Error("The controlled browser-session predicate failed.");
};
const token = (audience: string, sequence: number): string =>
  createHash("sha256")
    .update(`owned-synthetic:${audience}:${sequence}`)
    .digest("base64url");
type Wire = Readonly<{
  kind: Feature | "resume";
  exactBody: boolean;
  exactQuery: boolean;
  sameOriginalBody: boolean;
  sameOriginalKey: boolean;
  sameOriginalQuery: boolean;
  freshCatalogToken: boolean;
  distinctAudiences: boolean;
  correctHeaders: boolean;
  matchesCurrentValidator: boolean;
  differsFromOriginalValidator: boolean;
}>;
type Original = {
  body: Buffer;
  key: string;
  query: string;
  token: string;
  validator: string;
};
const bounded = async <T>(
  original: Promise<T>,
  milliseconds = 10000,
): Promise<T> => {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      original,
      new Promise<never>((_, reject) => {
        timer = setTimeout(
          () =>
            reject(
              new Error(
                "An original controlled browser operation did not settle.",
              ),
            ),
          milliseconds,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
};
// Use normal DOM input events without private field values in Playwright action titles.
export async function editField(
  page: Page,
  selector: string,
  value: string,
): Promise<void> {
  const field = page.locator(selector);
  await expect(field).toBeVisible();
  await expect(field).toBeEnabled();
  await expect(field).toBeEditable();
  await field.evaluate((node, ownedValue) => {
    if (
      !(
        node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement
      ) ||
      node.disabled ||
      node.readOnly ||
      node.closest("[inert]") ||
      node.getClientRects().length === 0
    )
      throw new Error("The controlled visible field was unavailable.");
    const prototype =
      node instanceof HTMLTextAreaElement
        ? HTMLTextAreaElement.prototype
        : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
    if (!setter)
      throw new Error("The controlled field accessor was unavailable.");
    setter.call(node, ownedValue);
    node.dispatchEvent(new Event("input", { bubbles: true }));
    node.dispatchEvent(new Event("change", { bubbles: true }));
  }, value);
}
export async function privateMarkersVisuallyHidden(
  page: Page,
  markers: readonly string[],
): Promise<boolean> {
  return page.evaluate((markers) => {
    const painted = (node: Element) => {
      const visibility = getComputedStyle(node).visibility;
      if (visibility === "hidden" || visibility === "collapse") return false;
      for (
        let ancestor: Element | null = node;
        ancestor !== null;
        ancestor = ancestor.parentElement
      ) {
        const style = getComputedStyle(ancestor);
        if (
          style.display === "none" ||
          style.contentVisibility === "hidden" ||
          style.opacity === "0"
        )
          return false;
      }
      return true;
    };
    const area = (rects: DOMRectList) =>
      Array.from(rects).some((rect) => rect.width > 0 && rect.height > 0);
    // Geometry and computed CSS include hidden ancestors. Neither inert nor
    // aria-hidden makes otherwise painted private content visually safe.
    const visible = (node: Element) =>
      painted(node) && area(node.getClientRects());
    const operands: string[] = [];
    document
      .querySelectorAll("input,textarea,a,[title],[aria-label]")
      .forEach((node) => {
        if (!visible(node)) return;
        if (
          node instanceof HTMLInputElement ||
          node instanceof HTMLTextAreaElement
        )
          operands.push(node.value);
        for (const name of ["href", "title", "aria-label"]) {
          const value = node.getAttribute(name);
          if (value) operands.push(value);
        }
      });
    const text = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const range = document.createRange();
    while (text.nextNode()) {
      const parent = text.currentNode.parentElement;
      if (!parent || !painted(parent)) continue;
      // A text range also detects painted text in a display:contents parent.
      range.selectNodeContents(text.currentNode);
      if (area(range.getClientRects()))
        operands.push(text.currentNode.textContent ?? "");
    }
    return markers.every((marker) =>
      operands.every((value) => !value.includes(marker)),
    );
  }, markers);
}
export class Held {
  readonly originalEntered: Promise<void>;
  readonly originalFinished: Promise<void>;
  get entered(): Promise<void> {
    return bounded(this.originalEntered);
  }
  get finished(): Promise<void> {
    return bounded(this.originalFinished);
  }
  #enter!: () => void;
  #finish!: () => void;
  #release!: () => void;
  #gate: Promise<void>;
  #timer: ReturnType<typeof setTimeout> | null = null;
  request: Request | null = null;
  canceled = false;
  settled = false;
  constructor(
    readonly kind: Kind,
    readonly reply: Reply | (() => Reply),
  ) {
    this.originalEntered = new Promise((resolve) => {
      this.#enter = resolve;
    });
    this.originalFinished = new Promise((resolve) => {
      this.#finish = resolve;
    });
    this.#gate = new Promise((resolve) => {
      this.#release = resolve;
    });
  }
  async wait(request: Request): Promise<Reply> {
    this.request = request;
    this.#enter();
    this.#timer = setTimeout(() => this.release(), 10000);
    await this.#gate;
    return typeof this.reply === "function" ? this.reply() : this.reply;
  }
  release(): void {
    if (this.#timer !== null) clearTimeout(this.#timer);
    this.#timer = null;
    this.#release();
  }
  finish(): void {
    this.settled = true;
    this.#finish();
  }
  toString(): string {
    return "HeldBrowserSessionReply";
  }
}

export class ControlledSession {
  readonly context: Context = context();
  readonly mapping = clone(mapping);
  readonly project = clone(project);
  readonly revision = clone(revision);
  readonly wires: Wire[] = [];
  readonly counts = new Map<Kind, number>();
  readonly order: Kind[] = [];
  readonly plans = new Map<Kind, Plan>();
  readonly held: Held[] = [];
  unexpected = 0;
  routeFailures = 0;
  authPosts = 0;
  mode: "session" | "development" | "disabled" = "session";
  session: "anonymous" | "authenticationRequired" | "authenticated" =
    "authenticated";
  #epoch = 1;
  #statusToken = "";
  #actionToken = "";
  #catalogToken = "";
  #original = new Map<Feature, Original>();
  #draft = {
    name: "Owned private draft",
    description: "Owned private description",
    filename: "owned +%.native.json",
  };
  #privateValues = new Set<string>(
    [
      ...Object.values(this.#draft),
      ...Object.values(ids),
      project.name,
      project.description,
      mapping.name,
      mapping.description,
      revision.originalFilename,
      "Owned private saved explanation",
      "Owned delayed private row",
      "Owned unsent retained value",
      "Owned memory-only draft",
      "owned-marker",
      "Owned foreign overwrite",
      "Owned foreign description",
      "owned-foreign-replacement.native.json",
      "owned-current-file.native.json",
      "Owned current healthy upload",
      "Owned current healthy description",
      "Owned distinct current baseline",
      "Owned distinct baseline description",
    ].filter((value): value is string => typeof value === "string"),
  );
  #bytes = Buffer.from([0x7b, 0x0d, 0x0a, 0x00, 0xc3, 0xa9, 0x7d, 0xff]);
  #reviewedHead = ids.revision;
  #operationFeature: "upload" | "append" = "upload";
  #work = new Set<Promise<void>>();
  #requestedSamples = 0;
  #sampleTasks: Promise<Snapshot>[] = [];
  #sampleTail: Promise<void> = Promise.resolve();
  #closed = false;
  #disposal: Promise<void> | null = null;
  #originalDrain: Promise<void[]> | null = null;
  constructor(
    readonly page: Page,
    readonly origin: string,
  ) {}
  count(kind: Kind): number {
    return this.counts.get(kind) ?? 0;
  }
  get fingerprint(): string {
    return (
      "v1." +
      token(
        `${this.context.allowedOrganizationId}/${this.context.actorPrincipalId}`,
        this.#epoch,
      )
    );
  }
  epoch(): void {
    ++this.#epoch;
  }
  identity(
    actor: string = ids.actor,
    organization: string = ids.organization,
  ): void {
    this.context.actorPrincipalId = actor;
    this.context.allowedOrganizationId = organization;
    ++this.#epoch;
  }
  draft(
    name: string,
    description = this.#draft.description,
    filename = this.#draft.filename,
  ): void {
    this.#draft = { name, description, filename };
    this.#remember(name, description, filename);
  }
  #remember(...values: string[]): void {
    check(this.#privateValues.size + values.length <= 1024);
    for (const value of values)
      if (value.length > 0) this.#privateValues.add(value);
  }
  file() {
    return {
      name: this.#draft.filename,
      mimeType: "application/json",
      buffer: Buffer.from(this.#bytes),
    };
  }
  status(): unknown {
    if (this.mode !== "session") return { version: 1, mode: this.mode };
    this.#actionToken = token("auth-action", this.count("status"));
    this.#statusToken = token("status-epoch", this.count("status"));
    this.#remember(this.#actionToken, this.#statusToken, this.fingerprint);
    return {
      version: 1,
      mode: "session",
      state: this.session,
      authActionRequestToken: this.#actionToken,
      requestToken: this.session === "authenticated" ? this.#statusToken : null,
      identityContext:
        this.session === "authenticated" ? this.fingerprint : null,
    };
  }
  contextReply(): Reply {
    this.#catalogToken = token("catalog-epoch", this.count("context"));
    this.#remember(this.#catalogToken, this.fingerprint);
    return {
      json: {
        ...clone(this.context),
        mode: this.mode,
        requestToken: this.#catalogToken,
        ...(this.mode === "session"
          ? { identityContext: this.fingerprint }
          : {}),
      },
    };
  }
  hold(kind: Kind, reply: Reply | (() => Reply)): Held {
    check(this.held.length < 16);
    const held = new Held(kind, reply);
    this.held.push(held);
    this.plans.set(kind, () => held.wait(held.request!));
    return held;
  }
  metadata() {
    return {
      scope: clone(scope),
      name: this.mapping.name,
      description: this.mapping.description,
      version: this.mapping.version,
    };
  }
  tag() {
    return `"metadata-v1:${ids.organization}:${ids.project}:${ids.mapping}:${this.mapping.version}"`;
  }
  creation(status: 200 | 201 = 201): Reply {
    const url = `/api/v1/organizations/${ids.organization}/projects/${ids.createdProject}`;
    return {
      status,
      headers: { Location: url },
      json: {
        outcome: status === 201 ? "created" : "replayed",
        projectUrl: url,
        receipt: {
          project: {
            organizationId: ids.organization,
            projectId: ids.createdProject,
          },
          name: this.#draft.name.trim(),
          description: this.#draft.description,
          creatorPrincipalId: ids.actor,
          createdAtUtc: "2026-01-01T00:00:00Z",
          initialProjectVersion: "1",
          initialMembershipRole: "administrator",
          initialMembershipActive: true,
          initialMembershipVersion: "1",
        },
      },
    };
  }
  operationPath(feature: "upload" | "append"): string {
    return `/api/v1/organizations/${ids.organization}/projects/${ids.project}/revision-ingestions/${ids.operation}`;
  }
  receipt(feature: "upload" | "append", phase = "reserved") {
    const futureMapping =
      feature === "upload" ? { ...scope, mappingId: ids.futureMapping } : scope;
    return {
      operationId: ids.operation,
      project: { organizationId: ids.organization, projectId: ids.project },
      futureMapping,
      futureRevision: { ...futureMapping, revisionId: ids.futureRevision },
      phase,
      claimCount: 1,
      reservedAtUtc: "2026-01-01T00:00:00Z",
      completedAtUtc: phase === "committed" ? "2026-01-01T00:00:01Z" : null,
      originalFilename: this.#draft.filename,
      committedSequence:
        phase === "committed"
          ? feature === "append"
            ? "9007199254740994"
            : "1"
          : null,
      committedAdmission: phase === "committed" ? "valid" : null,
      latestFailure: null,
    };
  }
  pending(feature: "upload" | "append"): Reply {
    this.#operationFeature = feature;
    return {
      status: 202,
      headers: { Location: this.operationPath(feature) },
      json: { outcome: "inProgress", receipt: this.receipt(feature) },
    };
  }
  committed(feature: "upload" | "append"): Reply {
    this.#operationFeature = feature;
    const receipt = this.receipt(feature, "committed");
    return {
      json: {
        outcome: "committedValid",
        receipt,
        rejection: null,
        failure: null,
        committedRevisionUrl: `/api/v1/organizations/${ids.organization}/projects/${ids.project}/mappings/${receipt.futureMapping.mappingId}/revisions/${ids.futureRevision}`,
      },
    };
  }
  #classify(request: Request, url: URL): Kind | null {
    const method = request.method(),
      path = url.pathname;
    if (path.startsWith("/api/auth/") && method !== "GET") {
      ++this.authPosts;
      return null;
    }
    if (method === "GET" && path === "/api/auth/session" && !url.search)
      return "status";
    if (method === "GET" && path === "/api/v1/catalog/context" && !url.search)
      return "context";
    const pp = api(projectPath),
      mp = api(mappingPath),
      rp = api(revisionPath);
    if (
      method === "POST" &&
      path === `/api/v1/organizations/${ids.organization}/projects` &&
      !url.search
    )
      return "creation";
    if (method === "PATCH" && path === mp + "/metadata" && !url.search)
      return "patch";
    if (method === "POST" && path === pp + "/mappings") return "upload";
    if (method === "POST" && path === mp + "/revisions") return "append";
    if (
      method === "GET" &&
      url.href ===
        `${this.origin}${rp}/reports/validation/${savedReports().validation.result!.derivationId}?projectionDerivationId=${ids.projection}`
    )
      return "validation";
    if (method === "GET" && !url.search) {
      if (
        [
          pp,
          `/api/v1/organizations/${ids.organization}/projects/${ids.createdProject}`,
        ].includes(path)
      )
        return "project";
      if (path === mp) return "mapping";
      if (path === mp + "/metadata") return "metadata";
      if (
        path === rp ||
        path === mp + `/revisions/${ids.futureRevision}` ||
        path ===
          api(projectPath) +
            `/mappings/${ids.futureMapping}/revisions/${ids.futureRevision}`
      )
        return "revision";
      if (path === rp + "/reports/projection") return "projection";
      if (path === rp + "/reports/validation") return "validation";
      if (
        [this.operationPath("upload"), this.operationPath("append")].includes(
          path,
        )
      )
        return "receipt";
    }
    if (
      method === "POST" &&
      !url.search &&
      [
        this.operationPath("upload") + "/resume",
        this.operationPath("append") + "/resume",
      ].includes(path)
    )
      return "resume";
    if (method === "GET" && url.search === "?limit=10") {
      if (path === `/api/v1/organizations/${ids.organization}/projects`)
        return "projects";
      if (
        [
          pp,
          `/api/v1/organizations/${ids.organization}/projects/${ids.createdProject}`,
        ].some((p) => path === p + "/mappings")
      )
        return "mappings";
      if (path === mp + "/revisions") return "revisions";
    }
    return null;
  }
  #default(kind: Kind, request: Request, url: URL): Reply {
    switch (kind) {
      case "status":
        return { json: this.status() };
      case "context":
        return this.contextReply();
      case "projects":
        return { json: { projects: [this.project], nextCursor: null } };
      case "project":
        return {
          json: {
            ...clone(this.project),
            ...(url.pathname.endsWith(ids.createdProject)
              ? {
                  scope: {
                    organizationId: ids.organization,
                    projectId: ids.createdProject,
                  },
                }
              : {}),
          },
        };
      case "mappings":
        return { json: { mappings: [this.mapping], nextCursor: null } };
      case "mapping":
        return { json: this.mapping };
      case "metadata":
        return request.method() === "GET"
          ? { json: this.metadata(), headers: { ETag: this.tag() } }
          : { abort: true };
      case "revisions":
        return { json: { revisions: [this.revision], nextCursor: null } };
      case "revision":
        return {
          json: url.pathname.endsWith(ids.futureRevision)
            ? {
                ...clone(this.revision),
                scope: {
                  ...scope,
                  mappingId: url.pathname.includes(ids.futureMapping)
                    ? ids.futureMapping
                    : ids.mapping,
                  revisionId: ids.futureRevision,
                },
                originalFilename: this.#draft.filename,
                nativeByteCount: this.#bytes.length,
                nativeDigest: {
                  algorithm: "sha256",
                  value: createHash("sha256").update(this.#bytes).digest("hex"),
                },
                sequence: url.pathname.includes(ids.futureMapping)
                  ? "1"
                  : "9007199254740994",
              }
            : this.revision,
        };
      case "projection":
        return { json: savedReports().projection };
      case "validation": {
        const validation = savedReports().validation;
        if (
          request.method() === "GET" &&
          url.href ===
            `${this.origin}${api(revisionPath)}/reports/validation/${validation.result!.derivationId}?projectionDerivationId=${ids.projection}`
        )
          validation.projectionDerivationId = ids.projection;
        return { json: validation };
      }
      case "receipt":
        return { json: this.receipt(this.#operationFeature) };
      default:
        return { abort: true };
    }
  }
  #wire(kind: Feature | "resume", request: Request, url: URL): void {
    const headers = request.headers(),
      bytes = request.postDataBuffer() ?? Buffer.alloc(0),
      key = headers["idempotency-key"] ?? "",
      currentToken = headers["x-xsrf-token"] ?? "";
    const feature = kind === "resume" ? this.#operationFeature : kind;
    if (feature === "upload" || feature === "append")
      this.#operationFeature = feature;
    const original = this.#original.get(feature);
    if (!original && kind !== "resume")
      this.#original.set(feature, {
        body: Buffer.from(bytes),
        query: url.search,
        key,
        token: currentToken,
        validator: headers["if-match"] ?? "",
      });
    const frozen = this.#original.get(feature);
    const encode = (value: string) =>
      encodeURIComponent(value).replace(
        /[!'()*]/g,
        (part) => "%" + part.charCodeAt(0).toString(16).toUpperCase(),
      );
    const expectedQuery =
      kind === "upload"
        ? `?name=${encode(this.#draft.name)}&backendId=ferrule&description=${encode(this.#draft.description)}&originalFilename=${encode(this.#draft.filename)}`
        : kind === "append"
          ? `?head=equal&expectedHeadRevisionId=${this.#reviewedHead}&originalFilename=${encode(this.#draft.filename)}`
          : "";
    this.wires.push({
      kind,
      exactBody:
        kind === "resume"
          ? bytes.length === 0
          : kind === "creation" || kind === "metadata"
            ? bytes.toString("utf8") ===
              JSON.stringify({
                name: this.#draft.name,
                description: this.#draft.description,
              })
            : bytes.equals(this.#bytes),
      exactQuery: url.search === expectedQuery,
      sameOriginalBody: !!frozen && bytes.equals(frozen.body),
      sameOriginalKey: !!frozen && key === frozen.key,
      sameOriginalQuery: !!frozen && url.search === frozen.query,
      freshCatalogToken:
        currentToken.length > 0 &&
        currentToken === this.#catalogToken &&
        (!original || currentToken !== original.token),
      distinctAudiences:
        currentToken !== this.#statusToken &&
        currentToken !== this.#actionToken,
      matchesCurrentValidator:
        kind === "metadata" && headers["if-match"] === this.tag(),
      differsFromOriginalValidator:
        kind === "metadata" &&
        !!original &&
        headers["if-match"] !== original.validator,
      correctHeaders:
        ![
          "authorization",
          "x-weft-actor",
          "x-weft-role",
          "x-weft-auth-token",
          "identitycontext",
          "identity-context",
          "x-weft-identity-context",
          "x-weft-session",
          "x-weft-login",
          "x-weft-logout",
          "x-weft-forget-local",
        ].some((name) => name in headers) &&
        (kind === "resume"
          ? bytes.length === 0 &&
            !headers["content-type"] &&
            !key &&
            !["if-match", "if-none-match", "content-encoding"].some(
              (name) => name in headers,
            )
          : kind === "metadata"
            ? headers["if-match"] === this.tag() &&
              !key &&
              headers["content-type"] ===
                "application/vnd.weft.mapping-metadata-edit.v1+json"
            : /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(key) &&
              headers["content-type"] ===
                (kind === "creation"
                  ? "application/vnd.weft.project-create.v1+json"
                  : "application/octet-stream")),
    });
  }
  async install(): Promise<void> {
    this.page.on("load", () => {
      this.#requestedSamples = 0;
    });
    await this.page.addInitScript(() => {
      const counts = { storage: 0, database: 0, cache: 0, worker: 0 };
      const original = Storage.prototype.setItem;
      Storage.prototype.setItem = function (key, value) {
        ++counts.storage;
        return original.call(this, key, value);
      };
      const remove = Storage.prototype.removeItem,
        clear = Storage.prototype.clear;
      Storage.prototype.removeItem = function (key) {
        ++counts.storage;
        return remove.call(this, key);
      };
      Storage.prototype.clear = function () {
        ++counts.storage;
        return clear.call(this);
      };
      const open = indexedDB.open.bind(indexedDB);
      indexedDB.open = (...args: Parameters<IDBFactory["open"]>) => {
        ++counts.database;
        return open(...args);
      };
      const deleteDatabase = indexedDB.deleteDatabase.bind(indexedDB);
      indexedDB.deleteDatabase = (
        ...args: Parameters<IDBFactory["deleteDatabase"]>
      ) => {
        ++counts.database;
        return deleteDatabase(...args);
      };
      if ("caches" in window) {
        const cache = caches.open.bind(caches);
        caches.open = (...args: Parameters<CacheStorage["open"]>) => {
          ++counts.cache;
          return cache(...args);
        };
      }
      if ("serviceWorker" in navigator) {
        const register = navigator.serviceWorker.register.bind(
          navigator.serviceWorker,
        );
        navigator.serviceWorker.register = (
          ...args: Parameters<ServiceWorkerContainer["register"]>
        ) => {
          ++counts.worker;
          return register(...args);
        };
      }
      Object.defineProperty(window, "__weft119StorageCounts", {
        value: () => ({ ...counts }),
        configurable: false,
      });
    });
    this.page.on("requestfailed", (request) => {
      for (const held of this.held)
        if (held.request === request) held.canceled = true;
    });
    this.page.on("pageerror", () => {
      ++this.routeFailures;
    });
    this.page.on("console", (message) => {
      if (
        [...this.#privateValues].some((value) => message.text().includes(value))
      )
        ++this.routeFailures;
    });
    await this.page.route("**/*", (route) => {
      const url = new URL(route.request().url());
      if (url.origin !== this.origin) {
        ++this.unexpected;
        return route.abort("blockedbyclient");
      }
      if (!url.pathname.startsWith("/api/")) return route.continue();
      const work = this.#handle(route);
      this.#work.add(work);
      work.finally(() => this.#work.delete(work)).catch(() => {});
      return work;
    });
  }
  async #handle(route: Route): Promise<void> {
    const request = route.request(),
      url = new URL(request.url());
    const kind =
      url.origin === this.origin ? this.#classify(request, url) : null;
    if (kind === null || this.#closed || this.order.length >= 256) {
      ++this.unexpected;
      await route.abort("blockedbyclient").catch(() => {});
      return;
    }
    this.counts.set(kind, this.count(kind) + 1);
    this.order.push(kind);
    const headers = request.headers();
    if (
      (request.postDataBuffer()?.length ?? 0) > 4194304 ||
      url.search.length > 65536 ||
      Object.entries(headers).reduce(
        (size, [name, value]) => size + name.length + value.length,
        0,
      ) > 32768
    ) {
      ++this.routeFailures;
      await route.abort("blockedbyclient").catch(() => {});
      return;
    }
    // No browser owner/role/fingerprint, auth-action selector or conditional authority on any read.
    const forbiddenRead = [
      "content-type",
      "content-encoding",
      "authorization",
      "x-xsrf-token",
      "idempotency-key",
      "if-match",
      "if-none-match",
      "if-modified-since",
      "if-unmodified-since",
      "if-range",
      "range",
      "x-weft-auth-token",
      "identitycontext",
      "identity-context",
      "x-weft-identity-context",
      "x-weft-actor",
      "x-weft-role",
      "x-weft-login",
      "x-weft-logout",
      "x-weft-forget-local",
    ];
    if (
      request.method() === "GET" &&
      ((request.postDataBuffer()?.length ?? 0) !== 0 ||
        forbiddenRead.some((name) => name in headers) ||
        (kind === "status"
          ? headers["x-weft-session"] !== "1"
          : "x-weft-session" in headers))
    ) {
      ++this.routeFailures;
      await route.abort("blockedbyclient").catch(() => {});
      return;
    }
    if (
      ["creation", "upload", "append", "resume"].includes(kind) ||
      kind === "patch"
    )
      this.#wire(
        kind === "patch" ? "metadata" : (kind as Feature | "resume"),
        request,
        url,
      );
    const held = this.held.find(
      (value) => value.kind === kind && !value.request && !value.settled,
    );
    let reply: Reply;
    try {
      // Hold ownership is exact per kind, not an arbitrary first outstanding gate.
      const plan = this.plans.get(kind);
      if (held && plan && typeof plan === "function") {
        held.request = request;
        this.plans.delete(kind);
      }
      reply = plan
        ? typeof plan === "function"
          ? await plan()
          : plan
        : this.#default(kind, request, url);
      if (reply.abort) await route.abort("failed");
      else if (reply.body !== undefined)
        await route.fulfill({
          status: reply.status ?? 200,
          body: reply.body,
          headers: reply.headers ?? { "Content-Type": "application/json" },
        });
      else
        await route.fulfill({
          status: reply.status ?? 200,
          json: Object.hasOwn(reply, "json") ? reply.json : {},
          headers: reply.headers,
        });
    } catch {
      if (!this.#closed && !request.failure() && !this.page.isClosed())
        ++this.routeFailures;
    } finally {
      held?.finish();
    }
  }
  async open(path: string, probe = false): Promise<void> {
    check(!this.#closed);
    const original = this.#sampleTail.then(async () => {
      check(!this.#closed);
      await this.page.goto(
        this.origin + (probe ? "/__weft119_probe" : "") + path,
      );
      this.#requestedSamples = 0;
    });
    this.#sampleTail = original.then(
      () => {},
      () => {},
    );
    await original;
  }
  async outsideRetry(): Promise<void> {
    const initial = await this.snapshot();
    const prior = Number(initial["completed"]) + Number(initial["failed"]);
    check(Number.isFinite(prior));
    await this.page.locator("#probe-admit").click();
    await expect
      .poll(async () => {
        const current = await this.snapshot();
        return Number(current["completed"]) + Number(current["failed"]);
      })
      .toBeGreaterThan(prior);
  }
  async observe(): Promise<void> {
    await this.page.locator("#probe-observe").click();
  }
  async invalidate(): Promise<void> {
    await this.page.locator("#probe-invalidate").click();
  }
  async arm(): Promise<void> {
    await this.page.locator("#probe-arm").click();
  }
  snapshot(): Promise<Snapshot> {
    check(!this.#closed);
    return this.#queueSample();
  }
  #queueSample(): Promise<Snapshot> {
    const original = this.#sampleTail.then(async () => {
      let reading: Promise<boolean> | null = null;
      try {
        const expected = ++this.#requestedSamples;
        await this.page.locator("#probe-sample").click({ timeout: 10000 });
        let publication: Snapshot | null = null;
        await expect
          .poll(
            () => {
              const originalRead = this.#readSample(expected).then((sample) => {
                publication = sample;
                return sample !== null;
              });
              reading = originalRead;
              return originalRead;
            },
            { timeout: 10000 },
          )
          .toBe(true);
        check(publication !== null);
        return publication;
      } catch {
        throw new Error("The controlled requested sample was not published.");
      } finally {
        // A poll deadline is not settlement of its last original DOM read.
        if (reading !== null) await Promise.allSettled([reading]);
      }
    });
    // Preserve every original result/rejection. Only the sequencing tail recovers,
    // so later diagnostics can run without erasing failure evidence at close.
    this.#sampleTasks.push(original);
    this.#sampleTail = original.then(
      () => {},
      () => {},
    );
    return original;
  }
  async #readSample(expected: number): Promise<Snapshot | null> {
    const text = await this.page
      .locator("#probe-result")
      .textContent({ timeout: 10000 });
    if (text === null || text.length > 4096) return null;
    let value: unknown;
    try {
      value = JSON.parse(text);
    } catch {
      return null;
    }
    if (value === null || typeof value !== "object" || Array.isArray(value))
      return null;
    const sample = value as Snapshot;
    if (
      sample["requestedSamples"] !== expected ||
      typeof sample["completed"] !== "number" ||
      !Number.isSafeInteger(sample["completed"]) ||
      sample["completed"] < 0 ||
      typeof sample["failed"] !== "number" ||
      !Number.isSafeInteger(sample["failed"]) ||
      sample["failed"] < 0 ||
      !sessionKinds.some((kind) => kind === sample["kind"])
    )
      return null;
    return sample;
  }
  async privateMarkersHidden(feature: Feature): Promise<boolean> {
    const original = this.#original.get(feature);
    const markers = [
      this.#draft.name,
      this.#draft.description,
      this.#draft.filename,
      ids.createdProject,
      ids.futureMapping,
      ids.futureRevision,
      ids.operation,
      original?.key,
      original?.validator,
      original?.body.toString("utf8"),
      original?.query,
      feature === "upload" || feature === "append"
        ? createHash("sha256").update(this.#bytes).digest("hex")
        : undefined,
    ].filter(
      (value): value is string => typeof value === "string" && value.length > 0,
    );
    return privateMarkersVisuallyHidden(this.page, markers);
  }
  async close(): Promise<void> {
    return (this.#disposal ??= this.#stop());
  }
  async #stop(): Promise<void> {
    this.#closed = true;
    for (const held of this.held) held.release();
    let fileCleanupFailed = false;
    try {
      if (!this.page.isClosed()) {
        const fileGate = await this.page.evaluate(() => {
          const gate = (
            window as unknown as { __weft119FileGate?: { release(): void } }
          ).__weft119FileGate;
          gate?.release();
          return gate !== undefined;
        });
        if (fileGate) {
          // Opening the original gate must not restore away a not-yet-started digest wrapper.
          await expect
            .poll(async () => (await this.#queueSample())["preparingFile"])
            .toBe(false);
          await expect
            .poll(() =>
              this.page.evaluate(() => {
                const gate = (
                  window as unknown as {
                    __weft119FileGate: { settled(): boolean };
                  }
                ).__weft119FileGate;
                return gate.settled();
              }),
            )
            .toBe(true);
          check(
            await this.page.evaluate(() =>
              (
                window as unknown as {
                  __weft119FileGate: { drain(): Promise<boolean> };
                }
              ).__weft119FileGate.drain(),
            ),
          );
          await this.page.evaluate(() =>
            (
              window as unknown as { __weft119FileGate: { restore(): void } }
            ).__weft119FileGate.restore(),
          );
        }
      }
    } catch {
      fileCleanupFailed = true;
    }
    // Intake is closed; the file-gate check above is owned by this teardown.
    // Join actual original samples and their sequencing tail before returning
    // the borrowed page, including samples whose callers already saw failure.
    const sampleResults = await Promise.allSettled(this.#sampleTasks);
    await this.#sampleTail;
    const originalDrain = (this.#originalDrain ??= Promise.all([
      ...this.#work,
    ]));
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        originalDrain,
        new Promise<never>((_, reject) => {
          timer = setTimeout(
            () =>
              reject(new Error("Original held-wire cleanup did not settle.")),
            15000,
          );
        }),
      ]);
    } finally {
      clearTimeout(timer);
    }
    check(
      this.#work.size === 0 &&
        this.held.every((value) => !value.request || value.settled),
    );
    check(
      this.unexpected === 0 && this.routeFailures === 0 && this.authPosts === 0,
    );
    check(
      !fileCleanupFailed &&
        sampleResults.every((result) => result.status === "fulfilled"),
    );
    for (const value of this.#original.values()) value.body.fill(0);
    this.#original.clear();
    this.#bytes.fill(0);
    this.#privateValues.clear();
  }
  toString(): string {
    return "ControlledBrowserSession";
  }
}

export const test = base.extend<
  { app: ControlledSession },
  { staticOwner: BrowserSessionStaticOwner }
>({
  staticOwner: [
    async ({}, use) => {
      check(process.env.PLAYWRIGHT_NO_COPY_PROMPT === "1");
      const owner = await BrowserSessionStaticOwner.open(
        process.env.WEFT_BROWSER_SESSION_CONTROLLED_CONFIG!,
      );
      let primary: unknown;
      try {
        await use(owner);
      } catch {
        primary = new Error("Controlled session worker failed.");
      }
      let cleanup: unknown;
      try {
        await owner.close();
        check(owner.settled && owner.unexpectedApi === 0);
      } catch {
        const facts = owner.closeFacts;
        cleanup = new Error(
          `Controlled session static-owner cleanup failed: unexpectedApi=${facts.unexpectedApi}; pendingRequests=${facts.pendingRequests}; sockets=${facts.sockets}; serverCloseStarted=${facts.serverCloseStarted}; serverCloseSettled=${facts.serverCloseSettled}; settled=${facts.settled}.`,
        );
      }
      if (primary && cleanup)
        throw new AggregateError(
          [primary, cleanup],
          "Controlled session worker and static cleanup failed.",
        );
      if (primary) throw primary;
      if (cleanup) throw cleanup;
    },
    { scope: "worker" },
  ],
  app: async ({ page, staticOwner }, use) => {
    const app = new ControlledSession(page, staticOwner.origin);
    await app.install();
    let primary: unknown;
    try {
      await use(app);
    } catch {
      primary = new Error("Controlled browser-session scenario failed.");
    }
    let cleanup: unknown;
    try {
      await app.close();
    } catch {
      cleanup = new Error(
        "Controlled browser-session original wire cleanup failed.",
      );
    }
    if (primary && cleanup)
      throw new AggregateError(
        [primary, cleanup],
        "Controlled session scenario and cleanup failed.",
      );
    if (primary) throw primary;
    if (cleanup) throw cleanup;
  },
});
