import {
  DestroyRef,
  Injectable,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { CatalogContextStore } from "../catalog/catalog-context";
import { catalogOwner, type CatalogContext } from "../catalog/catalog-types";
import type { ChatCreate } from "./chat-types";
import type { ChatScope, ChatFocus } from "./workspace-chat-types";
import { UnsentWork } from "../workbench/unsent-work";
import { hasChatUnsentWork } from "./chat-unsent-work";

export interface ChatWork {
  readonly conversationId: string | null;
  readonly draft: string;
  readonly pending: {
    readonly conversationId: string;
    readonly body: ChatCreate;
  } | null;
  readonly pendingConversation: string | null;
  readonly conversationScope?: ChatScope | null;
  readonly pendingFocus?: ChatFocus | null;
  readonly focus?: ChatFocus | null;
}

/** Provided by the shared panel: a failed session check can replace its content without losing an unsent or unacknowledged question. */
@Injectable()
export class ChatViewRecovery {
  private readonly contexts = inject(CatalogContextStore);
  private readonly kept = signal<{
    owner: string;
    scope: string;
    work: ChatWork;
  } | null>(null);
  readonly hasWork = computed(() => {
    const kept = this.kept();
    return kept !== null && hasChatUnsentWork(kept.work);
  });
  constructor() {
    inject(UnsentWork).track(() => this.hasWork());
    effect(() => {
      const context = this.contexts.context();
      if (context)
        untracked(() => {
          if (this.kept() && this.kept()!.owner !== catalogOwner(context))
            this.discard();
        });
    });
    inject(DestroyRef).onDestroy(() => this.discard());
  }
  keep(context: CatalogContext, scope: ChatScope, work: ChatWork): void {
    const current = this.contexts.context();
    if (current && catalogOwner(current) !== catalogOwner(context)) {
      this.discard();
      return;
    }
    this.kept.set({ owner: catalogOwner(context), scope: key(scope), work });
  }
  take(context: CatalogContext, scope: ChatScope): ChatWork | null {
    const kept = this.kept();
    if (
      !kept ||
      kept.owner !== catalogOwner(context) ||
      kept.scope !== key(scope)
    )
      return null;
    this.kept.set(null);
    return kept.work;
  }
  routeScope(scope: ChatScope | null): void {
    if (this.kept() && (!scope || this.kept()!.scope !== key(scope)))
      this.discard();
  }
  discard(): void {
    this.kept.set(null);
  }
}
function key(scope: ChatScope): string {
  return JSON.stringify([
    scope.organizationId,
    "revisionId" in scope
      ? [scope.projectId, scope.mappingId, scope.revisionId]
      : null,
  ]);
}
