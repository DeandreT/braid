import {
  dependencyReference,
  literalText,
  pathText,
  type MappingRow,
  ProjectionView,
} from "../mappings/projection-view";

export type MappingColumn = "source" | "rule" | "target" | "type";
export type MappingGrouping = "output" | "structure";
export interface MappingGroup {
  key: string;
  label: string;
  rows: readonly MappingRow[];
}

export function sourceLabel(row: MappingRow, view: ProjectionView): string {
  return (
    row.rule?.dependencies
      .map((value) => {
        const reference = dependencyReference(value);
        return reference
          ? view.label(reference)
          : value.kind === "constant"
            ? literalText(value.value)
            : "Unexplained input";
      })
      .join("; ") ||
    (row.incomplete ? "Inputs not fully explained" : "No source fields")
  );
}

export function groupMappingRows(
  rows: readonly MappingRow[],
  view: ProjectionView,
  grouping: MappingGrouping,
): readonly MappingGroup[] {
  const groups = new Map<
    string,
    { key: string; label: string; rows: MappingRow[] }
  >();
  for (const row of rows) {
    const boundary = view.boundaries.get(row.field.boundaryId);
    const parent = grouping === "structure" ? row.field.path.slice(0, -1) : [];
    const path = parent.length ? pathText(parent) : "";
    const key = JSON.stringify([row.field.boundaryId, parent]);
    const group = groups.get(key) ?? {
      key,
      label: `${boundary?.name ?? "Output"}${path ? " · " + path : ""}`,
      rows: [],
    };
    group.rows.push(row);
    groups.set(key, group);
  }
  return [...groups.values()];
}

export function mappingCellText(
  row: MappingRow,
  view: ProjectionView,
  column: MappingColumn,
): string {
  if (column === "source") return sourceLabel(row, view);
  if (column === "rule")
    return row.rule?.summary ?? "No producing rule explained";
  if (column === "target") return pathText(row.field.path);
  return row.field.dataType.scalarTypes.join(" / ") || row.field.dataType.kind;
}

export function sortMappingRows(
  rows: readonly MappingRow[],
  view: ProjectionView,
  column: MappingColumn | null,
  ascending: boolean,
): readonly MappingRow[] {
  if (!column) return rows;
  return rows
    .map((row, index) => ({ row, index }))
    .sort(
      (a, b) =>
        mappingCellText(a.row, view, column).localeCompare(
          mappingCellText(b.row, view, column),
          "en",
          { numeric: true },
        ) * (ascending ? 1 : -1) || a.index - b.index,
    )
    .map((value) => value.row);
}
