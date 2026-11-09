import type { components } from "../api/generated";
import {
  isCatalogId,
  type CatalogRevisionScope,
} from "../catalog/catalog-types";
import {
  event,
  integer,
  object,
  selector,
  text,
  type ChatAvailability,
  type ChatConversation,
  type ChatEvent,
} from "./chat-types";

export type ChatScope = CatalogRevisionScope | { organizationId: string };
export type ChatFocus = components["schemas"]["ChatFocusV1"];
// Nullable object references are checked at the transport boundary, independently of generated annotations.
export type OrganizationConversation = Omit<
  components["schemas"]["ChatOrganizationConversationV1"],
  "focus" | "revision"
> & {
  focus: ChatFocus | null;
  revision: CatalogRevisionScope | null;
};
export type AnyConversation = ChatConversation | OrganizationConversation;
export type WorkspaceCitation =
  components["schemas"]["ChatWorkspaceCitationV1"];
export type WorkspaceCitationEvent = Extract<
  ChatEvent,
  { type: "workspace.citation" }
>;
export type UiAvailability = Pick<
  ChatAvailability,
  "limits" | "toolsEnabled"
> & {
  profiles: (components["schemas"]["ChatProfileV1"] & {
    payloadsAllowed?: boolean;
  })[];
};
export function revisionScope(
  value: unknown,
  organizationId: string,
): value is CatalogRevisionScope {
  return (
    object(value) &&
    value.organizationId === organizationId &&
    [value.projectId, value.mappingId, value.revisionId].every(isCatalogId)
  );
}
export function focus(value: unknown): value is ChatFocus | null {
  return (
    value === null ||
    (object(value) &&
      isCatalogId(value.projectId) &&
      isCatalogId(value.mappingId) &&
      (value.revisionId === null || isCatalogId(value.revisionId)))
  );
}
export function organizationConversation(
  value: unknown,
  organizationId: string,
): value is OrganizationConversation {
  return (
    object(value) &&
    isCatalogId(value.conversationId) &&
    (value.kind === "workspace"
      ? value.revision === null && focus(value.focus)
      : value.kind === "revision" &&
        value.focus === null &&
        revisionScope(value.revision, organizationId)) &&
    text(value.createdAtUtc, 64) &&
    text(value.retainUntilUtc, 64) &&
    (value.lastTurnAtUtc === null || text(value.lastTurnAtUtc, 64)) &&
    (value.title === null || text(value.title)) &&
    integer(value.turnCount)
  );
}
export function workspaceAvailability(
  value: unknown,
  organizationId: string,
): UiAvailability | null {
  if (
    !object(value) ||
    value.schemaVersion !== "weft.chat.v1" ||
    value.organizationId !== organizationId ||
    !object(value.limits) ||
    !integer(value.limits.maxTextBytes, 32768) ||
    value.limits.maxTextBytes < 1 ||
    !integer(value.limits.maxSelectors, 32) ||
    !integer(value.limits.turnSeconds, 180) ||
    !Array.isArray(value.profiles) ||
    !value.profiles.length ||
    value.profiles.length > 16
  )
    return null;
  const profiles: UiAvailability["profiles"] = [];
  for (const item of value.profiles) {
    if (
      !object(item) ||
      typeof item.payloadsAllowed !== "boolean" ||
      !object(item.profile)
    )
      return null;
    const p = item.profile;
    if (
      !text(p.profileId, 64) ||
      !p.profileId ||
      !integer(p.version) ||
      !p.version ||
      !text(p.displayName, 256) ||
      !text(p.providerId, 64) ||
      !text(p.modelId, 256) ||
      p.supportsTools !== true
    )
      return null;
    profiles.push({
      profileId: p.profileId,
      version: p.version,
      displayName: p.displayName,
      providerId: p.providerId,
      modelId: p.modelId,
      supportsTools: true,
      payloadsAllowed: item.payloadsAllowed,
    });
  }
  if (new Set(profiles.map((p) => p.profileId)).size !== profiles.length)
    return null;
  return {
    profiles,
    toolsEnabled: true,
    limits: {
      maxTextBytes: value.limits.maxTextBytes,
      maxSelectors: value.limits.maxSelectors,
      turnSeconds: value.limits.turnSeconds,
    },
  };
}
export function workspaceEvent(
  value: unknown,
  organizationId: string,
): value is ChatEvent {
  if (event(value)) return value.type !== "citation";
  if (
    !object(value) ||
    !integer(value.sequence, 4096) ||
    value.sequence < 1 ||
    !isCatalogId(value.roundId)
  )
    return false;
  if (value.type === "data.shared")
    return (
      isCatalogId(value.projectId) &&
      isCatalogId(value.subjectId) &&
      [
        "previewInput",
        "previewOutput",
        "regressionCase",
        "regressionRunOutput",
      ].includes(String(value.source)) &&
      (value.version === null || decimal(value.version)) &&
      integer(value.bytes, 8192) &&
      value.bytes > 0
    );
  if (value.type === "mapping.write")
    return mappingWrite(value, organizationId);
  return (
    value.type === "workspace.citation" &&
    workspaceCitation(value.citation, organizationId)
  );
}
export type MappingWriteEvent = Extract<ChatEvent, { type: "mapping.write" }>;
function mappingWrite(
  value: Record<string, unknown>,
  organizationId: string,
): boolean {
  const base = value.baseRevision;
  const optional = (id: unknown, sequence: unknown) =>
    id === null ? sequence === null : isCatalogId(id) && decimal(sequence);
  const saved = value.outcome === "saved";
  return (
    object(base) &&
    base.organizationId === organizationId &&
    [base.projectId, base.mappingId, base.revisionId].every(isCatalogId) &&
    decimal(value.baseSequence) &&
    (saved
      ? value.reason === null && value.revisionId !== null
      : value.outcome === "proposed" &&
        (value.reason === "outOfDate"
          ? value.headRevisionId !== null
          : value.reason === "notPermitted" && value.headRevisionId === null) &&
        value.revisionId === null) &&
    optional(value.revisionId, value.revisionSequence) &&
    optional(value.headRevisionId, value.headSequence) &&
    integer(value.operationCount, 2000) &&
    value.operationCount > 0 &&
    typeof value.changesTruncated === "boolean" &&
    Array.isArray(value.changes) &&
    value.changes.length <= 8 &&
    value.changes.every(
      (c: unknown) =>
        object(c) &&
        [c.field, c.before, c.after].every(
          (t) => typeof t === "string" && t.length > 0 && t.length <= 256,
        ),
    )
  );
}
export function workspaceCitation(
  value: unknown,
  organizationId: string,
): value is WorkspaceCitation {
  if (
    !object(value) ||
    value.organizationId !== organizationId ||
    ![
      value.projectId,
      value.mappingId,
      value.revisionId,
      value.reportDerivationId,
      value.subjectId,
    ].every((v) => v === null || isCatalogId(v)) ||
    !(value.version === null || decimal(value.version))
  )
    return false;
  const project = isCatalogId(value.projectId),
    mapping = project && isCatalogId(value.mappingId),
    revision = mapping && isCatalogId(value.revisionId);
  const noReport = value.target === null && value.reportDerivationId === null;
  const noSubject = value.subjectId === null && value.version === null;
  if (value.kind === "guideSection")
    return (
      !project &&
      value.mappingId === null &&
      value.revisionId === null &&
      noReport &&
      noSubject &&
      typeof value.sectionId === "string" &&
      /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/.test(value.sectionId) &&
      value.sectionId.length <= 64
    );
  if (value.sectionId !== null || !project) return false;
  switch (value.kind) {
    case "project":
      return (
        value.mappingId === null &&
        value.revisionId === null &&
        noReport &&
        noSubject
      );
    case "mapping":
      return mapping && value.revisionId === null && noReport && noSubject;
    case "revision":
      return revision && noReport && noSubject;
    case "report":
      return (
        revision &&
        selector(value.target) &&
        isCatalogId(value.reportDerivationId) &&
        noSubject
      );
    case "buildEnvironment":
      return (
        value.mappingId === null &&
        value.revisionId === null &&
        noReport &&
        isCatalogId(value.subjectId) &&
        decimal(value.version)
      );
    case "regressionCase":
      return (
        mapping &&
        value.revisionId === null &&
        noReport &&
        isCatalogId(value.subjectId) &&
        decimal(value.version)
      );
    case "previewRun":
      return (
        revision &&
        noReport &&
        isCatalogId(value.subjectId) &&
        value.version === null
      );
    case "regressionRun":
    case "buildRun":
    case "buildApproval":
      return (
        mapping &&
        value.revisionId === null &&
        noReport &&
        isCatalogId(value.subjectId) &&
        value.version === null
      );
    default:
      return false;
  }
}
function decimal(value: unknown): value is string {
  return typeof value === "string" && /^[1-9][0-9]{0,19}$/.test(value);
}
export function isWorkspace(
  conversation: AnyConversation,
): conversation is OrganizationConversation {
  return "kind" in conversation && conversation.kind === "workspace";
}
export function conversationScope(
  conversation: AnyConversation,
  organizationId: string,
): ChatScope {
  return isWorkspace(conversation)
    ? { organizationId }
    : conversation.revision!;
}
const citationNames: Record<string, string> = {
  revision: "version",
  previewRun: "preview run",
  regressionCase: "test case",
  regressionRun: "test run",
  buildRun: "build",
  buildApproval: "build approval",
  buildEnvironment: "build environment",
  guideSection: "help",
  projectionDiagnostic: "explanation issue",
  validationDiagnostic: "validation issue",
  projectionReport: "explanation",
  validationReport: "validation",
};
/** Reader-facing name for a citation or selector kind. */
export function citationName(kind: string | null | undefined): string {
  return kind ? (citationNames[kind] ?? kind) : "";
}
export function citationLink(c: WorkspaceCitation): string {
  if (c.kind === "guideSection") return "/help/" + c.sectionId;
  if (
    [
      "previewRun",
      "regressionCase",
      "regressionRun",
      "buildRun",
      "buildApproval",
      "buildEnvironment",
    ].includes(c.kind)
  ) {
    const query = new URLSearchParams({
      kind: c.kind,
      organizationId: c.organizationId,
      projectId: c.projectId!,
      subjectId: c.subjectId!,
    });
    if (c.mappingId) query.set("mappingId", c.mappingId);
    if (c.revisionId) query.set("revisionId", c.revisionId);
    if (c.version) query.set("version", c.version);
    return `/chat/resources?${query}`;
  }
  const project = `/catalog/organizations/${c.organizationId}/projects/${c.projectId}`;
  if (c.kind === "project" || c.kind === "buildEnvironment")
    return project + (c.subjectId ? `#environment-${c.subjectId}` : "");
  const mapping = `${project}/mappings/${c.mappingId}`;
  if (!c.revisionId)
    return mapping + (c.subjectId ? `#${c.kind}-${c.subjectId}` : "");
  const revision = `${mapping}/revisions/${c.revisionId}`;
  if (c.kind === "previewRun")
    return `${revision}?tab=preview&run=${c.subjectId}`;
  if (c.kind === "regressionRun")
    return `${revision}?tab=regression&run=${c.subjectId}`;
  if (c.kind === "buildRun") return `${revision}?tab=builds&run=${c.subjectId}`;
  if (c.kind !== "report") return revision;
  const report = `${revision}?${c.target?.kind === "validationReport" || c.target?.kind === "validationDiagnostic" ? "validationDerivationId" : "projectionDerivationId"}=${c.reportDerivationId}`;
  return c.target?.kind === "field" || c.target?.kind === "rule"
    ? `${report}#${c.target.kind}:${encodeURIComponent(c.target.resourceId!)}`
    : report;
}
