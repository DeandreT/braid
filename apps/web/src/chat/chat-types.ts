import type { components } from "../api/generated";
import {
  isCatalogId,
  type CatalogRevisionScope,
} from "../catalog/catalog-types";

export type ChatAvailability = components["schemas"]["ChatAvailabilityV1"];
export type ChatConversation = components["schemas"]["ChatConversationV1"];
export type ChatDetail = components["schemas"]["ChatConversationDetailV1"];
export type ChatPage = components["schemas"]["ChatConversationPageV1"];
export type ChatTurn = components["schemas"]["ChatTurnV1"];
export type ChatEvent = components["schemas"]["ChatTurnEventV1"];
export type ChatCitation = Extract<ChatEvent, { type: "citation" }>;
export type ChatCreate = components["schemas"]["ChatTurnCreateV1"];

/** A delayed status response cannot undo a terminal event already observed. */
export function reconcileTurn(
  previous: ChatTurn,
  received: ChatTurn,
): ChatTurn {
  return previous.turnId === received.turnId && previous.phase !== "active"
    ? previous
    : received;
}

export class ChatError extends Error {
  constructor(readonly code: string) {
    super("The chat request could not be completed.");
  }
}

export function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
export function text(value: unknown, max = 32768): value is string {
  return typeof value === "string" && value.length <= max;
}
export function integer(value: unknown, max = 2147483647): value is number {
  return (
    Number.isSafeInteger(value) &&
    (value as number) >= 0 &&
    (value as number) <= max
  );
}
export function sameScope(
  value: unknown,
  scope: CatalogRevisionScope,
): boolean {
  return (
    object(value) &&
    value.organizationId === scope.organizationId &&
    value.projectId === scope.projectId &&
    value.mappingId === scope.mappingId &&
    value.revisionId === scope.revisionId
  );
}
export function availability(
  value: unknown,
  scope: CatalogRevisionScope,
): value is ChatAvailability {
  return (
    object(value) &&
    value.schemaVersion === "braid.chat.v1" &&
    sameScope(value.revision, scope) &&
    typeof value.toolsEnabled === "boolean" &&
    object(value.limits) &&
    integer(value.limits.maxTextBytes, 32768) &&
    value.limits.maxTextBytes > 0 &&
    integer(value.limits.maxSelectors, 32) &&
    integer(value.limits.turnSeconds, 180) &&
    Array.isArray(value.profiles) &&
    value.profiles.length <= 16 &&
    value.profiles.every(
      (p) =>
        object(p) &&
        text(p.profileId, 64) &&
        p.profileId.length > 0 &&
        integer(p.version) &&
        p.version > 0 &&
        text(p.displayName, 256) &&
        text(p.providerId, 64) &&
        text(p.modelId, 256) &&
        typeof p.supportsTools === "boolean",
    )
  );
}
export function conversation(
  value: unknown,
  scope: CatalogRevisionScope,
): value is ChatConversation {
  return (
    object(value) &&
    sameScope(value.revision, scope) &&
    isCatalogId(value.conversationId) &&
    isCatalogId(value.projectionDerivationId) &&
    isCatalogId(value.validationDerivationId) &&
    text(value.createdAtUtc, 64) &&
    text(value.retainUntilUtc, 64) &&
    (value.lastTurnAtUtc === null || text(value.lastTurnAtUtc, 64)) &&
    (value.title === null || text(value.title, 32768)) &&
    integer(value.turnCount)
  );
}
export function turn(
  value: unknown,
  conversationId: string,
): value is ChatTurn {
  return (
    object(value) &&
    value.conversationId === conversationId &&
    isCatalogId(value.turnId) &&
    ["active", "completed", "failed", "cancelled", "interrupted"].includes(
      String(value.phase),
    ) &&
    text(value.text) &&
    integer(value.lastSequence, 4096) &&
    value.lastSequence > 0 &&
    text(value.createdAtUtc, 64) &&
    text(value.deadlineUtc, 64) &&
    (value.phase === "active"
      ? value.finishedAtUtc === null
      : text(value.finishedAtUtc, 64)) &&
    Array.isArray(value.selectors) &&
    value.selectors.length <= 32 &&
    value.selectors.every(selector) &&
    object(value.profile) &&
    text(value.profile.profileId, 64) &&
    integer(value.profile.version) &&
    text(value.profile.providerId, 64) &&
    text(value.profile.modelId, 256)
  );
}
export function selector(value: unknown): boolean {
  if (!object(value)) return false;
  if (
    ["projectionDiagnostic", "validationDiagnostic"].includes(
      String(value.kind),
    )
  )
    return value.resourceId === null && integer(value.diagnosticOrdinal, 32767);
  if (["projectionReport", "validationReport"].includes(String(value.kind)))
    return value.resourceId === null && value.diagnosticOrdinal === null;
  return (
    ["field", "rule", "boundary", "parameter", "control"].includes(
      String(value.kind),
    ) &&
    text(value.resourceId, 2048) &&
    value.resourceId.length > 0 &&
    value.diagnosticOrdinal === null
  );
}
export function event(value: unknown): value is ChatEvent {
  if (!object(value) || !integer(value.sequence, 4096) || value.sequence < 1)
    return false;
  if (
    ["turn.started", "turn.completed", "turn.cancelled"].includes(
      String(value.type),
    )
  )
    return true;
  if (value.type === "turn.failed")
    return (
      [
        "provider",
        "outputLimit",
        "refused",
        "contentFiltered",
        "interrupted",
        "stopped",
      ].includes(String(value.reason)) &&
      (value.providerFailure === null ||
        [
          "invalidRequest",
          "profileUnavailable",
          "metadataDisallowed",
          "capabilityUnavailable",
          "credentialUnavailable",
          "busy",
          "budgetExceeded",
          "cancelled",
          "deadlineExceeded",
          "transportFailure",
          "providerAuthentication",
          "providerRateLimited",
          "providerRejected",
          "providerUnavailable",
          "unsupportedResponse",
          "malformedResponse",
          "truncatedResponse",
        ].includes(String(value.providerFailure)))
    );
  if (!isCatalogId(value.roundId)) return false;
  switch (value.type) {
    case "text.delta":
      return text(value.text, 262144);
    case "citation":
      return selector(value.target) && isCatalogId(value.reportDerivationId);
    case "usage":
      return (
        ["observation", "roundTerminal", "openRejection"].includes(
          String(value.origin),
        ) &&
        [value.inputTokens, value.outputTokens, value.totalTokens].every(
          (v) => v === null || integer(v, Number.MAX_SAFE_INTEGER),
        )
      );
    case "tool.started":
    case "tool.completed":
      return (
        text(value.name, 64) &&
        integer(value.ordinal, 8) &&
        value.ordinal > 0 &&
        (value.type === "tool.started" ||
          (integer(value.resultBytes) && integer(value.retrievedNodes)))
      );
    default:
      return false;
  }
}

export function terminal(value: ChatEvent): boolean {
  return ["turn.completed", "turn.failed", "turn.cancelled"].includes(
    value.type,
  );
}
