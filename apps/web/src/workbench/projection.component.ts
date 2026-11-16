import {
  Component,
  ElementRef,
  computed,
  effect,
  input,
  signal,
  viewChild,
} from "@angular/core";
import type { components } from "../api/generated";
import { MappingDetailsComponent } from "./detail.component";
import { MappingEntityComponent } from "./entity.component";
import { NativeEvidenceComponent } from "./native-evidence.component";
import {
  type NodeReference,
  type ProjectionDocument,
  type Dependency,
  ProjectionView,
  dependencyReference,
  literalText,
  pathText,
  boundaryFormat,
} from "./projection-view";

type ValidationReport = components["schemas"]["ValidationReportV1"];

@Component({
  selector: "braid-mapping-projection",
  imports: [
    MappingDetailsComponent,
    MappingEntityComponent,
    NativeEvidenceComponent,
  ],
  templateUrl: "./projection.component.html",
})
export class MappingProjectionComponent {
  readonly document = input.required<ProjectionDocument>();
  readonly validation = input<ValidationReport | null>(null);
  readonly view = computed(() => new ProjectionView(this.document()));
  readonly outputId = signal("");
  readonly search = signal("");
  readonly pageIndex = signal(0);
  readonly pageSize = 100;
  readonly expandedRows = signal<ReadonlySet<string>>(new Set());
  readonly selected = signal<NodeReference | null>(null);
  readonly entityPanel = viewChild<ElementRef<HTMLElement>>("entityPanel");
  readonly rows = computed(() => {
    const query = this.search().trim().toLocaleLowerCase();
    return this.view().rows.filter(
      (row) =>
        row.field.boundaryId === this.outputId() &&
        (!query || row.searchText.includes(query)),
    );
  });
  readonly outputRows = computed(
    () =>
      this.view().rows.filter((row) => row.field.boundaryId === this.outputId())
        .length,
  );
  readonly path = pathText;
  readonly format = boundaryFormat;
  readonly visibleRows = computed(() =>
    this.rows().slice(
      this.pageIndex() * this.pageSize,
      (this.pageIndex() + 1) * this.pageSize,
    ),
  );
  readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.rows().length / this.pageSize)),
  );
  readonly dependencyRef = dependencyReference;
  private returnFocus: HTMLElement | null = null;

  constructor() {
    effect(() => {
      const view = this.view();
      this.outputId.set(view.outputs[0]?.id ?? "");
      this.search.set("");
      this.pageIndex.set(0);
      this.expandedRows.set(new Set());
      this.selected.set(null);
      this.returnFocus = null;
    });
  }

  selectOutput(outputId: string): void {
    this.outputId.set(outputId);
    this.selected.set(null);
    this.pageIndex.set(0);
    this.expandedRows.set(new Set());
  }

  setSearch(query: string): void {
    this.search.set(query);
    this.pageIndex.set(0);
    this.expandedRows.set(new Set());
  }

  changePage(index: number): void {
    this.pageIndex.set(Math.max(0, Math.min(index, this.pageCount() - 1)));
    this.expandedRows.set(new Set());
  }

  toggleRow(key: string, event: Event): void {
    const details = event.target;
    if (!(details instanceof HTMLDetailsElement) || !details.isConnected)
      return;
    this.expandedRows.update((current) => {
      if (current.has(key) === details.open) return current;
      const next = new Set(current);
      if (details.open) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  navigate(reference: NodeReference): void {
    if (!this.selected())
      this.returnFocus =
        globalThis.document.activeElement instanceof HTMLElement
          ? globalThis.document.activeElement
          : null;
    let fieldId: string | null = null;
    if (reference.kind === "field") fieldId = reference.fieldId;
    if (reference.kind === "rule")
      fieldId = this.view().rules.get(reference.ruleId)?.outputFieldId ?? null;
    const boundaryId = fieldId
      ? this.view().fields.get(fieldId)?.boundaryId
      : reference.kind === "boundary"
        ? reference.boundaryId
        : null;
    if (
      boundaryId &&
      this.view().outputs.some((boundary) => boundary.id === boundaryId)
    )
      this.outputId.set(boundaryId);
    this.setSearch("");
    if (fieldId) {
      const rowIndex = this.rows().findIndex((row) => row.field.id === fieldId);
      if (rowIndex >= 0)
        this.pageIndex.set(Math.floor(rowIndex / this.pageSize));
    }
    this.selected.set(reference);
    setTimeout(() => this.entityPanel()?.nativeElement.focus(), 0);
  }

  closeDetails(): void {
    this.selected.set(null);
    if (this.returnFocus?.isConnected) this.returnFocus.focus();
    else globalThis.document.getElementById("mapping-output")?.focus();
    this.returnFocus = null;
  }

  fieldRef(fieldId: string): NodeReference {
    return { kind: "field", fieldId };
  }
  ruleRef(ruleId: string): NodeReference {
    return { kind: "rule", ruleId };
  }
  boundaryRef(boundaryId: string): NodeReference {
    return { kind: "boundary", boundaryId };
  }
  controlRef(controlId: string): NodeReference {
    return { kind: "control", controlId };
  }
  parameterRef(parameterId: string): NodeReference {
    return { kind: "parameter", parameterId };
  }
  constant(dependency: Dependency): string {
    return dependency.kind === "constant" ? literalText(dependency.value) : "";
  }
  coverageTitle(): string {
    return {
      complete: "Explanation complete",
      partial: "Explanation incomplete",
      unavailable: "Explanation unavailable",
    }[this.document().coverage.status];
  }
  capabilityStatus(status: components["schemas"]["CapabilityStatus"]): string {
    return {
      supported: "Available",
      unsupported: "Unavailable",
      conditional: "Available with conditions",
      notQualified: "Not yet verified",
    }[status];
  }
}
