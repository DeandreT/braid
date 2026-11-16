import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  computed,
  inject,
  signal,
  viewChild,
} from "@angular/core";
import { Router } from "@angular/router";
import { WorkspaceCommands } from "../../workbench/workspace-commands";
import type { WorkbenchCommand } from "../../workbench/workbench-frame.component";

@Component({
  selector: "braid-header-search",
  template: `
    <button
      class="search-trigger"
      type="button"
      (click)="open()"
      aria-haspopup="dialog"
    >
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <circle cx="11" cy="11" r="7" />
        <path d="m20 20-4-4" />
      </svg>
      <span class="search-label">Search / Go to</span> <kbd>Ctrl K</kbd>
    </button>
    <dialog
      #dialog
      aria-labelledby="workspace-search-title"
      (cancel)="$event.preventDefault(); close()"
    >
      <header>
        <h2 id="workspace-search-title">Search / Go to</h2>
        <button type="button" aria-label="Close search" (click)="close()">
          ×
        </button>
      </header>
      <form role="search" (submit)="$event.preventDefault(); search(query())">
        <input
          #searchInput
          type="search"
          autocomplete="off"
          spellcheck="false"
          aria-label="Search catalog"
          placeholder="Mappings, projects, commands, guide…"
          maxlength="256"
          [value]="query()"
          (input)="query.set($any($event.target).value)"
        />
        <button type="submit" [disabled]="!query().trim()">
          Search catalog
        </button>
      </form>
      @if (items().length) {
        <h3>Current workspace</h3>
        <div class="commands">
          @for (item of items(); track item.id) {
            <button type="button" (click)="choose(item)">
              {{ item.label }}<small>{{ item.detail }}</small>
            </button>
          }
        </div>
      }
      <h3>Navigate</h3>
      <nav aria-label="Workspace destinations">
        <button type="button" (click)="go('/catalog')">Catalog</button
        ><button type="button" (click)="go('/chat')">Conversations</button
        ><button type="button" (click)="go('/notifications')">
          Notifications</button
        ><button type="button" (click)="go('/help')">Guide</button
        ><button type="button" (click)="go('/administration')">
          Administration
        </button>
      </nav>
    </dialog>
  `,
  styles: `
    :host {
      flex: 1;
      display: flex;
      justify-content: center;
      min-width: 0;
    }
    button,
    input {
      font: inherit;
      color: inherit;
      min-height: 32px;
      padding: 6px 8px;
      border: 1px solid var(--wb-control-border);
      border-radius: 4px;
      background: var(--surface);
    }
    .search-trigger {
      display: flex;
      align-items: center;
      gap: 8px;
      width: min(28rem, 100%);
      color: var(--text-muted);
      font-size: 0.8125rem;
      cursor: text;
    }
    .search-trigger:hover {
      border-color: var(--border-accent-3);
    }
    .search-trigger svg {
      flex: none;
      width: 16px;
      height: 16px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.6;
      stroke-linecap: round;
    }
    .search-label {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    kbd {
      margin-left: auto;
      color: var(--wb-evidence-text);
      font-size: 0.6875rem;
    }
    dialog {
      color: var(--text);
      background: var(--surface);
      border: 1px solid var(--border-strong-5);
      border-radius: 6px;
      padding: 16px;
      width: min(540px, calc(100% - 24px));
      max-height: 85dvh;
      overflow: auto;
    }
    dialog::backdrop {
      background: var(--overlay);
    }
    header {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    h2 {
      flex: 1;
      font-size: 1.125rem;
      margin: 0;
    }
    h3 {
      margin: 16px 0 8px;
      font-size: 0.8125rem;
    }
    form {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 12px;
    }
    input {
      flex: 1;
      min-width: 120px;
    }
    .commands {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .commands button {
      text-align: left;
      border: 0;
    }
    small {
      display: block;
      color: var(--wb-evidence-text);
    }
    nav {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
    }
    @media (max-width: 679px) {
      kbd {
        display: none;
      }
      button,
      input {
        min-height: 44px;
      }
    }
  `,
})
export class HeaderSearchComponent {
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly commands = inject(WorkspaceCommands);
  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>("dialog");
  private readonly input =
    viewChild<ElementRef<HTMLInputElement>>("searchInput");
  readonly query = signal("");
  readonly items = computed(() =>
    this.commands
      .entries()
      .filter((item) =>
        `${item.label} ${item.detail ?? ""}`
          .toLocaleLowerCase()
          .includes(this.query().trim().toLocaleLowerCase()),
      ),
  );
  private returnFocus: HTMLElement | null = null;
  constructor() {
    const subscription = this.commands.requested.subscribe(() => this.open());
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }
  @HostListener("document:keydown", ["$event"])
  keyboard(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
      event.preventDefault();
      this.open();
    }
  }
  open(): void {
    if (this.dialog()?.nativeElement.open) return;
    this.returnFocus =
      this.document.activeElement instanceof HTMLElement
        ? this.document.activeElement
        : null;
    this.query.set("");
    this.commands.opening.next();
    this.dialog()?.nativeElement.showModal();
    this.input()?.nativeElement.focus();
  }
  close(): void {
    this.dialog()?.nativeElement.close();
    if (this.returnFocus?.isConnected) this.returnFocus.focus();
  }
  choose(item: WorkbenchCommand): void {
    this.close();
    this.commands.chosen.next(item);
  }
  go(url: string): void {
    this.close();
    void this.router.navigateByUrl(url);
  }
  search(query: string): void {
    if (query.trim()) {
      this.close();
      void this.router.navigate(["/catalog/search"], {
        queryParams: { q: query.trim() },
      });
    }
  }
}
