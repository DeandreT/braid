import {
  isCatalogId,
  type CatalogRevisionScope,
} from "../catalog/catalog-types";

// Local DTOs checked against integrated f4646073 ChatMappingEditProposalV1 and
// ChatMappingEditOperationsV1. Keep runtime validators when adopting generated aliases.
// These sequences are JSON integers in this API, unlike legacy mapping.write strings.
export interface MappingProposal {
  proposalId: string;
  fingerprint: string;
  status:
    | "awaitingApproval"
    | "approved"
    | "pending"
    | "saved"
    | "notSaved"
    | "notPermitted"
    | "outOfDate";
  baseRevision: CatalogRevisionScope;
  baseSequence: number;
  operationCount: number;
  operationsDigest: string;
  changes: { field: string; before: string; after: string }[];
  changesTruncated: boolean;
  revisionId: string | null;
  revisionSequence: number | null;
}
export interface MappingProposalOperations {
  proposalId: string;
  fingerprint: string;
  operationsDigest: string;
  operationCount: number;
  canonicalScript: string;
}
const object = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);
export const proposalDigest = (v: unknown): v is string =>
  typeof v === "string" && /^[0-9a-f]{64}$/.test(v);
const positive = (v: unknown): v is number =>
  typeof v === "number" && Number.isSafeInteger(v) && v > 0;
const text = (v: unknown): v is string =>
  typeof v === "string" &&
  v.length > 0 &&
  v.length <= 256 &&
  !/[\u0000-\u001f\u007f-\u009f]/.test(v);

export function mappingProposal(
  v: unknown,
  organization: string,
): v is MappingProposal {
  if (
    !object(v) ||
    !isCatalogId(v.proposalId) ||
    !proposalDigest(v.fingerprint) ||
    typeof v.status !== "string" ||
    ![
      "awaitingApproval",
      "approved",
      "pending",
      "saved",
      "notSaved",
      "notPermitted",
      "outOfDate",
    ].includes(v.status) ||
    !object(v.baseRevision) ||
    v.baseRevision.organizationId !== organization ||
    ![
      v.baseRevision.organizationId,
      v.baseRevision.projectId,
      v.baseRevision.mappingId,
      v.baseRevision.revisionId,
    ].every(isCatalogId) ||
    !positive(v.baseSequence) ||
    !positive(v.operationCount) ||
    v.operationCount > 2000 ||
    !proposalDigest(v.operationsDigest) ||
    typeof v.changesTruncated !== "boolean" ||
    !Array.isArray(v.changes) ||
    v.changes.length > 64 ||
    !v.changes.every(
      (c) => object(c) && text(c.field) && text(c.before) && text(c.after),
    )
  )
    return false;
  return v.status === "saved"
    ? isCatalogId(v.revisionId) &&
        positive(v.revisionSequence) &&
        v.revisionSequence > v.baseSequence
    : v.revisionId === null && v.revisionSequence === null;
}
export function mappingProposals(
  value: unknown,
  organization: string,
): MappingProposal[] | null {
  if (
    !object(value) ||
    !Array.isArray(value.proposals) ||
    value.proposals.length > 8 ||
    !value.proposals.every((p) => mappingProposal(p, organization))
  )
    return null;
  const proposals = value.proposals as MappingProposal[];
  return new Set(proposals.map((p) => p.proposalId)).size === proposals.length
    ? proposals
    : null;
}
export function sameProposal(a: MappingProposal, b: MappingProposal): boolean {
  return (
    a.proposalId === b.proposalId &&
    a.fingerprint === b.fingerprint &&
    a.operationsDigest === b.operationsDigest &&
    a.operationCount === b.operationCount &&
    a.baseSequence === b.baseSequence &&
    a.changesTruncated === b.changesTruncated &&
    a.changes.length === b.changes.length &&
    a.changes.every(
      (c, i) =>
        c.field === b.changes[i]!.field &&
        c.before === b.changes[i]!.before &&
        c.after === b.changes[i]!.after,
    ) &&
    ["organizationId", "projectId", "mappingId", "revisionId"].every(
      (key) =>
        a.baseRevision[key as keyof CatalogRevisionScope] ===
        b.baseRevision[key as keyof CatalogRevisionScope],
    )
  );
}
export async function proposalOperations(
  value: unknown,
  proposal: MappingProposal,
): Promise<MappingProposalOperations | null> {
  if (
    !object(value) ||
    value.proposalId !== proposal.proposalId ||
    value.fingerprint !== proposal.fingerprint ||
    value.operationsDigest !== proposal.operationsDigest ||
    value.operationCount !== proposal.operationCount ||
    typeof value.canonicalScript !== "string"
  )
    return null;
  const bytes = new TextEncoder().encode(value.canonicalScript);
  if (
    bytes.length === 0 ||
    bytes.length > 1024 * 1024 ||
    new TextDecoder().decode(bytes) !== value.canonicalScript
  )
    return null;
  try {
    const script: unknown = JSON.parse(value.canonicalScript);
    if (
      !object(script) ||
      script.format !== "ferrule.edit.v1" ||
      !Array.isArray(script.operations) ||
      script.operations.length !== proposal.operationCount
    )
      return null;
    const digest = Array.from(
      new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)),
      (b) => b.toString(16).padStart(2, "0"),
    ).join("");
    return digest === proposal.operationsDigest
      ? (value as unknown as MappingProposalOperations)
      : null;
  } catch {
    return null;
  }
}
