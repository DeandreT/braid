import type { components } from "../../api/generated";
export type PreviewRun = components["schemas"]["CatalogPreviewRunV1"];
export type PreviewOutput = components["schemas"]["CatalogPreviewOutputV1"];
export type PreviewFailure = components["schemas"]["CatalogPreviewFailureV1"];
export type PreviewSubmission =
  components["schemas"]["CatalogPreviewSubmissionV1"];

export type PreviewRunState = "queued" | "running" | "completed" | "canceled";
export type PreviewTargetKind = "all" | "primary" | "named";
export type PreviewParameterKind = "string" | "integer" | "float" | "boolean";

export const previewOutcomes = [
  "succeeded",
  "invalidRequest",
  "invalidMapping",
  "unsupportedArtifact",
  "unsupportedCapability",
  "missingInput",
  "invalidInput",
  "invalidParameter",
  "executionFailed",
  "resourceLimit",
  "protocolFailure",
  "backendUnavailable",
  "backendFailure",
  "timedOut",
  "canceled",
  "isolationFailure",
  "authorityRevoked",
  "attemptsExhausted",
] as const;
export type PreviewOutcome = (typeof previewOutcomes)[number];

export interface PreviewSubmitted {
  readonly outcome: "created" | "replayed";
  readonly run: PreviewRun;
}

export interface PreviewCanceled {
  readonly outcome: "canceled" | "requested" | "finished";
  readonly run: PreviewRun;
}

export type PreviewErrorKind =
  | "notFound"
  | "rejected"
  | "invalidLink"
  | "invalidRequest"
  | "conflict"
  | "bodyLimit"
  | "unsupportedMediaType"
  | "timedOut"
  | "integrity"
  | "incompatible"
  | "unavailable";

/** No response text, request token, input or output bytes are retained in errors. */
export class PreviewError extends Error {
  constructor(
    readonly kind: PreviewErrorKind,
    readonly status: number | null = null,
  ) {
    super("The preview request could not be completed.");
    this.name = "PreviewError";
  }
}

/** Compiled ceilings mirrored from the server's preview policy. */
export const previewLimits = {
  inputDocuments: 16,
  inputBytes: 8 * 1024 * 1024,
  parameters: 32,
  nameBytes: 256,
  parameterBytes: 4 * 1024,
  outputs: 64,
} as const;
