import { Component, computed, input, signal } from "@angular/core";
import type { DocumentRepresentation } from "./workbench-documents";

/** Display only: the owning workflow retains authority, downloads and comparison policy. */
@Component({
  selector: "weft-document-view",
  template: `
    <section aria-label="Document bytes">
      <div class="document-controls">
        <span
          >{{ data().byteCount }} bytes ·
          {{ data().mediaType ?? "No declared media type" }}</span
        >
        @if (data().segments) {
          <button
            type="button"
            [attr.aria-pressed]="segmentView()"
            (click)="mode.set('segments')"
          >
            EDI segments
          </button>
          <button
            type="button"
            [attr.aria-pressed]="!segmentView()"
            (click)="mode.set('raw')"
          >
            Raw text
          </button>
        } @else {
          <span>{{ data().format === "hex" ? "Hex bytes" : "Raw text" }}</span>
        }
      </div>
      @if (segmentView()) {
        <ol aria-label="EDI segments">
          @for (segment of data().segments; track $index) {
            <li>
              <code>{{ segment }}</code>
            </li>
          }
        </ol>
        <p>
          Display only, up to 2,000 segments. Downloads and comparisons retain
          the original bytes.
        </p>
      } @else {
        <pre tabindex="0">{{ data().text }}</pre>
      }
      @if (data().truncated) {
        <p>
          Display limited to the first 64 KiB. Download retains the full
          document.
        </p>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    section {
      min-width: 0;
    }
    .document-controls {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.5rem;
      margin-bottom: 0.5rem;
      color: var(--wb-evidence-text);
    }
    button {
      color: var(--wb-selection-text);
      border: 1px solid var(--wb-control-border);
      border-radius: 4px;
      background: transparent;
      padding: 0.25rem 0.5rem;
      font: inherit;
    }
    button[aria-pressed="true"] {
      background: var(--wb-selection-tint);
    }
    button:focus-visible,
    pre:focus-visible {
      outline: 2px solid var(--wb-focus);
      outline-offset: 2px;
    }
    ol {
      margin: 0.5rem 0;
      padding-left: 3rem;
    }
    li {
      padding: 0.2rem 0;
    }
    pre,
    code {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      tab-size: 2;
    }
    pre {
      margin: 0.5rem 0;
    }
    p {
      color: var(--wb-evidence-text);
      font-size: 0.875em;
    }
  `,
})
export class DocumentViewComponent {
  readonly data = input.required<DocumentRepresentation>();
  readonly mode = signal<"segments" | "raw">("segments");
  readonly segmentView = computed(
    () => this.mode() === "segments" && this.data().segments !== null,
  );
}
