import { Component, input, output } from "@angular/core";
import { MappingDetailsComponent } from "./detail.component";
import { NativeEvidenceComponent } from "./native-evidence.component";
import {
  type NodeReference,
  type Rule,
  type Control,
  type Dependency,
  ProjectionView,
  cardinalityText,
  dataTypeText,
  dependencyReference,
  literalText,
  pathText,
  boundaryFormat,
} from "./projection-view";

@Component({
  selector: "braid-mapping-entity",
  imports: [MappingDetailsComponent, NativeEvidenceComponent],
  template: `
    @let ref = reference();
    <div class="entity-heading">
      <div>
        <p class="eyebrow">Selected {{ ref.kind }}</p>
        <h3>{{ view().label(ref) }}</h3>
      </div>
      <button type="button" class="secondary-button" (click)="close.emit()">
        Close details
      </button>
    </div>
    @if (view().document.coverage.status !== "complete") {
      <p class="uncertainty-note">
        This explanation is incomplete. Source reads can be possibilities;
        review the gaps and rule details before treating lineage as certain.
      </p>
    }
    @switch (ref.kind) {
      @case ("field") {
        @if (view().fields.get(ref.fieldId); as field) {
          <dl class="field-metadata">
            <div>
              <dt>Path</dt>
              <dd>
                <code>{{ path(field.path) }}</code>
              </dd>
            </div>
            <div>
              <dt>Type</dt>
              <dd>{{ type(field) }}</dd>
            </div>
            <div>
              <dt>Occurrences</dt>
              <dd>{{ cardinality(field) }}</dd>
            </div>
            <div>
              <dt>Required</dt>
              <dd>{{ field.requirement }}</dd>
            </div>
            <div>
              <dt>Nulls</dt>
              <dd>{{ field.nullability }}</dd>
            </div>
          </dl>
          <h4>Values produced by</h4>
          @for (rule of view().producers(field.id); track rule.id) {
            <button
              type="button"
              class="lineage-link"
              (click)="navigate.emit(ruleRef(rule.id))"
            >
              {{ rule.summary }}
            </button>
          } @empty {
            <p class="muted">No producing rule is shown for this field.</p>
          }
          <h4>Used by</h4>
          @for (entity of view().uses(ref); track entity.id) {
            <button
              type="button"
              class="lineage-link"
              (click)="navigate.emit(entityRef(entity))"
            >
              {{ entity.summary }}
            </button>
          } @empty {
            <p class="muted">No dependent rule or control is shown.</p>
          }
          <braid-native-evidence [locator]="field.nativeLocator" />
        }
      }
      @case ("rule") {
        @if (view().rules.get(ref.ruleId); as rule) {
          <h4>Target</h4>
          <button
            type="button"
            class="text-button"
            (click)="navigate.emit(fieldRef(rule.outputFieldId))"
          >
            {{ view().label(fieldRef(rule.outputFieldId)) }}
          </button>
          <h4>Inputs, conditions, and defaults</h4>
          @for (dependency of rule.dependencies; track $index) {
            <div class="dependency">
              <span class="dependency-role">{{
                dependency.role ?? "value"
              }}</span>
              @if (dependencyRef(dependency); as target) {
                <button
                  type="button"
                  class="text-button"
                  (click)="navigate.emit(target)"
                >
                  {{ view().label(target) }}
                </button>
              } @else {
                <code>{{ constant(dependency) }}</code>
              }
            </div>
          } @empty {
            <p class="muted">
              {{
                view().document.coverage.status === "complete"
                  ? "No source fields are required by this explanation."
                  : "Inputs not fully explained."
              }}
            </p>
          }
          <h4>Rule explanation</h4>
          <braid-mapping-details
            [details]="rule.details"
            [view]="view()"
            (navigate)="navigate.emit($event)"
          />
          <braid-native-evidence [locator]="rule.nativeLocator" />
        }
      }
      @case ("control") {
        @if (view().controls.get(ref.controlId); as control) {
          <p class="control-kind">{{ control.kind }}</p>
          <h4>Applies to</h4>
          @for (target of control.appliesTo; track $index) {
            <button
              type="button"
              class="lineage-link"
              (click)="navigate.emit(target)"
            >
              {{ view().label(target) }}
            </button>
          }
          <h4>Dependencies</h4>
          @for (dependency of control.dependencies; track $index) {
            <div class="dependency">
              <span class="dependency-role">{{
                dependency.role ?? "value"
              }}</span>
              @if (dependencyRef(dependency); as target) {
                <button
                  type="button"
                  class="text-button"
                  (click)="navigate.emit(target)"
                >
                  {{ view().label(target) }}
                </button>
              } @else {
                <code>{{ constant(dependency) }}</code>
              }
            </div>
          } @empty {
            <p class="muted">No dependencies are shown.</p>
          }
          <braid-mapping-details
            [details]="control.details"
            [view]="view()"
            (navigate)="navigate.emit($event)"
          />
          <braid-native-evidence [locator]="control.nativeLocator" />
        }
      }
      @case ("parameter") {
        @if (view().parameters.get(ref.parameterId); as parameter) {
          <dl class="field-metadata">
            <div>
              <dt>Type</dt>
              <dd>{{ type(parameter) }}</dd>
            </div>
            <div>
              <dt>Required</dt>
              <dd>{{ parameter.requirement }}</dd>
            </div>
          </dl>
          @if (parameter.defaultValue; as value) {
            <h4>Default value</h4>
            <braid-mapping-details
              [details]="[{ kind: 'literal', value }]"
              [view]="view()"
              (navigate)="navigate.emit($event)"
            />
          } @else {
            <p class="muted">No default value is shown.</p>
          }
          <h4>Used by</h4>
          @for (entity of view().uses(ref); track entity.id) {
            <button
              type="button"
              class="lineage-link"
              (click)="navigate.emit(entityRef(entity))"
            >
              {{ entity.summary }}
            </button>
          }
          <braid-native-evidence [locator]="parameter.nativeLocator" />
        }
      }
      @case ("boundary") {
        @if (view().boundaries.get(ref.boundaryId); as boundary) {
          <dl class="field-metadata">
            <div>
              <dt>Role</dt>
              <dd>{{ boundary.role }}</dd>
            </div>
            <div>
              <dt>Format</dt>
              <dd>{{ format(boundary.format) }}</dd>
            </div>
            <div>
              <dt>Shape</dt>
              <dd>{{ boundary.shape }}</dd>
            </div>
          </dl>
          <h4>Fields</h4>
          @for (field of view().document.fields; track field.id) {
            @if (field.boundaryId === boundary.id) {
              <button
                type="button"
                class="lineage-link"
                (click)="navigate.emit(fieldRef(field.id))"
              >
                {{ path(field.path) }}
              </button>
            }
          }
          <braid-native-evidence [locator]="boundary.nativeLocator" />
        }
      }
    }
  `,
})
export class MappingEntityComponent {
  readonly reference = input.required<NodeReference>();
  readonly view = input.required<ProjectionView>();
  readonly navigate = output<NodeReference>();
  readonly close = output<void>();
  readonly path = pathText;
  readonly format = boundaryFormat;
  readonly type = dataTypeText;
  readonly cardinality = cardinalityText;
  readonly dependencyRef = dependencyReference;

  fieldRef(fieldId: string): NodeReference {
    return { kind: "field", fieldId };
  }
  ruleRef(ruleId: string): NodeReference {
    return { kind: "rule", ruleId };
  }
  entityRef(entity: Rule | Control): NodeReference {
    return "outputFieldId" in entity
      ? this.ruleRef(entity.id)
      : { kind: "control", controlId: entity.id };
  }
  constant(dependency: Dependency): string {
    return dependency.kind === "constant" ? literalText(dependency.value) : "";
  }
}
