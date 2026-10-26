import { DatePipe } from "@angular/common";
import { Component } from "@angular/core";
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
  selector: "weft-mapping-page",
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
