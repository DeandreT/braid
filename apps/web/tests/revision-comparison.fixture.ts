import { expect, type Page, type Request, type Route } from "@playwright/test";
import type { components } from "../src/api/generated";
import {
  catalogApi,
  mappingPath,
  projectPath,
  revisionPath,
  synthetic,
} from "./catalog.fixture";

// Deliberately synthetic DTOs. These qualify the comparison UI boundary, never persistence or authority.
type Revision = components["schemas"]["CatalogRevisionV1"];
type Document = components["schemas"]["ProjectionDocumentV1"];
type Projection = components["schemas"]["CatalogProjectionReportV1"];
type Validation = components["schemas"]["CatalogValidationReportV1"];
export type Comparison = components["schemas"]["RevisionComparisonV1"];
export type Item = components["schemas"]["ComparisonItemV1"];
type Facet = components["schemas"]["ComparisonFacetV1"];
type Coverage = components["schemas"]["ProjectionCoverage"];
export type Reply = { status?: number; json?: unknown; abort?: boolean };

export const REV = [1, 2, 3].map(
  (n) => `40000000-0000-4000-8000-00000000000${n}`,
);
export const P = [1, 2, 3].map(
  (n) => `50000000-0000-4000-8000-00000000000${n}`,
);
export const V = [1, 2, 3].map(
  (n) => `60000000-0000-4000-8000-00000000000${n}`,
);
const copy = <T>(value: T): T => structuredClone(value);
const locator: components["schemas"]["NativeLocator"] = {
  kind: "jsonPointer",
  documentId: "entry.native.json",
  pointer: "/synthetic",
};
const backend = {
  id: "ferrule",
  revision: "synthetic-backend-revision",
  buildId: "synthetic-build",
};

export function revision(index: number): Revision {
  return {
    ...copy(synthetic.revision),
    scope: { ...synthetic.revision.scope, revisionId: REV[index] },
    sequence: String(index + 1),
    nativeDigest: { algorithm: "sha256", value: "abd"[index].repeat(64) },
    originalFilename: `mapping-v${index + 1}.json`,
    committedAtUtc: `2026-0${index + 1}-01T00:00:00Z`,
  };
}

/** Report-local IDs are prefixed per revision so labels must resolve on their own side. */
export function document(index: number): Document {
  const rev = revision(index);
  const id = (value: string) => `r${index + 1}-${value}`;
  const field = (
    name: string,
    scalar: "string" | "integer" | "decimal",
    boundaryId = id("out"),
  ): Document["fields"][number] => ({
    id: id(`field-${name}`),
    name,
    boundaryId,
    path: [{ kind: "field", name }],
    dataType: { kind: "scalar", scalarTypes: [scalar] },
    cardinality: { kind: "exactlyOne" },
    requirement: "required",
    nullability: "nonNullable",
    nativeLocator: locator,
  });
  const later = index > 0;
  return {
    schemaVersion: "weft.mapping-projection.v1",
    provenance: {
      revision: rev.scope,
      nativeArtifactDigest: rev.nativeDigest,
      backend,
      adapterVersion: "synthetic-adapter",
      projectionVersion: "weft.ferrule-projection.v1",
    },
    boundaries: [
      {
        id: id("in"),
        name: "Orders",
        format: "json",
        role: "primaryInput",
        shape: "object",
        rootFieldId: null,
        nativeLocator: locator,
      },
      {
        id: id("out"),
        name: "Shipment",
        format: "json",
        role: "primaryOutput",
        shape: "object",
        rootFieldId: null,
        nativeLocator: locator,
      },
    ],
    fields: [
      field("orderId", "string", id("in")),
      field("id", "string"),
      field("qty", later ? "decimal" : "integer"),
      field(later ? "carrier" : "legacy", "string"),
    ],
    rules: [
      {
        id: id("rule-id"),
        outputFieldId: id("field-id"),
        summary: "Copy the order ID",
        nativeLocator: locator,
        dependencies: [{ kind: "field", fieldId: id("field-orderId") }],
        details: [],
      },
    ],
    controls: [],
    parameters: [
      {
        id: id("param-region"),
        name: "region",
        dataType: { kind: "scalar", scalarTypes: ["string"] },
        requirement: "required",
        defaultValue: null,
        nativeLocator: locator,
      },
      ...(later
        ? [
            {
              id: id("param-currency"),
              name: "currency",
              dataType: {
                kind: "scalar" as const,
                scalarTypes: ["string" as const],
              },
              requirement: "optional" as const,
              defaultValue: null,
              nativeLocator: locator,
            },
          ]
        : []),
    ],
    relationships: [],
    capabilities: [],
    diagnostics: [],
    coverage: { status: "complete", gaps: [] },
  };
}

const result = (derivationId: string) => ({
  derivationId,
  generation: "1",
  startedAtUtc: "2026-02-01T00:00:00Z",
  finishedAtUtc: "2026-02-01T00:00:01Z",
});

export function projection(index: number): Projection {
  const rev = revision(index);
  return {
    schemaVersion: "weft.catalog-revision-report.v1",
    operation: "projection",
    revision: rev.scope,
    state: "available",
    readOutcome: "observed",
    report: {
      status: "succeeded",
      diagnostics: [],
      provenance: {
        revision: rev.scope,
        nativeArtifactDigest: rev.nativeDigest,
        backend,
        adapterVersion: "synthetic-adapter",
        confirmed: true,
      },
      document: document(index),
    },
    result: result(P[index]),
    currentAttempt: null,
    status: "succeeded",
    failure: null,
    nextAttemptAtUtc: null,
  };
}

export function validation(index: number, valid = index === 0): Validation {
  const rev = revision(index);
  return {
    schemaVersion: "weft.catalog-revision-report.v1",
    operation: "validation",
    revision: rev.scope,
    state: "available",
    readOutcome: "observed",
    report: {
      status: valid ? "succeeded" : "invalidMapping",
      isValid: valid,
      provenance: {
        revision: rev.scope,
        nativeArtifactDigest: rev.nativeDigest,
        backend,
        adapterVersion: "synthetic-adapter",
        confirmed: true,
      },
      totalIssueCount: valid ? 0 : 1,
      emittedIssueCount: valid ? 0 : 1,
      truncated: false,
      diagnostics: valid
        ? []
        : [
            {
              code: "syntheticFinding",
              message: "Synthetic validation finding",
              severity: "error",
              messageTruncated: false,
              locationTruncated: false,
              nativeLocator: locator,
              projectionLocator: null,
            },
          ],
    },
    result: result(V[index]),
    currentAttempt: null,
    status: valid ? "succeeded" : "invalidMapping",
    failure: null,
    nextAttemptAtUtc: null,
    projectionDerivationId: null,
  };
}

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

export const node = (
  kind: "boundary" | "field" | "parameter" | "rule" | "control",
  id: string,
): Item["left"][number] =>
  ({
    node: { kind, [`${kind}Id`]: id },
    relationshipId: null,
  }) as Item["left"][number];

export const facet = (
  aspect: Facet["aspect"],
  hasObservedDifference = false,
  hasUncertainty = false,
  reasons: Facet["reasons"] = [],
): Facet => ({ aspect, hasObservedDifference, hasUncertainty, reasons });

export function item(
  entity: Item["entity"],
  match: Item["match"],
  left: Item["left"],
  right: Item["right"],
  facets: Facet[],
  counts: { left?: number; right?: number } = {},
): Item {
  const leftCandidateCount = counts.left ?? left.length;
  const rightCandidateCount = counts.right ?? right.length;
  return {
    entity,
    match,
    leftCandidateCount,
    rightCandidateCount,
    candidatesTruncated:
      left.length < leftCandidateCount || right.length < rightCandidateCount,
    left,
    right,
    facets,
  };
}

/** The ordinary pairing of revision 1 (left) with revision 3 (right). */
export function pairedItems(): Item[] {
  return [
    item(
      "boundary",
      "matched",
      [node("boundary", "r1-in")],
      [node("boundary", "r3-in")],
      [facet("shape"), facet("presentation")],
    ),
    item(
      "boundary",
      "matched",
      [node("boundary", "r1-out")],
      [node("boundary", "r3-out")],
      [facet("shape"), facet("presentation")],
    ),
    item(
      "field",
      "matched",
      [node("field", "r1-field-id")],
      [node("field", "r3-field-id")],
      [facet("shape"), facet("dataType")],
    ),
    item(
      "field",
      "matched",
      [node("field", "r1-field-qty")],
      [node("field", "r3-field-qty")],
      [facet("shape"), facet("dataType", true)],
    ),
    item(
      "field",
      "onlyLeft",
      [node("field", "r1-field-legacy")],
      [],
      [facet("shape", true)],
    ),
    item(
      "field",
      "onlyRight",
      [],
      [node("field", "r3-field-carrier")],
      [facet("shape", true)],
    ),
    item(
      "parameter",
      "matched",
      [node("parameter", "r1-param-region")],
      [node("parameter", "r3-param-region")],
      [
        facet("dataType"),
        facet("requirement"),
        facet("default"),
        facet("presentation"),
      ],
    ),
    item(
      "parameter",
      "onlyRight",
      [],
      [node("parameter", "r3-param-currency")],
      [facet("shape", true)],
    ),
    item(
      "rule",
      "matched",
      [node("rule", "r1-rule-id")],
      [node("rule", "r3-rule-id")],
      [facet("shape"), facet("value"), facet("details")],
    ),
    item(
      "validation",
      "matched",
      [],
      [],
      [facet("validationValidity", true), facet("validationCounts", true)],
    ),
  ];
}

export function comparison(options: {
  left?: number;
  right?: number;
  items?: Item[];
  leftValidation?: boolean;
  rightValidation?: boolean;
  leftCoverage?: Coverage;
  rightCoverage?: Coverage;
  total?: number;
}): Comparison {
  const l = options.left ?? 0,
    r = options.right ?? 2;
  const lv = options.leftValidation ?? true,
    rv = options.rightValidation ?? true;
  const items = options.items ?? pairedItems();
  const total = options.total ?? items.length;
  const leftCoverage = options.leftCoverage ?? { status: "complete", gaps: [] };
  const rightCoverage = options.rightCoverage ?? {
    status: "complete",
    gaps: [],
  };
  const truncated =
    total > items.length || items.some((value) => value.candidatesTruncated);
  const facets = items.flatMap((value) => value.facets);
  const citation = (index: number, withValidation: boolean) => {
    const rev = revision(index);
    return {
      revision: rev.scope,
      sequence: rev.sequence,
      nativeDigest: rev.nativeDigest,
      nativeByteCount: String(rev.nativeByteCount),
      projectionProvenance: document(index).provenance,
      projection: {
        derivationId: P[index],
        generation: "1",
        keyFingerprint: "c".repeat(64),
      },
      validation: withValidation
        ? {
            derivationId: V[index],
            generation: "1",
            keyFingerprint: "e".repeat(64),
          }
        : null,
    };
  };
  const summary = (index: number) => {
    const report = validation(index).report!;
    return {
      isValid: report.isValid!,
      totalIssueCount: report.totalIssueCount,
      emittedIssueCount: report.emittedIssueCount,
      truncated: false,
      hasTruncatedDiagnosticText: false,
    };
  };
  return {
    schemaVersion: "weft.revision-comparison.v1",
    policyVersion: "weft.projection-facts-comparison.v1",
    left: citation(l, lv),
    right: citation(r, rv),
    leftCoverage,
    rightCoverage,
    validationIncluded: lv || rv,
    leftValidation: lv ? summary(l) : null,
    rightValidation: rv ? summary(r) : null,
    hasObservedDifferences: facets.some((value) => value.hasObservedDifference),
    hasUncertainty:
      truncated ||
      leftCoverage.status !== "complete" ||
      rightCoverage.status !== "complete" ||
      facets.some((value) => value.hasUncertainty),
    totalItemCount: total,
    emittedItemCount: items.length,
    truncated,
    items,
  };
}

type Handler = (request: Request, url: URL) => Reply | Promise<Reply>;

/** A closed synthetic API: every unexpected request is recorded and refused. */
export class ComparisonFixture {
  readonly context = copy(synthetic.context);
  readonly mapping = copy(synthetic.mapping);
  readonly project = copy(synthetic.project);
  readonly revisions = [0, 1, 2].map(revision);
  readonly projections = [0, 1, 2].map(projection);
  readonly validations = [0, 1, 2].map((index) => validation(index));
  readonly comparisons: string[] = [];
  readonly unexpected: string[] = [];
  readonly posts: string[] = [];
  missingRevisions = new Set<string>();
  compare: Handler = () => ({ json: comparison({}) });

  constructor(readonly page: Page) {
    this.context.features.readReports = true;
    this.mapping.access.actions.readReports = true;
    this.mapping.head = {
      scope: this.revisions[2].scope,
      sequence: "3",
      admission: "valid",
    };
  }

  get mappingApi(): string {
    return catalogApi(mappingPath(this.mapping.scope));
  }

  comparisonPage(query = ""): string {
    return `${mappingPath(this.mapping.scope)}/comparisons${query ? "?" + query : ""}`;
  }

  revisionPage(index: number): string {
    return revisionPath(this.revisions[index].scope);
  }

  /** Hold the next comparison reply until released; reports whether the browser cancelled it. */
  holdComparison(reply: Reply) {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => (release = resolve));
    let held: Request | undefined;
    let cancelled = false;
    this.page.on("requestfailed", (request) => {
      if (request === held) cancelled = true;
    });
    const previous = this.compare;
    this.compare = async (request) => {
      held = request;
      this.compare = previous;
      await gate;
      return reply;
    };
    return { release, cancelled: () => cancelled, entered: () => !!held };
  }

  async install(): Promise<void> {
    await this.page.route("**/api/**", (route) => this.handle(route));
  }

  private async handle(route: Route): Promise<void> {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    let reply: Reply | null = null;
    if (request.method() !== "GET") {
      this.posts.push(`${request.method()} ${path}`);
      reply = { status: 404, json: { code: "notFound" } };
    } else if (path === "/api/auth/session")
      reply = { json: { version: 1, mode: "development" } };
    else if (path === "/api/v1/catalog/context") reply = { json: this.context };
    else if (path === catalogApi(projectPath(this.project.scope)))
      reply = { json: this.project };
    else if (path === this.mappingApi) reply = { json: this.mapping };
    else if (path === `${this.mappingApi}/revisions`)
      reply = {
        json: { revisions: [...this.revisions].reverse(), nextCursor: null },
      };
    else if (path === `${this.mappingApi}/comparisons`) {
      this.comparisons.push(url.search.slice(1));
      reply = await this.compare(request, url);
    } else {
      reply = this.revisionReply(path, url);
    }
    if (!reply) {
      this.unexpected.push(`${request.method()} ${path}${url.search}`);
      reply = { status: 404, json: { code: "notFound" } };
    }
    try {
      if (reply.abort) await route.abort("failed");
      else
        await route.fulfill({
          status: reply.status ?? 200,
          json: reply.json ?? {},
        });
    } catch (error) {
      if (!request.failure()) throw error;
    }
  }

  private revisionReply(path: string, url: URL): Reply | null {
    for (const [index, value] of this.revisions.entries()) {
      const base = catalogApi(revisionPath(value.scope));
      if (path === base)
        return this.missingRevisions.has(value.scope.revisionId)
          ? { status: 404, json: { code: "notFound" } }
          : { json: value };
      const projectionPath = `${base}/reports/projection`;
      const validationPath = `${base}/reports/validation`;
      const projection = this.projections[index];
      if (
        path === projectionPath ||
        (projection.result &&
          path === `${projectionPath}/${projection.result.derivationId}`)
      )
        return { json: projection };
      const validation = this.validations[index];
      if (
        path === validationPath ||
        (validation.result &&
          path === `${validationPath}/${validation.result.derivationId}`)
      ) {
        const linked = url.searchParams.get("projectionDerivationId");
        if (linked === null) return { json: validation };
        expect(linked).toBe(projection.result?.derivationId);
        return {
          json: { ...copy(validation), projectionDerivationId: linked },
        };
      }
      // Other retained derivation IDs are opaque misses, exactly like the server.
      if (
        new RegExp(
          `^${base}/reports/(?:projection|validation)/[0-9a-f-]{36}$`,
        ).test(path)
      )
        return { status: 404, json: { code: "notFound" } };
    }
    return null;
  }
}

export function comparisonQuery(
  left: number,
  right: number,
  options: { leftValidation?: boolean; rightValidation?: boolean } = {},
): string {
  const query = new URLSearchParams();
  query.set("leftRevision", REV[left]);
  query.set("leftProjection", P[left]);
  if (options.leftValidation ?? true) query.set("leftValidation", V[left]);
  query.set("rightRevision", REV[right]);
  query.set("rightProjection", P[right]);
  if (options.rightValidation ?? true) query.set("rightValidation", V[right]);
  return query.toString();
}
