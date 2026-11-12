import {
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
  DestroyRef,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { ActivatedRoute, Router } from "@angular/router";
import { of } from "rxjs";
import { DOCUMENT } from "@angular/common";
import { CatalogContextStore } from "../catalog/catalog-context";
import { digest } from "../catalog/creation/creation-model";
import {
  feedbackQuestion,
  type CreationFeedback,
} from "../catalog/creation/creation-library-model";
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
          } @else {
            @if (creationFeedback(); as feedback) {
              <section aria-label="Feedback on exact mapping proposal">
                <h2>Revise a new mapping proposal</h2>
                <p>
                  Proposal <code>{{ feedback.proposalId }}</code
                  >, exact fingerprint <code>{{ feedback.fingerprint }}</code
                  >.
                </p>
                <p>
                  Prepare an editable question, add your requested changes, then
                  send it explicitly. No sample input or output is transferred.
                </p>
                <button
                  class="catalog-button"
                  type="button"
                  [disabled]="
                    !workspace.panel() ||
                    workspace.streaming() ||
                    preparingFeedback()
                  "
                  (click)="prepareFeedback()"
                >
                  Prepare feedback question
                </button>
                <p role="status">{{ feedbackStatus() }}</p>
              </section>
            }
            <p>
              Use the shared panel to continue a conversation without leaving
              your work.
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
                    {{
                      conversation.title || conversation.createdAtUtc
                    }}</button
                  ><span>{{
                    isWorkspace(conversation)
                      ? "Workspace conversation"
                      : "Legacy · pinned revision"
                  }}</span>
                </li>
              }
            </ul>
          }
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
      color: var(--text);
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
      color: var(--text-muted);
    }
  `,
})
export class ChatPageComponent {
  readonly read = createCatalogRead<CatalogContext>();
  readonly workspace = inject(WorkspaceConversation);
  readonly isWorkspace = isWorkspace;
  readonly conversationId = signal<string | null>(null);
  readonly invalidLink = signal(false);
  readonly creationFeedback = signal<CreationFeedback | null>(null);
  readonly feedbackStatus = signal("");
  private readonly document = inject(DOCUMENT);
  private readonly contexts = inject(CatalogContextStore);
  private alive = true;
  readonly preparingFeedback = signal(false);
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
    inject(DestroyRef).onDestroy(() => {
      this.alive = false;
    });
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
                "creationProjectId",
                "creationProposalId",
                "creationFingerprint",
              ].includes(key) && query.getAll(key).length === 1,
          ) &&
          (conversationId === null || isCatalogId(conversationId)) &&
          (ids.every((id) => id === null) ||
            (!!conversationId && ids.every(isCatalogId))) &&
          ((query.get("creationProjectId") === null &&
            query.get("creationProposalId") === null &&
            query.get("creationFingerprint") === null) ||
            (isCatalogId(query.get("creationProjectId")) &&
              isCatalogId(query.get("creationProposalId")) &&
              digest(query.get("creationFingerprint")) &&
              ids.every((id) => id === null)));
        this.invalidLink.set(!valid);
        this.conversationId.set(valid ? conversationId : null);
        this.creationFeedback.set(
          valid && query.get("creationProjectId") !== null
            ? {
                projectId: query.get("creationProjectId")!,
                proposalId: query.get("creationProposalId")!,
                fingerprint: query.get("creationFingerprint")!,
              }
            : null,
        );
        this.feedbackStatus.set("");
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
      if (
        state.kind !== "ready" ||
        !state.context.features.chat ||
        this.invalidLink()
      )
        return;
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
  async prepareFeedback(): Promise<void> {
    const value = this.creationFeedback(),
      state = this.read.state();
    if (
      !value ||
      state.kind !== "ready" ||
      this.invalidLink() ||
      this.workspace.streaming() ||
      this.preparingFeedback()
    )
      return;
    if (
      this.workspace.unsent() &&
      !(
        this.document.defaultView?.confirm(
          "Replace the current unsent question with feedback on this exact proposal? No question will be sent automatically.",
        ) ?? false
      )
    )
      return;
    const key = JSON.stringify(value),
      context = state.context;
    const current = () =>
      this.alive &&
      !this.invalidLink() &&
      JSON.stringify(this.creationFeedback()) === key &&
      this.contexts.isCurrent(context);
    this.preparingFeedback.set(true);
    let ready = false;
    try {
      ready = await this.workspace.prepareQuestion(
        feedbackQuestion(value),
        current,
      );
    } finally {
      this.preparingFeedback.set(false);
    }
    if (current())
      this.feedbackStatus.set(
        ready
          ? "Feedback question prepared. Add your requested changes and send it when ready."
          : "Wait for the current request to settle, then prepare feedback again.",
      );
  }
  selected(conversation: AnyConversation | null): void {
    void this.router.navigate([], {
      queryParams: {
        conversationId: conversation?.conversationId ?? null,
        ...(this.creationFeedback() &&
        (!conversation || isWorkspace(conversation))
          ? {
              creationProjectId: this.creationFeedback()!.projectId,
              creationProposalId: this.creationFeedback()!.proposalId,
              creationFingerprint: this.creationFeedback()!.fingerprint,
            }
          : {}),
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
