import { DOCUMENT } from "@angular/common";
import {
  Component,
  HostListener,
  computed,
  inject,
  signal,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import {
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from "@angular/router";
import { EMPTY, catchError, filter, switchMap, tap } from "rxjs";
import { BrowserSessionState } from "./auth/browser-session-state";
import { BrowserAuthControlsComponent } from "./auth/browser-auth-controls.component";
import { CatalogContextStore } from "./catalog/catalog-context";
import { ConnectionNoticeComponent } from "./workbench/connection-notice.component";
import { HeaderSearchComponent } from "./catalog/search/header-search.component";
import { OrganizationSelection } from "./catalog/organization-selection";
import { OrganizationSwitcherComponent } from "./catalog/organization-switcher.component";
import { NotificationPopoverComponent } from "./catalog/automation/notification-popover.component";
import { WorkspaceChatPanelComponent } from "./workbench/workspace-chat-panel.component";
import { WorkspaceConversation } from "./workbench/workspace-conversation";
import { ThemePreference } from "./theme-preference";

/** Sign-in and invitation entry are full pages without the workspace header. */
const entryPage =
  /^\/(?:sign-in|invitations|organizations\/[^/?#]+\/invitations)(?:[?#]|$)/;

@Component({
  selector: "braid-root",
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    BrowserAuthControlsComponent,
    ConnectionNoticeComponent,
    HeaderSearchComponent,
    OrganizationSwitcherComponent,
    NotificationPopoverComponent,
    WorkspaceChatPanelComponent,
  ],
  templateUrl: "./app.component.html",
})
export class AppComponent {
  readonly contexts = inject(CatalogContextStore);
  readonly selection = inject(OrganizationSelection);
  readonly chat = inject(WorkspaceConversation);
  readonly chatVisible = computed(
    () =>
      this.chat.opened() &&
      this.contexts.canPresent(this.contexts.context()) &&
      this.contexts.context()?.features.chat === true,
  );
  readonly navigationOpen = signal(false);
  private readonly document = inject(DOCUMENT);
  readonly session = inject(BrowserSessionState);
  readonly workbench = signal(false);
  /** The sign-in page is a full page of its own, without the workspace header. */
  readonly signInPage = signal(false);

  constructor() {
    // Created with the shell so the System appearance follows the OS on every page.
    inject(ThemePreference);
    this.session
      .observe()
      .pipe(takeUntilDestroyed())
      .subscribe({ error: () => {} });
    let initialNavigation = true;
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        tap((event) => {
          this.workbench.set(!entryPage.test(event.urlAfterRedirects));
          this.signInPage.set(entryPage.test(event.urlAfterRedirects));
          this.navigationOpen.set(false);
          if (initialNavigation) {
            initialNavigation = false;
            return;
          }
          if (!this.document.activeElement?.closest("#workspace-chat"))
            queueMicrotask(() =>
              this.document.getElementById("main-content")?.focus(),
            );
        }),
        // Help has no catalog page to load the account's organization. Read once
        // per navigation, not per session observation: a denied organization can
        // still have a valid session, and must not cause a reclassification loop.
        // A failed read leaves the guide available and no organization displayed.
        switchMap((event) =>
          /^\/help(?:[/?#]|$)/.test(event.urlAfterRedirects) &&
          this.session.state().kind === "authenticated" &&
          this.contexts.context() === null
            ? this.contexts.refresh().pipe(catchError(() => EMPTY))
            : EMPTY,
        ),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  // Popup-style disclosures (column picker, overflow menus) close when the user clicks elsewhere.
  @HostListener("document:pointerdown", ["$event"])
  closePopups(event: PointerEvent): void {
    for (const popup of this.document.querySelectorAll<HTMLDetailsElement>(
      "details.wb-columns[open], details.catalog-menu[open]",
    ))
      if (!popup.contains(event.target as Node)) this.closePopup(popup);
  }

  // Overflow menus close on Escape like the column picker, returning focus to their summary.
  @HostListener("document:keydown.escape", ["$event"])
  escapePopup(event: Event): void {
    const popup =
      event.target instanceof Element
        ? event.target.closest<HTMLDetailsElement>("details.catalog-menu[open]")
        : null;
    if (!popup) return;
    event.preventDefault();
    this.closePopup(popup, true);
  }

  /** Focus inside a closing popup would be hidden, so it moves to the popup's summary. */
  private closePopup(popup: HTMLDetailsElement, refocus = false): void {
    const hadFocus = popup.contains(this.document.activeElement);
    popup.open = false;
    const summary = popup.querySelector<HTMLElement>(":scope > summary");
    if (refocus) summary?.focus();
    else if (hadFocus)
      // After the click settles: keep focus on a control the user clicked, but never on body.
      setTimeout(() => {
        const active = this.document.activeElement;
        if (!active || active === this.document.body || popup.contains(active))
          summary?.focus();
      });
  }

  skipToContent(event: MouseEvent): void {
    if (
      event.button !== 0 ||
      event.ctrlKey ||
      event.shiftKey ||
      event.altKey ||
      event.metaKey
    )
      return;
    const main = this.document.getElementById("main-content");
    if (main && !main.getClientRects().length && this.chatVisible()) {
      event.preventDefault();
      this.document.getElementById("workspace-chat-heading")?.focus();
    } else main?.focus();
  }
}
