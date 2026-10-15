import { DatePipe } from "@angular/common";
import { Component } from "@angular/core";
import { RouterLink } from "@angular/router";
import { forkJoin } from "rxjs";
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
  imports: [DatePipe, RouterLink, CatalogStatusComponent],
  templateUrl: "./project-page.component.html",
})
export class ProjectPageComponent extends CatalogRoutePage<
  ProjectScreen,
  CatalogProjectScope
> {
  constructor() {
    super();
    this.bind("project", (scope, cursor) =>
      forkJoin({
        project: this.api.project(scope),
        page: this.api.mappings(scope, cursor),
      }),
    );
  }
}
