import { DatePipe } from "@angular/common";
import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { NavigationStart, Router, RouterLink } from "@angular/router";
import { filter, type Subscription } from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import { catalogOwner } from "../catalog-types";
import {
  AutomationApi,
  notificationEventLabel,
  type Inbox,
} from "./automation-api";

/** Header quick view of the newest notifications. It reads only when opened. */
@Component({
  selector: "weft-notification-popover",
  imports: [DatePipe, RouterLink],
  template: `
    <button
      #toggle
      id="workspace-notifications-toggle"
      class="header-icon-button"
      type="button"
      title="Recent notifications"
      aria-controls="workspace-notifications"
      [attr.aria-expanded]="open()"
      (click)="open() ? close() : show()"
    >
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M6 16v-5a6 6 0 0 1 12 0v5l2 2H4zM10 20a2 2 0 0 0 4 0" />
      </svg>
      <span class="header-sr-only">Recent notifications</span>
    </button>
    @if (open()) {
      <section
        id="workspace-notifications"
        class="notification-popover"
        aria-labelledby="workspace-notifications-title"
      >
        <header>
          <h2 id="workspace-notifications-title">Notifications</h2>
        </header>
        @switch (state()) {
          @case ("loading") {
            <p class="popover-note" role="status">Loading notifications…</p>
          }
          @case ("unavailable") {
            <p class="popover-note" role="status">
              Notifications are unavailable here. Open the notification centre
              to check.
            </p>
          }
          @case ("ready") {
            <ul aria-label="Newest notifications">
              @for (item of items(); track item.id) {
                <li>
                  <span
                    class="popover-dot"
                    [class.popover-dot-read]="item.read"
                    aria-hidden="true"
                  ></span>
                  <a [routerLink]="item.link" (click)="close()"
                    ><strong>{{ label(item.event) }}</strong>
                    <small
                      >{{ item.read ? "Read" : "Unread" }} ·
                      <time [attr.datetime]="item.createdAtUtc">{{
                        item.createdAtUtc | date: "MMM d, HH:mm" : "UTC"
                      }}</time>
                      UTC</small
                    ></a
                  >
                </li>
              } @empty {
                <li class="popover-note">You're all caught up.</li>
              }
            </ul>
          }
        }
        <a class="popover-footer" routerLink="/notifications" (click)="close()"
          >Open notification centre</a
        >
      </section>
    }
  `,
  styles: `
    :host {
      position: relative;
      display: block;
    }
    .notification-popover {
      position: absolute;
      top: calc(100% + 8px);
      right: 0;
      z-index: 30;
      width: min(320px, calc(100vw - 24px));
      background: #fff;
      border: 1px solid #d7e2d1;
      border-radius: 8px;
      box-shadow: 0 6px 18px #18352b22;
      font-size: 0.8125rem;
      overflow: hidden;
    }
    header {
      padding: 10px 12px;
      border-bottom: 1px solid #d7e2d1;
    }
    h2 {
      margin: 0;
      font-size: 0.875rem;
    }
    ul {
      list-style: none;
      margin: 0;
      padding: 4px 0;
      max-height: 320px;
      overflow: auto;
    }
    li {
      display: flex;
      gap: 8px;
      align-items: flex-start;
      padding: 6px 12px;
    }
    li a {
      flex: 1;
      min-width: 0;
      text-decoration: none;
    }
    li a:hover strong {
      text-decoration: underline;
    }
    small {
      display: block;
      color: #526b5e;
      font-size: 0.75rem;
    }
    .popover-dot {
      flex: none;
      width: 8px;
      height: 8px;
      margin-top: 6px;
      border-radius: 50%;
      background: var(--wb-running-text);
    }
    .popover-dot-read {
      background: #c9d2c6;
    }
    .popover-note {
      margin: 0;
      padding: 12px;
      color: #526b5e;
    }
    .popover-footer {
      display: block;
      padding: 10px 12px;
      border-top: 1px solid #d7e2d1;
      text-align: center;
      color: #174d3f;
      font-weight: 600;
    }
    @media (max-width: 679px) {
      .notification-popover {
        position: fixed;
        top: 104px;
        left: 12px;
        right: 12px;
        width: auto;
      }
    }
  `,
})
export class NotificationPopoverComponent {
  readonly open = signal(false);
  readonly state = signal<"loading" | "ready" | "unavailable">("loading");
  readonly items = signal<Inbox["notifications"]>([]);
  readonly label = notificationEventLabel;
  private readonly api = inject(AutomationApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly toggle =
    viewChild.required<ElementRef<HTMLButtonElement>>("toggle");
  private request: Subscription | null = null;
  /** The owner whose notifications are shown; another owner never sees them. */
  private owner: string | null = null;

  constructor() {
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationStart),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.close());
    this.contexts.invalidated
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.close());
    effect(() => {
      const context = this.contexts.context();
      const key = context
        ? catalogOwner(context) + "#" + this.contexts.identityVersion()
        : null;
      untracked(() => {
        if (this.owner !== null && key !== this.owner) this.close();
      });
    });
    inject(DestroyRef).onDestroy(() => this.request?.unsubscribe());
  }

  @HostListener("document:pointerdown", ["$event"])
  outside(event: PointerEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node))
      this.close();
  }

  // Escape works from the bell as well as from inside the popup.
  @HostListener("keydown.escape", ["$event"])
  escape(event: Event): void {
    if (!this.open()) return;
    event.preventDefault();
    this.close(true);
  }

  show(): void {
    this.open.set(true);
    this.request?.unsubscribe();
    this.items.set([]);
    const context = this.contexts.context();
    if (!context || !this.contexts.canDispatch(context)) {
      this.state.set("unavailable");
      return;
    }
    this.owner = catalogOwner(context) + "#" + this.contexts.identityVersion();
    this.state.set("loading");
    // One explicit read per opening; nothing refreshes in the background.
    this.request = this.api.inbox(context).subscribe({
      next: (inbox) => {
        if (
          !this.contexts.isCurrent(context) ||
          !this.contexts.canPresent(context)
        ) {
          this.close();
          return;
        }
        this.items.set(inbox.notifications.slice(0, 6));
        this.state.set("ready");
      },
      error: () => this.state.set("unavailable"),
    });
  }

  close(restoreFocus = false): void {
    this.request?.unsubscribe();
    this.request = null;
    this.items.set([]);
    this.owner = null;
    if (!this.open()) return;
    this.open.set(false);
    this.state.set("loading");
    if (restoreFocus) this.toggle().nativeElement.focus();
  }
}
