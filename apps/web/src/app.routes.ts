import { type CanDeactivateFn, type Routes } from "@angular/router";
import { CatalogShellComponent } from "./catalog/catalog-shell.component";
import { MappingPageComponent } from "./catalog/mapping-page.component";
import { ProjectPageComponent } from "./catalog/project-page.component";
import { ProjectsPageComponent } from "./catalog/projects-page.component";
import { RevisionPageComponent } from "./catalog/revision-page.component";
import { CreateProjectPageComponent } from "./catalog/projects/create-project-page.component";
import type { EditMappingPageComponent } from "./catalog/metadata/edit-mapping-page.component";
import type { UploadMappingPageComponent } from "./catalog/uploads/upload-mapping-page.component";
import { ExamplesComponent } from "./examples.component";
import { NotFoundComponent } from "./not-found.component";

const leaveMappingEditor: CanDeactivateFn<EditMappingPageComponent> = (
  component,
) => component.requestLeave();

const leaveMappingUpload: CanDeactivateFn<UploadMappingPageComponent> = (
  component,
) => component.requestLeave();

export const routes: Routes = [
  { path: "", pathMatch: "full", redirectTo: "examples" },
  { path: "examples", component: ExamplesComponent, title: "Examples · Weft" },
  {
    path: "catalog",
    component: CatalogShellComponent,
    children: [
      {
        path: "organizations/:organizationId/projects/:projectId/mappings/new",
        loadComponent: () =>
          import("./catalog/uploads/upload-mapping-page.component").then(
            (module) => module.UploadMappingPageComponent,
          ),
        canDeactivate: [leaveMappingUpload],
        runGuardsAndResolvers: "paramsOrQueryParamsChange",
        title: "Upload mapping · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/edit",
        loadComponent: () =>
          import("./catalog/metadata/edit-mapping-page.component").then(
            (module) => module.EditMappingPageComponent,
          ),
        canDeactivate: [leaveMappingEditor],
        runGuardsAndResolvers: "paramsOrQueryParamsChange",
        title: "Edit mapping metadata · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/revisions/:revisionId",
        component: RevisionPageComponent,
        title: "Revision · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId",
        component: MappingPageComponent,
        title: "Mapping · Weft",
      },
      {
        path: "organizations/:organizationId/projects/new",
        component: CreateProjectPageComponent,
        title: "Create project · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId",
        component: ProjectPageComponent,
        title: "Project · Weft",
      },
      {
        path: "organizations/:organizationId/projects",
        component: ProjectsPageComponent,
        title: "Projects · Weft",
      },
    ],
    title: "Catalog · Weft",
  },
  {
    path: "**",
    component: NotFoundComponent,
    title: "Page unavailable · Weft",
  },
];
