import {
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { CatalogContextStore } from "./catalog-context";
import {
  catalogOwner,
  type CatalogContext,
  type CatalogMappingScope,
  type CatalogProjectScope,
  type CatalogRevisionScope,
} from "./catalog-types";

type RetainedScope =
  CatalogProjectScope | CatalogMappingScope | CatalogRevisionScope;

/**
 * One panel's unsent draft or uncertain request, kept in page memory while that panel is
 * suspended or replaced. A failed, negative or canceled session check confirms no new owner,
 * so it only suspends this work. A fresh context for another owner, another resource scope or the
 * page's disposal discards it. Subclasses are provided by the page, never the application.
 */
export abstract class RetainedViewWork<
  T,
  Scope extends RetainedScope = CatalogRevisionScope,
> {
  private readonly contexts = inject(CatalogContextStore);
  private readonly kept = signal<{
    readonly owner: string;
    readonly scope: Readonly<Scope>;
    readonly work: T;
  } | null>(null);
  readonly hasWork = computed(() => this.kept() !== null);

  constructor() {
    effect(() => {
      const context = this.contexts.context();
      if (context) untracked(() => this.confirm(context));
    });
    inject(DestroyRef).onDestroy(() => this.discard());
  }

  /** Suspends work under this context and resource scope, unless a fresh context already names another owner. */
  keep(context: CatalogContext, scope: Scope, work: T): void {
    const owner = catalogOwner(context),
      current = this.contexts.context();
    if (current && catalogOwner(current) !== owner) {
      this.discard();
      return;
    }
    this.kept.set({ owner, scope: Object.freeze({ ...scope }), work });
  }

  /** The suspended work, visible only to its own owner on its own resource scope. */
  peek(context: CatalogContext, scope: Scope): T | null {
    const kept = this.kept();
    return kept &&
      kept.owner === catalogOwner(context) &&
      sameScope(kept.scope, scope)
      ? kept.work
      : null;
  }

  /** Hands the work back after the panel's own fresh admission; it is no longer kept here. */
  take(context: CatalogContext, scope: Scope): T | null {
    const work = this.peek(context, scope);
    if (work !== null) this.kept.set(null);
    return work;
  }

  routeScope(scope: Scope | null): void {
    const kept = this.kept();
    if (kept && (!scope || !sameScope(kept.scope, scope))) this.discard();
  }

  discard(): void {
    this.kept.set(null);
  }

  private confirm(context: CatalogContext): void {
    const kept = this.kept();
    if (kept && kept.owner !== catalogOwner(context)) this.discard();
  }
}

function sameScope(left: RetainedScope, right: RetainedScope): boolean {
  return (
    left.organizationId === right.organizationId &&
    left.projectId === right.projectId &&
    ("mappingId" in left ? left.mappingId : null) ===
      ("mappingId" in right ? right.mappingId : null) &&
    ("revisionId" in left ? left.revisionId : null) ===
      ("revisionId" in right ? right.revisionId : null)
  );
}
