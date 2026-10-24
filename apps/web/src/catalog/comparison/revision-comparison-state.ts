import { signal } from "@angular/core";
import {
  catchError,
  of,
  switchMap,
  take,
  throwError,
  type Observable,
  type Subscription,
} from "rxjs";
import {
  comparisonFailure,
  type RevisionComparisonApi,
} from "./revision-comparison-api";
import {
  ComparisonError,
  type ComparisonRequest,
  type RevisionComparison,
} from "./revision-comparison-types";

export type RevisionComparisonView =
  | { readonly phase: "idle" }
  | { readonly phase: "loading"; readonly request: ComparisonRequest }
  | {
      readonly phase: "ready";
      readonly request: ComparisonRequest;
      readonly comparison: RevisionComparison;
    }
  | {
      readonly phase: "error";
      readonly request: ComparisonRequest;
      readonly error: ComparisonError;
    };

/** One page owns one request at a time; every selection replaces and cancels the previous one. */
export class RevisionComparisonState {
  private readonly current = signal<RevisionComparisonView>({ phase: "idle" });
  private request: ComparisonRequest | null = null;
  private subscription: Subscription | null = null;
  private generation = 0;
  private destroyed = false;
  readonly view = this.current.asReadonly();

  /** `admit` waits for any pending access check and fails, unsent, when access is gone. */
  constructor(
    private readonly api: RevisionComparisonApi,
    private readonly admit: () => Observable<unknown> = () => of(undefined),
  ) {}

  /** Repeated selection of the same admitted reports is idempotent and starts no request. */
  select(request: ComparisonRequest | null): void {
    if (this.destroyed) return;
    if (request && this.request && sameRequest(request, this.request)) return;
    this.cancel();
    this.request = request;
    if (request) this.run();
    else this.current.set({ phase: "idle" });
  }

  retry(): void {
    if (this.request && !this.destroyed && this.current().phase !== "loading")
      this.run();
  }

  clear(): void {
    this.select(null);
  }

  destroy(): void {
    this.clear();
    this.destroyed = true;
  }

  /** Only a view of the reports the page currently displays is presented. */
  viewFor(request: ComparisonRequest | null): RevisionComparisonView {
    const view = this.current();
    return view.phase !== "idle" &&
      request !== null &&
      sameRequest(view.request, request)
      ? view
      : { phase: "idle" };
  }

  private run(): void {
    this.cancel();
    const request = this.request;
    if (!request) return;
    const generation = this.generation;
    this.current.set({ phase: "loading", request });
    let emitted = false;
    this.subscription = this.admit()
      .pipe(
        take(1),
        catchError(() => throwError(() => new ComparisonError("rejected"))),
        switchMap(() => this.api.compare(request).pipe(take(1))),
      )
      .subscribe({
        next: (comparison) => {
          emitted = true;
          if (generation === this.generation && !this.destroyed)
            this.current.set({ phase: "ready", request, comparison });
        },
        error: (error: unknown) => {
          if (generation === this.generation && !this.destroyed)
            this.current.set({
              phase: "error",
              request,
              error: comparisonFailure(error),
            });
        },
        complete: () => {
          if (!emitted && generation === this.generation && !this.destroyed)
            this.current.set({
              phase: "error",
              request,
              error: comparisonFailure(null),
            });
        },
      });
  }

  private cancel(): void {
    ++this.generation;
    this.subscription?.unsubscribe();
    this.subscription = null;
  }
}

/** Exact revision DTO identity is part of the late-reply guard; IDs alone are not enough. */
function sameRequest(a: ComparisonRequest, b: ComparisonRequest): boolean {
  return (["left", "right"] as const).every(
    (side) =>
      a[side].revision === b[side].revision &&
      a[side].projectionDerivationId === b[side].projectionDerivationId &&
      a[side].validationDerivationId === b[side].validationDerivationId,
  );
}
