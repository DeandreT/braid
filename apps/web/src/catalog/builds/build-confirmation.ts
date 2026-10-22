import type { ConfirmationView } from "../../workbench/confirmation";
import type { PendingBuildMutation } from "./build-recovery";

export interface BuildConfirmationContext {
  sequence: string;
  environment?: { environmentId: string; version: string };
  previousState?: string;
}

/** Labels use explicit captured facts, never a guessed environment name or active release. */
export function buildConfirmation(
  operation: PendingBuildMutation,
  context: BuildConfirmationContext,
): ConfirmationView {
  const facts = [
    { label: "Mapping version", value: context.sequence },
    { label: "Version reference", value: operation.owner.scope.revisionId },
    { label: "Request reference", value: operation.id },
  ];
  let environment = context.environment;
  if (operation.kind === "environment")
    environment = { environmentId: operation.id, version: operation.version };
  if (operation.kind === "approval") {
    const body = JSON.parse(operation.body) as {
      environmentId: string;
      environmentVersion: string;
      regressionEvidenceId: string;
    };
    environment = {
      environmentId: body.environmentId,
      version: body.environmentVersion,
    };
    facts.push({
      label: "Test result reference",
      value: body.regressionEvidenceId,
    });
  }
  facts.push({
    label: "Environment reference",
    value: environment?.environmentId ?? "Not supplied with this operation",
  });
  if (environment)
    facts.push({ label: "Environment version", value: environment.version });
  if (context.previousState)
    facts.push({ label: "State when opened", value: context.previousState });
  const afterLeaving =
    "After confirmation, the request may still finish if you leave. Recovery details remain only while this version page and its access stay active.";
  const version = context.sequence;
  switch (operation.kind) {
    case "environment":
      return {
        title: `Register environment version ${operation.version}`,
        action: `Register environment version ${operation.version}`,
        effect:
          "Registers the offered runtime profile. A next version supersedes the previous environment version; this does not put a mapping version in use.",
        afterLeaving,
        facts,
      };
    case "approval":
      return {
        title: `Approve preparation of version ${version}`,
        action: `Approve version ${version}`,
        effect:
          "Allows a later preparation request for this saved version and environment. This does not start preparation or change the version in use.",
        afterLeaving,
        facts,
      };
    case "submit":
      return {
        title: `Prepare version ${version}`,
        action: `Prepare version ${version}`,
        effect:
          "Prepares this saved version using the recorded approval. The version in use and saved test expectations stay unchanged.",
        afterLeaving,
        facts,
      };
    case "revoke":
      return {
        title: `Withdraw approval for version ${version}`,
        action: "Withdraw approval",
        effect:
          "Withdraws this approval. Existing work may still finish. Completed prepared files and the version in use are not deleted or changed.",
        afterLeaving,
        facts,
      };
    case "cancel":
      return {
        title: `Cancel preparation of version ${version}`,
        action: "Request cancellation",
        effect:
          "Requests cancellation of this preparation. It may already have finished; review the returned result. The version in use stays unchanged.",
        afterLeaving,
        facts,
      };
  }
}
