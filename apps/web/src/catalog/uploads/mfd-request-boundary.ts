import {
  catchError,
  map,
  throwError,
  type MonoTypeOperatorFunction,
} from "rxjs";
import type { CatalogContext } from "../catalog-types";

export interface MfdRequestBoundary {
  generation(): number;
  accepts(context: CatalogContext): boolean;
  rejectProtectedRequest(generation: number, context: CatalogContext): void;
}

/** A late response can neither publish private data nor invalidate a newer identity. */
export function mfdProtectedResponse<T>(
  boundary: MfdRequestBoundary,
  context: CatalogContext,
  generation: number,
  status: (error: unknown) => number | null,
): MonoTypeOperatorFunction<T> {
  const current = () =>
    boundary.generation() === generation && boundary.accepts(context);
  const stale = () =>
    new Error("The MFD request's identity is no longer current.");
  return (source) =>
    source.pipe(
      map((value) => {
        if (!current()) throw stale();
        return value;
      }),
      catchError((error: unknown) => {
        if (!current()) return throwError(stale);
        const code = status(error);
        if (context.mode === "session" && (code === 401 || code === 403))
          boundary.rejectProtectedRequest(generation, context);
        return throwError(() => error);
      }),
    );
}
