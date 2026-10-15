import { Component } from "@angular/core";
import { RouterLink } from "@angular/router";

@Component({
  selector: "weft-not-found",
  imports: [RouterLink],
  template: `<section class="welcome" aria-labelledby="not-found-title">
    <p class="eyebrow">Weft</p>
    <h1 id="not-found-title">Page unavailable</h1>
    <p>This link does not match a page in your workspace.</p>
    <p>
      <a routerLink="/catalog">Open the catalog</a> or
      <a routerLink="/examples">browse examples</a>.
    </p>
  </section>`,
})
export class NotFoundComponent {}
