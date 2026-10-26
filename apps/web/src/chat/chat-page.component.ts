import { DOCUMENT } from "@angular/common";
import { Component, computed, inject, signal, viewChild } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { ActivatedRoute, Router } from "@angular/router";
import { of } from "rxjs";
import { createCatalogRead } from "../catalog/catalog-state";
import { CatalogStatusComponent } from "../catalog/catalog-shell.component";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogRevisionScope,
} from "../catalog/catalog-types";
import { RevisionChatComponent } from "./revision-chat.component";
import { ChatViewRecovery } from "./chat-view-recovery";
import { isWorkspace, type AnyConversation } from "./workspace-chat-types";

@Component({
  selector: "weft-chat-page",
  imports: [RevisionChatComponent, CatalogStatusComponent],
  providers: [ChatViewRecovery],
  template: ` <section class="workspace-chat-page">
    <header>
      <p class="chat-page-eyebrow">Your workspace</p>
      <h1>Chat</h1>
      <p>
        Reason about mappings in their wider context. Conversations are private
        to you.
      </p>
    </header>
    @if (invalidLink()) {
      <p role="alert">
        This chat link is invalid. Open Chat without query parameters to choose
        a conversation.
      </p>
    } @else {
      @if (read.state(); as state) {
        @if (state.kind === "ready") {
          <weft-revision-chat
            [context]="state.context"
            [requestedConversation]="conversationId()"
            [requestedLegacyScope]="legacyScope()"
            (selectedConversation)="selected($event)"
          />
        } @else {
          <weft-catalog-status [kind]="state.kind" (retry)="read.retry()" />
        }
      }
    }
  </section>`,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .workspace-chat-page {
      max-width: 72rem;
      margin: 0 auto;
      padding: 2rem clamp(1rem, 3vw, 3rem) 4rem;
    }
    .workspace-chat-page > header {
      margin-bottom: 1.5rem;
      color: #213b34;
    }
    .workspace-chat-page h1 {
      font-size: 2.25rem;
      margin: 0.3rem 0;
    }
    .chat-page-eyebrow {
      text-transform: uppercase;
      letter-spacing: 0.12em;
      font-size: 0.75rem;
      color: #526b5e;
    }
  `,
})
export class ChatPageComponent {
  readonly read = createCatalogRead<CatalogContext>();
  readonly conversationId = signal<string | null>(null);
  readonly invalidLink = signal(false);
  private readonly legacyIds = signal<{
    projectId: string;
    mappingId: string;
    revisionId: string;
  } | null>(null);
  readonly legacyScope = computed<CatalogRevisionScope | null>(() => {
    const state = this.read.state(),
      ids = this.legacyIds();
    return state.kind === "ready" && ids
      ? { organizationId: state.context.allowedOrganizationId, ...ids }
      : null;
  });
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly recovery = inject(ChatViewRecovery);
  private readonly chat = viewChild(RevisionChatComponent);
  constructor() {
    inject(ActivatedRoute)
      .queryParamMap.pipe(takeUntilDestroyed())
      .subscribe((query) => {
        const conversationId = query.get("conversationId");
        const ids = [
          query.get("projectId"),
          query.get("mappingId"),
          query.get("revisionId"),
        ];
        const valid =
          query.keys.every(
            (key) =>
              [
                "conversationId",
                "projectId",
                "mappingId",
                "revisionId",
              ].includes(key) && query.getAll(key).length === 1,
          ) &&
          (conversationId === null || isCatalogId(conversationId)) &&
          (ids.every((id) => id === null) ||
            (!!conversationId && ids.every(isCatalogId)));
        this.invalidLink.set(!valid);
        this.conversationId.set(valid ? conversationId : null);
        this.legacyIds.set(
          valid && ids.every(isCatalogId)
            ? { projectId: ids[0]!, mappingId: ids[1]!, revisionId: ids[2]! }
            : null,
        );
      });
    this.read.select(null, (context) => of(context));
  }
  selected(conversation: AnyConversation): void {
    void this.router.navigate([], {
      queryParams: {
        conversationId: conversation.conversationId,
        ...(!isWorkspace(conversation) && conversation.revision
          ? {
              projectId: conversation.revision.projectId,
              mappingId: conversation.revision.mappingId,
              revisionId: conversation.revision.revisionId,
            }
          : {}),
      },
      replaceUrl: true,
    });
  }
  requestLeave(): boolean {
    return (
      !(this.chat()?.hasUnsent() || this.recovery.hasWork()) ||
      (this.document.defaultView?.confirm(
        "Leave chat? Unsent work or recovery information kept only in this tab may be lost. A sent question may already have committed.",
      ) ??
        false)
    );
  }
}
