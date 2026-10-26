import { Injectable, inject } from "@angular/core";
import { CatalogContextStore } from "../catalog/catalog-context";
import { BrowserSessionState } from "../auth/browser-session-state";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogRevisionScope,
} from "../catalog/catalog-types";
import {
  ChatError,
  availability,
  conversation,
  event,
  object,
  terminal,
  turn,
  type ChatAvailability,
  type ChatConversation,
  type ChatCreate,
  type ChatDetail,
  type ChatEvent,
  type ChatPage,
  type ChatTurn,
} from "./chat-types";
import {
  focus,
  organizationConversation,
  workspaceAvailability,
  workspaceEvent,
  revisionScope,
  type ChatScope,
  type ChatFocus,
  type OrganizationConversation,
  type UiAvailability,
} from "./workspace-chat-types";

/** Same-origin, private-tab transport. No automatic mutation retries or provider URLs. */
@Injectable({ providedIn: "root" })
export class ChatApi {
  private readonly contexts = inject(CatalogContextStore);
  private readonly sessions = inject(BrowserSessionState);

  async workspaceAvailability(
    scope: ChatScope,
    context: CatalogContext,
    signal: AbortSignal,
  ): Promise<UiAvailability> {
    const value = workspaceAvailability(
      await this.json(
        { organizationId: scope.organizationId },
        "",
        context,
        signal,
      ),
      scope.organizationId,
    );
    if (!value) throw new ChatError("incompatible");
    return value;
  }
  async organizationList(
    scope: ChatScope,
    context: CatalogContext,
    signal: AbortSignal,
    revisionId?: string,
    after?: string,
  ): Promise<{ conversations: OrganizationConversation[]; hasMore: boolean }> {
    if (revisionId) this.id(revisionId);
    if (after) this.id(after);
    const value = await this.json(
      { organizationId: scope.organizationId },
      `/conversations?limit=50${revisionId ? `&revisionId=${revisionId}` : ""}${after ? `&after=${after}` : ""}`,
      context,
      signal,
    );
    if (
      !object(value) ||
      !Array.isArray(value.conversations) ||
      value.conversations.length > 50 ||
      !value.conversations.every((c) =>
        organizationConversation(c, scope.organizationId),
      ) ||
      new Set(value.conversations.map((c) => c.conversationId)).size !==
        value.conversations.length ||
      typeof value.hasMore !== "boolean"
    )
      throw new ChatError("incompatible");
    return { conversations: value.conversations, hasMore: value.hasMore };
  }
  async createWorkspace(
    scope: ChatScope,
    context: CatalogContext,
    signal: AbortSignal,
    id: string,
    selectedFocus: ChatFocus | null,
  ): Promise<OrganizationConversation> {
    this.id(id);
    if (!focus(selectedFocus)) throw new ChatError("invalidRequest");
    const value = await this.json(
      { organizationId: scope.organizationId },
      "/conversations",
      context,
      signal,
      { conversationId: id, focus: selectedFocus },
    );
    if (
      !organizationConversation(value, scope.organizationId) ||
      value.kind !== "workspace" ||
      value.conversationId !== id ||
      JSON.stringify(value.focus) !== JSON.stringify(selectedFocus)
    )
      throw new ChatError("incompatible");
    return value;
  }
  async workspaceDetail(
    scope: ChatScope,
    context: CatalogContext,
    signal: AbortSignal,
    id: string,
  ): Promise<{
    conversation: OrganizationConversation;
    turns: ChatTurn[];
    hasEarlierTurns: boolean;
  }> {
    this.id(id);
    const value = await this.json(
      { organizationId: scope.organizationId },
      `/conversations/${id}`,
      context,
      signal,
    );
    if (
      !object(value) ||
      !organizationConversation(value.conversation, scope.organizationId) ||
      value.conversation.kind !== "workspace" ||
      value.conversation.conversationId !== id ||
      !Array.isArray(value.turns) ||
      value.turns.length > 100 ||
      !value.turns.every(
        (t) => turn(t, id) && validTurnFocus(t.focus, scope.organizationId),
      ) ||
      new Set(value.turns.map((t) => t.turnId)).size !== value.turns.length ||
      typeof value.hasEarlierTurns !== "boolean"
    )
      throw new ChatError("incompatible");
    return {
      conversation: value.conversation,
      turns: value.turns,
      hasEarlierTurns: value.hasEarlierTurns,
    };
  }

  async availability(
    scope: CatalogRevisionScope,
    context: CatalogContext,
    signal: AbortSignal,
  ): Promise<ChatAvailability> {
    const value = await this.json(scope, "", context, signal);
    if (!availability(value, scope)) throw new ChatError("incompatible");
    return value;
  }
  async list(
    scope: CatalogRevisionScope,
    context: CatalogContext,
    signal: AbortSignal,
  ): Promise<ChatPage> {
    const value = await this.json(
      scope,
      "/conversations?limit=50",
      context,
      signal,
    );
    if (
      !object(value) ||
      !Array.isArray(value.conversations) ||
      value.conversations.length > 50 ||
      !value.conversations.every((c) => conversation(c, scope)) ||
      new Set(value.conversations.map((c) => c.conversationId)).size !==
        value.conversations.length ||
      typeof value.hasMore !== "boolean"
    )
      throw new ChatError("incompatible");
    return value as ChatPage;
  }
  async create(
    scope: CatalogRevisionScope,
    context: CatalogContext,
    signal: AbortSignal,
    id: string,
  ): Promise<ChatConversation> {
    this.id(id);
    const value = await this.json(scope, "/conversations", context, signal, {
      conversationId: id,
    });
    if (!conversation(value, scope) || value.conversationId !== id)
      throw new ChatError("incompatible");
    return value;
  }
  async detail(
    scope: CatalogRevisionScope,
    context: CatalogContext,
    signal: AbortSignal,
    id: string,
  ): Promise<ChatDetail> {
    this.id(id);
    const value = await this.json(
      scope,
      `/conversations/${id}`,
      context,
      signal,
    );
    if (
      !object(value) ||
      !conversation(value.conversation, scope) ||
      value.conversation.conversationId !== id ||
      !Array.isArray(value.turns) ||
      value.turns.length > 100 ||
      !value.turns.every((t) => turn(t, id)) ||
      new Set(value.turns.map((t) => t.turnId)).size !== value.turns.length ||
      typeof value.hasEarlierTurns !== "boolean"
    )
      throw new ChatError("incompatible");
    return value as ChatDetail;
  }
  async send(
    scope: ChatScope,
    context: CatalogContext,
    signal: AbortSignal,
    id: string,
    body: ChatCreate,
  ): Promise<ChatTurn> {
    this.id(id);
    this.id(body.turnId);
    const value = await this.json(
      scope,
      `/conversations/${id}/turns`,
      context,
      signal,
      body,
    );
    if (
      !turn(value, id) ||
      (!("revisionId" in scope) &&
        !validTurnFocus(value.focus, scope.organizationId)) ||
      value.turnId !== body.turnId ||
      value.text !== body.text ||
      value.profile.profileId !== body.profileId ||
      value.profile.version !== body.profileVersion ||
      JSON.stringify(value.selectors) !== JSON.stringify(body.selectors)
    )
      throw new ChatError("incompatible");
    return value;
  }
  async cancel(
    scope: ChatScope,
    context: CatalogContext,
    signal: AbortSignal,
    id: string,
    turnId: string,
  ): Promise<ChatTurn> {
    this.id(id);
    this.id(turnId);
    const value = await this.json(
      scope,
      `/conversations/${id}/turns/${turnId}/cancel`,
      context,
      signal,
      null,
    );
    if (!turn(value, id) || value.turnId !== turnId)
      throw new ChatError("incompatible");
    return value;
  }
  async stream(
    scope: ChatScope,
    context: CatalogContext,
    signal: AbortSignal,
    id: string,
    turnId: string,
    after: number,
    receive: (event: ChatEvent) => void,
  ): Promise<void> {
    this.id(id);
    this.id(turnId);
    const response = await this.request(
      scope,
      `/conversations/${id}/turns/${turnId}/events?after=${after}`,
      context,
      signal,
    );
    if (
      !response.headers.get("content-type")?.startsWith("text/event-stream") ||
      !response.body
    )
      throw new ChatError("incompatible");
    const reader = response.body.getReader();
    const acceptsEvent: (value: unknown) => value is ChatEvent =
      "revisionId" in scope
        ? event
        : (value: unknown): value is ChatEvent =>
            workspaceEvent(value, scope.organizationId);
    const decoder = new TextDecoder("utf-8", { fatal: true });
    let buffer = "",
      bytes = 0,
      sequence = after;
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) break;
        bytes += chunk.value.byteLength;
        if (bytes > 4 * 1024 * 1024) throw new ChatError("bodyLimit");
        buffer += decoder.decode(chunk.value, { stream: true });
        let match: RegExpExecArray | null;
        while ((match = /\r?\n\r?\n/.exec(buffer))) {
          const frame = buffer.slice(0, match.index);
          buffer = buffer.slice(match.index + match[0].length);
          if (frame.length > 262144) throw new ChatError("bodyLimit");
          const lines = frame
            .split(/\r?\n/)
            .filter((line) => !line.startsWith(":"));
          const data = lines
            .filter((line) => line.startsWith("data:"))
            .map((line) => line.slice(5).replace(/^ /, ""))
            .join("\n");
          if (!data) continue;
          const value: unknown = JSON.parse(data);
          const ids = lines.filter((line) => line.startsWith("id:"));
          const names = lines.filter((line) => line.startsWith("event:"));
          if (
            !acceptsEvent(value) ||
            ids.length !== 1 ||
            names.length !== 1 ||
            ids[0]!.slice(3).trim() !== String(value.sequence) ||
            names[0]!.slice(6).trim() !== value.type ||
            value.sequence <= sequence
          )
            throw new ChatError("incompatible");
          sequence = value.sequence;
          receive(value);
          if (terminal(value)) return;
        }
        if (buffer.length > 262144) throw new ChatError("bodyLimit");
      }
      if (decoder.decode().length || buffer.trim())
        throw new ChatError("interrupted");
    } finally {
      await reader.cancel().catch(() => undefined);
      reader.releaseLock();
    }
  }
  private id(id: string): void {
    if (!isCatalogId(id)) throw new ChatError("invalidRequest");
  }
  private async request(
    scope: ChatScope,
    suffix: string,
    context: CatalogContext,
    signal: AbortSignal,
    body?: unknown,
  ): Promise<Response> {
    if (
      !isCatalogId(scope.organizationId) ||
      ("revisionId" in scope &&
        ![scope.projectId, scope.mappingId, scope.revisionId].every(
          isCatalogId,
        )) ||
      context.allowedOrganizationId !== scope.organizationId ||
      !this.contexts.canDispatch(context)
    )
      throw new ChatError("identityRejected");
    const headers: Record<string, string> = {
      Accept: suffix.includes("/events?")
        ? "text/event-stream"
        : "application/json",
    };
    if (body !== undefined) {
      if (!context.requestToken) throw new ChatError("identityRejected");
      headers["X-XSRF-TOKEN"] = context.requestToken;
      if (body !== null) headers["Content-Type"] = "application/json";
    }
    const boundedSignal = AbortSignal.any([
      signal,
      AbortSignal.timeout(suffix.includes("/events?") ? 210000 : 10000),
    ]);
    const generation = this.sessions.generation();
    const response = await fetch(
      `/api/v1/organizations/${scope.organizationId}${"revisionId" in scope ? `/projects/${scope.projectId}/mappings/${scope.mappingId}/revisions/${scope.revisionId}` : ""}/chat${suffix}`,
      {
        method: body === undefined ? "GET" : "POST",
        headers,
        body:
          body === undefined || body === null
            ? undefined
            : JSON.stringify(body),
        credentials: "same-origin",
        mode: "same-origin",
        redirect: "error",
        cache: "no-store",
        signal: boundedSignal,
      },
    );
    if (signal.aborted || generation !== this.sessions.generation())
      throw new ChatError("stale");
    if (!response.ok) {
      if (response.status === 401 || response.status === 403) {
        this.sessions.rejectProtectedRequest(generation, context);
        throw new ChatError("identityRejected");
      }
      let code = "unavailable";
      try {
        const value: unknown = JSON.parse(await boundedText(response, 4096));
        if (object(value) && typeof value.code === "string") code = value.code;
      } catch {
        /* Never retain raw errors. */
      }
      throw new ChatError(code);
    }
    return response;
  }
  private async json(
    scope: ChatScope,
    suffix: string,
    context: CatalogContext,
    signal: AbortSignal,
    body?: unknown,
  ): Promise<unknown> {
    const response = await this.request(scope, suffix, context, signal, body);
    if (!response.headers.get("content-type")?.startsWith("application/json"))
      throw new ChatError("incompatible");
    return JSON.parse(await boundedText(response, 4 * 1024 * 1024)) as unknown;
  }
}

function validTurnFocus(value: unknown, organizationId: string): boolean {
  return (
    object(value) &&
    (value.state === "resolved"
      ? revisionScope(value.revision, organizationId)
      : ["none", "unavailable"].includes(String(value.state)) &&
        value.revision === null)
  );
}

async function boundedText(response: Response, limit: number): Promise<string> {
  if (!response.body) throw new ChatError("incompatible");
  const reader = response.body.getReader(),
    decoder = new TextDecoder("utf-8", { fatal: true });
  let bytes = 0,
    result = "";
  try {
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) return result + decoder.decode();
      bytes += chunk.value.byteLength;
      if (bytes > limit) throw new ChatError("bodyLimit");
      result += decoder.decode(chunk.value, { stream: true });
    }
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}
