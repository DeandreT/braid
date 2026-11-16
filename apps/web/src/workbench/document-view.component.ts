import { Component, computed, input, signal } from "@angular/core";
import type { DocumentRepresentation } from "./workbench-documents";
import { csvDocumentLimits } from "./csv-document";

/** Display only: the owning workflow retains authority, downloads and comparison policy. */
@Component({
  selector: "braid-document-view",
  template: `
    <section aria-label="Document bytes">
      @if (data().xmlMetadataText; as metadata) {
        <details>
          <summary>Saved XML metadata (read only)</summary>
          <p>
            Schema locations are lexical hints only. No schema or network
            resource is fetched. XML regression compares exact bytes; compiled
            execution is unavailable.
          </p>
          <pre tabindex="0">{{ metadata }}</pre>
        </details>
      }
      <div class="document-controls">
        <span
          >{{ data().byteCount }} bytes ·
          {{ data().mediaType ?? "No declared media type" }}</span
        >
        @if (data().xmlOutline) {
          <button
            type="button"
            [attr.aria-pressed]="mode() === 'formatted'"
            (click)="mode.set('formatted')"
          >
            XML outline
          </button>
          <button
            type="button"
            [attr.aria-pressed]="mode() === 'raw'"
            (click)="mode.set('raw')"
          >
            Raw text
          </button>
        } @else if (csvTable()) {
          <button
            type="button"
            [attr.aria-pressed]="tableView()"
            (click)="mode.set('formatted')"
          >
            Table
          </button>
          <button
            type="button"
            [attr.aria-pressed]="!tableView()"
            (click)="mode.set('raw')"
          >
            Raw text
          </button>
        } @else if (data().segments) {
          <button
            type="button"
            [attr.aria-pressed]="segmentView()"
            (click)="mode.set('formatted')"
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
      @if (mode() === "formatted" && data().xmlOutline) {
        <ol aria-label="Read-only XML lexical outline">
          @for (line of data().xmlOutline; track $index) {
            <li [style.margin-inline-start.em]="line.depth">
              <code>{{ line.text }}</code>
            </li>
          }
        </ol>
        <p>
          Bounded lexical outline only, not schema validation. Download retains
          the original bytes.
        </p>
      } @else if (tableView()) {
        @if (csvTable(); as table) {
          <div
            class="csv-table-scroll"
            tabindex="0"
            role="region"
            aria-label="CSV/TSV table, scroll to see all columns"
          >
            <table>
              <caption>
                {{
                  data().name ?? "Primary document"
                }}
                ·
                {{
                  table.dialect.delimiter === "	" ? "TSV" : "CSV"
                }}
                table ·
                {{
                  table.rowCount
                }}
                data rows
              </caption>
              @if (table.header; as header) {
                <thead>
                  <tr>
                    @for (cell of header; track $index) {
                      <th scope="col">
                        <span [title]="cell">{{ cell }}</span>
                      </th>
                    }
                  </tr>
                </thead>
              }
              <tbody>
                @for (row of table.rows; track $index) {
                  <tr>
                    @for (cell of row; track $index) {
                      <td>
                        <span>{{ cell }}</span>
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (table.header === null) {
            <p>No header row declared. Values are shown in source order.</p>
          }
          @if (table.rowCount === 0) {
            <p>No data rows.</p>
          }
          @if (table.truncated) {
            <p>
              Showing the first {{ table.rows.length }} of
              {{ table.rowCount }} data rows. All
              {{ table.recordCount }} records were parsed for display.
            </p>
          }
          <p>
            Display only, up to {{ csvRowLimit }} data rows. Downloads and
            comparisons retain the original bytes. Long headers are clipped;
            downloads retain their full text.
          </p>
        }
      } @else if (segmentView()) {
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
        @if (data().mediaType === "application/xml" && !data().xmlOutline) {
          <p>
            XML outline unavailable within display limits. Escaped raw text and
            the exact-byte download remain available.
          </p>
        }
        @if (csvUnavailableMessage(); as message) {
          <p>{{ message }}</p>
        }
        <pre tabindex="0">{{ data().text }}</pre>
      }
      @if (data().truncated && !tableView()) {
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
    .csv-table-scroll:focus-visible,
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
    .csv-table-scroll {
      width: 100%;
      max-width: 100%;
      min-width: 0;
      max-height: 28rem;
      overflow: auto;
      border: 1px solid var(--border);
      background: var(--surface);
    }
    table {
      width: max-content;
      min-width: 100%;
      border-collapse: collapse;
      font-size: 0.8125rem;
    }
    caption {
      padding: 6px;
      color: var(--wb-selection-text);
      text-align: left;
      overflow-wrap: anywhere;
    }
    th,
    td {
      height: 28px;
      padding: 3px 6px;
      border-bottom: 1px solid var(--border);
      border-inline-end: 1px solid var(--border);
      text-align: left;
      vertical-align: top;
    }
    th {
      position: sticky;
      top: 0;
      background: var(--wb-selection-tint);
      color: var(--wb-selection-text);
      font-weight: 600;
    }
    th span,
    td span {
      display: block;
      min-width: 3rem;
      max-width: 24rem;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      tab-size: 2;
    }
    th span {
      line-height: 1.2em;
      max-height: 3.6em;
      overflow: hidden;
    }
    p {
      color: var(--wb-evidence-text);
      font-size: 0.875em;
    }
  `,
})
export class DocumentViewComponent {
  readonly data = input.required<DocumentRepresentation>();
  readonly csvRowLimit = csvDocumentLimits.maximumDisplayedRows;
  readonly mode = signal<"formatted" | "raw">("formatted");
  readonly csvTable = computed(() => {
    const csv = this.data().csvDocument;
    return csv?.kind === "table" ? csv : null;
  });
  readonly tableView = computed(
    () => this.mode() === "formatted" && this.csvTable() !== null,
  );
  readonly segmentView = computed(
    () => this.mode() === "formatted" && this.data().segments !== null,
  );
  readonly csvUnavailableMessage = computed(() => {
    const csv = this.data().csvDocument;
    if (csv?.kind !== "unavailable") return null;
    switch (csv.reason) {
      case "invalidDialect":
        return "CSV/TSV table unavailable: authoritative versioned dialect metadata is missing or invalid. Raw view and the original download remain available.";
      case "invalidUtf8":
        return "CSV/TSV table unavailable: the document is not valid UTF-8. Raw bytes and the original download remain available.";
      case "invalidCsv":
        return "CSV/TSV table unavailable: the document does not match its declared dialect. Raw view and the original download remain available.";
      default:
        return "CSV/TSV table unavailable: this document exceeds display limits. Raw view and the original download remain available.";
    }
  });
}
