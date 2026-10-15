import { DatePipe } from "@angular/common";
import { Component } from "@angular/core";
import { RouterLink } from "@angular/router";
import { forkJoin } from "rxjs";
import {
  CatalogRoutePage,
  CatalogStatusComponent,
} from "./catalog-shell.component";
import type {
  CatalogMapping,
  CatalogProject,
  CatalogRevision,
  CatalogRevisionScope,
} from "./catalog-types";

interface RevisionScreen {
  project: CatalogProject;
  mapping: CatalogMapping;
  revision: CatalogRevision;
}

@Component({
  selector: "weft-revision-page",
  imports: [DatePipe, RouterLink, CatalogStatusComponent],
  templateUrl: "./revision-page.component.html",
})
export class RevisionPageComponent extends CatalogRoutePage<
  RevisionScreen,
  CatalogRevisionScope
> {
  constructor() {
    super();
    this.bind("revision", (scope) =>
      forkJoin({
        project: this.api.project({
          organizationId: scope.organizationId,
          projectId: scope.projectId,
        }),
        mapping: this.api.mapping({
          organizationId: scope.organizationId,
          projectId: scope.projectId,
          mappingId: scope.mappingId,
        }),
        revision: this.api.revision(scope),
      }),
    );
  }
}
