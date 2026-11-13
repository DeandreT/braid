import { DOCUMENT } from "@angular/common";
import {
  Injectable,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from "@angular/core";
import type { RevisionChatComponent } from "../chat/revision-chat.component";
import type { CatalogRevisionScope } from "../catalog/catalog-types";
import { Subject } from "rxjs";
import type { ChatCitation, ChatConversation } from "../chat/chat-types";
import type {
  AnyConversation,
  WorkspaceCitationEvent,
} from "../chat/workspace-chat-types";

/** In-tab presentation only. The chat component retains its existing admission and recovery authority. */
@Injectable({ providedIn: "root" })
export class WorkspaceConversation {
  readonly panel = signal<RevisionChatComponent | null>(null);
  readonly activated = signal(false);
  readonly opened = signal(false);
  readonly requested = signal<string | null>(null);
  readonly requestVersion = signal(0);
  readonly legacyScope = signal<CatalogRevisionScope | null>(null);
  readonly revision = signal<CatalogRevisionScope | null>(null);
  readonly viewLabel = signal<string | null>(null);
  readonly projectionId = signal<string | null>(null);
  readonly validationId = signal<string | null>(null);
  readonly citation = new Subject<{
    conversation: ChatConversation;
    citation: ChatCitation;
  }>();
  readonly workspaceCitation = new Subject<WorkspaceCitationEvent>();
  readonly selected = new Subject<AnyConversation | null>();
  readonly current = computed(() => this.panel()?.current() ?? null);
  readonly unsent = computed(() => this.panel()?.hasUnsent() ?? false);
  readonly streaming = computed(() => this.panel()?.streaming() ?? false);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  open(): void {
    this.activated.set(true);
    this.opened.set(true);
    afterNextRender(
      () => {
        if (this.opened())
          this.document
            .querySelector<HTMLElement>("#workspace-chat-heading")
            ?.focus();
      },
      { injector: this.injector },
    );
  }
  hide(): void {
    this.opened.set(false);
    queueMicrotask(() =>
      this.document.getElementById("workspace-chat-toggle")?.focus(),
    );
  }
  select(id: string | null, legacyScope: CatalogRevisionScope | null): void {
    this.legacyScope.set(legacyScope);
    this.requested.set(id);
    this.requestVersion.update((value) => value + 1);
    if (!this.opened()) this.open();
  }
  async prepareQuestion(
    text: string,
    isCurrent: () => boolean,
  ): Promise<boolean> {
    this.open();
    const panel = this.panel();
    if (!panel || this.streaming()) return false;
    return panel.prepareWorkspaceQuestion(
      text,
      () => this.panel() === panel && isCurrent(),
    );
  }
}
