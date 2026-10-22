import { Injectable } from "@angular/core";
import { RetainedViewWork } from "../catalog/retained-view-work";
import type { ChatCreate } from "./chat-types";

export interface ChatWork {
  readonly conversationId: string | null;
  readonly draft: string;
  readonly pending: {
    readonly conversationId: string;
    readonly body: ChatCreate;
  } | null;
  readonly pendingConversation: string | null;
}

/** Provided by the revision page: a refresh or failed session check can replace the chat panel without losing an unsent or unacknowledged question. */
@Injectable()
export class ChatViewRecovery extends RetainedViewWork<ChatWork> {}
