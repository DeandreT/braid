import { DatePipe } from "@angular/common";
import { Component } from "@angular/core";
import { RouterLink } from "@angular/router";
import {
  CatalogRoutePage,
  CatalogStatusComponent,
} from "./catalog-shell.component";
import type { CatalogProjectPage } from "./catalog-types";

@Component({
  selector: "braid-projects-page",
  imports: [DatePipe, RouterLink, CatalogStatusComponent],
  templateUrl: "./projects-page.component.html",
})
export class ProjectsPageComponent extends CatalogRoutePage<
  CatalogProjectPage,
  { organizationId: string }
> {
  constructor() {
    super();
    this.bind("projects", (scope, cursor) =>
      this.api.projects(scope.organizationId, cursor),
    );
  }
}
