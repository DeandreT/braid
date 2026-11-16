import { Component, computed, input } from "@angular/core";
import { formatByteCount, formatUtcTimestamp } from "./metadata-format";

@Component({
  selector: "braid-metadata-value",
  template: `
    <details>
      <summary
        [attr.aria-label]="label() + ': ' + display() + '. Show exact value'"
      >
        {{ display() }}
      </summary>
      <code>{{ value() }}{{ kind() === "bytes" ? " bytes" : "" }}</code>
    </details>
  `,
  styles: [
    `
      :host {
        display: inline-block;
        max-width: 100%;
        font-size: inherit;
      }
      details {
        margin: 0;
      }
      summary {
        cursor: pointer;
        overflow-wrap: anywhere;
      }
      summary:focus-visible {
        outline: 2px solid var(--wb-focus);
        outline-offset: 1px;
      }
      code {
        display: block;
        overflow-wrap: anywhere;
        white-space: normal;
        font-size: 0.75rem;
        color: var(--wb-evidence-text);
      }
    `,
  ],
})
export class MetadataValueComponent {
  readonly value = input.required<string | number>();
  readonly kind = input.required<"bytes" | "time">();
  readonly label = input("Metadata");
  readonly display = computed(() =>
    this.kind() === "bytes"
      ? formatByteCount(this.value())
      : formatUtcTimestamp(this.value()),
  );
}
