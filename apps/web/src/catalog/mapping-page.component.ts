import { DatePipe } from "@angular/common";
import { Component, DestroyRef, effect, inject } from "@angular/core";
import { WorkspaceConversation } from "../workbench/workspace-conversation";
import { RouterLink } from "@angular/router";
import { forkJoin } from "rxjs";
import { RegressionStatusComponent } from "./automation/regression-status.component";
import {
  CatalogRoutePage,
  CatalogStatusComponent,
} from "./catalog-shell.component";
import type {
  CatalogMapping,
  CatalogMappingScope,
  CatalogProject,
  CatalogRevisionPage,
} from "./catalog-types";

interface MappingScreen {
  project: CatalogProject;
  mapping: CatalogMapping;
  page: CatalogRevisionPage;
}

@Component({
  selector: "braid-mapping-page",
  imports: [
    DatePipe,
    RouterLink,
    CatalogStatusComponent,
    RegressionStatusComponent,
  ],
  templateUrl: "./mapping-page.component.html",
})
export class MappingPageComponent extends CatalogRoutePage<
  MappingScreen,
  CatalogMappingScope
> {
  constructor() {
    super();
    const workspace = inject(WorkspaceConversation);
    effect(() => {
      const view = this.data();
      workspace.viewLabel.set(
        view
          ? `${view.project.name} / ${view.mapping.name} · ${this.roleLabel(view.mapping.access.role)}`
          : null,
      );
    });
    inject(DestroyRef).onDestroy(() => workspace.viewLabel.set(null));
    this.bind("mapping", (scope, cursor) =>
      forkJoin({
        project: this.api.project({
          organizationId: scope.organizationId,
          projectId: scope.projectId,
        }),
        mapping: this.api.mapping(scope),
        page: this.api.revisions(scope, cursor),
      }),
    );
  }
}
