import { expect, type Page, type Request, type Route } from "@playwright/test";
import { test as base } from "./revision-reports-real.fixture";
import type { components } from "../src/api/generated";
// Pure data/path exports only. The catalog's extended test (and automatic owner) is not used.
import {
  synthetic,
  catalogApi,
  revisionPath,
  mappingPath,
  projectPath,
} from "./catalog.fixture";

export type Projection = components["schemas"]["CatalogProjectionReportV1"];
export type Validation = components["schemas"]["CatalogValidationReportV1"];
export type Operation = "projection" | "validation";
export type Reply = {
  status?: number;
  json?: unknown;
  headers?: Record<string, string>;
  abort?: boolean;
};
export type Observation = {
  method: string;
  path: string;
  query: string;
  observedAt: number;
  bodyBytes: number;
  hasToken: boolean;
  hasTokenHeader: boolean;
  hasConditional: boolean;
  hasContentType: boolean;
};
export const P = "50000000-0000-4000-8000-000000000001";
export const P2 = "50000000-0000-4000-8000-000000000002";
export const V = "60000000-0000-4000-8000-000000000001";
export const V2 = "60000000-0000-4000-8000-000000000002";
export const ATTEMPT = "70000000-0000-4000-8000-000000000001";
export const copy = <T>(value: T): T => structuredClone(value);

export function controlledData(
  revisionId = synthetic.revision.scope.revisionId,
) {
  const revision = {
    ...copy(synthetic.revision),
    scope: { ...synthetic.revision.scope, revisionId },
  };
  const mapping = copy(synthetic.mapping);
  mapping.head.scope = revision.scope;
  const context = copy(synthetic.context);
  context.features.readReports = true;
  context.features.deriveReports = true;
  mapping.access.actions.readReports = true;
  mapping.access.actions.deriveReports = true;
  const provenance: components["schemas"]["OperationProvenanceV1"] = {
    revision: revision.scope,
    nativeArtifactDigest: revision.nativeDigest,
    backend: {
      id: "ferrule",
      revision: "controlled-backend-revision",
      buildId: "controlled-build",
    },
    adapterVersion: "controlled-adapter-version",
    confirmed: true,
  };
  const locator: components["schemas"]["NativeLocator"] = {
    kind: "jsonPointer",
    documentId: "entry.native.json",
    pointer: "/controlled",
  };
  const document: components["schemas"]["ProjectionDocumentV1"] = {
    schemaVersion: "braid.mapping-projection.v1",
    provenance: {
      revision: revision.scope,
      nativeArtifactDigest: revision.nativeDigest,
      backend: provenance.backend,
      adapterVersion: provenance.adapterVersion,
      projectionVersion: "braid.ferrule-projection.v1",
    },
    boundaries: [
      {
        id: "controlled-output",
        name: "Controlled output",
        format: "json",
        role: "primaryOutput",
        shape: "object",
        rootFieldId: null,
        nativeLocator: locator,
      },
    ],
    fields: [
      {
        id: "controlled-field",
        name: "savedValue",
        boundaryId: "controlled-output",
        path: [{ kind: "field", name: "savedValue" }],
        dataType: { kind: "scalar", scalarTypes: ["integer"] },
        cardinality: { kind: "exactlyOne" },
        requirement: "required",
        nullability: "nonNullable",
        nativeLocator: locator,
      },
    ],
    rules: [
      {
        id: "controlled-rule",
        outputFieldId: "controlled-field",
        summary: "Controlled saved explanation",
        nativeLocator: locator,
        dependencies: [
          {
            kind: "constant",
            value: {
              kind: "exactNumber",
              flavor: "integer",
              value: "9223372036854775807",
            },
          },
        ],
        details: [
          {
            kind: "literal",
            value: {
              kind: "exactNumber",
              flavor: "integer",
              value: "9223372036854775807",
            },
          },
        ],
      },
    ],
    controls: [],
    parameters: [],
    relationships: [],
    capabilities: [],
    diagnostics: [],
    coverage: {
      status: "partial",
      gaps: [
        {
          code: "controlledGap",
          message: "Controlled ambiguity remains visible",
          nativeLocator: locator,
        },
      ],
    },
  };
  const result = (
    id: string,
  ): components["schemas"]["CatalogRevisionReportResultV1"] => ({
    derivationId: id,
    generation: "1",
    startedAtUtc: "2026-01-01T00:00:00Z",
    finishedAtUtc: "2026-01-01T00:00:01Z",
  });
  const projection: Projection = {
    schemaVersion: "braid.catalog-revision-report.v1",
    operation: "projection",
    revision: revision.scope,
    state: "available",
    readOutcome: "observed",
    report: { status: "succeeded", diagnostics: [], provenance, document },
    result: result(P),
    currentAttempt: null,
    status: "succeeded",
    failure: null,
    nextAttemptAtUtc: null,
  };
  const validation: Validation = {
    schemaVersion: "braid.catalog-revision-report.v1",
    operation: "validation",
    revision: revision.scope,
    state: "available",
    readOutcome: "observed",
    report: {
      status: "invalidMapping",
      isValid: false,
      provenance,
      totalIssueCount: 3,
      emittedIssueCount: 1,
      truncated: true,
      diagnostics: [
        {
          code: "controlledFinding",
          message: "Controlled saved validation finding",
          severity: "error",
          messageTruncated: false,
          locationTruncated: false,
          nativeLocator: locator,
          projectionLocator: null,
        },
      ],
    },
    result: result(V),
    currentAttempt: null,
    status: "invalidMapping",
    failure: null,
    nextAttemptAtUtc: null,
    projectionDerivationId: null,
  };
  const browserPath = revisionPath(revision.scope);
  const apiPath = catalogApi(browserPath);
  const reportPath = (operation: Operation, id?: string) =>
    `${apiPath}/reports/${operation}${id ? "/" + id : ""}`;
  return {
    revision,
    mapping,
    context,
    project: copy(synthetic.project),
    projection,
    validation,
    browserPath,
    apiPath,
    reportPath,
  };
}
export type Data = ReturnType<typeof controlledData>;
export function notDerived<T extends Projection | Validation>(value: T): T {
  return {
    ...value,
    state: "notDerived",
    report: null,
    result: null,
    currentAttempt: null,
    status: null,
    failure: null,
    nextAttemptAtUtc: null,
  };
}
export function pending<T extends Projection | Validation>(value: T): T {
  return {
    ...notDerived(value),
    state: "pending",
    nextAttemptAtUtc: "2026-01-01T00:00:02Z",
    currentAttempt: {
      derivationId: ATTEMPT,
      generation: "2",
      startedAtUtc: "2026-01-01T00:00:00Z",
      finishedAtUtc: null,
      status: null,
      failure: null,
    },
  };
}
export const panel = (page: Page, operation: Operation) =>
  page.getByRole("region", {
    name: operation === "projection" ? "Saved explanation" : "Saved validation",
    exact: true,
  });

type Handler = (
  request: Request,
  observation: Observation,
) => Promise<Reply> | Reply;
export class ControlledReports {
  readonly data = controlledData();
  readonly requests: Observation[] = [];
  readonly unexpected: string[] = [];
  private readonly replies = new Map<string, Reply | Handler>();
  private readonly deferred: (() => void)[] = [];
  private readonly origin: string;
  private pendingHealthReads = 0;
  constructor(readonly page: Page) {
    const origin = process.env.BRAID_REPORTS_BASE_URL;
    if (!origin || new URL(origin).origin !== origin)
      throw new Error(
        "Controlled reports require the explicit packaged origin",
      );
    this.origin = origin;
    page.on("request", (request) => {
      if (
        request.isNavigationRequest() &&
        request.frame() === page.mainFrame() &&
        new URL(request.url()).origin === this.origin
      )
        ++this.pendingHealthReads;
    });
    this.add(this.data);
  }
  private key(method: string, path: string, query = ""): string {
    const entries: [string, string][] = [];
    new URLSearchParams(query).forEach((value, name) =>
      entries.push([name, value]),
    );
    entries.sort(
      ([a, av], [b, bv]) => a.localeCompare(b) || av.localeCompare(bv),
    );
    return `${method} ${path}?${new URLSearchParams(entries).toString()}`;
  }
  set(
    method: "GET" | "POST",
    path: string,
    reply: Reply | Handler,
    query = "",
  ): void {
    this.replies.set(this.key(method, path, query), reply);
  }
  add(data: Data): void {
    this.set("GET", "/api/v1/catalog/context", { json: data.context });
    this.set("GET", catalogApi(projectPath(data.project.scope)), {
      json: data.project,
    });
    this.set("GET", catalogApi(mappingPath(data.mapping.scope)), {
      json: data.mapping,
    });
    this.set("GET", data.apiPath, { json: data.revision });
    this.set("GET", data.reportPath("projection"), { json: data.projection });
    this.set("GET", data.reportPath("validation"), { json: data.validation });
    const projectionId = data.projection.result?.derivationId ?? P;
    const validationId = data.validation.result?.derivationId ?? V;
    this.set("GET", data.reportPath("projection", projectionId), {
      json: data.projection,
    });
    this.set("GET", data.reportPath("validation", validationId), {
      json: data.validation,
    });
    const linked = copy(data.validation);
    linked.projectionDerivationId = projectionId;
    if (linked.report)
      linked.report.diagnostics[0].projectionLocator = {
        kind: "rule",
        ruleId: "controlled-rule",
      };
    this.set(
      "GET",
      data.reportPath("validation", validationId),
      { json: linked },
      `projectionDerivationId=${projectionId}`,
    );
    for (const operation of ["projection", "validation"] as const)
      this.set("POST", data.reportPath(operation) + "/ensure", {
        status: 503,
        json: { code: "revisionReportUnavailable" },
      });
  }
  hold(method: "GET" | "POST", path: string, reply: Reply, query = "") {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let entered = false;
    let cancelled = false;
    let held: Request | undefined;
    this.page.on("requestfailed", (request) => {
      if (request === held) cancelled = true;
    });
    this.set(
      method,
      path,
      async (request) => {
        held = request;
        entered = true;
        await gate;
        return reply;
      },
      query,
    );
    this.deferred.push(release);
    return { release, entered: () => entered, cancelled: () => cancelled };
  }
  async install(): Promise<void> {
    await this.page.route("**/api/**", async (route: Route) => {
      const request = route.request();
      const url = new URL(request.url());
      const headers = request.headers();
      if (
        url.pathname === "/api/v1/health" &&
        url.origin === this.origin &&
        request.method() === "GET" &&
        !url.search &&
        (request.postDataBuffer()?.length ?? 0) === 0 &&
        this.pendingHealthReads > 0
      ) {
        --this.pendingHealthReads;
        await route.fulfill({
          json: { status: "ok", service: "braid-api", apiVersion: "v1" },
        });
        return;
      }
      // Explicit bootstrap is outside the unchanged protected-report observation census.
      if (url.pathname === "/api/auth/session") {
        const admitted =
          url.origin === this.origin &&
          request.method() === "GET" &&
          !url.search &&
          (request.postDataBuffer()?.length ?? 0) === 0 &&
          headers["x-braid-session"] === "1" &&
          ![
            "content-type",
            "authorization",
            "x-xsrf-token",
            "x-braid-auth-token",
            "idempotency-key",
            "if-match",
            "if-none-match",
            "identitycontext",
            "identity-context",
            "x-braid-identity-context",
            "x-braid-actor",
            "x-braid-role",
            "x-braid-login",
            "x-braid-logout",
            "x-braid-forget-local",
          ].some((name) => name in headers);
        if (!admitted) this.unexpected.push("Refused auth bootstrap");
        await route.fulfill(
          admitted
            ? { json: { version: 1, mode: "development" } }
            : { status: 404, json: { code: "notFound" } },
        );
        return;
      }
      const observation: Observation = {
        method: request.method(),
        path: url.pathname,
        query: url.searchParams.toString(),
        observedAt: performance.now(),
        bodyBytes: request.postDataBuffer()?.length ?? 0,
        hasToken: headers["x-xsrf-token"] === this.data.context.requestToken,
        hasTokenHeader: "x-xsrf-token" in headers,
        hasConditional: [
          "if-match",
          "if-none-match",
          "if-modified-since",
          "if-unmodified-since",
          "if-range",
          "idempotency-key",
        ].some((name) => name in headers),
        hasContentType: "content-type" in headers,
      };
      this.requests.push(observation);
      const configured = this.replies.get(
        this.key(request.method(), url.pathname, url.search),
      );
      if (!configured || url.origin !== this.origin) {
        this.unexpected.push(`${request.method()} ${url.pathname}`);
        await route.fulfill({ status: 404, json: { code: "notFound" } });
        return;
      }
      const reply =
        typeof configured === "function"
          ? await configured(request, observation)
          : configured;
      try {
        if (reply.abort) await route.abort("failed");
        else
          await route.fulfill({
            status: reply.status ?? 200,
            headers: reply.headers,
            json: reply.json ?? {},
          });
      } catch (error) {
        if (!request.failure()) throw error;
      }
    });
  }
  reads(operation: Operation): Observation[] {
    return this.requests.filter(
      (r) => r.method === "GET" && r.path.includes(`/reports/${operation}`),
    );
  }
  posts(operation: Operation): Observation[] {
    return this.requests.filter(
      (r) =>
        r.method === "POST" &&
        r.path === this.data.reportPath(operation) + "/ensure",
    );
  }
  release(): void {
    for (const release of this.deferred) release();
  }
}

export const test = base.extend<{ reports: ControlledReports }>({
  reports: async ({ page }, use) => {
    const reports = new ControlledReports(page);
    await reports.install();
    try {
      await use(reports);
    } finally {
      reports.release();
      expect(reports.unexpected).toEqual([]);
    }
  },
});
export {
  expect,
  synthetic,
  catalogApi,
  revisionPath,
  mappingPath,
  projectPath,
};
