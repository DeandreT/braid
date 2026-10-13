import { Component, forwardRef, input, output } from "@angular/core";
import { NativeEvidenceComponent } from "./native-evidence.component";
import {
  type Detail,
  type NodeReference,
  ProjectionView,
  literalKind,
  literalText,
} from "./projection-view";

@Component({
  selector: "weft-mapping-details",
  imports: [forwardRef(() => MappingDetailsComponent), NativeEvidenceComponent],
  template: `
    <ul class="mapping-details">
      @for (detail of details(); track $index) {
        <li>
          @switch (detail.kind) {
            @case ("group") {
              <details open>
                <summary>{{ detail.label }}</summary>
                <weft-mapping-details
                  [details]="detail.children"
                  [view]="view()"
                  (navigate)="navigate.emit($event)"
                />
              </details>
            }
            @case ("expression") {
              <details open>
                <summary>{{ detail.operation }}</summary>
                <weft-mapping-details
                  [details]="detail.arguments"
                  [view]="view()"
                  (navigate)="navigate.emit($event)"
                />
              </details>
            }
            @case ("reference") {
              <button
                type="button"
                class="text-button"
                (click)="navigate.emit(detail.reference)"
              >
                {{ view().label(detail.reference) }}
              </button>
            }
            @case ("literal") {
              <span class="literal-kind">{{ kind(detail.value) }}</span>
              <code class="literal-value">{{ text(detail.value) }}</code>
              @if (
                detail.value.kind === "exactNumber" &&
                detail.value.flavor === "binaryFloat"
              ) {
                <span class="literal-bits"
                  >IEEE-754 bits: <code>{{ detail.value.value }}</code></span
                >
              }
            }
            @case ("unsupported") {
              <div class="explanation-gap">
                <strong>{{ detail.construct }}</strong>
                <p>{{ detail.description }}</p>
                <span>Explanation incomplete</span>
                <weft-native-evidence [locator]="detail.nativeLocator" />
              </div>
            }
          }
        </li>
      }
    </ul>
  `,
})
export class MappingDetailsComponent {
  readonly details = input.required<Detail[]>();
  readonly view = input.required<ProjectionView>();
  readonly navigate = output<NodeReference>();
  readonly text = literalText;
  readonly kind = literalKind;
}
