import { DOCUMENT } from "@angular/common";
import { DestroyRef, inject, signal } from "@angular/core";
import {
  EMPTY,
  defer,
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

/** A controller belongs to one routed screen; every attempt replaces its previous selection. */
export class CatalogRead<T> {
  private readonly current = signal<CatalogReadState<T>>({ kind: "idle" });
  readonly state = this.current.asReadonly();
  private selection: Selection<T> | null = null;
  private subscription: Subscription | null = null;
  private generation = 0;
  private destroyed = false;
  private identityVersion: number;

  constructor(private readonly contexts: CatalogContextStore) {
    this.identityVersion = contexts.identityVersion();
  }

  select(
    scope: CatalogScope | null,
    load: (context: CatalogContext) => Observable<T>,
    cursor: string | null = null,
  ): void {
    this.cancel();
    if (this.destroyed) return;
    try {
      if (cursor !== null && !isCatalogCursor(cursor))
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
    this.run();
  }

  retry(): void {
    if (this.selection && !this.destroyed) this.run();
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
    this.destroyed = true;
    this.clear();
  }

  private cancel(): void {
    ++this.generation;
    this.subscription?.unsubscribe();
    this.subscription = null;
  }

  private run(): void {
    this.cancel();
    const selection = this.selection;
    if (!selection || this.destroyed) return;
    const generation = this.generation;
    this.current.set({ kind: "loading" });
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
      )
      .subscribe({
        next: ({ data, context }) => {
          if (
            !this.destroyed &&
            generation === this.generation &&
            selection === this.selection
          ) {
            this.current.set(
              this.contexts.context() === context
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
  options: { refreshOnFocus?: boolean } = {},
): CatalogRead<T> {
  const read = new CatalogRead<T>(inject(CatalogContextStore));
  const destroyRef = inject(DestroyRef);
  const document = inject(DOCUMENT);
  const window = document.defaultView;
  let focusTimer: ReturnType<typeof setTimeout> | null = null;
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
    window?.removeEventListener("focus", refresh);
    document.removeEventListener("visibilitychange", refresh);
    if (focusTimer !== null) clearTimeout(focusTimer);
    read.destroy();
  });
  return read;
}
