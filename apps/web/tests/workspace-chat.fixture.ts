import type { Page } from "@playwright/test";
import { controlledData, P, V } from "./revision-reports-controlled.fixture";
import { catalogApi, mappingPath, projectPath } from "./catalog.fixture";
import type {
  ChatEvent,
  ChatTurn,
  ChatConversation,
} from "../src/chat/chat-types";
import type { OrganizationConversation } from "../src/chat/workspace-chat-types";
import type { MappingWriteReceipt } from "../src/chat/workspace-chat-types";

export const round = "a0000000-0000-4000-8000-000000000001";
export const legacyId = "80000000-0000-4000-8000-000000000001";
export const otherProject = "20000000-0000-4000-8000-000000000002";
export const otherMapping = "30000000-0000-4000-8000-000000000002";
export const frame = (event: ChatEvent) =>
  `id: ${event.sequence}\nevent: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`;

/** Synthetic UI fixture only. Real authorization/tool offering is qualified by the PostgreSQL and provider-wire suites. */
export async function workspaceFixture(
  page: Page,
  options: {
    payloadsAllowed?: boolean;
    grant?: boolean;
    secondProfile?: boolean;
  } = {},
) {
  const data = controlledData();
  data.context.features.chat = true;
  data.mapping.access.actions.chat = true;
  const org = data.revision.scope.organizationId;
  const root = `/api/v1/organizations/${org}/chat`;
  const profile = {
    profileId: "fixture",
    version: 1,
    displayName: "Fixture model",
    providerId: "fixture",
    modelId: "fixture-model",
    supportsTools: true,
  };
  const secondProfile = {
    ...profile,
    profileId: "fixture-payloads",
    displayName: "Fixture samples",
  };
  const legacy: ChatConversation = {
    conversationId: legacyId,
    revision: data.revision.scope,
    projectionDerivationId: P,
    validationDerivationId: V,
    title: "Earlier revision conversation",
    createdAtUtc: "2026-01-01T00:00:00Z",
    retainUntilUtc: "2027-01-01T00:00:00Z",
    lastTurnAtUtc: null,
    turnCount: 0,
  };
  const conversations = new Map<string, OrganizationConversation>();
  const owners = new Map<string, string>([
    [legacyId, data.context.actorPrincipalId],
  ]);
  conversations.set(legacyId, {
    conversationId: legacyId,
    kind: "revision",
    revision: legacy.revision,
    focus: null,
    title: legacy.title,
    createdAtUtc: legacy.createdAtUtc,
    retainUntilUtc: legacy.retainUntilUtc,
    lastTurnAtUtc: null,
    turnCount: 0,
  });
  const turns = new Map<string, ChatTurn[]>();
  const events = new Map<string, ChatEvent[]>();
  const posts: { path: string; body: any; token: string | undefined }[] = [];
  const reads: string[] = [];
  const offered: string[][] = [];
  const state = {
    payloadsAllowed: options.payloadsAllowed ?? false,
    grant: options.grant ?? false,
    loseSend: false,
    loseCreate: false,
    profilesAvailable: true,
    reject: false,
    partial: false,
    foreignCitation: false,
    /** A mapping.write event the next workspace answer carries at sequence 7. */
    write: null as ChatEvent | null,
    receipts: [] as MappingWriteReceipt[],
  };
  const secondProject = structuredClone(data.project);
  secondProject.scope = { organizationId: org, projectId: otherProject };
  secondProject.name = "Partner exchange";
  const secondMapping = structuredClone(data.mapping);
  secondMapping.scope = {
    organizationId: org,
    projectId: otherProject,
    mappingId: otherMapping,
  };
  secondMapping.name = "Partner orders";
  secondMapping.head.scope = {
    ...data.revision.scope,
    projectId: otherProject,
    mappingId: otherMapping,
  };
  const citation = (
    kind: "mapping" | "guideSection",
    sequence: number,
  ): ChatEvent => ({
    type: "workspace.citation",
    sequence,
    roundId: round,
    citation: {
      kind,
      organizationId: state.foreignCitation
        ? "10000000-0000-4000-8000-000000000099"
        : org,
      projectId: kind === "mapping" ? otherProject : null,
      mappingId: kind === "mapping" ? otherMapping : null,
      revisionId: null,
      target: null,
      reportDerivationId: null,
      subjectId: null,
      version: null,
      sectionId: kind === "guideSection" ? "chat" : null,
    },
  });
  await page.context().route("**/api/**", async (route) => {
    const request = route.request(),
      url = new URL(request.url()),
      path = url.pathname;
    if (request.method() === "POST")
      posts.push({
        path,
        body: request.postData() ? request.postDataJSON() : null,
        token: request.headers()["x-xsrf-token"],
      });
    else reads.push(path + url.search);
    if (path === "/api/auth/session")
      return route.fulfill({ json: { version: 1, mode: "development" } });
    if (path === "/api/v1/health")
      return route.fulfill({
        json: {
          status: "ok",
          service: "weft-api",
          version: "0.1.0",
          apiVersion: "v1",
        },
      });
    if (path === "/api/v1/catalog/context")
      return route.fulfill(
        state.reject
          ? { status: 403, json: { code: "identityRejected" } }
          : { json: data.context },
      );
    if (path === "/api/v1/help/sections")
      return route.fulfill({
        json: {
          sections: [
            {
              id: "chat",
              title: "Using chat",
              summary: "Read-only workspace questions.",
            },
          ],
        },
      });
    if (path === "/api/v1/help/sections/chat")
      return route.fulfill({
        json: {
          id: "chat",
          title: "Using chat",
          summary: "Read-only workspace questions.",
          blocks: [
            {
              kind: "paragraph",
              content: [
                {
                  kind: "text",
                  text: "Use verified citations to inspect the source. Chat does not start runs or edit mappings.",
                },
              ],
            },
          ],
        },
      });
    if (path === catalogApi(projectPath(data.project.scope)))
      return route.fulfill({ json: data.project });
    if (path === catalogApi(mappingPath(data.mapping.scope)))
      return route.fulfill({ json: data.mapping });
    if (path === catalogApi(mappingPath(data.mapping.scope)) + "/revisions")
      return route.fulfill({
        json: { revisions: [data.revision], nextCursor: null },
      });
    if (path === data.apiPath) return route.fulfill({ json: data.revision });
    if (path.startsWith(data.reportPath("projection")))
      return route.fulfill({ json: data.projection });
    if (path.startsWith(data.reportPath("validation")))
      return route.fulfill({ json: data.validation });
    if (path === `/api/v1/organizations/${org}/projects`)
      return route.fulfill({
        json: { projects: [data.project, secondProject], nextCursor: null },
      });
    if (path === catalogApi(projectPath(data.project.scope)) + "/mappings")
      return route.fulfill({
        json: { mappings: [data.mapping], nextCursor: null },
      });
    if (path === catalogApi(projectPath(secondProject.scope)) + "/mappings")
      return route.fulfill({
        json: { mappings: [secondMapping], nextCursor: null },
      });
    if (path === catalogApi(mappingPath(secondMapping.scope)))
      return route.fulfill({ json: secondMapping });
    if (path === catalogApi(mappingPath(secondMapping.scope)) + "/revisions")
      return route.fulfill({
        json: {
          revisions: [{ ...data.revision, scope: secondMapping.head.scope }],
          nextCursor: null,
        },
      });
    const workspace = path.startsWith(root);
    const chatRoot = workspace ? root : `${data.apiPath}/chat`;
    if (path === chatRoot)
      return route.fulfill(
        workspace && !state.profilesAvailable
          ? { status: 409, json: { code: "profileUnavailable" } }
          : {
              json: workspace
                ? {
                    schemaVersion: "weft.chat.v1",
                    organizationId: org,
                    limits: {
                      maxTextBytes: 32768,
                      maxSelectors: 32,
                      turnSeconds: 120,
                    },
                    profiles: [
                      { profile, payloadsAllowed: state.payloadsAllowed },
                      ...(options.secondProfile
                        ? [{ profile: secondProfile, payloadsAllowed: true }]
                        : []),
                    ],
                  }
                : {
                    schemaVersion: "weft.chat.v1",
                    revision: data.revision.scope,
                    toolsEnabled: true,
                    profiles: [profile],
                    limits: {
                      maxTextBytes: 32768,
                      maxSelectors: 32,
                      turnSeconds: 120,
                    },
                  },
            },
      );
    if (path === `${chatRoot}/conversations`) {
      if (request.method() === "POST") {
        const body = request.postDataJSON();
        const conversation: OrganizationConversation = {
          conversationId: body.conversationId,
          kind: workspace ? "workspace" : "revision",
          revision: workspace ? null : data.revision.scope,
          focus: workspace ? (body.focus ?? null) : null,
          title: "Workspace question",
          createdAtUtc: "2026-10-24T10:00:00Z",
          retainUntilUtc: "2027-01-01T00:00:00Z",
          turnCount: 0,
          lastTurnAtUtc: null,
        };
        conversations.set(body.conversationId, conversation);
        owners.set(body.conversationId, data.context.actorPrincipalId);
        if (state.loseCreate) {
          state.loseCreate = false;
          return route.abort("failed");
        }
        return route.fulfill({
          status: 201,
          json: workspace
            ? conversation
            : { ...legacy, conversationId: body.conversationId },
        });
      }
      const list = [...conversations.values()]
        .reverse()
        .filter(
          (c) =>
            owners.get(c.conversationId) === data.context.actorPrincipalId &&
            (!url.searchParams.has("revisionId") ||
              c.revision?.revisionId === url.searchParams.get("revisionId") ||
              c.focus?.revisionId === url.searchParams.get("revisionId")),
        );
      return route.fulfill({
        json: { conversations: workspace ? list : [legacy], hasMore: false },
      });
    }
    const id = path.slice(chatRoot.length).split("/")[2];
    if (
      workspace &&
      (path.endsWith("/mapping-writes") ||
        path.endsWith("/mapping-writes/recover"))
    )
      return route.fulfill({ json: { receipts: state.receipts } });
    if (path.endsWith("/turns") && request.method() === "POST") {
      const body = request.postDataJSON(),
        conversation = conversations.get(id!);
      if (!conversation || owners.get(id!) !== data.context.actorPrincipalId)
        return route.fulfill({ status: 404, json: { code: "notFound" } });
      const replay = turns
        .get(id!)
        ?.find((turn) => turn.turnId === body.turnId);
      if (replay) return route.fulfill({ json: replay });
      const turn: ChatTurn = {
        conversationId: id!,
        turnId: body.turnId,
        text: body.text,
        selectors: [],
        phase: "active",
        profile:
          options.secondProfile && body.profileId === secondProfile.profileId
            ? secondProfile
            : profile,
        createdAtUtc: "2026-10-24T10:00:00Z",
        deadlineUtc: "2026-10-24T10:02:00Z",
        finishedAtUtc: null,
        lastSequence: 1,
        ...(workspace
          ? {
              focus: conversation.focus
                ? {
                    state: "resolved" as const,
                    revision:
                      conversation.focus.mappingId === otherMapping
                        ? secondMapping.head.scope
                        : data.revision.scope,
                  }
                : { state: "none" as const, revision: null },
            }
          : {}),
      };
      if (!(turns.get(id!) ?? []).some((t) => t.turnId === body.turnId))
        turns.set(id!, [...(turns.get(id!) ?? []), turn]);
      const tools = [
        "get_mapping",
        "search_user_guide",
        ...(state.payloadsAllowed && state.grant ? ["get_preview_output"] : []),
      ];
      offered.push(tools);
      const response: ChatEvent[] = [
        { type: "turn.started", sequence: 1 },
        {
          type: "tool.started",
          sequence: 2,
          roundId: round,
          ordinal: 1,
          name: "get_mapping",
        },
        {
          type: "tool.completed",
          sequence: 3,
          roundId: round,
          ordinal: 1,
          name: "get_mapping",
          resultBytes: 100,
          retrievedNodes: 1,
        },
        ...(workspace
          ? [citation("mapping", 4), citation("guideSection", 5)]
          : [
              {
                type: "citation" as const,
                sequence: 4,
                roundId: round,
                reportDerivationId: P,
                target: {
                  kind: "projectionReport" as const,
                  resourceId: null,
                  diagnosticOrdinal: null,
                },
              },
            ]),
        {
          type: "text.delta",
          sequence: 6,
          roundId: round,
          text: "Partner orders maps the order fields. The user guide explains how to inspect retained reports.",
        },
        ...(workspace && state.payloadsAllowed && state.grant
          ? [
              {
                type: "data.shared" as const,
                sequence: 7,
                roundId: round,
                projectId: otherProject,
                source: "previewOutput" as const,
                subjectId: legacyId,
                version: null,
                bytes: 32,
              },
            ]
          : workspace && state.write
            ? [state.write]
            : []),
        { type: "turn.completed", sequence: 8 },
      ];
      events.set(body.turnId, response);
      if (state.loseSend) {
        state.loseSend = false;
        return route.abort("failed");
      }
      return route.fulfill({ status: 201, json: turn });
    }
    if (path.endsWith("/events")) {
      const turnId = path.split("/").at(-2)!;
      const after = Number(url.searchParams.get("after") ?? 0);
      const response = (events.get(turnId) ?? []).filter(
        (e) => e.sequence > after && (!state.partial || e.sequence <= 6),
      );
      const terminal = response.find((e) =>
        ["turn.completed", "turn.cancelled", "turn.failed"].includes(e.type),
      );
      const turn = turns.get(id!)?.find((t) => t.turnId === turnId);
      if (turn && terminal) {
        turn.phase =
          terminal.type === "turn.completed"
            ? "completed"
            : terminal.type === "turn.cancelled"
              ? "cancelled"
              : "failed";
        turn.finishedAtUtc = "2026-10-24T10:01:00Z";
        turn.lastSequence = terminal.sequence;
      }
      return route.fulfill({
        contentType: "text/event-stream",
        body: response.map(frame).join(""),
      });
    }
    if (path.endsWith("/cancel")) {
      const turnId = path.split("/").at(-2)!;
      const turn = turns.get(id!)?.find((t) => t.turnId === turnId);
      if (!turn)
        return route.fulfill({ status: 404, json: { code: "notFound" } });
      turn.phase = "cancelled";
      turn.finishedAtUtc = "2026-10-24T10:01:00Z";
      events.set(turnId, [
        ...(events.get(turnId) ?? []).filter((e) => e.sequence < 8),
        { type: "turn.cancelled", sequence: 8 },
      ]);
      state.partial = false;
      return route.fulfill({ json: turn });
    }
    if (
      path === `${chatRoot}/conversations/${id}` &&
      conversations.has(id!) &&
      owners.get(id!) === data.context.actorPrincipalId
    )
      return route.fulfill({
        json: {
          conversation: workspace
            ? conversations.get(id!)
            : id === legacyId
              ? legacy
              : { ...legacy, conversationId: id },
          turns: turns.get(id!) ?? [],
          hasEarlierTurns: false,
        },
      });
    return route.fulfill({ status: 404, json: { code: "notFound" } });
  });
  return {
    data,
    root,
    state,
    posts,
    reads,
    offered,
    conversations,
    turns,
    events,
  };
}
