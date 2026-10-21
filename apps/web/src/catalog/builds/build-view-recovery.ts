import { DestroyRef, Injectable, inject, signal } from "@angular/core";
import { BrowserSessionState } from "../../auth/browser-session-state";
import type { CatalogMapping, CatalogRevisionScope } from "../catalog-types";
import {
  sameOwner,
  sameRevision,
  type BuildOwner,
  type PendingBuildMutation,
} from "./build-recovery";

/** Provided by the revision page, never the application: a metadata refresh can replace its child without losing an uncertain request. */
@Injectable()
export class BuildViewRecovery {
  readonly pending = signal<PendingBuildMutation | null>(null);
  private binding: {
    owner: BuildOwner;
    access: string;
    generation: number;
  } | null = null;
  constructor() {
    const subscription = inject(BrowserSessionState).invalidated.subscribe(() =>
      this.clear(),
    );
    inject(DestroyRef).onDestroy(() => {
      subscription.unsubscribe();
      this.clear();
    });
  }
  bind(
    owner: BuildOwner,
    access: CatalogMapping["access"],
    generation: number,
  ): boolean {
    const key = JSON.stringify(access);
    const changed = !!(
      this.binding &&
      (!sameOwner(this.binding.owner, owner) ||
        this.binding.access !== key ||
        this.binding.generation !== generation)
    );
    if (changed) this.pending.set(null);
    this.binding = { owner, access: key, generation };
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
