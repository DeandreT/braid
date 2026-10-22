import type { DockView, WorkbenchView } from "./workbench-model";

/** Presentation only. Keys remain API/workflow identities, never translated text. */
export const workbenchLabels: Readonly<Record<WorkbenchView, string>> = {
  mapping: "Data mapping",
  preview: "Try sample",
  revisions: "Versions",
  compare: "Compare",
  tests: "Tests",
  builds: "Prepare",
  releases: "In use",
  audit: "Change history",
};

export const dockLabels: Readonly<Record<DockView, string>> = {
  ask: "Ask Weft",
  diagnostics: "Issues",
  input: "Incoming data",
  output: "Result",
  diff: "Diff",
  activity: "Activity",
};

export type StructuralRole = "source" | "rule" | "output" | "evidence";
export function fileStatusLabel(admission: string | null | undefined): string {
  if (admission === "valid") return "File accepted";
  if (admission === "decodedInvalid") return "Saved with validation issues";
  return "File status not confirmed";
}
export function columnRole(column: string): StructuralRole {
  if (column === "source" || column === "input") return "source";
  if (column === "rule") return "rule";
  if (column === "target" || column === "output") return "output";
  return "evidence";
}

export type StatusTone =
  | "success"
  | "running"
  | "attention"
  | "failure"
  | "uncertain"
  | "unavailable"
  | "neutral";
export interface PresentedStatus {
  label: string;
  tone: StatusTone;
  icon: string;
}

const status = (
  label: string,
  tone: StatusTone,
  icon: string,
): PresentedStatus => ({ label, tone, icon });

/** Unknown codes are not successes, cancellations, or proof of missing access. */
export function comparisonStatus(
  code: string | null | undefined,
  reason?: string,
): PresentedStatus {
  switch (code) {
    case "passed":
      return status("Matches expected result", "success", "✓");
    case "failed":
      return status("Result differs from expected", "failure", "×");
    case "notEvaluated":
      if (reason === "missingBaseline")
        return status("Expected result needed", "attention", "!");
      if (reason === "executionUnsuccessful")
        return status("Could not run this test successfully", "failure", "×");
      if (reason === "unsupportedPolicy")
        return status("Comparison not supported", "unavailable", "—");
      return status("Comparison not performed", "attention", "!");
    default:
      return status("No confirmed comparison", "unavailable", "—");
  }
}

export function operationStatus(
  code: string | null | undefined,
): PresentedStatus {
  switch (code) {
    case "queued":
      return status("Queued", "running", "◷");
    case "running":
      return status("Running", "running", "◷");
    case "completed":
      return status("Finished — review result", "neutral", "•");
    case "canceled":
      return status("Canceled", "neutral", "—");
    default:
      return status("State not confirmed", "unavailable", "?");
  }
}

export const uncertainStatus = status(
  "Result not confirmed — check the previous request",
  "uncertain",
  "?",
);

export function buildStatus(code: string | null | undefined): PresentedStatus {
  switch (code) {
    case "succeeded":
      return status("Preparation complete", "success", "✓");
    case "failed":
      return status("Preparation failed", "failure", "×");
    case "exhausted":
      return status("Preparation attempts exhausted", "failure", "×");
    case "uncertain":
      return uncertainStatus;
    default:
      return operationStatus(code);
  }
}
