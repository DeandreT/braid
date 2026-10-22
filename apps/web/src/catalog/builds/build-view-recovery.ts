import {
  DestroyRef,
  Injectable,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { CatalogContextStore } from "../catalog-context";
import {
  catalogOwner,
  type CatalogMapping,
  type CatalogRevisionScope,
} from "../catalog-types";
import {
  sameOwner,
  sameRevision,
  type BuildOwner,
  type PendingBuildMutation,
} from "./build-recovery";

/**
 * Provided by the revision page, never the application: a metadata refresh or a failed session
 * check can replace its child without losing an uncertain request. Only a fresh context for
 * another owner, changed mapping access, another revision or page disposal clears the frame.
 */
@Injectable()
export class BuildViewRecovery {
  readonly pending = signal<PendingBuildMutation | null>(null);
  private binding: {
    owner: BuildOwner;
    access: string;
  } | null = null;
  constructor() {
    const contexts = inject(CatalogContextStore);
    effect(() => {
      const context = contexts.context();
      untracked(() => {
        if (
          context &&
          this.binding &&
          this.binding.owner.identity !== catalogOwner(context)
        )
          this.clear();
      });
    });
    inject(DestroyRef).onDestroy(() => this.clear());
  }
  bind(owner: BuildOwner, access: CatalogMapping["access"]): boolean {
    const key = JSON.stringify(access);
    const changed = !!(
      this.binding &&
      (!sameOwner(this.binding.owner, owner) || this.binding.access !== key)
    );
    if (changed) this.pending.set(null);
    this.binding = { owner, access: key };
    return changed;
  }
  routeScope(scope: CatalogRevisionScope | null): void {
    if (
      !scope ||
      (this.binding && !sameRevision(this.binding.owner.scope, scope))
    )
      this.clear();
  }
  clear(): void {
    this.pending.set(null);
    this.binding = null;
  }
}
