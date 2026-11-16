import { Component, computed, input, output } from "@angular/core";
import { MappingDetailsComponent } from "./detail.component";
import { MappingEntityComponent } from "./entity.component";
import { NativeEvidenceComponent } from "./native-evidence.component";
import {
  cardinalityText,
  dataTypeText,
  dependencyReference,
  literalKind,
  literalText,
  pathText,
  type Control,
  type NativeLocator,
  type NodeReference,
  type Rule,
  ProjectionView,
} from "./projection-view";
import type { InspectorTab } from "./workbench-model";

@Component({
  selector: "braid-workbench-entity-inspector",
  imports: [
    MappingDetailsComponent,
    MappingEntityComponent,
    NativeEvidenceComponent,
  ],
  template: `
    @if (reference(); as ref) {
      <header class="selected-entity">
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
      </header>
      @if (tab() === 2) {
        <section class="inspector-evidence" aria-label="Saved mapping evidence">
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
          <braid-native-evidence [locator]="locator()" />
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
        </section>
      } @else if (tab() === 1) {
        <h4>How this field is filled</h4>
        @if (ref.kind === "field" || ref.kind === "rule") {
          @for (rule of declaredRules(ref); track rule.id) {
            <div
              class="declared-flow"
              role="group"
              [attr.aria-label]="'Declared data flow: ' + rule.summary"
            >
              <div class="flow-dependencies">
                @for (dependency of rule.dependencies; track $index) {
                  @if (dependencyRef(dependency); as source) {
                    <button
                      type="button"
                      class="flow-card"
                      [attr.data-role]="referenceRole(source)"
                      [attr.aria-label]="view().label(source)"
                      (click)="navigate.emit(source)"
                    >
                      <span class="card-label">{{
                        referenceLabel(source)
                      }}</span>
                      <span class="card-value">{{ view().label(source) }}</span>
                    </button>
                  } @else if (dependency.kind === "constant") {
                    <div class="flow-card" data-role="evidence">
                      <span class="card-label"
                        >Constant · {{ literalKind(dependency.value) }}</span
                      >
                      <code class="card-value">{{
                        literalText(dependency.value)
                      }}</code>
                    </div>
                  }
                } @empty {
                  <div class="flow-card" data-role="evidence">
                    <span class="card-label">Declared dependencies</span>
                    <p>No dependency details are available for this rule.</p>
                  </div>
                }
              </div>
              <span class="flow-arrow" aria-hidden="true">→</span>
              <button
                type="button"
                class="flow-card"
                data-role="rule"
                [attr.aria-label]="rule.summary"
                (click)="navigate.emit({ kind: 'rule', ruleId: rule.id })"
              >
                <span class="card-label">Transformation rule</span>
                <strong class="card-value">{{ rule.summary }}</strong>
              </button>
              <span class="flow-arrow" aria-hidden="true">→</span>
              <button
                type="button"
                class="flow-card"
                data-role="output"
                [attr.aria-label]="
                  view().label({ kind: 'field', fieldId: rule.outputFieldId })
                "
                (click)="
                  navigate.emit({ kind: 'field', fieldId: rule.outputFieldId })
                "
              >
                <span class="card-label">Result field</span>
                <span class="card-value">{{
                  view().label({ kind: "field", fieldId: rule.outputFieldId })
                }}</span>
              </button>
            </div>
          } @empty {
            <p>No rule details are available for this field.</p>
          }
        }
        @if (ref.kind !== "field") {
          <section class="inspector-card" [attr.data-role]="referenceRole(ref)">
            <braid-mapping-entity
              [view]="view()"
              [reference]="ref"
              (navigate)="navigate.emit($event)"
              (close)="navigate.emit(null)"
            />
          </section>
        }
        <p class="flow-note">Declared data flow, not observed execution.</p>
        <h4>Used by</h4>
        <div class="dependent-links">
          @for (entity of view().uses(ref); track entity.id) {
            <button
              type="button"
              [attr.data-role]="referenceRole(entityReference(entity))"
              (click)="navigate.emit(entityReference(entity))"
            >
              {{ entity.summary }}
            </button>
          } @empty {
            <p>
              No dependent rule or control is shown in the available details.
            </p>
          }
        </div>
      } @else {
        @if (ref.kind === "field") {
          @if (view().fields.get(ref.fieldId); as field) {
            <div class="field-rule-layout">
              <section
                class="field-properties"
                aria-label="Selected field properties"
              >
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
              </section>
              <div class="rule-stack">
                @for (rule of view().producers(field.id); track rule.id) {
                  <section class="inspector-card" data-role="rule">
                    <p class="card-label">Transformation rule</p>
                    <h4>{{ rule.summary }}</h4>
                    <button
                      type="button"
                      class="inspect-link"
                      (click)="navigate.emit({ kind: 'rule', ruleId: rule.id })"
                    >
                      Inspect rule
                    </button>
                    <braid-mapping-details
                      [view]="view()"
                      [details]="rule.details"
                      (navigate)="navigate.emit($event)"
                    />
                  </section>
                } @empty {
                  <p>No rule details are available for this field.</p>
                }
              </div>
            </div>
            @if (fieldControls(field.id).length) {
              <h4>Governing controls</h4>
              <div class="dependent-links">
                @for (control of fieldControls(field.id); track control.id) {
                  <button
                    type="button"
                    data-role="evidence"
                    (click)="
                      navigate.emit({ kind: 'control', controlId: control.id })
                    "
                  >
                    {{ control.summary }}
                  </button>
                }
              </div>
            }
          }
        } @else if (ref.kind === "rule") {
          @if (view().rules.get(ref.ruleId); as rule) {
            <div class="field-rule-layout">
              <section class="inspector-card" data-role="output">
                <p class="card-label">Result field</p>
                <button
                  type="button"
                  (click)="
                    navigate.emit({
                      kind: 'field',
                      fieldId: rule.outputFieldId,
                    })
                  "
                >
                  {{
                    view().label({ kind: "field", fieldId: rule.outputFieldId })
                  }}
                </button>
              </section>
              <section class="inspector-card" data-role="rule">
                <p class="card-label">Transformation rule</p>
                <braid-mapping-details
                  [view]="view()"
                  [details]="rule.details"
                  (navigate)="navigate.emit($event)"
                />
              </section>
            </div>
          }
        } @else {
          <section class="inspector-card" [attr.data-role]="referenceRole(ref)">
            <braid-mapping-entity
              [view]="view()"
              [reference]="ref"
              (navigate)="navigate.emit($event)"
              (close)="navigate.emit(null)"
            />
          </section>
        }
      }
      @if (view().document.coverage.status !== "complete") {
        <p class="warning">
          Some mapping details aren't available from this file. Declared data
          flow does not prove which values were used during a run.
        </p>
      }
    } @else {
      <div class="inspector-empty">
        <h3>Select a field</h3>
        <p class="empty">Select a field to see how it is filled.</p>
      </div>
    }
    @if (view().document.coverage.gaps.length) {
      <details class="coverage-gaps">
        <summary>
          Missing mapping details ({{ view().document.coverage.gaps.length }})
        </summary>
        @for (gap of view().document.coverage.gaps; track $index) {
          <p>{{ gap.message }}</p>
          <braid-native-evidence [locator]="gap.nativeLocator" />
        }
      </details>
    }
  `,
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
        font-size: 0.8125rem;
        color: var(--text);
      }
      h3 {
        margin: 0;
        font-size: 0.9375rem;
        overflow-wrap: anywhere;
      }
      h4 {
        margin: 8px 0 6px;
        font-size: 0.875rem;
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
        font-weight: 650;
        white-space: nowrap;
      }
      .selected-entity {
        display: flex;
        align-items: baseline;
        flex-wrap: wrap;
        gap: 6px 12px;
        margin-bottom: 8px;
      }
      .field-rule-layout {
        display: grid;
        grid-template-columns: minmax(0, 1fr) minmax(0, 1.15fr);
        align-items: start;
        gap: 20px;
      }
      .field-properties,
      .rule-stack {
        min-width: 0;
      }
      .rule-stack {
        display: grid;
        gap: 12px;
      }
      .inspector-card,
      .flow-card {
        box-sizing: border-box;
        min-width: 0;
        border: 1px solid
          color-mix(
            in srgb,
            var(--wb-role-text, var(--border)) 30%,
            transparent
          );
        border-radius: 7px;
        padding: 16px;
        background: var(--wb-role-tint, var(--surface-subtle));
        color: var(--text);
        overflow-wrap: anywhere;
      }
      .inspector-card h4 {
        color: var(--wb-role-text, var(--text));
        margin: 4px 0 8px;
      }
      .flow-card {
        padding: 12px;
      }
      .card-label {
        display: block;
        margin: 0 0 8px;
        color: var(--wb-role-text, var(--text-muted));
        font-size: 0.6875rem;
        font-weight: 650;
        letter-spacing: 0.04em;
        text-transform: uppercase;
      }
      .card-value {
        display: block;
        overflow-wrap: anywhere;
        line-height: 1.6;
      }
      .declared-flow {
        display: grid;
        grid-template-columns:
          minmax(0, 1fr) 20px minmax(0, 1.15fr)
          20px minmax(0, 1fr);
        align-items: stretch;
        gap: 10px;
        margin: 6px 0;
      }
      .flow-dependencies {
        display: grid;
        align-content: start;
        gap: 10px;
        min-width: 0;
      }
      .flow-arrow {
        display: grid;
        align-items: center;
        justify-content: center;
        color: var(--text-muted);
        font-size: 1.25rem;
      }
      button.flow-card {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        justify-content: center;
        margin: 0;
        min-height: 80px;
      }
      button.flow-card:hover {
        border-color: var(--wb-role-text);
        box-shadow: inset 0 0 0 1px var(--wb-role-text);
      }
      .flow-note {
        color: var(--text-muted);
        font-size: 0.75rem;
        margin: 12px 0;
      }
      .dependent-links {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 8px;
      }
      .dependent-links button {
        margin: 0;
        color: var(--wb-role-text);
        background: var(--wb-role-tint);
        border-color: color-mix(in srgb, var(--wb-role-text) 30%, transparent);
      }
      .inspector-evidence {
        padding: 16px;
        border: 1px solid var(--border);
        border-radius: 7px;
        background: var(--surface-subtle);
      }
      dl {
        display: grid;
        grid-template-columns: 112px minmax(0, 1fr);
        gap: 10px 14px;
        margin: 0 0 12px;
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
        border-radius: 6px;
        padding: 7px 10px;
        min-height: 36px;
        text-align: left;
        font: inherit;
        font-size: 0.75rem;
        margin: 3px 0;
        cursor: pointer;
        color: var(--text);
      }
      button:focus-visible,
      summary:focus-visible {
        outline: 2px solid var(--wb-focus);
        outline-offset: 1px;
      }
      button.inspect-link {
        display: inline-block;
        color: var(--wb-rule-text);
        border: 0;
        background: none;
        padding: 0;
        text-decoration: underline;
        text-underline-offset: 3px;
      }
      details {
        margin-top: 7px;
      }
      summary {
        cursor: pointer;
      }
      .warning {
        border-left: 3px solid var(--wb-attention-text);
        padding: 10px 12px;
        background: var(--wb-attention-tint);
        color: var(--wb-attention-text);
        margin-top: 16px;
      }
      .empty {
        color: var(--text-muted-17);
      }
      .inspector-empty {
        padding: 16px 0;
      }
      .inspector-empty h3 {
        margin-bottom: 8px;
      }
      .coverage-gaps {
        border-top: 1px solid var(--border);
        padding-top: 12px;
        margin-top: 16px;
      }
      @media (max-width: 700px) {
        .field-rule-layout,
        .declared-flow {
          grid-template-columns: minmax(0, 1fr);
          gap: 12px;
        }
        .flow-arrow {
          transform: rotate(90deg);
          min-height: 20px;
        }
        button.flow-card {
          min-height: 72px;
        }
      }
      @media (max-width: 679px) {
        button {
          min-height: 44px;
        }
        dl {
          grid-template-columns: 96px minmax(0, 1fr);
          gap: 8px 10px;
        }
        .inspector-card,
        .flow-card,
        .inspector-evidence {
          padding: 12px;
        }
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
  readonly literalText = literalText;
  readonly literalKind = literalKind;
  declaredRules(reference: NodeReference): readonly Rule[] {
    const view = this.view();
    if (reference.kind === "field") return view.producers(reference.fieldId);
    if (reference.kind !== "rule") return [];
    const rule = view.rules.get(reference.ruleId);
    return rule ? [rule] : [];
  }
  referenceRole(reference: NodeReference): string {
    if (reference.kind === "field") return this.fieldRole(reference.fieldId);
    return reference.kind === "rule" ? "rule" : "evidence";
  }
  referenceLabel(reference: NodeReference): string {
    if (reference.kind === "field") {
      const role = this.fieldRole(reference.fieldId);
      return role === "source"
        ? "Incoming field"
        : role === "output"
          ? "Result field"
          : "Field";
    }
    switch (reference.kind) {
      case "rule":
        return "Transformation rule";
      case "parameter":
        return "Parameter";
      case "control":
        return "Control";
      case "boundary":
        return "Document boundary";
    }
  }
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
