import { Component, computed, input } from "@angular/core";
import { RouterLink } from "@angular/router";
import { helpRoute, type HelpSectionId } from "./help-sections";

/** A contextual link to one user guide section, opened in a new tab. */
@Component({
  selector: "braid-help-link",
  imports: [RouterLink],
  // A new tab keeps unsent work and request recovery on the current page.
  template: `<a
    class="help-link"
    [routerLink]="route()"
    target="_blank"
    rel="noopener"
    ><span class="help-link-mark" aria-hidden="true">?</span><ng-content /><span
      class="help-link-hint"
      >(opens in a new tab)</span
    ></a
  >`,
  styles: `
    :host {
      display: block;
      margin-block: 6px;
    }
    .help-link {
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--wb-selection-text);
      font-size: 12px;
      font-weight: 600;
      text-decoration: none;
    }
    .help-link:hover {
      text-decoration: underline;
    }
    .help-link-hint {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }
    .help-link-mark {
      display: inline-grid;
      place-items: center;
      width: 16px;
      height: 16px;
      border-radius: 50%;
      background: var(--wb-selection-tint);
      font-size: 11px;
      font-weight: 700;
    }
  `,
})
export class HelpLinkComponent {
  readonly section = input.required<HelpSectionId>();
  readonly route = computed(() => helpRoute(this.section()));
}
