import { Component, computed, input, output } from "@angular/core";
import { MappingDetailsComponent } from "./detail.component";
import { MappingEntityComponent } from "./entity.component";
import { NativeEvidenceComponent } from "./native-evidence.component";
import {
  cardinalityText,
  dataTypeText,
  dependencyReference,
  pathText,
  type Control,
  type NativeLocator,
  type NodeReference,
  type Rule,
  ProjectionView,
} from "./projection-view";
import type { InspectorTab } from "./workbench-model";

@Component({
  selector: "weft-workbench-entity-inspector",
  imports: [
    MappingDetailsComponent,
    MappingEntityComponent,
    NativeEvidenceComponent,
  ],
  template: `
    @if (reference(); as ref) {
      <p
        class="kind"
        [attr.data-role]="
          ref.kind === 'rule'
            ? 'rule'
            : ref.kind === 'field'
              ? fieldRole(ref.fieldId)
              : 'evidence'
        "
      >
        Selected {{ ref.kind }}
      </p>
      <h3>{{ view().label(ref) }}</h3>
      @if (tab() === 2) {
        <dl>
          <dt>Version ID</dt>
          <dd>
            <code>{{ view().document.provenance.revision.revisionId }}</code>
          </dd>
          <dt>Engine</dt>
          <dd>{{ view().document.provenance.backend.id }}</dd>
          <dt>Detail coverage</dt>
          <dd>{{ view().document.coverage.status }}</dd>
        </dl>
        <weft-native-evidence [locator]="locator()" />
        <details>
          <summary>Technical details</summary>
          <dl>
            <dt>Engine revision</dt>
            <dd>
              <code>{{ view().document.provenance.backend.revision }}</code>
            </dd>
            <dt>Backend build</dt>
            <dd>
              <code>{{ view().document.provenance.backend.buildId }}</code>
            </dd>
            <dt>Adapter</dt>
            <dd>
              <code>{{ view().document.provenance.adapterVersion }}</code>
            </dd>
            <dt>Native digest</dt>
            <dd>
              <code>{{
                view().document.provenance.nativeArtifactDigest.value
              }}</code>
            </dd>
          </dl>
        </details>
      } @else if (tab() === 1) {
        <h4>How this field is filled</h4>
        @if (ref.kind === "field") {
          @for (rule of view().producers(ref.fieldId); track rule.id) {
            <button
              type="button"
              (click)="navigate.emit({ kind: 'rule', ruleId: rule.id })"
            >
              {{ rule.summary }}
            </button>
            @for (dependency of rule.dependencies; track $index) {
              @if (dependencyRef(dependency); as source) {
                <button
                  type="button"
                  class="link"
                  (click)="navigate.emit(source)"
                >
                  {{ view().label(source) }}
                </button>
              }
            }
          } @empty {
            <p>No rule details are available for this field.</p>
          }
        } @else {
          <weft-mapping-entity
            [view]="view()"
            [reference]="ref"
            (navigate)="navigate.emit($event)"
            (close)="navigate.emit(null)"
          />
        }
        <h4>Used by</h4>
        @for (entity of view().uses(ref); track entity.id) {
          <button
            type="button"
            (click)="navigate.emit(entityReference(entity))"
          >
            {{ entity.summary }}
          </button>
        } @empty {
          <p>No dependent rule or control is shown in the available details.</p>
        }
      } @else {
        @if (ref.kind === "field") {
          @if (view().fields.get(ref.fieldId); as field) {
            <dl>
              <dt>Path</dt>
              <dd>
                <code>{{ path(field.path) }}</code>
              </dd>
              <dt>Type</dt>
              <dd>{{ type(field) }}</dd>
              <dt>Occurrences</dt>
              <dd>{{ cardinality(field) }}</dd>
              <dt>Required</dt>
              <dd>{{ field.requirement }}</dd>
              <dt>Nulls</dt>
              <dd>{{ field.nullability }}</dd>
            </dl>
            @for (rule of view().producers(field.id); track rule.id) {
              <h4>{{ rule.summary }}</h4>
              <weft-mapping-details
                [view]="view()"
                [details]="rule.details"
                (navigate)="navigate.emit($event)"
              />
            } @empty {
              <p>No rule details are available for this field.</p>
            }
            @if (fieldControls(field.id).length) {
              <h4>Governing controls</h4>
              @for (control of fieldControls(field.id); track control.id) {
                <button
                  type="button"
                  (click)="
                    navigate.emit({ kind: 'control', controlId: control.id })
                  "
                >
                  {{ control.summary }}
                </button>
              }
            }
          }
        } @else if (ref.kind === "rule") {
          @if (view().rules.get(ref.ruleId); as rule) {
            <button
              type="button"
              (click)="
                navigate.emit({ kind: 'field', fieldId: rule.outputFieldId })
              "
            >
              {{ view().label({ kind: "field", fieldId: rule.outputFieldId }) }}
            </button>
            <weft-mapping-details
              [view]="view()"
              [details]="rule.details"
              (navigate)="navigate.emit($event)"
            />
          }
        } @else {
          <weft-mapping-entity
            [view]="view()"
            [reference]="ref"
            (navigate)="navigate.emit($event)"
            (close)="navigate.emit(null)"
          />
        }
      }
      @if (view().document.coverage.status !== "complete") {
        <p class="warning">
          Some mapping details aren't available from this file. Declared data
          flow does not prove which values were used during a run.
        </p>
      }
    } @else {
      <p class="empty">Select a field to see how it is filled.</p>
    }
    @if (view().document.coverage.gaps.length) {
      <details>
        <summary>
          Missing mapping details ({{ view().document.coverage.gaps.length }})
        </summary>
        @for (gap of view().document.coverage.gaps; track $index) {
          <p>{{ gap.message }}</p>
          <weft-native-evidence [locator]="gap.nativeLocator" />
        }
      </details>
    }
  `,
  styles: [
    `
      :host {
        display: block;
        font-size: 0.8125rem;
        color: var(--text);
      }
      h3 {
        margin: 3px 0 8px;
        font-size: 0.875rem;
        overflow-wrap: anywhere;
      }
      h4 {
        margin: 9px 0 4px;
        font-size: 0.8125rem;
      }
      p {
        margin: 5px 0;
        line-height: 1.5;
      }
      .kind {
        color: var(--wb-role-text, var(--wb-evidence-text));
        font-size: 0.6875rem;
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }
      dl {
        display: grid;
        grid-template-columns: 72px minmax(0, 1fr);
        gap: 5px 6px;
        margin: 6px 0;
      }
      dt {
        color: var(--text-muted-17);
      }
      dd {
        margin: 0;
        overflow-wrap: anywhere;
      }
      code {
        font-size: 0.75rem;
      }
      button {
        display: block;
        border: 1px solid var(--wb-control-border);
        background: var(--surface);
        border-radius: 3px;
        padding: 4px 6px;
        text-align: left;
        font: inherit;
        font-size: 0.75rem;
        margin: 3px 0;
        cursor: pointer;
      }
      button:focus-visible,
      summary:focus-visible {
        outline: 2px solid var(--wb-focus);
        outline-offset: 1px;
      }
      .link {
        color: var(--text-accent-5);
        border: 0;
        background: none;
        padding-left: 0;
      }
      details {
        margin-top: 7px;
      }
      summary {
        cursor: pointer;
      }
      .warning {
        border-left: 2px solid var(--border-warn-5);
        padding-left: 6px;
        color: var(--wb-attention-text);
      }
      .empty {
        color: var(--text-muted-17);
      }
    `,
  ],
})
export class WorkbenchEntityInspectorComponent {
  readonly view = input.required<ProjectionView>();
  readonly reference = input<NodeReference | null>(null);
  readonly tab = input<InspectorTab>(0);
  readonly navigate = output<NodeReference | null>();
  readonly path = pathText;
  readonly type = dataTypeText;
  readonly cardinality = cardinalityText;
  readonly dependencyRef = dependencyReference;
  fieldRole(id: string): string {
    const view = this.view(),
      field = view.fields.get(id);
    if (view.outputs.some((boundary) => boundary.id === field?.boundaryId))
      return "output";
    if (view.inputs.some((boundary) => boundary.id === field?.boundaryId))
      return "source";
    return "evidence";
  }
  fieldControls(id: string): readonly Control[] {
    const view = this.view(),
      field = view.fields.get(id);
    if (!field) return [];
    const controls = new Map<string, Control>();
    const rules = view.producers(id);
    for (const rule of rules.length ? rules : [null])
      for (const control of view.controlsFor(field, rule))
        controls.set(control.id, control);
    return [...controls.values()];
  }
  entityReference(entity: Rule | Control): NodeReference {
    return "outputFieldId" in entity
      ? { kind: "rule", ruleId: entity.id }
      : { kind: "control", controlId: entity.id };
  }
  readonly locator = computed<NativeLocator | null>(() => {
    const ref = this.reference();
    if (!ref) return null;
    const view = this.view();
    switch (ref.kind) {
      case "field":
        return view.fields.get(ref.fieldId)?.nativeLocator ?? null;
      case "rule":
        return view.rules.get(ref.ruleId)?.nativeLocator ?? null;
      case "control":
        return view.controls.get(ref.controlId)?.nativeLocator ?? null;
      case "parameter":
        return view.parameters.get(ref.parameterId)?.nativeLocator ?? null;
      case "boundary":
        return view.boundaries.get(ref.boundaryId)?.nativeLocator ?? null;
    }
  });
}
