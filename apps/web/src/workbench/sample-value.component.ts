import { Component, input } from "@angular/core";
import type { SampleObservation } from "./workbench-sample";

@Component({
  selector: "braid-sample-value",
  template: `@let item = value();
    @if (item.kind === "observed") {
      @for (value of item.values; track $index) {
        <code
          >{{ value.text }}{{ value.truncated ? "… (truncated)" : "" }}</code
        >
      }
      @if (item.more) {
        <span class="wb-muted">More observed values…</span>
      }
    } @else if (item.kind === "missing") {
      <span class="wb-muted">Not present</span>
    } @else {
      <span class="wb-muted">{{ label(item.reason) }}</span>
    }`,
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
      }
      code {
        display: block;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
    `,
  ],
})
export class SampleValueComponent {
  readonly value = input.required<SampleObservation>();
  label(reason: string): string {
    return (
      (
        {
          noEvidence: "No observed value",
          boundaryFormat: "Format not supported",
          tooLarge: "Sample exceeds display limit",
          invalidJson: "Not valid JSON",
          ambiguousJson: "Ambiguous JSON keys",
          unsupportedPath: "Path not supported",
        } as Record<string, string>
      )[reason] ?? "Unavailable"
    );
  }
}
