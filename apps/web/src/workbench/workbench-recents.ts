import { DestroyRef, Injectable, inject } from "@angular/core";
import { BrowserSessionState } from "../auth/browser-session-state";
import type {
  CatalogContext,
  CatalogMappingScope,
} from "../catalog/catalog-types";

/** Navigation identifiers only. Names and access are freshly read when the palette opens. */
@Injectable({ providedIn: "root" })
export class WorkbenchRecents {
  private readonly sessions = inject(BrowserSessionState);
  private owner: string | null = null;
  private items: CatalogMappingScope[] = [];

  constructor() {
    const subscription = this.sessions.invalidated.subscribe(() =>
      this.clear(),
    );
    inject(DestroyRef).onDestroy(() => {
      subscription.unsubscribe();
      this.clear();
    });
  }

  remember(context: CatalogContext, scope: CatalogMappingScope): void {
    if (
      !this.sessions.canDispatch(context) ||
      context.allowedOrganizationId !== scope.organizationId
    )
      return;
    const owner = this.identity(context);
    if (this.owner !== owner) {
      this.clear();
      this.owner = owner;
    }
    this.items = [
      {
        organizationId: scope.organizationId,
        projectId: scope.projectId,
        mappingId: scope.mappingId,
      },
      ...this.items.filter(
        (item) =>
          item.mappingId !== scope.mappingId ||
          item.projectId !== scope.projectId,
      ),
    ].slice(0, 6);
  }

  scopes(context: CatalogContext): readonly CatalogMappingScope[] {
    if (
      !this.sessions.canDispatch(context) ||
      this.owner !== this.identity(context)
    ) {
      this.clear();
      return [];
    }
    return this.items.map((item) => ({ ...item }));
  }

  private identity(context: CatalogContext): string {
    return JSON.stringify([
      context.allowedOrganizationId,
      context.actorPrincipalId,
      context.mode,
      context.mode === "session" ? context.identityContext : "",
      this.sessions.generation(),
    ]);
  }
  private clear(): void {
    this.owner = null;
    this.items = [];
  }
}
