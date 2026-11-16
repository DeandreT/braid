import { isCatalogId } from "../catalog-types";

// Mirrors the authenticated creation contract; every response is validated before display.
export interface CreationProposal {
  projectId: string;
  proposalId: string;
  fingerprint: string;
  snapshotDigest: string;
  planDigest: string;
  nativeDigest: string;
  name: string;
  description: string | null;
  planJson: string;
  approved: boolean;
  evidence: {
    bundleId: string;
    sourceId: string;
    sourceVersion: number;
    chunkOrdinal: number;
  }[];
  assumptions: { id: string; text: string; critical: boolean }[];
  questions: { text: string; critical: boolean }[];
  receipt: {
    operationId: string;
    phase: string;
    mappingId: string | null;
    revisionId: string | null;
  } | null;
}
export interface CreationSelection {
  project: string;
  proposal: string;
  fingerprint: string;
}
export interface CreationOutput {
  format: string;
  mediaType: string;
  digest: string;
  text: string;
}
export const digest = (v: unknown): v is string =>
  typeof v === "string" && /^[0-9a-f]{64}$/.test(v);
const object = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);
const keys = (v: Record<string, unknown>, names: string[]) =>
  Object.keys(v).length === names.length &&
  names.every((n) => Object.hasOwn(v, n));
const integer = (v: unknown, minimum: number): v is number =>
  typeof v === "number" && Number.isSafeInteger(v) && v >= minimum;
const utf8 = (v: unknown, maximum: number, empty = false): v is string => {
  if (
    typeof v !== "string" ||
    (!empty && !v.trim()) ||
    v.includes("\0") ||
    v.length > maximum
  )
    return false;
  const bytes = new TextEncoder().encode(v);
  return bytes.length <= maximum && new TextDecoder().decode(bytes) === v;
};
const scalarText = (
  value: unknown,
  maximum: number,
  empty = false,
): value is string =>
  utf8(value, maximum * 4, empty) && Array.from(value).length <= maximum;
export async function sha256(bytes: Uint8Array): Promise<string> {
  return Array.from(
    new Uint8Array(
      await crypto.subtle.digest("SHA-256", new Uint8Array(bytes)),
    ),
    (b) => b.toString(16).padStart(2, "0"),
  ).join("");
}
export async function creationProposal(
  value: unknown,
  selection: CreationSelection,
): Promise<CreationProposal> {
  if (
    !object(value) ||
    !keys(value, [
      "projectId",
      "proposalId",
      "fingerprint",
      "snapshotDigest",
      "planDigest",
      "nativeDigest",
      "name",
      "description",
      "planJson",
      "approved",
      "evidence",
      "assumptions",
      "questions",
      "receipt",
    ]) ||
    !isCatalogId(selection.project) ||
    !isCatalogId(selection.proposal) ||
    !digest(selection.fingerprint) ||
    value.projectId !== selection.project ||
    value.proposalId !== selection.proposal ||
    value.fingerprint !== selection.fingerprint ||
    !digest(value.snapshotDigest) ||
    !digest(value.planDigest) ||
    !digest(value.nativeDigest) ||
    !scalarText(value.name, 200) ||
    (value.description !== null &&
      !scalarText(value.description, 4000, true)) ||
    !utf8(value.planJson, 65536) ||
    typeof value.approved !== "boolean" ||
    !Array.isArray(value.evidence) ||
    value.evidence.length < 1 ||
    value.evidence.length > 256 ||
    !value.evidence.every(
      (e) =>
        object(e) &&
        keys(e, ["bundleId", "sourceId", "sourceVersion", "chunkOrdinal"]) &&
        isCatalogId(e.bundleId) &&
        isCatalogId(e.sourceId) &&
        integer(e.sourceVersion, 1) &&
        integer(e.chunkOrdinal, 0),
    ) ||
    !Array.isArray(value.assumptions) ||
    value.assumptions.length > 64 ||
    !value.assumptions.every(
      (a) =>
        object(a) &&
        keys(a, ["id", "text", "critical"]) &&
        typeof a.id === "string" &&
        /^[a-zA-Z0-9]{1,64}$/.test(a.id) &&
        utf8(a.text, 2048) &&
        typeof a.critical === "boolean",
    ) ||
    new Set(value.assumptions.map((a) => a.id)).size !==
      value.assumptions.length ||
    !Array.isArray(value.questions) ||
    value.questions.length > 64 ||
    !value.questions.every(
      (q) =>
        object(q) &&
        keys(q, ["text", "critical"]) &&
        utf8(q.text, 2048) &&
        typeof q.critical === "boolean",
    )
  )
    throw new Error("Invalid creation proposal");
  const r = value.receipt;
  if (
    r !== null &&
    (!value.approved ||
      !object(r) ||
      !keys(r, ["operationId", "phase", "mappingId", "revisionId"]) ||
      !isCatalogId(r.operationId) ||
      ![
        "Reserved",
        "Staged",
        "Ready",
        "Committed",
        "Rejected",
        "HeadConflict",
        "AttemptExhausted",
      ].includes(String(r.phase)) ||
      (r.phase === "Committed"
        ? !isCatalogId(r.mappingId) || !isCatalogId(r.revisionId)
        : r.mappingId !== null || r.revisionId !== null))
  )
    throw new Error("Invalid creation receipt");
  const plan: unknown = JSON.parse(value.planJson);
  if (
    !object(plan) ||
    !["braid.creation-plan.v1", "braid.creation-plan.v2"].includes(
      String(plan.schema),
    ) ||
    (await sha256(new TextEncoder().encode(value.planJson))) !==
      value.planDigest
  )
    throw new Error("Invalid plan digest");
  return value as unknown as CreationProposal;
}
export function sameCreation(
  a: CreationProposal,
  b: CreationProposal,
): boolean {
  return creationIdentity(a) === creationIdentity(b);
}
export function creationIdentity(p: CreationProposal): string {
  return JSON.stringify([
    p.projectId,
    p.proposalId,
    p.fingerprint,
    p.snapshotDigest,
    p.planDigest,
    p.nativeDigest,
    p.name,
    p.description,
    p.planJson,
    p.evidence,
    p.assumptions,
    p.questions,
  ]);
}
export function creationStateFollows(
  previous: Pick<CreationProposal, "approved" | "receipt">,
  current: CreationProposal,
): boolean {
  if (previous.approved && !current.approved) return false;
  if (!previous.receipt) return true;
  if (
    !current.receipt ||
    previous.receipt.operationId !== current.receipt.operationId
  )
    return false;
  if (
    ["Committed", "Rejected", "HeadConflict", "AttemptExhausted"].includes(
      previous.receipt.phase,
    )
  )
    return (
      previous.receipt.phase === current.receipt.phase &&
      previous.receipt.mappingId === current.receipt.mappingId &&
      previous.receipt.revisionId === current.receipt.revisionId
    );
  return true;
}
export function base64(bytes: Uint8Array): string {
  let text = "";
  for (let i = 0; i < bytes.length; i += 8192)
    text += String.fromCharCode(...bytes.subarray(i, i + 8192));
  return btoa(text);
}
export async function creationOutput(
  value: unknown,
  p: CreationProposal,
  sampleDigest: string,
): Promise<CreationOutput | null> {
  if (
    !object(value) ||
    !keys(value, [
      "proposalId",
      "fingerprint",
      "nativeDigest",
      "sampleDigest",
      "outcome",
      "format",
      "mediaType",
      "outputDigest",
      "outputBase64",
    ]) ||
    value.proposalId !== p.proposalId ||
    value.fingerprint !== p.fingerprint ||
    value.nativeDigest !== p.nativeDigest ||
    value.sampleDigest !== sampleDigest ||
    !digest(sampleDigest)
  )
    throw new Error("Substituted preview");
  if (value.outcome === "Failed") {
    if (
      [
        value.format,
        value.mediaType,
        value.outputDigest,
        value.outputBase64,
      ].some((v) => v !== null)
    )
      throw new Error("Unexpected failed output");
    return null;
  }
  const types: Record<string, string> = {
    json: "application/json",
    xml: "application/xml",
    csv: "text/csv",
    tsv: "text/tab-separated-values",
    x12: "application/edi-x12",
    edifact: "application/edifact",
  };
  if (
    value.outcome !== "Succeeded" ||
    typeof value.format !== "string" ||
    !Object.hasOwn(types, value.format) ||
    types[value.format] !== value.mediaType ||
    !digest(value.outputDigest) ||
    typeof value.outputBase64 !== "string" ||
    value.outputBase64.length > 1398104
  )
    throw new Error("Invalid preview");
  const bytes = Uint8Array.from(atob(value.outputBase64), (c) =>
    c.charCodeAt(0),
  );
  if (
    bytes.length > 1048576 ||
    base64(bytes) !== value.outputBase64 ||
    (await sha256(bytes)) !== value.outputDigest
  )
    throw new Error("Invalid output digest");
  return {
    format: value.format,
    mediaType: types[value.format]!,
    digest: value.outputDigest,
    text: new TextDecoder("utf-8", { fatal: true }).decode(bytes),
  };
}
