import { DatePipe } from "@angular/common";
import { Component, DestroyRef, effect, inject } from "@angular/core";
import { WorkspaceConversation } from "../workbench/workspace-conversation";
import { CreationEntryComponent } from "./creation/creation-entry.component";
import { RouterLink } from "@angular/router";
import { forkJoin } from "rxjs";
import { RegressionStatusComponent } from "./automation/regression-status.component";
import {
  CatalogRoutePage,
  CatalogStatusComponent,
} from "./catalog-shell.component";
import type {
  CatalogMappingPage,
  CatalogProject,
  CatalogProjectScope,
} from "./catalog-types";

interface ProjectScreen {
  project: CatalogProject;
  page: CatalogMappingPage;
}

@Component({
  selector: "weft-project-page",
  imports: [
    DatePipe,
    RouterLink,
    CatalogStatusComponent,
    RegressionStatusComponent,
    CreationEntryComponent,
  ],
  templateUrl: "./project-page.component.html",
})
export class ProjectPageComponent extends CatalogRoutePage<
  ProjectScreen,
  CatalogProjectScope
> {
  constructor() {
    super();
    const workspace = inject(WorkspaceConversation);
    effect(() => {
      const view = this.data();
      workspace.viewLabel.set(
        view
          ? `${view.project.name} · ${this.roleLabel(view.project.access.role)}`
          : null,
      );
    });
    inject(DestroyRef).onDestroy(() => workspace.viewLabel.set(null));
    this.bind("project", (scope, cursor) =>
      forkJoin({
        project: this.api.project(scope),
        page: this.api.mappings(scope, cursor),
      }),
    );
  }
}
