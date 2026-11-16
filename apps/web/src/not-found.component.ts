import { Component } from "@angular/core";
import { RouterLink } from "@angular/router";

@Component({
  selector: "braid-not-found",
  imports: [RouterLink],
  template: `<section class="braid-state" aria-labelledby="not-found-title">
    <p class="braid-state-code">Not found</p>
    <h1 id="not-found-title">Page unavailable</h1>
    <p>
      This link does not match a page in your workspace. It may have moved, or
      you may not have access to it.
    </p>
    <div class="braid-state-actions">
      <a class="catalog-button catalog-button-primary" routerLink="/catalog"
        >Open the catalog</a
      >
      <a class="catalog-button" routerLink="/help">Open the guide</a>
    </div>
  </section>`,
})
export class NotFoundComponent {}
