import { Component, inject } from "@angular/core";
import { Router } from "@angular/router";

@Component({
  selector: "weft-header-search",
  template: `
    <form role="search" (submit)="$event.preventDefault(); search(input.value)">
      <input
        #input
        type="search"
        autocomplete="off"
        spellcheck="false"
        aria-label="Search catalog"
        placeholder="Search catalog…"
        maxlength="256"
        required
      />
      <button type="submit" aria-label="Search">⌕</button>
    </form>
  `,
  styles: `
    :host {
      min-width: 0;
      max-width: 24rem;
      flex: 1;
      margin-inline: 1rem;
    }
    form {
      display: flex;
      gap: 0.3rem;
      align-items: center;
    }
    input {
      min-width: 0;
      width: 100%;
      padding: 0.5rem 0.75rem;
      color: inherit;
      background: transparent;
      border: 1px solid var(--wb-control-border, #7b8c78);
      border-radius: 0.4rem;
    }
    button {
      color: inherit;
      background: transparent;
      border: 0;
      font-size: 1.4rem;
      cursor: pointer;
    }
    input:focus-visible {
      outline: 3px solid var(--wb-focus, #174d3f);
      outline-offset: 3px;
    }
    @media (max-width: 700px) {
      :host {
        flex-basis: 100%;
        max-width: none;
        order: 4;
        margin-inline: 0;
      }
    }
  `,
})
export class HeaderSearchComponent {
  private readonly router = inject(Router);
  search(query: string): void {
    if (query.trim())
      void this.router.navigate(["/catalog/search"], {
        queryParams: { q: query.trim() },
      });
  }
}
