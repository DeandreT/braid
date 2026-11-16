import {
  normalizeWorkbenchLayout,
  type WorkbenchLayout,
} from "./workbench-model";
import type { MappingColumn, MappingGrouping } from "./mapping-table-model";

export const layoutStorageKey = "braid.workbench.layout.v1";
export const tableColumns = [
  "source",
  "rule",
  "target",
  "type",
  "input",
  "output",
] as const;
export interface MappingTableLayout {
  widths: Record<(typeof tableColumns)[number], number>;
  hiddenColumns: ("rule" | "type")[];
  sort: MappingColumn | null;
  ascending: boolean;
  grouping: MappingGrouping;
  pinKeyColumn: boolean;
}
export interface SavedWorkbenchLayout {
  version: 1;
  frame: WorkbenchLayout;
  table: MappingTableLayout;
}

const object = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};

/** Explicitly reconstruct every saved field. Never serialize component state. */
export function normalizeTableLayout(value: unknown): MappingTableLayout {
  const input = object(value),
    widths = object(input.widths);
  const defaults = {
    source: 190,
    rule: 210,
    target: 190,
    type: 100,
    input: 180,
    output: 180,
  };
  return {
    widths: Object.fromEntries(
      tableColumns.map((column) => [
        column,
        typeof widths[column] === "number" && Number.isFinite(widths[column])
          ? Math.round(Math.max(80, Math.min(720, widths[column])))
          : defaults[column],
      ]),
    ) as MappingTableLayout["widths"],
    hiddenColumns: (["rule", "type"] as const).filter(
      (column) =>
        Array.isArray(input.hiddenColumns) &&
        input.hiddenColumns.includes(column),
    ),
    sort: ["source", "rule", "target", "type"].includes(String(input.sort))
      ? (input.sort as MappingColumn)
      : null,
    ascending: input.ascending !== false,
    grouping: input.grouping === "output" ? "output" : "structure",
    pinKeyColumn: input.pinKeyColumn !== false,
  };
}

export function normalizeSavedLayout(value: unknown): SavedWorkbenchLayout {
  const input = object(value);
  return {
    version: 1,
    frame: normalizeWorkbenchLayout(input.frame),
    table: normalizeTableLayout(input.table),
  };
}

export function readSavedLayout(
  storage: Pick<Storage, "getItem"> | null,
): SavedWorkbenchLayout {
  try {
    const text = storage?.getItem(layoutStorageKey);
    if (text && text.length <= 8192) {
      const value = JSON.parse(text);
      if (object(value).version === 1) return normalizeSavedLayout(value);
    }
  } catch {
    /* Disabled storage or invalid saved data must not block the workbench. */
  }
  return normalizeSavedLayout(null);
}

export function writeSavedLayout(
  storage: Pick<Storage, "setItem"> | null,
  value: unknown,
): void {
  try {
    storage?.setItem(
      layoutStorageKey,
      JSON.stringify(normalizeSavedLayout(value)),
    );
  } catch {
    /* Layout still works in memory when browser storage is unavailable. */
  }
}
