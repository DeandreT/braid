import {
  DestroyRef,
  Injectable,
  computed,
  inject,
  signal,
} from "@angular/core";
import { BrowserSessionState } from "../auth/browser-session-state";
import { LayoutPreferencesStore } from "./layout-preferences-store";
import { CatalogContextStore } from "../catalog/catalog-context";
import type {
  CatalogContext,
  CatalogMapping,
  CatalogRevisionScope,
} from "../catalog/catalog-types";
import {
  emptyWorkbenchSelection,
  inspectorLabels,
  reduceWorkbenchSelection,
  workbenchScopeKey,
  type WorkbenchAction,
  type WorkbenchLayout,
} from "./workbench-model";

/** One authorized revision page owns this state. It is never provided at application scope. */
@Injectable()
export class WorkbenchState {
  private readonly current = signal(emptyWorkbenchSelection());
  private readonly preferences = inject(LayoutPreferencesStore);
  readonly selection = this.current.asReadonly();
  readonly layout = this.preferences.frame;
  readonly lifetime = computed(
    () => `${this.current().epoch}:${this.current().binding ?? ""}`,
  );
  readonly inspectorLabels = computed(() =>
    inspectorLabels(this.current().view),
  );
  private readonly sessions = inject(BrowserSessionState);

  constructor() {
    const context = inject(CatalogContextStore);
    const sessionChange = this.sessions.invalidated.subscribe(() =>
      this.clear(),
    );
    const catalogChange = context.invalidated.subscribe(() => this.clear());
    inject(DestroyRef).onDestroy(() => {
      sessionChange.unsubscribe();
      catalogChange.unsubscribe();
      this.clear();
    });
  }

  bind(
    context: CatalogContext,
    mapping: CatalogMapping,
    scope: CatalogRevisionScope,
    generation: number,
  ): void {
    const key = workbenchScopeKey(scope);
    if (
      !this.sessions.presents(context) ||
      !mapping.access.actions.view ||
      context.allowedOrganizationId !== scope.organizationId ||
      mapping.scope.organizationId !== scope.organizationId ||
      mapping.scope.projectId !== scope.projectId ||
      mapping.scope.mappingId !== scope.mappingId
    ) {
      this.clear();
      return;
    }
    this.dispatch({
      kind: "bind",
      binding: JSON.stringify([
        key,
        context.mode,
        context.actorPrincipalId,
        context.mode === "session" ? context.identityContext : "",
        generation,
        mapping.access,
      ]),
    });
  }

  dispatch(action: WorkbenchAction): void {
    this.current.update((value) => reduceWorkbenchSelection(value, action));
  }

  updateLayout(change: Partial<WorkbenchLayout>): void {
    this.preferences.updateFrame(change);
  }

  resetLayout(): void {
    this.preferences.reset();
  }

  clear(): void {
    this.dispatch({ kind: "clear" });
  }
}
