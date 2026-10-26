import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from "@angular/core";
import {
  type MappingRow,
  type NodeReference,
  ProjectionView,
  pathText,
  dataTypeText,
} from "./projection-view";
import {
  groupMappingRows,
  mappingCellText,
  sortMappingRows,
  type MappingColumn,
  type MappingGrouping,
} from "./mapping-table-model";
import { ResizeHandleComponent } from "./resize-handle.component";
import { SampleValueComponent } from "./sample-value.component";
import { type WorkbenchSample } from "./workbench-sample";
import { LayoutPreferencesStore } from "./layout-preferences-store";
import { columnRole } from "./presentation";

@Component({
  selector: "weft-workbench-mapping-table",
  imports: [ResizeHandleComponent, SampleValueComponent],
  templateUrl: "./mapping-table.component.html",
  styleUrl: "./mapping-table.component.css",
})
export class WorkbenchMappingTableComponent {
  private readonly preferences = inject(LayoutPreferencesStore);
  readonly view = input.required<ProjectionView>();
  readonly selected = input<NodeReference | null>(null);
  readonly sample = input<WorkbenchSample | null>(null);
  readonly sampleMode = input(false);
  readonly sampleModeChange = output<boolean>();
  readonly inspect = output<NodeReference | null>();
  readonly search = signal("");
  readonly outputId = signal("");
  readonly grouping = computed(() => this.preferences.table().grouping);
  readonly partialOnly = signal(false);
  readonly sort = computed(() => this.preferences.table().sort);
  readonly ascending = computed(() => this.preferences.table().ascending);
  readonly closed = signal<ReadonlySet<string>>(new Set());
  readonly page = signal(0);
  readonly showSamples = computed(() => this.sampleMode() && !!this.sample());
  readonly showLineage = signal(false);
  readonly hiddenColumns = computed(
    () => new Set<string>(this.preferences.table().hiddenColumns),
  );
  readonly widths = computed<Record<string, number>>(
    () => this.preferences.table().widths,
  );
  readonly pinKeyColumn = computed(() => this.preferences.table().pinKeyColumn);
  readonly role = columnRole;
  readonly pageSize = 100;
  readonly columns = computed(() => [
    { id: "source", label: "Incoming field" },
    ...(this.sample() && this.showSamples()
      ? [{ id: "input", label: "Sample input" }]
      : []),
    ...(!this.hiddenColumns().has("rule")
      ? [{ id: "rule", label: "Rule" }]
      : []),
    { id: "target", label: "Result field" },
    ...(this.sample() && this.showSamples()
      ? [{ id: "output", label: "Sample result" }]
      : []),
    ...(!this.hiddenColumns().has("type")
      ? [{ id: "type", label: "Type" }]
      : []),
  ]);
  readonly minimumWidth = computed(() =>
    this.columns().reduce((sum, col) => sum + this.widths()[col.id]!, 0),
  );
  readonly filtered = computed(() => {
    const query = this.search().trim().toLocaleLowerCase();
    return sortMappingRows(
      this.view().rows.filter(
        (row) =>
          (!this.outputId() || row.field.boundaryId === this.outputId()) &&
          (!query || row.searchText.includes(query)) &&
          (!this.partialOnly() || row.incomplete),
      ),
      this.view(),
      this.sort(),
      this.ascending(),
    );
  });
  readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.filtered().length / this.pageSize)),
  );
  readonly groups = computed(() =>
    groupMappingRows(
      this.filtered().slice(
        this.page() * this.pageSize,
        (this.page() + 1) * this.pageSize,
      ),
      this.view(),
      this.grouping(),
    ),
  );
  readonly visible = computed(() =>
    this.groups().flatMap((group) =>
      this.isClosed(group.key) ? [] : group.rows,
    ),
  );
  readonly focusedKey = signal<string | null>(null);
  readonly focused = computed(() =>
    this.visible().some((row) => row.key === this.focusedKey())
      ? this.focusedKey()
      : (this.visible()[0]?.key ?? null),
  );
  readonly selectedRow = computed(
    () => this.view().rows.find((row) => this.isSelected(row)) ?? null,
  );
  readonly path = pathText;
  readonly type = dataTypeText;
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private destroyed = false;

  constructor() {
    const destroy = inject(DestroyRef);
    destroy.onDestroy(() => {
      this.destroyed = true;
    });
    afterNextRender(() => {
      const viewport =
        this.element.nativeElement.querySelector<HTMLElement>(
          ".wb-table-scroll",
        );
      if (!viewport) return;
      // Non-overlay horizontal scrollbars consume part of the CSS minimum.
      // Reserve their actual height so the minimum describes usable row space.
      const reserveScrollbar = () =>
        viewport.style.setProperty(
          "--wb-table-scrollbar-height",
          `${Math.max(0, viewport.offsetHeight - viewport.clientHeight)}px`,
        );
      let frame: number | null = null;
      const observer = new ResizeObserver(() => {
        // Resize the observed viewport on the next frame, outside the current
        // observer delivery, to avoid a resize-loop notification.
        if (frame === null)
          frame = requestAnimationFrame(() => {
            frame = null;
            reserveScrollbar();
          });
      });
      observer.observe(viewport);
      reserveScrollbar();
      destroy.onDestroy(() => {
        observer.disconnect();
        if (frame !== null) cancelAnimationFrame(frame);
      });
    });
    effect(() => {
      this.view();
      this.search.set("");
      this.outputId.set("");
      this.page.set(0);
      this.closed.set(new Set());
      this.focusedKey.set(null);
    });
    effect(() => {
      const selected = this.selected();
      const all = this.view().rows;
      untracked(() => {
        if (!selected) return;
        const row = all.find((value) =>
          selected.kind === "field"
            ? value.field.id === selected.fieldId
            : selected.kind === "rule"
              ? value.rule?.id === selected.ruleId
              : false,
        );
        if (!row) return;
        if (!this.filtered().some((value) => value.key === row.key)) {
          this.search.set("");
          this.outputId.set("");
          this.partialOnly.set(false);
        }
        const index = this.filtered().findIndex(
          (value) => value.key === row.key,
        );
        if (index >= 0) this.page.set(Math.floor(index / this.pageSize));
        const group = this.groups().find((value) =>
          value.rows.some((value) => value.key === row.key),
        );
        if (group && this.closed().has(group.key))
          this.closed.update(
            (value) => new Set([...value].filter((key) => key !== group.key)),
          );
      });
    });
  }
  isSelected(row: MappingRow): boolean {
    const selected = this.selected();
    return selected?.kind === "field"
      ? selected.fieldId === row.field.id
      : selected?.kind === "rule"
        ? selected.ruleId === row.rule?.id
        : false;
  }
  isClosed(key: string): boolean {
    return !this.search() && this.closed().has(key);
  }
  toggleGroup(key: string): void {
    this.closed.update((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }
  setSearch(value: string): void {
    this.search.set(value.slice(0, 512));
    this.page.set(0);
  }
  chooseOutput(value: string): void {
    this.outputId.set(value);
    this.page.set(0);
  }
  sortColumn(column: string): void {
    if (!["source", "rule", "target", "type"].includes(column)) return;
    this.preferences.updateTable({
      sort: column as MappingColumn,
      ascending: this.sort() === column ? !this.ascending() : true,
    });
    this.page.set(0);
  }
  setGrouping(grouping: string): void {
    this.preferences.updateTable({ grouping: grouping as MappingGrouping });
  }
  togglePin(): void {
    this.preferences.updateTable({ pinKeyColumn: !this.pinKeyColumn() });
  }
  toggleColumn(column: MappingColumn): void {
    if (column === "source" || column === "target") return;
    const next = new Set(this.preferences.table().hiddenColumns);
    if (next.has(column)) next.delete(column);
    else next.add(column);
    this.preferences.updateTable({ hiddenColumns: [...next] });
  }
  resize(column: string, value: number): void {
    this.preferences.updateTable({
      widths: { ...this.preferences.table().widths, [column]: value },
    });
  }
  select(row: MappingRow): void {
    this.focusedKey.set(row.key);
    this.inspect.emit({ kind: "field", fieldId: row.field.id });
  }
  cell(row: MappingRow, column: string): string {
    return mappingCellText(row, this.view(), column as MappingColumn);
  }
  nextPage(direction: number): void {
    this.page.update((value) =>
      Math.max(0, Math.min(this.pageCount() - 1, value + direction)),
    );
  }
  key(event: KeyboardEvent, row: MappingRow): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.inspect.emit(null);
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      this.select(row);
      return;
    }
    if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const rows = this.visible();
    const index =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? rows.length - 1
          : rows.findIndex((value) => value.key === row.key) +
            (event.key === "ArrowDown" ? 1 : -1);
    const next = rows[Math.max(0, Math.min(rows.length - 1, index))];
    if (!next) return;
    this.focusedKey.set(next.key);
    queueMicrotask(() => {
      if (!this.destroyed)
        this.element.nativeElement
          .querySelector<HTMLElement>(
            `[data-row-key="${CSS.escape(next.key)}"]`,
          )
          ?.focus();
    });
  }
}
