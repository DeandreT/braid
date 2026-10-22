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
import { filter } from "rxjs";
import { BrowserSessionState } from "./auth/browser-session-state";
import { BrowserAuthControlsComponent } from "./auth/browser-auth-controls.component";
import { ConnectionNoticeComponent } from "./workbench/connection-notice.component";

@Component({
  selector: "weft-root",
  imports: [
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    BrowserAuthControlsComponent,
    ConnectionNoticeComponent,
  ],
  templateUrl: "./app.component.html",
})
export class AppComponent {
  private readonly document = inject(DOCUMENT);
  readonly session = inject(BrowserSessionState);
  readonly workbench = signal(false);

  constructor() {
    this.session
      .observe()
      .pipe(takeUntilDestroyed())
      .subscribe({ error: () => {} });
    let initialNavigation = true;
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        this.workbench.set(
          /\/mappings\/[^/]+\/revisions\/(?!new(?:[?#]|$))[^/?#]+(?:[?#]|$)/.test(
            event.urlAfterRedirects,
          ),
        );
        if (initialNavigation) {
          initialNavigation = false;
          return;
        }
        queueMicrotask(() =>
          this.document.getElementById("main-content")?.focus(),
        );
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
    this.document.getElementById("main-content")?.focus();
  }
}
