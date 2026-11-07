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
  selector: "weft-header-search",
  template: `
    <button
      class="search-trigger"
      type="button"
      (click)="open()"
      aria-haspopup="dialog"
    >
      Search / Go to <kbd>Ctrl K</kbd>
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
      min-width: 0;
      margin-left: auto;
    }
    button,
    input {
      font: inherit;
      color: inherit;
      min-height: 32px;
      padding: 6px 8px;
      border: 1px solid #7b8c78;
      border-radius: 4px;
      background: white;
    }
    .search-trigger {
      font-size: 0.8125rem;
    }
    kbd {
      margin-left: 12px;
      color: #5d6e80;
      font-size: 0.6875rem;
    }
    dialog {
      color: #213b34;
      background: white;
      border: 1px solid #b5c4ac;
      border-radius: 6px;
      padding: 16px;
      width: min(540px, calc(100% - 24px));
      max-height: 85dvh;
      overflow: auto;
    }
    dialog::backdrop {
      background: #153f3344;
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
      color: #5d6e80;
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
