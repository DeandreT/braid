import {
  isCatalogId,
  type CatalogRevisionScope,
} from "../catalog/catalog-types";
import type { NodeReference } from "./projection-view";

export const workbenchViews = [
  "mapping",
  "preview",
  "revisions",
  "compare",
  "tests",
  "builds",
  "releases",
  "audit",
] as const;
export type WorkbenchView = (typeof workbenchViews)[number];
export type DockView =
  "ask" | "diagnostics" | "input" | "output" | "diff" | "activity";
export type WorkbenchDensity = "compact" | "comfortable" | "dense";
export type InspectorTab = 0 | 1 | 2;

export interface WorkbenchLayout {
  density: WorkbenchDensity;
  inspectorWidth: number;
  inspectorCollapsed: boolean;
  dockHeight: number;
  dockCollapsed: boolean;
}
export const defaultWorkbenchLayout: Readonly<WorkbenchLayout> = Object.freeze({
  density: "compact",
  inspectorWidth: 244,
  inspectorCollapsed: false,
  dockHeight: 144,
  dockCollapsed: false,
});
export const rowHeights: Readonly<Record<WorkbenchDensity, number>> = {
  compact: 28,
  comfortable: 32,
  dense: 24,
};

/** Layout has an explicit allowlist. It never serializes selection, samples or request recovery. */
export function normalizeWorkbenchLayout(value: unknown): WorkbenchLayout {
  const source =
    value && typeof value === "object"
      ? (value as Record<string, unknown>)
      : {};
  const bounded = (key: string, fallback: number, min: number, max: number) =>
    typeof source[key] === "number" && Number.isFinite(source[key])
      ? Math.round(Math.max(min, Math.min(max, source[key])))
      : fallback;
  return {
    density: ["compact", "comfortable", "dense"].includes(
      String(source.density),
    )
      ? (source.density as WorkbenchDensity)
      : "compact",
    inspectorWidth: bounded("inspectorWidth", 244, 210, 420),
    inspectorCollapsed: source.inspectorCollapsed === true,
    dockHeight: bounded("dockHeight", 144, 90, 320),
    dockCollapsed: source.dockCollapsed === true,
  };
}

export function inspectorLabels(
  view: WorkbenchView,
): readonly [string, string, string] {
  if (view === "tests") return ["Result", "Test setup", "Outputs"];
  if (view === "builds") return ["Readiness", "Approval", "Prepared version"];
  if (view === "releases" || view === "audit")
    return ["Required checks", "In use", "Change history"];
  if (view === "preview") return ["Incoming data", "Context", "Details"];
  return ["Rule", "Data flow", "Details"];
}

export function workbenchScopeKey(scope: CatalogRevisionScope): string {
  const parts = [
    scope.organizationId,
    scope.projectId,
    scope.mappingId,
    scope.revisionId,
  ];
  if (!parts.every(isCatalogId))
    throw new Error("A complete revision scope is required.");
  return parts.join("/");
}

export interface CaseSelection {
  caseId: string;
  version: string;
}
export interface RunSelection extends CaseSelection {
  runId: string;
}
export interface InvestigationTrail {
  run: RunSelection;
  path: readonly NodeReference[];
}
export interface WorkbenchSelection {
  binding: string | null;
  epoch: number;
  view: WorkbenchView;
  dock: DockView;
  inspectorTab: InspectorTab;
  entity: NodeReference | null;
  case: CaseSelection | null;
  run: RunSelection | null;
  sampleMode: boolean;
  investigation: InvestigationTrail | null;
}
export function emptyWorkbenchSelection(
  binding: string | null = null,
  epoch = 0,
): WorkbenchSelection {
  return {
    binding,
    epoch,
    view: "mapping",
    dock: "ask",
    inspectorTab: 0,
    entity: null,
    case: null,
    run: null,
    sampleMode: false,
    investigation: null,
  };
}

export type WorkbenchAction =
  | { kind: "bind"; binding: string | null }
  | { kind: "view"; view: WorkbenchView }
  | { kind: "dock"; dock: DockView }
  | { kind: "tab"; tab: InspectorTab }
  | {
      kind: "entity";
      binding: string;
      epoch: number;
      entity: NodeReference | null;
    }
  | {
      kind: "case";
      binding: string;
      epoch: number;
      selection: CaseSelection | null;
    }
  | {
      kind: "run";
      binding: string;
      epoch: number;
      selection: RunSelection | null;
    }
  | { kind: "sample"; binding: string; epoch: number; enabled: boolean }
  | {
      kind: "investigate";
      binding: string;
      epoch: number;
      run: RunSelection;
      entity: NodeReference;
    }
  | { kind: "clear" };

function validCase(value: CaseSelection): boolean {
  return (
    isCatalogId(value.caseId) &&
    /^[1-9][0-9]{0,18}$/.test(value.version) &&
    BigInt(value.version) <= 9223372036854775807n
  );
}

/** Late completions carry the captured binding. A workflow switch alone does not erase its selected evidence. */
export function reduceWorkbenchSelection(
  state: WorkbenchSelection,
  action: WorkbenchAction,
): WorkbenchSelection {
  const nextEpoch = () => {
    if (
      !Number.isSafeInteger(state.epoch) ||
      state.epoch >= Number.MAX_SAFE_INTEGER
    )
      throw new Error("Workbench lifetime exhausted.");
    return state.epoch + 1;
  };
  const retired = (binding: string | null): WorkbenchSelection => ({
    ...emptyWorkbenchSelection(binding, nextEpoch()),
    view: state.view,
    dock: state.dock,
  });
  if (action.kind === "clear") return retired(null);
  if (action.kind === "bind")
    return state.binding === action.binding ? state : retired(action.binding);
  if (action.kind === "view")
    return { ...state, view: action.view, inspectorTab: 0 };
  if (action.kind === "dock") return { ...state, dock: action.dock };
  if (action.kind === "tab") return { ...state, inspectorTab: action.tab };
  if (
    !state.binding ||
    state.binding !== action.binding ||
    state.epoch !== action.epoch
  )
    return state;
  if (action.kind === "investigate") {
    if (
      !state.run ||
      !sameRun(state.run, action.run) ||
      !state.case ||
      state.case.caseId !== action.run.caseId ||
      state.case.version !== action.run.version
    )
      return state;
    return {
      ...state,
      investigation: { run: { ...state.run }, path: [{ ...action.entity }] },
    };
  }
  if (action.kind === "entity") {
    const trail = state.investigation;
    const existing =
      trail?.path.findIndex(
        (value) => JSON.stringify(value) === JSON.stringify(action.entity),
      ) ?? -1;
    return {
      ...state,
      entity: action.entity ? { ...action.entity } : null,
      investigation:
        trail && action.entity
          ? {
              ...trail,
              path:
                existing >= 0
                  ? trail.path.slice(0, existing + 1)
                  : [...trail.path.slice(-7), { ...action.entity }],
            }
          : null,
    };
  }
  if (action.kind === "sample") return { ...state, sampleMode: action.enabled };
  if (action.kind === "case") {
    if (action.selection && !validCase(action.selection)) return state;
    const value = action.selection;
    const sameRun =
      value &&
      state.run?.caseId === value.caseId &&
      state.run.version === value.version;
    return {
      ...state,
      case: value ? { ...value } : null,
      run: sameRun ? state.run : null,
      sampleMode: false,
      investigation: sameRun ? state.investigation : null,
    };
  }
  if (
    action.selection &&
    (!validCase(action.selection) || !isCatalogId(action.selection.runId))
  )
    return state;
  const run = action.selection;
  return {
    ...state,
    run: run ? { ...run } : null,
    case: run ? { caseId: run.caseId, version: run.version } : state.case,
    sampleMode: false,
    investigation:
      run && state.run && sameRun(run, state.run) ? state.investigation : null,
  };
}

function sameRun(a: RunSelection, b: RunSelection): boolean {
  return (
    a.runId === b.runId && a.caseId === b.caseId && a.version === b.version
  );
}
