import { DOCUMENT } from "@angular/common";
import { inject } from "@angular/core";
import { type CanDeactivateFn, type Routes } from "@angular/router";
import type { Observable } from "rxjs";
import { BrowserSessionState } from "./auth/browser-session-state";
import { requireSignIn, signInEntry } from "./auth/sign-in-guards";
import { SignInPageComponent } from "./auth/sign-in-page.component";
import { CatalogShellComponent } from "./catalog/catalog-shell.component";
import { CreateProjectPageComponent } from "./catalog/projects/create-project-page.component";
import type { EditMappingPageComponent } from "./catalog/metadata/edit-mapping-page.component";
import type { UploadMappingPageComponent } from "./catalog/uploads/upload-mapping-page.component";
import type { AppendRevisionPageComponent } from "./catalog/uploads/append-revision-page.component";
import { NotFoundComponent } from "./not-found.component";

const leaveMappingEditor: CanDeactivateFn<EditMappingPageComponent> = (
  component,
) => requestWorkspaceLeave(component);

const leaveMappingUpload: CanDeactivateFn<UploadMappingPageComponent> = (
  component,
) => requestWorkspaceLeave(component);

const leaveRevisionAppend: CanDeactivateFn<AppendRevisionPageComponent> = (
  component,
) => requestWorkspaceLeave(component);

function requestWorkspaceLeave(component: {
  requestLeave(): Observable<boolean>;
}): boolean | Observable<boolean> {
  if (inject(BrowserSessionState).visible()) return component.requestLeave();
  // The ordinary page decision is inside the hidden/inert workspace. This fixed
  // browser-owned decision reveals no owner, draft, receipt or recovery status.
  try {
    return (
      inject(DOCUMENT).defaultView?.confirm(
        "Leave this workspace view? Private in-tab work and recovery information may be lost. A previously sent request may already have committed.",
      ) ?? false
    );
  } catch {
    return false;
  }
}

export const routes: Routes = [
  {
    path: "administration",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./administration/administration-page.component").then(
        (m) => m.AdministrationPageComponent,
      ),
    title: "Administration · Weft",
  },
  {
    path: "invitations",
    loadComponent: () =>
      import("./administration/invitation-page.component").then(
        (m) => m.InvitationPageComponent,
      ),
    title: "Organization invitation · Weft",
  },
  { path: "", pathMatch: "full", redirectTo: "catalog" },
  {
    path: "sign-in",
    component: SignInPageComponent,
    canActivate: [signInEntry],
    title: "Sign in · Weft",
  },
  {
    path: "catalog",
    component: CatalogShellComponent,
    canActivate: [requireSignIn],
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
        path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/revisions/new",
        loadComponent: () =>
          import("./catalog/uploads/append-revision-page.component").then(
            (module) => module.AppendRevisionPageComponent,
          ),
        canDeactivate: [leaveRevisionAppend],
        runGuardsAndResolvers: "paramsOrQueryParamsChange",
        title: "Upload revision · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/comparisons",
        loadComponent: () =>
          import("./catalog/comparison/revision-comparison-page.component").then(
            (module) => module.RevisionComparisonPageComponent,
          ),
        title: "Compare revisions · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/revisions/:revisionId",
        loadComponent: () =>
          import("./catalog/revision-page.component").then(
            (module) => module.RevisionPageComponent,
          ),
        title: "Revision · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId",
        loadComponent: () =>
          import("./catalog/mapping-page.component").then(
            (module) => module.MappingPageComponent,
          ),
        title: "Mapping · Weft",
      },
      {
        path: "organizations/:organizationId/projects/new",
        component: CreateProjectPageComponent,
        title: "Create project · Weft",
      },
      {
        path: "organizations/:organizationId/projects/:projectId",
        loadComponent: () =>
          import("./catalog/project-page.component").then(
            (module) => module.ProjectPageComponent,
          ),
        title: "Project · Weft",
      },
      {
        path: "organizations/:organizationId/projects",
        loadComponent: () =>
          import("./catalog/projects-page.component").then(
            (module) => module.ProjectsPageComponent,
          ),
        title: "Projects · Weft",
      },
    ],
    title: "Catalog · Weft",
  },
  {
    path: "help",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./help/help-page.component").then(
        (module) => module.HelpPageComponent,
      ),
    title: "Help · Weft",
  },
  {
    path: "help/:sectionId",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./help/help-page.component").then(
        (module) => module.HelpPageComponent,
      ),
    title: "Help · Weft",
  },
  {
    path: "**",
    component: NotFoundComponent,
    title: "Page unavailable · Weft",
  },
];
