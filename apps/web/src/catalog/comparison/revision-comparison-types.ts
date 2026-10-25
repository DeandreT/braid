import type { components } from "../../api/generated";
import type { CatalogRevision } from "../catalog-types";

export type RevisionComparison = Omit<
  components["schemas"]["RevisionComparisonV1"],
  "left" | "right"
> & { left: ComparisonSideCitation; right: ComparisonSideCitation };
export type ComparisonItem = components["schemas"]["ComparisonItemV1"];
export type ComparisonFacet = components["schemas"]["ComparisonFacetV1"];
export type ComparisonCandidate =
  components["schemas"]["ComparisonCandidateV1"];
export type ComparisonEntity = components["schemas"]["ComparisonEntityV1"];
export type ComparisonMatch = components["schemas"]["ComparisonMatchV1"];
export type ComparisonAspect = components["schemas"]["ComparisonAspectV1"];
export type ComparisonReason = components["schemas"]["ComparisonReasonV1"];
type AnySide = components["schemas"]["ComparisonSideCitationV1"];
// This reader admits revision/revision comparisons only. Draft comparisons have a separate editor reader.
export type ComparisonSideCitation = AnySide & {
  revision: NonNullable<AnySide["revision"]>;
  projection: NonNullable<AnySide["projection"]>;
  sequence: string;
};
export type ComparisonValidationSummary =
  components["schemas"]["ComparisonValidationSummaryV1"];
export type ComparisonSideName = "left" | "right";

/** One side names an exact revision and exact retained derivations, never a current selector. */
export interface ComparisonSideRequest {
  readonly revision: CatalogRevision;
  readonly projectionDerivationId: string;
  readonly validationDerivationId: string | null;
}

export interface ComparisonRequest {
  readonly left: ComparisonSideRequest;
  readonly right: ComparisonSideRequest;
}

export type ComparisonErrorKind =
  | "notFound"
  | "rejected"
  | "invalidLink"
  | "admissionBusy"
  | "dependencyUnavailable"
  | "resourceLimit"
  | "timedOut"
  | "incompatible"
  | "unavailable";

/** No response body, provider text or request token is retained in errors. */
export class ComparisonError extends Error {
  constructor(
    readonly kind: ComparisonErrorKind,
    readonly status: number | null = null,
  ) {
    super("The revision comparison could not be completed.");
    this.name = "ComparisonError";
  }
}

export const comparisonEntities: readonly ComparisonEntity[] = [
  "boundary",
  "field",
  "parameter",
  "rule",
  "control",
  "relationship",
  "validation",
];
export const comparisonMatches: readonly ComparisonMatch[] = [
  "matched",
  "onlyLeft",
  "onlyRight",
  "ambiguous",
];
export const comparisonAspects: readonly ComparisonAspect[] = [
  "shape",
  "dataType",
  "cardinality",
  "requirement",
  "nullability",
  "value",
  "condition",
  "default",
  "details",
  "control",
  "relationship",
  "presentation",
  "validationValidity",
  "validationCounts",
];
export const comparisonReasons: readonly ComparisonReason[] = [
  "incompleteCoverage",
  "unknownFact",
  "unsupportedDetail",
  "incompatibleProfile",
  "ambiguousMatch",
  "unresolvedReference",
  "missingValidation",
  "truncatedValidation",
  "candidatesOmitted",
  "itemsOmitted",
];
