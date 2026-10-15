import type { ParamMap } from "@angular/router";
import { isCatalogId } from "../catalog-types";
import type { RevisionReportSelectors } from "./revision-report-state";

/** Browser selectors are closed, singular retained-success IDs, never API query passthrough. */
export function revisionReportSelectors(
  query: ParamMap,
): RevisionReportSelectors | null {
  const keys = ["projectionDerivationId", "validationDerivationId"];
  if (query.keys.some((key) => !keys.includes(key))) return null;
  const projection = query.getAll("projectionDerivationId");
  const validation = query.getAll("validationDerivationId");
  if (
    projection.length > 1 ||
    validation.length > 1 ||
    (projection.length === 1 && !isCatalogId(projection[0])) ||
    (validation.length === 1 && !isCatalogId(validation[0]))
  )
    return null;
  return {
    projectionDerivationId: projection[0] ?? null,
    validationDerivationId: validation[0] ?? null,
  };
}
