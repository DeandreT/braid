import type {
  APIResponse,
  Page,
  Request,
  Route,
  Response as BrowserResponse,
} from "@playwright/test";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { isDeepStrictEqual } from "node:util";
import type { components } from "../src/api/generated";
import {
  test,
  expect,
  canonicalId,
  requireUpload,
  uploadPath,
  catalogApi,
  projectPath,
  mappingPath,
  revisionPath,
  type NativeMappingUploadOwner,
  type UploadCounts,
  type Sample,
  type Profile,
  type Receipt,
  type Revision,
  type UploadResult,
} from "./native-mapping-upload.fixture";

type Context = components["schemas"]["CatalogContextV1"];
type Project = components["schemas"]["CatalogProjectV1"];
type FixedSample = {
  readonly sample: Sample;
  readonly actorProfile: "administrator" | "publisher";
  readonly draftName: string;
  readonly acceptedName: string;
  readonly description: string;
  readonly originalFilename: string;
  readonly source: string | null;
  readonly byteCount: number;
  readonly sha256: string;
  readonly status: 200 | 422;
  readonly outcome: "committedValid" | "committedDecodedInvalid" | "rejected";
  readonly admission: "valid" | "decodedInvalid" | null;
  readonly rejection: "nativeDecode" | "unsupportedArtifact" | null;
};
// Private literals match the accepted 22876da6 five-row sample matrix; no sample content enters the pipe.
const fixed: Readonly<Record<Sample, FixedSample>> = {
  legacy: {
    sample: "legacy",
    actorProfile: "administrator",
    draftName: "\u0085E legacy 😀\u0085",
    acceptedName: "E legacy 😀",
    description: "Exact legacy bytes + % & =\nline two 😀",
    originalFilename: "legacy + %.native.json",
    source: "valid-basic.json",
    byteCount: 2191,
    sha256: "4220877674cba63c004226d487f5a80042369aab30350a964dc5fff5df48dc26",
    status: 200,
    outcome: "committedValid",
    admission: "valid",
    rejection: null,
  },
  versioned: {
    sample: "versioned",
    actorProfile: "publisher",
    draftName: "E versioned 9007199254740993",
    acceptedName: "E versioned 9007199254740993",
    description:
      "Versioned integer 9007199254740993; bits 3ff0000000000000 / 8000000000000000",
    originalFilename: "versioned-精確😀.native.json",
    source: "versioned-exact.json",
    byteCount: 1566,
    sha256: "b9ac8045a2b3fba94520043cdb5f1a0be7dde34aa2ecd577fafce9168dd440e7",
    status: 200,
    outcome: "committedValid",
    admission: "valid",
    rejection: null,
  },
  decodedInvalid: {
    sample: "decodedInvalid",
    actorProfile: "publisher",
    draftName: "E decoded invalid",
    acceptedName: "E decoded invalid",
    description: "Decoded native input retained with actual validation issues.",
    originalFilename: "invalid-function.native.json",
    source: "invalid-function.json",
    byteCount: 988,
    sha256: "646595d5def08a5c9f2a1e009d811361730eaa6024326b915ae14e97016fc38e",
    status: 200,
    outcome: "committedDecodedInvalid",
    admission: "decodedInvalid",
    rejection: null,
  },
  decodeRejected: {
    sample: "decodeRejected",
    actorProfile: "publisher",
    draftName: "E decode rejection",
    acceptedName: "E decode rejection",
    description: "Intentionally malformed native bytes; no mapping commit.",
    originalFilename: "malformed.native.json",
    source: null,
    byteCount: 16,
    sha256: "d7cb2eb2bce4003424a58a730a02aced4561946de703e4a74dd28073b1731f30",
    status: 422,
    outcome: "rejected",
    admission: null,
    rejection: "nativeDecode",
  },
  unsupportedRejected: {
    sample: "unsupportedRejected",
    actorProfile: "publisher",
    draftName: "E unsupported version",
    acceptedName: "E unsupported version",
    description: "Authored unsupported envelope version; no mapping commit.",
    originalFilename: "unsupported-version.native.json",
    source: "versioned-exact.json",
    byteCount: 1568,
    sha256: "7c5ccd2bd23dd3b1e4c9b7e491ca352daec79642965ad40b28154851770e3585",
    status: 422,
    outcome: "rejected",
    admission: null,
    rejection: "unsupportedArtifact",
  },
};
const sha = (bytes: Buffer) => createHash("sha256").update(bytes).digest("hex");
async function sampleBytes(sample: FixedSample): Promise<Buffer> {
  let bytes: Buffer;
  if (!sample.source)
    bytes = Buffer.from("7b6e6f74206e6174697665204a534f4e", "hex");
  else {
    try {
      bytes = await readFile(
        new URL(
          `../../../engines/ferrule-host/tests/fixtures/${sample.source}`,
          import.meta.url,
        ),
      );
    } catch {
      throw new Error(
        "Native mapping upload browser fixture: source sample could not be read",
      );
    }
    if (sample.sample === "unsupportedRejected") {
      requireUpload(
        bytes.length === fixed.versioned.byteCount &&
          sha(bytes) === fixed.versioned.sha256,
        "unsupported source bytes differ",
      );
      const from = Buffer.from('"version": 2', "ascii"),
        to = Buffer.from('"version": 999', "ascii"),
        offset = bytes.indexOf(from);
      requireUpload(
        offset === 51 && bytes.indexOf(from, offset + from.length) === -1,
        "unsupported transformation is not one exact ASCII token",
      );
      bytes = Buffer.concat([
        bytes.subarray(0, offset),
        to,
        bytes.subarray(offset + from.length),
      ]);
    }
  }
  requireUpload(
    bytes.length === sample.byteCount && sha(bytes) === sample.sha256,
    "source sample length/hash differs",
  );
  return bytes;
}
const button = (page: Page, name: string) =>
  page.getByRole("button", { name, exact: true });
const heading = (page: Page, name: string) =>
  page.getByRole("heading", { name, exact: true });
async function visibleHeading(page: Page, name: string): Promise<void> {
  await expect
    .poll(() => heading(page, name).isVisible(), {
      message: "Expected upload lifecycle heading did not settle",
      timeout: 45000,
    })
    .toBe(true);
}
async function responseDone(response: BrowserResponse): Promise<void> {
  let nonempty: boolean;
  try {
    nonempty = (await response.body()).length > 0;
  } catch {
    throw new Error(
      "Native mapping upload browser fixture: expected browser response body could not be consumed",
    );
  }
  requireUpload(nonempty, "expected browser response body was empty");
}
async function privateJson(
  response: APIResponse,
  category: string,
): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    throw new Error("Native mapping upload browser fixture: " + category);
  }
}
function waitResponse(
  page: Page,
  method: string,
  path: string,
  status: number,
): Promise<BrowserResponse> {
  return page.waitForResponse(
    (reply) => {
      const url = new URL(reply.url());
      return (
        reply.request().method() === method &&
        url.pathname === path &&
        !url.search &&
        reply.status() === status
      );
    },
    { timeout: 45000 },
  );
}
function sameCounts(actual: UploadCounts, expected: UploadCounts): void {
  requireUpload(
    isDeepStrictEqual(actual, expected),
    "exact expected upload counts changed",
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
  url: string;
  key: string;
  receipt: Receipt | null;
  revision: Revision | null;
};
type Delivery = "normal" | "lose" | "hold-forward";
type Plan = {
  readonly kind: "create" | "resume";
  readonly original: Original;
  readonly profile: "administrator" | "publisher";
  readonly delivery: Delivery;
  readonly contexts: number;
  readonly projects: number;
  readonly entered: () => void;
  readonly wait: Promise<void>;
};
type Mutation = {
  readonly kind: "create" | "resume";
  readonly original: Original;
  readonly contexts: number;
  readonly projects: number;
  readonly done: Promise<void>;
  readonly finish: () => void;
  status: number | null;
  result: UploadResult | null;
};

/** Private real HTTP observation. No original request bytes/query/key/token are attached or logged. */
class UploadWire {
  private readonly contexts: Request[] = [];
  private readonly projects: Request[] = [];
  private readonly contextValues = new Map<Request, Context>();
  private readonly projectValues = new Map<Request, Project>();
  private readonly reads: { path: string; status: number; value: unknown }[] =
    [];
  private readonly readTasks: Promise<void>[] = [];
  private readonly mutations: Mutation[] = [];
  private readonly originals = new Map<Sample, Original>();
  private readonly captured = new Set<Sample>();
  private readonly releases: (() => void)[] = [];
  private failure: Error | null = null;
  private plan: Plan | null = null;
  private expectedCounts: UploadCounts;
  readonly projectApi: string;
  readonly createApi: string;
  constructor(
    private readonly page: Page,
    private readonly owner: NativeMappingUploadOwner,
    initial: UploadCounts,
  ) {
    this.expectedCounts = { ...initial };
    this.projectApi = catalogApi(projectPath(owner.manifest.project.scope));
    this.createApi = this.projectApi + "/mappings";
  }
  get postCount(): number {
    return this.mutations.length;
  }
  get contextCount(): number {
    return this.contexts.length;
  }
  get projectCount(): number {
    return this.projects.length;
  }
  get receiptReadCount(): number {
    return this.reads.filter((r) =>
      r.path.startsWith(this.projectApi + "/revision-ingestions/"),
    ).length;
  }
  get revisionReadCount(): number {
    return this.reads.filter((r) => r.path.includes("/revisions/")).length;
  }
  /** Observe foreground admission and readiness; this is not a timer-drain guarantee. */
  async focusAdmission(
    action:
      | "Upload mapping"
      | "Resend original upload"
      | "Replay upload result"
      | "Check upload status",
  ): Promise<void> {
    await this.page.bringToFront();
    await expect
      .poll(
        () =>
          this.page.evaluate(
            () => document.hasFocus() && document.visibilityState === "visible",
          ),
        {
          message: "Actual upload page did not become visibly focused",
          timeout: 10000,
        },
      )
      .toBe(true);
    await expect(button(this.page, "Refresh access")).toBeEnabled();
    const response = (path: string) =>
      this.page.waitForResponse(
        (reply) => {
          const url = new URL(reply.url());
          return (
            reply.request().method() === "GET" &&
            url.origin === this.owner.manifest.readOrigin &&
            url.pathname === path &&
            !url.search &&
            reply.status() === 200
          );
        },
        { timeout: 10000 },
      );
    const context = response("/api/v1/catalog/context"),
      project = response(this.projectApi);
    await this.page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await Promise.all([context.then(responseDone), project.then(responseDone)]);
    await this.settleReads();
    await expect(button(this.page, "Refresh access")).toBeEnabled();
    await expect(button(this.page, action)).toBeEnabled();
  }
  private remember(error: unknown, category: string): void {
    this.failure ??=
      error instanceof Error &&
      error.message.startsWith("Native mapping upload browser fixture:")
        ? error
        : new Error("Native mapping upload browser fixture: " + category);
  }
  private async settleReads(): Promise<void> {
    let read = 0;
    while (read < this.readTasks.length) {
      const next = this.readTasks.slice(read);
      read += next.length;
      await Promise.all(next);
    }
    if (this.failure) throw this.failure;
  }
  async settle(): Promise<void> {
    await this.settleReads();
  }
  async install(): Promise<void> {
    const origin = this.owner.manifest.readOrigin;
    this.page.on("response", (reply) => {
      const request = reply.request(),
        url = new URL(reply.url());
      if (
        request.method() !== "GET" ||
        url.origin !== origin ||
        !url.pathname.startsWith("/api/")
      )
        return;
      const task = (async () => {
        try {
          await responseDone(reply);
          const value: unknown = await reply.json();
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
          if (url.pathname === this.projectApi && reply.status() === 200)
            this.projectValues.set(request, value as Project);
          for (const original of this.originals.values()) {
            if (
              original.receipt &&
              url.pathname === receiptPath(original.receipt) &&
              reply.status() === 200
            )
              requireUpload(
                isDeepStrictEqual(value, original.receipt),
                "terminal own receipt GET changed immutable facts",
              );
            if (
              original.receipt?.phase === "committed" &&
              url.pathname ===
                catalogApi(revisionPath(original.receipt.futureRevision)) &&
              reply.status() === 200
            ) {
              this.verifyRevision(value, original);
              if (original.revision)
                requireUpload(
                  isDeepStrictEqual(value, original.revision),
                  "ordinary saved revision facts changed",
                );
              original.revision = value as Revision;
            }
          }
        } catch (error) {
          // An obsolete project GET can be aborted by the foreign-identity guard. Explicit admission
          // and revision boundaries below still require their own successful complete responses.
          if (!request.failure())
            this.remember(error, "actual GET observation failed");
        }
      })();
      this.readTasks.push(task);
    });
    await this.page.route("**/api/**", (route) => this.observe(route));
  }
  private allowedGet(url: URL): boolean {
    const protectedScope = this.owner.manifest.protected.revision;
    const fixedPaths = new Set([
      "/api/v1/catalog/context",
      this.projectApi,
      catalogApi(projectPath(this.owner.manifest.protected.project)),
      catalogApi(mappingPath(protectedScope)),
      catalogApi(revisionPath(protectedScope)),
    ]);
    for (const original of this.originals.values())
      if (original.receipt) {
        fixedPaths.add(receiptPath(original.receipt));
        if (original.receipt.phase === "committed") {
          fixedPaths.add(
            catalogApi(mappingPath(original.receipt.futureMapping)),
          );
          fixedPaths.add(
            catalogApi(revisionPath(original.receipt.futureRevision)),
          );
        }
      }
    return !url.search && fixedPaths.has(url.pathname);
  }
  private async observe(route: Route): Promise<void> {
    const request = route.request(),
      url = new URL(request.url()),
      headers = request.headers();
    let mutation: Mutation | null = null;
    try {
      requireUpload(
        url.origin === this.owner.manifest.readOrigin,
        "browser API request escaped fixed origin",
      );
      if (request.method() === "GET") {
        requireUpload(
          this.allowedGet(url),
          "unexpected catalog or operation GET",
        );
        requireUpload(
          ![
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "idempotency-key",
            "x-xsrf-token",
          ].some((key) => key in headers),
          "read unexpectedly carried mutation or conditional headers",
        );
        if (url.pathname === "/api/v1/catalog/context")
          this.contexts.push(request);
        if (url.pathname === this.projectApi) this.projects.push(request);
        await route.continue();
        return;
      }
      const plan = this.plan;
      this.plan = null;
      requireUpload(
        request.method() === "POST" && plan,
        "unexpected browser mutation",
      );
      const original = plan.original;
      let finish!: () => void;
      const done = new Promise<void>((resolve) => {
        finish = resolve;
      });
      mutation = {
        kind: plan.kind,
        original,
        contexts: this.contexts.length - 1,
        projects: this.projects.length - 1,
        done,
        finish,
        status: null,
        result: null,
      };
      this.mutations.push(mutation);
      requireUpload(
        mutation.contexts === plan.contexts &&
          mutation.projects === plan.projects,
        "deliberate mutation did not fresh-admit exactly one context/project",
      );
      await this.settleReads();
      const context = this.contextValues.get(this.contexts[mutation.contexts]),
        project = this.projectValues.get(this.projects[mutation.projects]);
      requireUpload(
        context?.mode === "development" &&
          context.actorPrincipalId ===
            this.owner.manifest.profiles[plan.profile].actorPrincipalId &&
          context.allowedOrganizationId ===
            this.owner.manifest.organizationId &&
          context.features.createMappings &&
          project?.access.actions.createMappings &&
          isDeepStrictEqual(project.scope, this.owner.manifest.project.scope) &&
          typeof context.requestToken === "string" &&
          context.requestToken.length > 0 &&
          headers["x-xsrf-token"] === context.requestToken,
        "mutation lacked fresh original-actor project capability/token",
      );
      requireUpload(
        headers["origin"] === this.owner.manifest.readOrigin &&
          ![
            "if-match",
            "if-none-match",
            "if-modified-since",
            "if-unmodified-since",
            "if-range",
            "content-encoding",
          ].some((key) => key in headers),
        "mutation origin or conditional envelope differs",
      );
      if (plan.kind === "create") {
        const encode = (value: string) =>
          encodeURIComponent(value).replace(
            /[!'()*]/g,
            (c) => "%" + c.charCodeAt(0).toString(16).toUpperCase(),
          );
        const expectedQuery =
          "?name=" +
          encode(original.sample.acceptedName) +
          "&backendId=ferrule&description=" +
          encode(original.sample.description) +
          "&originalFilename=" +
          encode(original.sample.originalFilename);
        requireUpload(
          url.pathname === this.createApi &&
            url.search === expectedQuery &&
            canonicalId(headers["idempotency-key"]),
          "actual create target or original key is invalid",
        );
        if (!original.key) {
          original.key = headers["idempotency-key"];
          original.url = url.href;
        }
        requireUpload(
          url.pathname === this.createApi &&
            url.href === original.url &&
            headers["content-type"] === "application/octet-stream" &&
            canonicalId(headers["idempotency-key"]) &&
            headers["idempotency-key"] === original.key &&
            request.postDataBuffer()?.equals(original.body),
          "original native bytes/query/key/media changed",
        );
      } else {
        requireUpload(
          original.receipt &&
            url.pathname === receiptPath(original.receipt) + "/resume" &&
            !url.search &&
            request.postData() === null &&
            !["idempotency-key", "content-type"].some((key) => key in headers),
          "resume was not exact source-free bodyless request",
        );
      }
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
        const completedBody = await reply.body();
        let result: unknown;
        try {
          result = JSON.parse(completedBody.toString("utf8"));
        } catch {
          throw new Error(
            "Native mapping upload browser fixture: actual mutation response is not JSON",
          );
        }
        mutation.status = reply.status();
        mutation.result = result as UploadResult;
        if (reply.status() === 404) {
          const row = result as Record<string, unknown>;
          requireUpload(
            result !== null &&
              typeof result === "object" &&
              !Array.isArray(result) &&
              Object.keys(row).join("|") === "code" &&
              (row.code === "notFound" || row.code === "unavailable"),
            "actual revoked mutation did not return opaque404",
          );
        } else {
          const receipt = this.verifyResult(result, original, reply.status());
          if (original.receipt)
            requireUpload(
              isDeepStrictEqual(receipt, original.receipt),
              "replay changed terminal own operation",
            );
          else original.receipt = receipt;
          if (!this.captured.has(original.sample.sample)) {
            const ack = await this.owner.control({
              action: "capture-upload",
              sample: original.sample.sample,
              operationId: receipt.operationId,
            });
            requireUpload(
              ack.action === "capture-upload",
              "expected captured native upload",
            );
            const committed = receipt.phase === "committed";
            this.expectedCounts = {
              mappings: this.expectedCounts.mappings + (committed ? 1 : 0),
              revisions: this.expectedCounts.revisions + (committed ? 1 : 0),
              ingestions: this.expectedCounts.ingestions + 1,
              ingestionAttempts: this.expectedCounts.ingestionAttempts + 1,
              nativePublications: this.expectedCounts.nativePublications + 1,
              workersCreated: this.expectedCounts.workersCreated + 1,
            };
            sameCounts(ack.counts, this.expectedCounts);
            this.captured.add(original.sample.sample);
          } else sameCounts(await this.owner.checkpoint(), this.expectedCounts);
        }
        // Body completion and independent persistence capture precede the intentional HTTP delivery loss.
        if (plan.delivery === "lose") await route.abort("failed");
        else await route.fulfill({ response: reply });
      } finally {
        await reply.dispose();
      }
      if (plan.delivery !== "hold-forward") plan.entered();
    } catch (error) {
      this.remember(error, "actual mutation observation failed");
      try {
        await route.abort("failed");
      } catch {
        /* The original page may already have canceled waiting. */
      }
    } finally {
      mutation?.finish();
    }
  }
  private verifyResult(
    value: unknown,
    original: Original,
    status: number,
  ): Receipt {
    const sample = original.sample;
    requireUpload(
      value !== null && typeof value === "object" && !Array.isArray(value),
      "actual upload result is not an object",
    );
    const result = value as UploadResult;
    requireUpload(
      Object.keys(result).every((k) =>
        [
          "outcome",
          "receipt",
          "rejection",
          "failure",
          "committedRevisionUrl",
        ].includes(k),
      ) &&
        status === sample.status &&
        result.outcome === sample.outcome &&
        (result.rejection ?? null) === sample.rejection &&
        (result.failure ?? null) === null &&
        result.receipt,
      "actual terminal result classification differs",
    );
    const receipt = result.receipt,
      project = this.owner.manifest.project.scope;
    requireUpload(
      Object.keys(receipt).sort().join("|") ===
        [
          "claimCount",
          "committedAdmission",
          "committedSequence",
          "completedAtUtc",
          "futureMapping",
          "futureRevision",
          "latestFailure",
          "operationId",
          "originalFilename",
          "phase",
          "project",
          "reservedAtUtc",
        ]
          .sort()
          .join("|") &&
        canonicalId(receipt.operationId) &&
        isDeepStrictEqual(receipt.project, project) &&
        receipt.futureMapping.organizationId === project.organizationId &&
        receipt.futureMapping.projectId === project.projectId &&
        canonicalId(receipt.futureMapping.mappingId) &&
        isDeepStrictEqual(receipt.futureMapping, {
          organizationId: receipt.futureRevision.organizationId,
          projectId: receipt.futureRevision.projectId,
          mappingId: receipt.futureRevision.mappingId,
        }) &&
        canonicalId(receipt.futureRevision.revisionId) &&
        receipt.originalFilename === sample.originalFilename &&
        receipt.claimCount === 1 &&
        receipt.phase === (sample.status === 200 ? "committed" : "rejected") &&
        receipt.committedSequence === (sample.status === 200 ? "1" : null) &&
        receipt.committedAdmission === sample.admission &&
        receipt.latestFailure === null &&
        typeof receipt.reservedAtUtc === "string" &&
        Number.isFinite(Date.parse(receipt.reservedAtUtc)) &&
        typeof receipt.completedAtUtc === "string" &&
        Date.parse(receipt.completedAtUtc) >= Date.parse(receipt.reservedAtUtc),
      "actual own receipt identity/terminal facts differ",
    );
    requireUpload(
      (result.committedRevisionUrl ?? null) ===
        (sample.status === 200
          ? catalogApi(revisionPath(receipt.futureRevision))
          : null),
      "actual committed revision location differs",
    );
    return receipt;
  }
  private verifyRevision(value: unknown, original: Original): void {
    const receipt = original.receipt,
      sample = original.sample,
      revision = value as Revision;
    requireUpload(
      receipt &&
        value !== null &&
        typeof value === "object" &&
        !Array.isArray(value) &&
        isDeepStrictEqual(revision.scope, receipt.futureRevision) &&
        revision.sequence === "1" &&
        revision.admission === sample.admission &&
        revision.creatorPrincipalId ===
          this.owner.manifest.profiles[sample.actorProfile].actorPrincipalId &&
        revision.backendId === "ferrule" &&
        revision.logicalDocument === "entry.native.json" &&
        revision.originalFilename === sample.originalFilename &&
        revision.nativeByteCount === sample.byteCount &&
        revision.nativeDigest?.algorithm === "sha256" &&
        revision.nativeDigest.value === sample.sha256,
      "ordinary saved revision is not the exact original native upload",
    );
  }
  async arm(
    sample: FixedSample,
    delivery: Delivery = "normal",
    replay = false,
    kind: "create" | "resume" = "create",
  ): Promise<{ entered: () => boolean; release: () => void }> {
    await this.settleReads();
    requireUpload(!this.plan, "overlapping upload delivery plans");
    let original = this.originals.get(sample.sample);
    if (!original) {
      requireUpload(
        !replay && kind === "create",
        "cannot replay an unobserved original",
      );
      // Key is learned only from the actual first outgoing browser request, never authored by the test.
      original = {
        sample,
        body: await sampleBytes(sample),
        url: "",
        key: "",
        receipt: null,
        revision: null,
      };
      this.originals.set(sample.sample, original);
    } else
      requireUpload(
        replay || original.receipt === null,
        "first sample cannot be captured twice",
      );
    await this.focusAdmission(
      kind === "resume"
        ? "Replay upload result"
        : replay
          ? "Resend original upload"
          : "Upload mapping",
    );
    let entered = false,
      release!: () => void;
    const wait = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.releases.push(release);
    this.plan = {
      kind,
      original,
      profile: sample.actorProfile,
      delivery,
      contexts: this.contexts.length,
      projects: this.projects.length,
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
        message: "Expected deliberate upload request was not observed",
        timeout: 10000,
      })
      .toBe(true);
    const mutation = this.mutations[index];
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        mutation.done,
        new Promise<never>((_, reject) => {
          timer = setTimeout(
            () =>
              reject(
                new Error(
                  "Native mapping upload browser fixture: actual mutation did not settle",
                ),
              ),
            45000,
          );
        }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
    await this.settleReads();
    requireUpload(mutation.status === status, "actual mutation status differs");
  }
  operation(sample: Sample): Receipt {
    const receipt = this.originals.get(sample)?.receipt;
    requireUpload(receipt, "expected an actual learned own receipt");
    return receipt;
  }
  async checkpoint(): Promise<UploadCounts> {
    await this.settleReads();
    const current = await this.owner.checkpoint();
    sameCounts(current, this.expectedCounts);
    return current;
  }
  async revision(sample: Sample): Promise<Revision> {
    await this.settleReads();
    const revision = this.originals.get(sample)?.revision;
    requireUpload(revision, "expected exact ordinary saved revision read");
    return revision;
  }
  async protectedRevision(): Promise<Revision> {
    await this.settleReads();
    const path = catalogApi(
        revisionPath(this.owner.manifest.protected.revision),
      ),
      row = this.reads
        .filter((r) => r.path === path && r.status === 200)
        .at(-1);
    requireUpload(row, "expected actual protected revision read");
    return row.value as Revision;
  }
  async profile(profile: Profile): Promise<void> {
    await this.settleReads();
    const context = this.contextValues.get(this.contexts.at(-1)!);
    requireUpload(
      context?.actorPrincipalId ===
        this.owner.manifest.profiles[profile].actorPrincipalId &&
        context.allowedOrganizationId === this.owner.manifest.organizationId,
      "fresh actual context profile differs",
    );
  }
  /** API-only negative checks: the page correctly disables these operations after demotion. */
  async demotedOwnRefusals(sample: Sample): Promise<void> {
    const receipt = this.operation(sample),
      origin = this.owner.manifest.readOrigin;
    const contextReply = await this.page.request.get(
      origin + "/api/v1/catalog/context",
      { maxRedirects: 0, timeout: 8000 },
    );
    let token = "";
    try {
      requireUpload(
        contextReply.status() === 200,
        "demoted own-receipt API context was unavailable",
      );
      const raw = await privateJson(
        contextReply,
        "demoted API context JSON is incompatible",
      );
      requireUpload(
        raw !== null && typeof raw === "object" && !Array.isArray(raw),
        "demoted API context body is incompatible",
      );
      const context = raw as Context;
      requireUpload(
        context.actorPrincipalId ===
          this.owner.manifest.profiles.publisher.actorPrincipalId &&
          context.allowedOrganizationId ===
            this.owner.manifest.organizationId &&
          typeof context.requestToken === "string" &&
          context.requestToken.length > 0,
        "demoted own-receipt API context identity differs",
      );
      token = context.requestToken;
    } finally {
      await contextReply.dispose();
    }
    const own = await this.page.request.get(origin + receiptPath(receipt), {
      maxRedirects: 0,
      timeout: 8000,
    });
    try {
      const value = await privateJson(
        own,
        "demoted receipt JSON is incompatible",
      );
      requireUpload(
        own.status() === 404 &&
          value !== null &&
          typeof value === "object" &&
          !Array.isArray(value) &&
          Object.keys(value).join("|") === "code" &&
          (value as { code: unknown }).code === "notFound",
        "demoted own-receipt GET was not opaque404",
      );
    } finally {
      await own.dispose();
    }
    const resume = await this.page.request.post(
      origin + receiptPath(receipt) + "/resume",
      {
        headers: { Origin: origin, "X-XSRF-TOKEN": token },
        maxRedirects: 0,
        timeout: 30000,
      },
    );
    try {
      const value = await privateJson(
        resume,
        "demoted resume JSON is incompatible",
      );
      requireUpload(
        resume.status() === 404 &&
          value !== null &&
          typeof value === "object" &&
          !Array.isArray(value) &&
          Object.keys(value).join("|") === "code" &&
          (value as { code: unknown }).code === "notFound",
        "demoted bodyless own-resume was not opaque404",
      );
    } finally {
      await resume.dispose();
    }
  }
  release(): void {
    for (const release of this.releases) release();
  }
  async closed(): Promise<void> {
    await Promise.all(this.mutations.map((m) => m.done));
    await this.settleReads();
    requireUpload(
      !this.plan && this.captured.size === 5,
      "unused delivery plan or incomplete five-sample capture",
    );
  }
}

async function ready(page: Page): Promise<void> {
  await visibleHeading(page, "Upload mapping");
  await expect
    .poll(() => page.getByLabel("Name", { exact: true }).isEditable(), {
      message: "Actual upload editor was not admitted",
      timeout: 10000,
    })
    .toBe(true);
}
async function navigateUpload(
  page: Page,
  wire: UploadWire,
  owner: NativeMappingUploadOwner,
  admitted: boolean,
): Promise<void> {
  const context = waitResponse(page, "GET", "/api/v1/catalog/context", 200),
    project = waitResponse(page, "GET", wire.projectApi, 200);
  await page.goto(uploadPath(owner.manifest.project.scope));
  await Promise.all([context.then(responseDone), project.then(responseDone)]);
  await wire.settle();
  if (admitted) await ready(page);
  else {
    await visibleHeading(page, "Upload form unavailable");
    await expect
      .poll(() => page.getByLabel("Name", { exact: true }).count(), {
        message: "Refused editor exposed private fields",
      })
      .toBe(0);
    await expect
      .poll(() => button(page, "Upload mapping").count(), {
        message: "Refused editor exposed a send control",
      })
      .toBe(0);
  }
}
async function refresh(
  page: Page,
  wire: UploadWire,
  projectExpected = true,
): Promise<void> {
  const before = wire.contextCount,
    context = waitResponse(page, "GET", "/api/v1/catalog/context", 200);
  const project = projectExpected
    ? waitResponse(page, "GET", wire.projectApi, 200)
    : null;
  await button(page, "Refresh access").click();
  await responseDone(await context);
  if (project) await responseDone(await project);
  await wire.settle();
  await expect
    .poll(() => button(page, "Refresh access").isEnabled(), {
      message: "Explicit access refresh did not settle",
    })
    .toBe(true);
  requireUpload(
    wire.contextCount === before + 1,
    "explicit refresh did not use one fresh context",
  );
}
async function choose(page: Page, sample: FixedSample): Promise<void> {
  const bytes = await sampleBytes(sample);
  // Only these private input operations translate Playwright's value-bearing call logs to a closed category.
  try {
    await page
      .getByLabel("Native mapping file", { exact: true })
      .setInputFiles({
        name: sample.originalFilename,
        mimeType: "application/json",
        buffer: bytes,
      });
  } catch {
    throw new Error(
      "Native mapping upload browser fixture: native file selection action failed",
    );
  }
  try {
    await page.getByLabel("Name", { exact: true }).fill(sample.draftName);
    await page
      .getByLabel("Description (optional)", { exact: true })
      .fill(sample.description);
    await page
      .getByLabel("Saved filename", { exact: true })
      .fill(sample.originalFilename);
  } catch {
    throw new Error(
      "Native mapping upload browser fixture: native metadata entry action failed",
    );
  }
  await expect
    .poll(() => button(page, "Upload mapping").isEnabled(), {
      message: "Exact selected source did not finish local preparation",
      timeout: 10000,
    })
    .toBe(true);
  requireUpload(
    (await page.getByLabel("Name", { exact: true }).inputValue()) ===
      sample.draftName &&
      (await page
        .getByLabel("Description (optional)", { exact: true })
        .inputValue()) === sample.description &&
      (await page
        .getByLabel("Saved filename", { exact: true })
        .inputValue()) === sample.originalFilename,
    "ordinary input values differ from fixed sample draft",
  );
  requireUpload(
    (await page
      .getByLabel("Native mapping file", { exact: true })
      .inputValue()) === "",
    "file input retained a live selection after capture",
  );
}
async function saved(
  page: Page,
  wire: UploadWire,
  sample: FixedSample,
): Promise<void> {
  await visibleHeading(
    page,
    sample.admission === "decodedInvalid"
      ? "Mapping saved with validation issues"
      : "Mapping saved",
  );
  await expect
    .poll(
      () =>
        page
          .getByRole("link", { name: "View revision", exact: true })
          .isVisible(),
      {
        message:
          "Acknowledged upload did not settle exact ordinary revision access",
        timeout: 10000,
      },
    )
    .toBe(true);
  await wire.settle();
  const revision = await wire.revision(sample.sample);
  requireUpload(
    (await page
      .getByRole("link", { name: "View revision", exact: true })
      .getAttribute("href")) === revisionPath(revision.scope),
    "saved link is not the exact ordinary revision",
  );
  requireUpload(
    (await page.locator(".upload-facts dd").first().textContent()) ===
      sample.originalFilename,
    "saved filename literal changed in the DOM",
  );
}
async function startAnother(page: Page): Promise<void> {
  await button(page, "Upload another mapping").click();
  await ready(page);
  await expect
    .poll(() => heading(page, "Mapping saved").count(), {
      message: "New explicit draft retained old acknowledgement",
    })
    .toBe(0);
}

test.describe.configure({ mode: "serial" });
test("real packaged native create/upload: exact original bytes, durable recovery, authority and truthful native outcomes", async ({
  page,
  nativeMappingUploadOwner: owner,
}) => {
  test.setTimeout(600000);
  const initial = await owner.checkpoint(),
    wire = new UploadWire(page, owner, initial),
    fixture = owner.manifest;
  requireUpload(
    initial.workersCreated > 0,
    "fixture must include genuine protected native worker baseline",
  );
  await wire.install();
  let protectedBefore: Revision | null = null;
  try {
    await page.setViewportSize({ width: 390, height: 844 });
    await test.step("actual Viewer, Runner and disabled feature direct routes refuse upload without work", async () => {
      for (const profile of ["viewer", "runner"] as const) {
        await owner.control({
          action: "restart-profile",
          profile,
          mode: "upload",
        });
        await navigateUpload(page, wire, owner, false);
        await wire.profile(profile);
        requireUpload(
          wire.postCount === 0,
          "read-only direct route sent a mutation",
        );
        sameCounts(await wire.checkpoint(), initial);
      }
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "featureOff",
      });
      await navigateUpload(page, wire, owner, false);
      await wire.profile("publisher");
      requireUpload(
        wire.postCount === 0,
        "feature-disabled route sent a mutation",
      );
      sameCounts(await wire.checkpoint(), initial);
    });
    await test.step("Administrator deliberately uploads unchanged legacy File bytes once and opens its exact first revision", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "administrator",
        mode: "upload",
      });
      const protectedReply = waitResponse(
        page,
        "GET",
        catalogApi(revisionPath(fixture.protected.revision)),
        200,
      );
      await page.goto(revisionPath(fixture.protected.revision));
      await responseDone(await protectedReply);
      await expect
        .poll(
          () =>
            page
              .getByRole("heading", { name: "Revision 1", exact: true })
              .isVisible(),
          { message: "Protected actual revision view did not load" },
        )
        .toBe(true);
      protectedBefore = await wire.protectedRevision();
      await navigateUpload(page, wire, owner, true);
      await choose(page, fixed.legacy);
      const editorDimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      requireUpload(
        editorDimensions.scroll <= editorDimensions.width + 1,
        "mobile upload editor overflows viewport",
      );
      await wire.arm(fixed.legacy);
      const index = wire.postCount;
      await button(page, "Upload mapping").dblclick();
      await wire.complete(index, 200);
      await saved(page, wire, fixed.legacy);
      requireUpload(
        wire.postCount === index + 1,
        "double activation dispatched more than one original upload",
      );
      const revision = await wire.revision("legacy"),
        reply = waitResponse(
          page,
          "GET",
          catalogApi(revisionPath(revision.scope)),
          200,
        );
      await page
        .getByRole("link", { name: "View revision", exact: true })
        .click();
      await responseDone(await reply);
      await expect
        .poll(
          () =>
            page
              .getByRole("heading", { name: "Revision 1", exact: true })
              .isVisible(),
          { message: "Created first revision view did not load" },
        )
        .toBe(true);
      requireUpload(
        new URL(page.url()).pathname === revisionPath(revision.scope),
        "revision navigation changed saved scope",
      );
      await wire.checkpoint();
    });
    await test.step("Publisher completed200 is captured before delivery loss; original private recovery survives identity quarantine and API plus normal PG restart", async () => {
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "upload",
      });
      await navigateUpload(page, wire, owner, true);
      await choose(page, fixed.versioned);
      await wire.arm(fixed.versioned, "lose");
      const index = wire.postCount,
        receiptsBefore = wire.receiptReadCount;
      await button(page, "Upload mapping").click();
      await wire.complete(index, 200);
      await visibleHeading(page, "Upload outcome is unknown");
      requireUpload(
        (await heading(page, "Mapping saved").count()) === 0 &&
          wire.receiptReadCount === receiptsBefore &&
          wire.postCount === index + 1,
        "lost response was adopted or automatically retried/searched",
      );
      await wire.focusAdmission("Resend original upload");
      const committed = wire.operation("versioned"),
        trusted = await wire.checkpoint();
      await owner.control({
        action: "restart-profile",
        profile: "viewer",
        mode: "upload",
      });
      await refresh(page, wire, false);
      await visibleHeading(page, "An original upload is retained privately");
      requireUpload(
        (await page.getByLabel("Name", { exact: true }).count()) === 0 &&
          (await heading(page, "Mapping saved").count()) === 0 &&
          (await button(page, "Resend original upload").count()) === 0 &&
          wire.postCount === index + 1,
        "foreign actor exposed or replayed original upload",
      );
      await owner.control({
        action: "restart-profile",
        profile: "publisher",
        mode: "upload",
      });
      await refresh(page, wire);
      await visibleHeading(page, "Upload outcome is unknown");
      sameCounts(await wire.checkpoint(), trusted);
      await owner.control({ action: "api-stop" });
      await owner.control({ action: "pg-normal-restart" });
      await owner.control({
        action: "api-start",
        profile: "publisher",
        mode: "upload",
      });
      sameCounts(await wire.checkpoint(), trusted);
      await wire.arm(fixed.versioned, "normal", true);
      await button(page, "Resend original upload").click();
      await wire.complete(index + 1, 200);
      await saved(page, wire, fixed.versioned);
      requireUpload(
        isDeepStrictEqual(wire.operation("versioned"), committed) &&
          wire.postCount === index + 2,
        "durable original resend did not replay exact terminal receipt once",
      );
      sameCounts(await wire.checkpoint(), trusted);
      const receiptReply = waitResponse(
          page,
          "GET",
          receiptPath(committed),
          200,
        ),
        receiptRevision = waitResponse(
          page,
          "GET",
          catalogApi(revisionPath(committed.futureRevision)),
          200,
        ),
        priorPosts = wire.postCount;
      await button(page, "Check upload status").click();
      await responseDone(await receiptReply);
      await responseDone(await receiptRevision);
      await saved(page, wire, fixed.versioned);
      requireUpload(
        wire.postCount === priorPosts,
        "own receipt read submitted a mutation",
      );
      sameCounts(await wire.checkpoint(), trusted);
      await wire.arm(fixed.versioned, "normal", true, "resume");
      const replayReply = waitResponse(
          page,
          "POST",
          receiptPath(committed) + "/resume",
          200,
        ),
        replayRevision = waitResponse(
          page,
          "GET",
          catalogApi(revisionPath(committed.futureRevision)),
          200,
        );
      await button(page, "Replay upload result").click();
      await responseDone(await replayReply);
      await responseDone(await replayRevision);
      await wire.complete(priorPosts, 200);
      await saved(page, wire, fixed.versioned);
      sameCounts(await wire.checkpoint(), trusted);
    });
    await test.step("same-actor Viewer demotion preserves acknowledged ordinary view while own receipt and source-free resume refuse404", async () => {
      await wire.focusAdmission("Check upload status");
      const trusted = await wire.checkpoint(),
        posts = wire.postCount;
      await owner.control({ action: "revoke-publisher" });
      const demotedRead = waitResponse(
        page,
        "GET",
        catalogApi(revisionPath(wire.operation("versioned").futureRevision)),
        200,
      );
      await refresh(page, wire);
      await responseDone(await demotedRead);
      await saved(page, wire, fixed.versioned);
      await visibleHeading(page, "Upload form unavailable");
      requireUpload(
        (await button(page, "Replay upload result").count()) === 0 &&
          (await page.getByLabel("Name", { exact: true }).count()) === 0,
        "demotion retained upload capability",
      );
      const revision = wire.operation("versioned"),
        read = waitResponse(
          page,
          "GET",
          catalogApi(revisionPath(revision.futureRevision)),
          200,
        );
      await button(page, "Retry revision access").click();
      await responseDone(await read);
      await saved(page, wire, fixed.versioned);
      await wire.demotedOwnRefusals("versioned");
      requireUpload(
        wire.postCount === posts,
        "ordinary revision access emitted an upload",
      );
      sameCounts(await wire.checkpoint(), trusted);
      await owner.control({ action: "restore-publisher" });
      const restoredRead = waitResponse(
        page,
        "GET",
        catalogApi(revisionPath(wire.operation("versioned").futureRevision)),
        200,
      );
      await refresh(page, wire);
      await responseDone(await restoredRead);
      await saved(page, wire, fixed.versioned);
      requireUpload(
        wire.postCount === posts,
        "restored capability automatically replayed upload",
      );
      sameCounts(await wire.checkpoint(), trusted);
    });
    await test.step("fresh advisory and actual pre-forward revocation make no work; restored original bytes commit decoded-invalid with distinct saved truth", async () => {
      await startAnother(page);
      await choose(page, fixed.decodedInvalid);
      await wire.focusAdmission("Upload mapping");
      const trusted = await wire.checkpoint(),
        index = wire.postCount;
      await owner.control({ action: "revoke-publisher" });
      const freshProject = waitResponse(page, "GET", wire.projectApi, 200);
      await button(page, "Upload mapping").click();
      await responseDone(await freshProject);
      await visibleHeading(page, "Upload form unavailable");
      requireUpload(
        wire.postCount === index,
        "fresh denied capability dispatched a native POST",
      );
      sameCounts(await wire.checkpoint(), trusted);
      await owner.control({ action: "restore-publisher" });
      await refresh(page, wire);
      await ready(page);
      const held = await wire.arm(fixed.decodedInvalid, "hold-forward");
      await button(page, "Upload mapping").click();
      await expect
        .poll(held.entered, {
          message:
            "Expected actual request was not held before server forwarding",
          timeout: 10000,
        })
        .toBe(true);
      await owner.control({ action: "revoke-publisher" });
      held.release();
      await wire.complete(index, 404);
      await visibleHeading(page, "Upload form unavailable");
      sameCounts(await wire.checkpoint(), trusted);
      await owner.control({ action: "restore-publisher" });
      await refresh(page, wire);
      await expect
        .poll(() => button(page, "Resend original upload").isEnabled(), {
          message: "Original refused request recovery was not restored",
        })
        .toBe(true);
      requireUpload(
        wire.postCount === index + 1,
        "restoring authority automatically submitted original",
      );
      await wire.arm(fixed.decodedInvalid, "normal", true);
      await button(page, "Resend original upload").click();
      await wire.complete(index + 1, 200);
      await saved(page, wire, fixed.decodedInvalid);
      requireUpload(
        wire.postCount === index + 2,
        "restored original was not one deliberate resend",
      );
      await wire.checkpoint();
    });
    await test.step("actual malformed and unsupported native inputs retain distinct rejected receipts without inventing mapping commits or private deletion", async () => {
      for (const sample of [fixed.decodeRejected, fixed.unsupportedRejected]) {
        await startAnother(page);
        await choose(page, sample);
        await wire.arm(sample);
        const index = wire.postCount;
        await button(page, "Upload mapping").click();
        await wire.complete(index, 422);
        await visibleHeading(page, "Upload ended without a saved revision");
        await expect
          .poll(
            () =>
              page
                .getByText(
                  sample.rejection === "nativeDecode"
                    ? "The native file could not be decoded."
                    : "This native file uses an unsupported artifact form.",
                  { exact: true },
                )
                .isVisible(),
            {
              message: "Native rejection category was not rendered distinctly",
            },
          )
          .toBe(true);
        requireUpload(
          (await heading(page, "Mapping saved").count()) === 0 &&
            (await heading(
              page,
              "Mapping saved with validation issues",
            ).count()) === 0 &&
            (await page
              .getByRole("link", { name: "View revision", exact: true })
              .count()) === 0,
          "native rejection was rendered as a saved mapping",
        );
        const receipt = wire.operation(sample.sample);
        requireUpload(
          receipt.phase === "rejected" &&
            receipt.committedSequence === null &&
            receipt.committedAdmission === null,
          "rejected native operation invented a committed revision",
        );
        requireUpload(
          wire.postCount === index + 1,
          "native rejection automatically retried",
        );
        await wire.checkpoint();
      }
    });
    await test.step("five exact captures preserve protected revision and full baseline across normal restarts with no replay worker or extra native work", async () => {
      const expected = {
        mappings: initial.mappings + 3,
        revisions: initial.revisions + 3,
        ingestions: initial.ingestions + 5,
        ingestionAttempts: initial.ingestionAttempts + 5,
        nativePublications: initial.nativePublications + 5,
        workersCreated: initial.workersCreated + 5,
      };
      sameCounts(await wire.checkpoint(), expected);
      await owner.control({
        action: "restart-profile",
        profile: "administrator",
        mode: "upload",
      });
      const reply = waitResponse(
        page,
        "GET",
        catalogApi(revisionPath(fixture.protected.revision)),
        200,
      );
      await page.goto(revisionPath(fixture.protected.revision));
      await responseDone(await reply);
      await expect
        .poll(
          () =>
            page
              .getByRole("heading", { name: "Revision 1", exact: true })
              .isVisible(),
          { message: "Protected revision did not remain ordinarily readable" },
        )
        .toBe(true);
      requireUpload(
        protectedBefore &&
          isDeepStrictEqual(await wire.protectedRevision(), protectedBefore),
        "original protected revision changed across uploads/replay/restart",
      );
      sameCounts(await wire.checkpoint(), expected);
      const dimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      requireUpload(
        dimensions.scroll <= dimensions.width + 1,
        "mobile revision layout overflows viewport",
      );
      requireUpload(
        wire.postCount === 8 && wire.receiptReadCount === 1,
        "actual browser mutation or own-receipt read census differs",
      );
      await wire.closed();
    });
  } finally {
    wire.release();
  }
});
