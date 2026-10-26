import {
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
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
import { WorkspaceConversation } from "../workbench/workspace-conversation";
import { isWorkspace, type AnyConversation } from "./workspace-chat-types";

@Component({
  selector: "weft-chat-page",
  imports: [CatalogStatusComponent],
  template: ` <section class="workspace-chat-page">
    <header>
      <p class="chat-page-eyebrow">Your workspace</p>
      <h1>Conversations</h1>
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
          @if (!state.context.features.chat) {
            <p role="status">Chat is not configured for this workspace.</p>
          }
          <p>
            Use the shared panel to continue a conversation without leaving your
            work.
          </p>
          <button
            class="catalog-button"
            type="button"
            (click)="workspace.open()"
          >
            Open chat
          </button>
          <ul class="conversation-library">
            @for (
              conversation of workspace.panel()?.conversations() ?? [];
              track conversation.conversationId
            ) {
              <li>
                <button
                  class="catalog-button"
                  type="button"
                  (click)="choose(conversation)"
                >
                  {{ conversation.title || conversation.createdAtUtc }}</button
                ><span>{{
                  isWorkspace(conversation)
                    ? "Workspace conversation"
                    : "Legacy · pinned revision"
                }}</span>
              </li>
            }
          </ul>
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
      padding: 16px;
    }
    .workspace-chat-page > header {
      margin-bottom: 16px;
      color: #213b34;
    }
    .workspace-chat-page h1 {
      font-size: 1.5rem;
      margin: 0.3rem 0;
    }
    .chat-page-eyebrow {
      display: none;
      text-transform: uppercase;
      letter-spacing: 0.12em;
      font-size: 0.75rem;
      color: #526b5e;
    }
  `,
})
export class ChatPageComponent {
  readonly read = createCatalogRead<CatalogContext>();
  readonly workspace = inject(WorkspaceConversation);
  readonly isWorkspace = isWorkspace;
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
  private lastRouteSelection: string | null = null;
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
    effect(() => {
      const state = this.read.state();
      const id = this.conversationId(),
        scope = this.legacyScope();
      if (state.kind !== "ready" || this.invalidLink()) return;
      const key = JSON.stringify([
        id,
        scope,
        state.context.allowedOrganizationId,
      ]);
      untracked(() => {
        if (key === this.lastRouteSelection) return;
        this.lastRouteSelection = key;
        this.workspace.select(id, scope);
      });
    });
    this.workspace.selected
      .pipe(takeUntilDestroyed())
      .subscribe((conversation) => {
        if (!this.invalidLink()) this.selected(conversation);
      });
  }
  choose(conversation: AnyConversation): void {
    this.workspace.open();
    void this.workspace.panel()?.open(conversation.conversationId);
  }
  selected(conversation: AnyConversation | null): void {
    void this.router.navigate([], {
      queryParams: {
        conversationId: conversation?.conversationId ?? null,
        ...(conversation && !isWorkspace(conversation) && conversation.revision
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
    // The conversation and its in-tab recovery now outlive route content.
    return true;
  }
}
