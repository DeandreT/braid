import { DOCUMENT } from "@angular/common";
import { Component, inject, signal } from "@angular/core";
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

@Component({
  selector: "weft-root",
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    BrowserAuthControlsComponent,
    ConnectionNoticeComponent,
    HeaderSearchComponent,
  ],
  templateUrl: "./app.component.html",
})
export class AppComponent {
  readonly contexts = inject(CatalogContextStore);
  private readonly document = inject(DOCUMENT);
  readonly session = inject(BrowserSessionState);
  readonly workbench = signal(false);
  /** The sign-in page is a full page of its own, without the workspace header. */
  readonly signInPage = signal(false);

  constructor() {
    this.session
      .observe()
      .pipe(takeUntilDestroyed())
      .subscribe({ error: () => {} });
    let initialNavigation = true;
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        tap((event) => {
          this.workbench.set(
            /\/mappings\/[^/]+\/revisions\/(?!new(?:[?#]|$))[^/?#]+(?:[?#]|$)/.test(
              event.urlAfterRedirects,
            ),
          );
          this.signInPage.set(
            /^\/sign-in(?:[?#]|$)/.test(event.urlAfterRedirects),
          );
          if (initialNavigation) {
            initialNavigation = false;
            return;
          }
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

  skipToContent(event: MouseEvent): void {
    if (
      event.button !== 0 ||
      event.ctrlKey ||
      event.shiftKey ||
      event.altKey ||
      event.metaKey
    )
      return;
    this.document.getElementById("main-content")?.focus();
  }
}
