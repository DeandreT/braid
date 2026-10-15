import { DOCUMENT } from "@angular/common";
import { Component, inject } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import {
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from "@angular/router";
import { filter } from "rxjs";

@Component({
  selector: "weft-root",
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: "./app.component.html",
})
export class AppComponent {
  private readonly document = inject(DOCUMENT);

  constructor() {
    let initialNavigation = true;
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
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
