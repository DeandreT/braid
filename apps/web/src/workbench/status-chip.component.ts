import { Component, input } from "@angular/core";
import type { PresentedStatus } from "./presentation";

@Component({
  selector: "weft-status-chip",
  template: `<span class="status" [attr.data-tone]="status().tone"
    ><span aria-hidden="true">{{ status().icon }}</span>
    {{ status().label }}</span
  >`,
  styles: [
    `
      :host {
        display: inline;
      }
      .status {
        display: inline-flex;
        align-items: baseline;
        gap: 0.25em;
        max-width: 100%;
        padding: 1px 5px;
        border: 1px solid transparent;
        border-radius: 3px;
        font-size: 0.75rem;
        color: var(--wb-evidence-text);
        background: var(--wb-evidence-tint);
      }
      [data-tone="success"] {
        color: var(--wb-success-text);
        background: var(--wb-success-tint);
      }
      [data-tone="running"] {
        color: var(--wb-running-text);
        background: var(--wb-running-tint);
      }
      [data-tone="attention"],
      [data-tone="uncertain"] {
        color: var(--wb-attention-text);
        background: var(--wb-attention-tint);
      }
      [data-tone="uncertain"] {
        border: 1px dashed currentColor;
      }
      [data-tone="failure"] {
        color: var(--wb-failure-text);
        background: var(--wb-failure-tint);
      }
      [data-tone="unavailable"] {
        color: var(--wb-unavailable-text);
        background: var(--wb-unavailable-tint);
      }
      @media (forced-colors: active) {
        .status {
          border-color: CanvasText;
        }
      }
    `,
  ],
})
export class StatusChipComponent {
  readonly status = input.required<PresentedStatus>();
}
