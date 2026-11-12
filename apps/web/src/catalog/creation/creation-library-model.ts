import { isCatalogId } from "../catalog-types";
import { digest } from "./creation-model";

export interface CreationLink {
  proposalId: string;
  fingerprint: string;
  reviewPath: string;
}
export interface CreationPage {
  proposals: CreationLink[];
  afterProposal: string | null;
}
export function creationReviewPath(
  organization: string,
  project: string,
  proposal: string,
  fingerprint: string,
): string {
  return `/catalog/organizations/${organization}/projects/${project}/creation/${proposal}?fingerprint=${fingerprint}`;
}
export function creationCapability(value: unknown): boolean {
  if (
    !value ||
    typeof value !== "object" ||
    Array.isArray(value) ||
    Object.keys(value).length !== 1 ||
    typeof (value as { available?: unknown }).available !== "boolean"
  )
    throw new Error("Invalid creation capability");
  return (value as { available: boolean }).available;
}
export function creationPage(
  value: unknown,
  organization: string,
  project: string,
  after: string | null,
): CreationPage {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Invalid proposal page");
  const page = value as Record<string, unknown>;
  if (
    Object.keys(page).sort().join(",") !== "afterProposal,proposals" ||
    !Array.isArray(page.proposals) ||
    page.proposals.length > 20
  )
    throw new Error("Invalid proposal page");
  let previous = after;
  const proposals: CreationLink[] = page.proposals.map((item: unknown) => {
    if (!item || typeof item !== "object" || Array.isArray(item))
      throw new Error("Invalid proposal link");
    const row = item as Record<string, unknown>;
    if (
      Object.keys(row).sort().join(",") !==
        "fingerprint,proposalId,reviewPath" ||
      !isCatalogId(row.proposalId) ||
      !digest(row.fingerprint) ||
      (previous !== null && row.proposalId <= previous) ||
      row.reviewPath !==
        creationReviewPath(
          organization,
          project,
          row.proposalId,
          row.fingerprint,
        )
    )
      throw new Error("Invalid proposal link");
    previous = row.proposalId;
    return {
      proposalId: row.proposalId,
      fingerprint: row.fingerprint,
      reviewPath: row.reviewPath as string,
    };
  });
  if (
    page.afterProposal !== null &&
    (!isCatalogId(page.afterProposal) ||
      proposals.length !== 20 ||
      page.afterProposal !== previous)
  )
    throw new Error("Invalid proposal cursor");
  return { proposals, afterProposal: page.afterProposal as string | null };
}

export interface CreationFeedback {
  projectId: string;
  proposalId: string;
  fingerprint: string;
}
export function feedbackQuestion(value: CreationFeedback): string {
  return `I want to revise retained creation proposal ${value.proposalId} in project ${value.projectId}, exact fingerprint ${value.fingerprint}. Read its exact retained plan, assumptions and questions using read_specification mode proposal before applying my feedback. Recheck the selected source evidence and generate a new immutable proposal for separate review and approval. My requested changes: `;
}
