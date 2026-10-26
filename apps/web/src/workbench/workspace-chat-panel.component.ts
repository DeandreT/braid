import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { CatalogContextStore } from "../catalog/catalog-context";
import { catalogOwner } from "../catalog/catalog-types";
import { RevisionChatComponent } from "../chat/revision-chat.component";
import { ChatViewRecovery } from "../chat/chat-view-recovery";
import { WorkspaceConversation } from "./workspace-conversation";
import { ResizeHandleComponent } from "./resize-handle.component";

@Component({
  selector: "weft-workspace-chat-panel",
  imports: [RevisionChatComponent, ResizeHandleComponent],
  providers: [ChatViewRecovery],
  host: {
    "[class.chat-open]": "shown()",
    "[class.chat-focused]": "narrow()",
    "[style.--workspace-chat-width]": "width() + 'px'",
  },
  template: `
    @if (
      chat.activated() &&
        contexts.canPresent(contexts.context()) &&
        contexts.context();
      as context
    ) {
      @if (context.features.chat) {
        <div
          class="chat-splitter"
          [hidden]="!chat.opened() || narrow() || maximum() <= 300"
        >
          <weft-resize-handle
            label="Resize chat"
            controls="workspace-chat"
            orientation="vertical"
            [direction]="-1"
            [value]="width()"
            [minimum]="300"
            [maximum]="maximum()"
            (resized)="setWidth($event)"
          />
        </div>
        <aside
          id="workspace-chat"
          aria-labelledby="workspace-chat-heading"
          [hidden]="!chat.opened()"
        >
          <header>
            <h2 id="workspace-chat-heading" tabindex="-1">Ask Weft</h2>
            <span>Read-only</span
            ><button type="button" (click)="chat.hide()" aria-label="Hide chat">
              Hide chat
            </button>
          </header>
          <weft-revision-chat
            [context]="context"
            [requestedConversation]="chat.requested()"
            [requestedVersion]="chat.requestVersion()"
            (selectionRejected)="chat.selected.next(chat.current())"
            [requestedLegacyScope]="chat.legacyScope()"
            [suggestedFocus]="chat.revision()"
            [inspectionScope]="chat.revision()"
            [projectionId]="chat.projectionId()"
            [validationId]="chat.validationId()"
            (citation)="chat.citation.next($event)"
            (workspaceCitation)="chat.workspaceCitation.next($event)"
            (selectedConversation)="chat.selected.next($event)"
          />
          <div class="chat-width" [hidden]="narrow()">
            <label
              >Chat width
              <input
                type="range"
                min="300"
                [max]="maximum()"
                [value]="width()"
                (input)="setWidth(+$any($event.target).value)" /></label
            ><output>{{ width() }}px</output
            ><button type="button" (click)="setWidth(360)">Reset width</button>
          </div>
        </aside>
      }
    }
  `,
  styles: `
    :host {
      display: none;
      min-width: 0;
      min-height: 0;
    }
    :host.chat-open {
      display: flex;
      width: calc(var(--workspace-chat-width) + 8px);
      flex-shrink: 0;
    }
    .chat-splitter {
      width: 8px;
      position: relative;
      flex-shrink: 0;
      background: #eff3ec;
      border-inline: 1px solid #d7e2d1;
    }
    aside {
      display: flex;
      flex-direction: column;
      width: var(--workspace-chat-width);
      min-height: 0;
      min-width: 0;
      background: #fafdf6;
    }
    header {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
      padding: 8px 12px;
      border-bottom: 1px solid #d7e2d1;
    }
    h2 {
      font-size: 0.9375rem;
      margin: 0;
    }
    header span {
      font-size: 0.75rem;
      color: #526b5e;
    }
    header button {
      margin-left: auto;
    }
    button {
      min-height: 32px;
      font: inherit;
      color: inherit;
      background: white;
      border: 1px solid #7b8c78;
      border-radius: 4px;
      padding: 4px 8px;
    }
    weft-revision-chat {
      flex: 1;
      min-height: 0;
      overflow: auto;
      container-type: size;
      container-name: workspace-conversation;
    }
    .chat-width {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
      padding: 4px 12px;
      border-top: 1px solid #d7e2d1;
      font-size: 0.75rem;
    }
    label {
      display: flex;
      align-items: center;
      gap: 8px;
      flex: 1;
    }
    input {
      min-width: 40px;
      width: 70px;
      flex: 1;
    }
    :host.chat-focused.chat-open {
      width: 100%;
    }
    :host.chat-focused aside {
      width: 100%;
    }
    [hidden] {
      display: none !important;
    }
  `,
})
export class WorkspaceChatPanelComponent {
  readonly chat = inject(WorkspaceConversation);
  readonly contexts = inject(CatalogContextStore);
  readonly shown = computed(
    () =>
      this.chat.opened() &&
      this.contexts.canPresent(this.contexts.context()) &&
      this.contexts.context()?.features.chat === true,
  );
  private readonly document = inject(DOCUMENT);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly panel = viewChild(RevisionChatComponent);
  private readonly available = signal(0);
  private readonly viewportWidth = signal(
    this.document.defaultView?.innerWidth ?? 0,
  );
  private readonly preferred = signal(360);
  readonly narrow = computed(
    () => this.viewportWidth() < 680 || this.available() < 628,
  );
  readonly maximum = computed(() =>
    Math.max(
      300,
      Math.min(
        560,
        this.available() - (this.available() >= 916 ? 480 : 320) - 8,
      ),
    ),
  );
  readonly width = computed(() =>
    Math.min(this.maximum(), Math.max(300, this.preferred())),
  );
  private owner: string | null = null;
  private storageKey: string | null = null;
  constructor() {
    effect(() => this.chat.panel.set(this.panel() ?? null));
    effect(() => {
      const context = this.contexts.context();
      if (!context) return;
      const owner = catalogOwner(context);
      untracked(() => {
        if (owner === this.owner) return;
        const changedOwner = this.owner !== null;
        if (this.owner !== null) {
          this.chat.requested.set(null);
          this.chat.legacyScope.set(null);
        }
        this.owner = owner;
        this.storageKey = `weft.chat-width.v1:${context.mode}:${context.actorPrincipalId}:${context.allowedOrganizationId}`;
        let value = 360;
        try {
          const saved = Number(
            this.document.defaultView?.localStorage.getItem(this.key()),
          );
          if (Number.isFinite(saved) && saved >= 300 && saved <= 560)
            value = saved;
        } catch {
          /* Storage is optional. */
        }
        this.preferred.set(value);
        if (
          (!this.chat.activated() || changedOwner) &&
          (this.document.defaultView?.innerWidth ?? 0) >= 680
        ) {
          try {
            const savedOpen =
              this.document.defaultView?.localStorage.getItem(
                `${this.key()}:open`,
              ) === "true";
            if (changedOwner) this.chat.opened.set(savedOpen);
            if (savedOpen) this.chat.open();
          } catch {
            /* Storage is optional. */
          }
        }
      });
    });
    effect(() => {
      const opened = this.chat.opened(),
        activated = this.chat.activated(),
        context = this.contexts.context();
      if (activated && context && catalogOwner(context) === this.owner) {
        try {
          this.document.defaultView?.localStorage.setItem(
            `${this.key()}:open`,
            String(opened),
          );
        } catch {
          /* Storage is optional. */
        }
      }
    });
    const resize = () => {
      const parent = this.element.nativeElement.parentElement;
      const previous = this.narrow();
      this.viewportWidth.set(this.document.defaultView?.innerWidth ?? 0);
      this.available.set(parent?.clientWidth ?? 0);
      if (!previous && this.narrow()) this.chat.hide();
    };
    afterNextRender(() => {
      resize();
      const observer = new ResizeObserver(resize);
      if (this.element.nativeElement.parentElement)
        observer.observe(this.element.nativeElement.parentElement);
      this.destroy.onDestroy(() => observer.disconnect());
    });
    const escape = (event: KeyboardEvent) => {
      if (
        event.key === "Escape" &&
        !event.defaultPrevented &&
        this.narrow() &&
        this.chat.opened() &&
        !this.document.querySelector("dialog[open]")
      )
        this.chat.hide();
    };
    this.document.addEventListener("keydown", escape);
    this.document.defaultView?.addEventListener("resize", resize);
    this.destroy.onDestroy(() => {
      this.document.removeEventListener("keydown", escape);
      this.document.defaultView?.removeEventListener("resize", resize);
      this.chat.panel.set(null);
    });
  }
  private readonly destroy = inject(DestroyRef);
  private key(): string {
    return this.storageKey ?? "weft.chat-width.v1";
  }
  setWidth(value: number): void {
    this.preferred.set(
      Math.round(Math.max(300, Math.min(this.maximum(), value))),
    );
    if (this.owner)
      try {
        this.document.defaultView?.localStorage.setItem(
          this.key(),
          String(this.preferred()),
        );
      } catch {
        /* Storage is optional. */
      }
  }
}
