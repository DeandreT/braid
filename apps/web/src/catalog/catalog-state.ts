import { DOCUMENT } from "@angular/common";
import { DestroyRef, computed, inject, signal } from "@angular/core";
import {
  EMPTY,
  defer,
  finalize,
  map,
  switchMap,
  type Observable,
  type Subscription,
} from "rxjs";
import { catalogFailure } from "./catalog-api";
import { CatalogContextStore } from "./catalog-context";
import {
  CatalogReadError,
  isCatalogCursor,
  snapshotCatalogScope,
  type CatalogContext,
  type CatalogReadState,
  type CatalogScope,
} from "./catalog-types";

export type { CatalogReadState } from "./catalog-types";

type Selection<T> = {
  scope: CatalogScope | null;
  cursor: string | null;
  load: (context: CatalogContext) => Observable<T>;
};

/**
 * A controller belongs to one routed screen; every attempt replaces its previous selection.
 * A new selection starts empty. Reading the displayed selection again (Refresh, Retry, returning
 * to the tab) keeps its ready view in place until the fresh read settles.
 */
export class CatalogRead<T> {
  private readonly current = signal<CatalogReadState<T>>({ kind: "idle" });
  private readonly reloading = signal(false);
  readonly state = computed<CatalogReadState<T>>(() => {
    const state = this.current();
    return state.kind === "ready" && !this.contexts.isCurrent(state.context)
      ? { kind: "unavailable" }
      : state;
  });
  /** A fresh read of the displayed selection is in flight. */
  readonly refreshing = computed(
    () => this.reloading() && this.state().kind === "ready",
  );
  private selection: Selection<T> | null = null;
  private subscription: Subscription | null = null;
  private generation = 0;
  private destroyed = false;
  private identityVersion: number;
  private readonly invalidation: Subscription;

  constructor(
    private readonly contexts: CatalogContextStore,
    private readonly validCursor: (
      value: unknown,
    ) => value is string = isCatalogCursor,
  ) {
    this.identityVersion = contexts.identityVersion();
    this.invalidation = contexts.invalidated.subscribe(() => {
      this.cancel();
      this.current.set({ kind: "unavailable" });
    });
  }

  select(
    scope: CatalogScope | null,
    load: (context: CatalogContext) => Observable<T>,
    cursor: string | null = null,
  ): void {
    this.cancel();
    if (this.destroyed) return;
    try {
      if (cursor !== null && !this.validCursor(cursor))
        throw new CatalogReadError("invalidLink");
      this.selection = {
        scope: scope === null ? null : snapshotCatalogScope(scope),
        cursor,
        load,
      };
    } catch {
      this.selection = null;
      this.contexts.clear();
      this.current.set({ kind: "invalidLink" });
      return;
    }
    this.run(false);
  }

  retry(): void {
    if (this.selection && !this.destroyed) this.run(true);
  }

  clear(): void {
    this.cancel();
    this.selection = null;
    this.current.set({ kind: "idle" });
  }

  invalidLink(): void {
    this.cancel();
    this.selection = null;
    this.contexts.clear();
    this.current.set({ kind: "invalidLink" });
  }

  destroy(): void {
    this.invalidation.unsubscribe();
    this.destroyed = true;
    this.clear();
  }

  private cancel(): void {
    ++this.generation;
    this.subscription?.unsubscribe();
    this.subscription = null;
    this.reloading.set(false);
  }

  private run(inPlace: boolean): void {
    this.cancel();
    const selection = this.selection;
    if (!selection || this.destroyed) return;
    const generation = this.generation;
    // The displayed selection stays readable while it is read again; another one starts empty.
    if (!inPlace) this.contexts.beginSelection();
    if (!inPlace || this.state().kind !== "ready")
      this.current.set({ kind: "loading" });
    this.reloading.set(true);
    this.subscription = this.contexts
      .refresh()
      .pipe(
        switchMap((context) => {
          if (generation !== this.generation)
            throw new CatalogReadError("unavailable");
          const identityVersion = this.contexts.identityVersion();
          const identityChanged = this.identityVersion !== identityVersion;
          this.identityVersion = identityVersion;
          if (identityChanged && selection.cursor !== null) {
            this.selection = null;
            this.current.set({ kind: "invalidLink" });
            return EMPTY;
          }
          if (
            selection.scope &&
            selection.scope.organizationId !== context.allowedOrganizationId
          )
            throw new CatalogReadError("notFound");
          return defer(() => selection.load(context)).pipe(
            map((data) => ({ data, context })),
          );
        }),
        finalize(() => {
          if (generation === this.generation) this.reloading.set(false);
        }),
      )
      .subscribe({
        next: ({ data, context }) => {
          if (
            !this.destroyed &&
            generation === this.generation &&
            selection === this.selection
          ) {
            this.current.set(
              this.contexts.isCurrent(context)
                ? { kind: "ready", data, context }
                : { kind: "unavailable" },
            );
          }
        },
        error: (error: unknown) => {
          if (
            !this.destroyed &&
            generation === this.generation &&
            selection === this.selection
          )
            this.current.set({ kind: catalogFailure(error).kind });
        },
      });
  }
}

/** Call within a component's injection context; no polling or listeners survive its lifetime. */
export function createCatalogRead<T>(
  options: {
    refreshOnFocus?: boolean;
    cursorValidator?: (value: unknown) => value is string;
  } = {},
): CatalogRead<T> {
  const contexts = inject(CatalogContextStore);
  const read = new CatalogRead<T>(contexts, options.cursorValidator);
  const destroyRef = inject(DestroyRef);
  const document = inject(DOCUMENT);
  const window = document.defaultView;
  let focusTimer: ReturnType<typeof setTimeout> | null = null;
  const invalidation = contexts.invalidated.subscribe(() => {
    if (focusTimer !== null) clearTimeout(focusTimer);
    focusTimer = null;
  });
  const refresh = () => {
    if (document.visibilityState !== "visible" || focusTimer !== null) return;
    focusTimer = setTimeout(() => {
      focusTimer = null;
      if (document.visibilityState === "visible") read.retry();
    }, 100);
  };
  if (options.refreshOnFocus !== false) {
    window?.addEventListener("focus", refresh);
    document.addEventListener("visibilitychange", refresh);
  }
  destroyRef.onDestroy(() => {
    invalidation.unsubscribe();
    window?.removeEventListener("focus", refresh);
    document.removeEventListener("visibilitychange", refresh);
    if (focusTimer !== null) clearTimeout(focusTimer);
    read.destroy();
  });
  return read;
}
