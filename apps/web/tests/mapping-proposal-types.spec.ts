import { expect, test } from "@playwright/test";
import { createHash } from "node:crypto";
import {
  mappingProposal,
  mappingProposals,
  proposalOperations,
  sameProposal,
  type MappingProposal,
} from "../src/chat/mapping-proposal-types";

const id = (n: number) =>
  `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const script = '{"format":"ferrule.edit.v1","operations":[{"op":"bind"}]}';
const p: MappingProposal = {
  proposalId: id(1),
  fingerprint: "a".repeat(64),
  status: "awaitingApproval",
  baseRevision: {
    organizationId: id(2),
    projectId: id(3),
    mappingId: id(4),
    revisionId: id(5),
  },
  baseSequence: 2,
  operationCount: 1,
  operationsDigest: createHash("sha256").update(script).digest("hex"),
  changes: [{ field: "code", before: "Not mapped", after: "Constant" }],
  changesTruncated: true,
  revisionId: null,
  revisionSequence: null,
};
const details = {
  proposalId: p.proposalId,
  fingerprint: p.fingerprint,
  operationsDigest: p.operationsDigest,
  operationCount: 1,
  canonicalScript: script,
};

test("local proposal shape matches numeric runtime sequences and rejects unsafe, foreign, or contradictory states", () => {
  expect(mappingProposal(p, id(2))).toBe(true);
  for (const invalid of [
    { ...p, baseSequence: "2" },
    { ...p, baseSequence: Number.MAX_SAFE_INTEGER + 1 },
    { ...p, fingerprint: "A".repeat(64) },
    { ...p, status: ["awaitingApproval"] },
    { ...p, status: "saved" },
    { ...p, revisionId: id(6) },
    { ...p, operationCount: 2001 },
    { ...p, changes: Array(65).fill(p.changes[0]) },
    { ...p, changes: [{ field: "code", before: "bad\ntext", after: "new" }] },
    { ...p, changesTruncated: null },
    { ...p, status: "saved", revisionId: id(6), revisionSequence: 2 },
  ])
    expect(mappingProposal(invalid, id(2))).toBe(false);
  expect(mappingProposal(p, id(9))).toBe(false);
  expect(
    mappingProposal(
      { ...p, status: "saved", revisionId: id(6), revisionSequence: 3 },
      id(2),
    ),
  ).toBe(true);
  expect(mappingProposals({ proposals: [p, p] }, id(2))).toBeNull();
  expect(mappingProposals({ proposals: Array(9).fill(p) }, id(2))).toBeNull();
  expect(mappingProposals({ proposals: [p] }, id(2))).toEqual([p]);
});

test("full operation review requires exact identity, fingerprint, count and actual canonical-byte digest", async () => {
  expect(await proposalOperations(details, p)).toEqual(details);
  for (const invalid of [
    { ...details, proposalId: id(9) },
    { ...details, fingerprint: "b".repeat(64) },
    { ...details, operationCount: 2 },
    { ...details, operationsDigest: "b".repeat(64) },
    { ...details, canonicalScript: script.replace("bind", "erase") },
    { ...details, canonicalScript: "x".repeat(1024 * 1024 + 1) },
  ])
    expect(await proposalOperations(invalid, p)).toBeNull();
  expect(sameProposal(p, { ...p, status: "pending" })).toBe(true);
  expect(
    sameProposal(p, {
      ...p,
      baseRevision: { ...p.baseRevision, mappingId: id(9) },
    }),
  ).toBe(false);
});
