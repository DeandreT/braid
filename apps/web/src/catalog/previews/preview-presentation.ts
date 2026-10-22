import {
  operationStatus,
  type PresentedStatus,
  type StatusTone,
} from "../../workbench/presentation";
import type { PreviewOutcome, PreviewRun } from "./preview-types";

const outcomes: Readonly<
  Record<PreviewOutcome, readonly [string, StatusTone, string]>
> = {
  succeeded: ["Sample ran successfully", "success", "✓"],
  invalidRequest: ["Invalid request", "failure", "×"],
  invalidMapping: ["The mapping is not valid for preview", "failure", "×"],
  unsupportedArtifact: [
    "This mapping format is not supported for preview",
    "unavailable",
    "—",
  ],
  unsupportedCapability: [
    "The mapping uses features that preview does not support",
    "unavailable",
    "—",
  ],
  missingInput: ["A required input is missing", "attention", "!"],
  invalidInput: [
    "An input is not valid JSON for this mapping",
    "attention",
    "!",
  ],
  invalidParameter: ["A parameter is missing or invalid", "attention", "!"],
  executionFailed: ["The mapping failed while running", "failure", "×"],
  resourceLimit: ["A preview limit was exceeded", "failure", "×"],
  protocolFailure: [
    "The execution response could not be verified",
    "failure",
    "×",
  ],
  backendUnavailable: [
    "The execution service is unavailable",
    "unavailable",
    "—",
  ],
  backendFailure: ["The execution service failed", "failure", "×"],
  timedOut: ["The preview timed out", "failure", "×"],
  canceled: ["Canceled", "neutral", "—"],
  isolationFailure: ["The execution safety checks failed", "failure", "×"],
  authorityRevoked: [
    "Your access changed before the preview ran",
    "unavailable",
    "—",
  ],
  attemptsExhausted: [
    "The preview worker was unavailable for every attempt",
    "unavailable",
    "—",
  ],
};

export function previewOutcomeStatus(code: string | null): PresentedStatus {
  const item =
    code && Object.hasOwn(outcomes, code)
      ? outcomes[code as PreviewOutcome]
      : null;
  return item
    ? { label: item[0], tone: item[1], icon: item[2] }
    : { label: "Sample result not confirmed", tone: "unavailable", icon: "?" };
}

export function previewRunStatus(
  run: Pick<PreviewRun, "state" | "outcome">,
): PresentedStatus {
  return run.state === "completed"
    ? previewOutcomeStatus(run.outcome)
    : operationStatus(run.state);
}
