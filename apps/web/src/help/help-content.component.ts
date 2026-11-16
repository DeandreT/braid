import { NgTemplateOutlet } from "@angular/common";
import { Component, input } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { components } from "../api/generated";
import type { HelpBlock, HelpInline } from "./help-api";

type Schemas = components["schemas"];

/**
 * Renders guide blocks as text and router links only. The contract has no markup, and nothing
 * here binds HTML, so guide text can never become script or styling.
 */
@Component({
  selector: "braid-help-content",
  imports: [NgTemplateOutlet, RouterLink],
  styleUrl: "./help-content.component.css",
  template: `
    <ng-template #inline let-content>
      @for (item of asContent(content); track $index) {
        @switch (item.kind) {
          @case ("strong") {
            <strong>{{ item.text }}</strong>
          }
          @case ("emphasis") {
            <em>{{ item.text }}</em>
          }
          @case ("code") {
            <code>{{ item.text }}</code>
          }
          @case ("link") {
            @if (asLink(item); as link) {
              <a
                [routerLink]="['/help', link.sectionId]"
                [fragment]="link.anchor ?? undefined"
                >{{ link.text }}</a
              >
            }
          }
          @default {
            <span>{{ item.text }}</span>
          }
        }
      }
    </ng-template>
    @for (block of blocks(); track $index) {
      @switch (block.kind) {
        @case ("heading") {
          @if (asHeading(block); as heading) {
            @if (heading.level === 2) {
              <h2 [id]="'help-' + heading.anchor" tabindex="-1">
                {{ heading.text }}
              </h2>
            } @else {
              <h3 [id]="'help-' + heading.anchor" tabindex="-1">
                {{ heading.text }}
              </h3>
            }
          }
        }
        @case ("paragraph") {
          @if (asParagraph(block); as paragraph) {
            <p>
              <ng-container
                [ngTemplateOutlet]="inline"
                [ngTemplateOutletContext]="{ $implicit: paragraph.content }"
              />
            </p>
          }
        }
        @case ("list") {
          @if (asList(block); as list) {
            @if (list.ordered) {
              <ol>
                @for (item of list.items; track $index) {
                  <li>
                    <ng-container
                      [ngTemplateOutlet]="inline"
                      [ngTemplateOutletContext]="{ $implicit: item.content }"
                    />
                  </li>
                }
              </ol>
            } @else {
              <ul>
                @for (item of list.items; track $index) {
                  <li>
                    <ng-container
                      [ngTemplateOutlet]="inline"
                      [ngTemplateOutletContext]="{ $implicit: item.content }"
                    />
                  </li>
                }
              </ul>
            }
          }
        }
        @case ("table") {
          @if (asTable(block); as table) {
            <div
              class="help-table"
              role="region"
              tabindex="0"
              aria-label="Table"
            >
              <table>
                <thead>
                  <tr>
                    @for (cell of table.header; track $index) {
                      <th scope="col">
                        <ng-container
                          [ngTemplateOutlet]="inline"
                          [ngTemplateOutletContext]="{
                            $implicit: cell.content,
                          }"
                        />
                      </th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (row of table.rows; track $index) {
                    <tr>
                      @for (cell of row.cells; track $index) {
                        <td>
                          <ng-container
                            [ngTemplateOutlet]="inline"
                            [ngTemplateOutletContext]="{
                              $implicit: cell.content,
                            }"
                          />
                        </td>
                      }
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
        @case ("codeBlock") {
          @if (asCode(block); as code) {
            <pre tabindex="0"><code>{{ code.text }}</code></pre>
          }
        }
      }
    }
  `,
})
export class HelpContentComponent {
  readonly blocks = input.required<readonly HelpBlock[]>();

  asContent(value: unknown): readonly HelpInline[] {
    return value as readonly HelpInline[];
  }

  asLink(item: HelpInline): Schemas["HelpInlineV1HelpLinkV1"] | null {
    return item.kind === "link" ? item : null;
  }

  asHeading(block: HelpBlock): Schemas["HelpBlockV1HelpHeadingV1"] | null {
    return block.kind === "heading" ? block : null;
  }

  asParagraph(block: HelpBlock): Schemas["HelpBlockV1HelpParagraphV1"] | null {
    return block.kind === "paragraph" ? block : null;
  }

  asList(block: HelpBlock): Schemas["HelpBlockV1HelpListV1"] | null {
    return block.kind === "list" ? block : null;
  }

  asTable(block: HelpBlock): Schemas["HelpBlockV1HelpTableV1"] | null {
    return block.kind === "table" ? block : null;
  }

  asCode(block: HelpBlock): Schemas["HelpBlockV1HelpCodeBlockV1"] | null {
    return block.kind === "codeBlock" ? block : null;
  }
}
