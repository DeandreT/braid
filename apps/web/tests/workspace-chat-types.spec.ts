import { expect, test } from "@playwright/test";
import { isReturnPath } from "../src/auth/return-path";
import {
  citationLink,
  organizationConversation,
  workspaceAvailability,
  workspaceCitation,
  workspaceEvent,
} from "../src/chat/workspace-chat-types";

const org = "10000000-0000-4000-8000-000000000001";
const id = "20000000-0000-4000-8000-000000000001";
const scope = {
  organizationId: org,
  projectId: id,
  mappingId: id,
  revisionId: id,
};
const base = {
  organizationId: org,
  projectId: null,
  mappingId: null,
  revisionId: null,
  target: null,
  reportDerivationId: null,
  subjectId: null,
  version: null,
  sectionId: null,
};

test("workspace conversation guards distinguish legacy pins from optional focus and bind the organization", () => {
  const c = {
    conversationId: id,
    kind: "workspace",
    revision: null,
    focus: null,
    title: null,
    createdAtUtc: "2026-01-01T00:00:00Z",
    retainUntilUtc: "2027-01-01T00:00:00Z",
    lastTurnAtUtc: null,
    turnCount: 0,
  };
  expect(organizationConversation(c, org)).toBe(true);
  expect(
    organizationConversation(
      { ...c, focus: { projectId: id, mappingId: id, revisionId: null } },
      org,
    ),
  ).toBe(true);
  expect(
    organizationConversation({ ...c, kind: "revision", revision: scope }, org),
  ).toBe(true);
  expect(
    organizationConversation(
      {
        ...c,
        kind: "revision",
        revision: scope,
        focus: { projectId: id, mappingId: id, revisionId: id },
      },
      org,
    ),
  ).toBe(false);
  expect(
    organizationConversation(
      { ...c, kind: "revision", revision: { ...scope, organizationId: id } },
      org,
    ),
  ).toBe(false);
  expect(
    organizationConversation({ ...c, kind: "workspace", revision: scope }, org),
  ).toBe(false);
});

test("profile data policy is a destination flag, not a grant or a client-selected tool manifest", () => {
  const profile = {
    profileId: "fixture",
    version: 1,
    displayName: "Model",
    providerId: "fixture",
    modelId: "model",
    supportsTools: true,
  };
  const value = {
    schemaVersion: "weft.chat.v1",
    organizationId: org,
    limits: { maxTextBytes: 32768, maxSelectors: 32, turnSeconds: 120 },
    profiles: [{ profile, payloadsAllowed: false }],
  };
  expect(workspaceAvailability(value, org)?.profiles[0]?.payloadsAllowed).toBe(
    false,
  );
  expect(
    workspaceAvailability(
      { ...value, profiles: [{ profile, payloadsAllowed: true }] },
      org,
    )?.profiles[0]?.payloadsAllowed,
  ).toBe(true);
  expect(
    workspaceAvailability(
      {
        ...value,
        profiles: [
          {
            profile: { ...profile, supportsTools: false },
            payloadsAllowed: false,
          },
        ],
      },
      org,
    ),
  ).toBeNull();
  expect(
    workspaceAvailability(
      { ...value, profiles: [...value.profiles, ...value.profiles] },
      org,
    ),
  ).toBeNull();
  expect(workspaceAvailability(value, id)).toBeNull();
});

test("every citation kind has a closed, same-organization shape and a concrete app view", () => {
  const values = [
    { ...base, kind: "project", projectId: id },
    { ...base, kind: "mapping", projectId: id, mappingId: id },
    { ...base, kind: "revision", ...scope },
    {
      ...base,
      kind: "report",
      ...scope,
      reportDerivationId: id,
      target: {
        kind: "field",
        resourceId: "field.one",
        diagnosticOrdinal: null,
      },
    },
    { ...base, kind: "previewRun", ...scope, subjectId: id },
    {
      ...base,
      kind: "regressionCase",
      projectId: id,
      mappingId: id,
      subjectId: id,
      version: "1",
    },
    {
      ...base,
      kind: "regressionRun",
      projectId: id,
      mappingId: id,
      subjectId: id,
    },
    { ...base, kind: "buildRun", projectId: id, mappingId: id, subjectId: id },
    {
      ...base,
      kind: "buildApproval",
      projectId: id,
      mappingId: id,
      subjectId: id,
    },
    {
      ...base,
      kind: "buildEnvironment",
      projectId: id,
      subjectId: id,
      version: "1",
    },
    { ...base, kind: "guideSection", sectionId: "chat" },
  ];
  for (const c of values) {
    expect(workspaceCitation(c, org), c.kind).toBe(true);
    expect(workspaceCitation({ ...c, organizationId: id }, org), c.kind).toBe(
      false,
    );
    if (workspaceCitation(c, org)) {
      expect(citationLink(c)).toMatch(
        /^\/(?:catalog\/|help\/|chat\/resources\?)/,
      );
      expect(isReturnPath(citationLink(c)), c.kind).toBe(true);
    }
  }
  expect(
    workspaceCitation(
      { ...base, kind: "guideSection", sectionId: "javascript:alert(1)" },
      org,
    ),
  ).toBe(false);
  expect(
    workspaceCitation({ ...base, kind: "mapping", projectId: id }, org),
  ).toBe(false);
  expect(
    workspaceCitation(
      {
        ...base,
        kind: "report",
        ...scope,
        target: { kind: "field", resourceId: "f", diagnosticOrdinal: null },
      },
      org,
    ),
  ).toBe(false);
});

test("retained field and rule citations preserve exact report and UTF-8 resource fragments", () => {
  for (const kind of ["field", "rule"] as const) {
    const citation = {
      ...base,
      ...scope,
      kind: "report",
      reportDerivationId: id,
      target: { kind, resourceId: "field/é#1", diagnosticOrdinal: null },
    };
    expect(workspaceCitation(citation, org)).toBe(true);
    if (!workspaceCitation(citation, org))
      throw new Error("Invalid authored citation");
    const link = citationLink(citation);
    expect(link).toContain(`?projectionDerivationId=${id}`);
    expect(link).toContain(`#${kind}:field%2F%C3%A9%231`);
    expect(isReturnPath(link)).toBe(true);
  }
});

test("workspace streams reject legacy report frames, invalid disclosure sizes and forged citations", () => {
  const common = { sequence: 1, roundId: id };
  expect(
    workspaceEvent(
      {
        ...common,
        type: "citation",
        reportDerivationId: id,
        target: { kind: "field", resourceId: "f", diagnosticOrdinal: null },
      },
      org,
    ),
  ).toBe(false);
  const shared = {
    ...common,
    type: "data.shared",
    projectId: id,
    source: "previewOutput",
    subjectId: id,
    version: null,
    bytes: 32,
  };
  expect(workspaceEvent(shared, org)).toBe(true);
  for (const bytes of [-1, 0, 8193, 1.5])
    expect(workspaceEvent({ ...shared, bytes }, org)).toBe(false);
  expect(
    workspaceEvent(
      {
        ...common,
        type: "workspace.citation",
        citation: {
          ...base,
          kind: "guideSection",
          sectionId: "chat",
          organizationId: id,
        },
      },
      org,
    ),
  ).toBe(false);
});
