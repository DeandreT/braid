import { DestroyRef, effect, inject, signal, untracked } from "@angular/core";
import { CatalogContextStore } from "./catalog-context";
import {
  catalogOwner,
  type CatalogContext,
  type CatalogRevisionScope,
} from "./catalog-types";

/**
 * One panel's unsent draft or uncertain request, kept in page memory while that panel is
 * suspended or replaced. A failed, negative or canceled session check confirms no new owner,
 * so it only suspends this work. A fresh context for another owner, another revision or the
 * page's disposal discards it. Subclasses are provided by the page, never the application.
 */
export abstract class RetainedViewWork<T> {
  private readonly contexts = inject(CatalogContextStore);
  private readonly kept = signal<{
    readonly owner: string;
    readonly scope: Readonly<CatalogRevisionScope>;
    readonly work: T;
  } | null>(null);

  constructor() {
    effect(() => {
      const context = this.contexts.context();
      if (context) untracked(() => this.confirm(context));
    });
    inject(DestroyRef).onDestroy(() => this.discard());
  }

  /** Suspends work admitted under this context and revision, unless a fresh context already names another owner. */
  keep(context: CatalogContext, scope: CatalogRevisionScope, work: T): void {
    const owner = catalogOwner(context),
      current = this.contexts.context();
    if (current && catalogOwner(current) !== owner) {
      this.discard();
      return;
    }
    this.kept.set({ owner, scope: Object.freeze({ ...scope }), work });
  }

  /** The suspended work, visible only to its own owner on its own revision. */
  peek(context: CatalogContext, scope: CatalogRevisionScope): T | null {
    const kept = this.kept();
    return kept &&
      kept.owner === catalogOwner(context) &&
      sameRevision(kept.scope, scope)
      ? kept.work
      : null;
  }

  /** Hands the work back after the panel's own fresh admission; it is no longer kept here. */
  take(context: CatalogContext, scope: CatalogRevisionScope): T | null {
    const work = this.peek(context, scope);
    if (work !== null) this.kept.set(null);
    return work;
  }

  routeScope(scope: CatalogRevisionScope | null): void {
    const kept = this.kept();
    if (kept && (!scope || !sameRevision(kept.scope, scope))) this.discard();
  }

  discard(): void {
    this.kept.set(null);
  }

  private confirm(context: CatalogContext): void {
    const kept = this.kept();
    if (kept && kept.owner !== catalogOwner(context)) this.discard();
  }
}

function sameRevision(
  left: CatalogRevisionScope,
  right: CatalogRevisionScope,
): boolean {
  return (
    left.organizationId === right.organizationId &&
    left.projectId === right.projectId &&
    left.mappingId === right.mappingId &&
    left.revisionId === right.revisionId
  );
}
