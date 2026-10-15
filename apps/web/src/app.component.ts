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
  constructor() {
    const document = inject(DOCUMENT);
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
        queueMicrotask(() => document.getElementById("main-content")?.focus());
      });
  }
}
