import type { ParamMap, Params } from "@angular/router";
import {
  catalogRoutes,
  isCatalogCursor,
  isCatalogId,
  type CatalogMappingScope,
} from "../catalog-types";
import type { RevisionReportSelectors } from "../reports/revision-report-state";
import type { ComparisonSideName } from "./revision-comparison-types";

export interface ComparisonSideSelection {
  readonly revisionId: string;
  /** Optional exact saved reports; absent selectors read each revision's current reports. */
  readonly selectors: RevisionReportSelectors;
}

export interface ComparisonSelection {
  readonly scope: CatalogMappingScope;
  readonly left: ComparisonSideSelection | null;
  readonly right: ComparisonSideSelection | null;
  readonly cursor: string | null;
}

const sideKeys = {
  left: ["left", "leftProjection", "leftValidation"],
  right: ["right", "rightProjection", "rightValidation"],
} as const;
const queryKeys: readonly string[] = [
  ...sideKeys.left,
  ...sideKeys.right,
  "cursor",
];

export function comparisonPath(scope: CatalogMappingScope): string {
  return `${catalogRoutes.mapping(scope)}/comparisons`;
}

/** Browser selectors are closed, singular IDs, never API query passthrough. */
export function comparisonSelection(
  params: ParamMap,
  query: ParamMap,
): ComparisonSelection | null {
  const organizationId = params.get("organizationId");
  const projectId = params.get("projectId");
  const mappingId = params.get("mappingId");
  if (
    !isCatalogId(organizationId) ||
    !isCatalogId(projectId) ||
    !isCatalogId(mappingId) ||
    query.keys.some((key) => !queryKeys.includes(key)) ||
    query.keys.some((key) => query.getAll(key).length !== 1)
  )
    return null;
  const cursor = query.get("cursor");
  if (cursor !== null && !isCatalogCursor(cursor)) return null;
  const left = side(query, "left");
  const right = side(query, "right");
  if (left === undefined || right === undefined) return null;
  return {
    scope: { organizationId, projectId, mappingId },
    left,
    right,
    cursor,
  };
}

function side(
  query: ParamMap,
  name: ComparisonSideName,
): ComparisonSideSelection | null | undefined {
  const [revisionKey, projectionKey, validationKey] = sideKeys[name];
  const revisionId = query.get(revisionKey);
  const projection = query.get(projectionKey);
  const validation = query.get(validationKey);
  if (revisionId === null)
    return projection === null && validation === null ? null : undefined;
  if (
    !isCatalogId(revisionId) ||
    (projection !== null && !isCatalogId(projection)) ||
    (validation !== null && !isCatalogId(validation))
  )
    return undefined;
  return {
    revisionId,
    selectors: {
      projectionDerivationId: projection,
      validationDerivationId: validation,
    },
  };
}

/** A complete replacement query; omitted names are removed from the link. */
export function comparisonQuery(
  left: ComparisonSideSelection | null,
  right: ComparisonSideSelection | null,
  cursor: string | null = null,
): Params {
  const query: Params = {};
  for (const [name, value] of [
    ["left", left],
    ["right", right],
  ] as const) {
    if (!value) continue;
    const [revisionKey, projectionKey, validationKey] = sideKeys[name];
    query[revisionKey] = value.revisionId;
    if (value.selectors.projectionDerivationId)
      query[projectionKey] = value.selectors.projectionDerivationId;
    if (value.selectors.validationDerivationId)
      query[validationKey] = value.selectors.validationDerivationId;
  }
  if (cursor) query["cursor"] = cursor;
  return query;
}

export function currentSide(revisionId: string): ComparisonSideSelection {
  return {
    revisionId,
    selectors: { projectionDerivationId: null, validationDerivationId: null },
  };
}
