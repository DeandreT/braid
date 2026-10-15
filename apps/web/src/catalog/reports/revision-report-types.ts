import type { components } from "../../api/generated";

export type CatalogProjectionReport =
  components["schemas"]["CatalogProjectionReportV1"];
export type CatalogValidationReport =
  components["schemas"]["CatalogValidationReportV1"];
export type RevisionReportResult =
  components["schemas"]["CatalogRevisionReportResultV1"];
export type RevisionReportAttempt =
  components["schemas"]["CatalogRevisionReportAttemptV1"];
export type RevisionReportOperation = "projection" | "validation";
export type RevisionReport = CatalogProjectionReport | CatalogValidationReport;

/** Lifecycle/readOutcome in the body remains independent of HTTP admission. */
export interface RevisionReportResponse<T extends RevisionReport> {
  readonly status: 200 | 202;
  readonly body: T;
  readonly location: string | null;
  readonly retryAfterSeconds: number | null;
}

export type RevisionReportErrorKind =
  | "notFound"
  | "rejected"
  | "invalidLink"
  | "claimLost"
  | "selectionChanged"
  | "generationExhausted"
  | "admissionBusy"
  | "dependencyUnavailable"
  | "resourceLimit"
  | "timedOut"
  | "incompatible"
  | "unavailable";

/** No response/provider text, request token or response body is retained in errors. */
export class RevisionReportError extends Error {
  constructor(
    readonly kind: RevisionReportErrorKind,
    readonly status: number | null = null,
  ) {
    super("The saved report request could not be completed.");
    this.name = "RevisionReportError";
  }
}
