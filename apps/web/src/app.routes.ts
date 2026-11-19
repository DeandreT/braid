import { DOCUMENT } from "@angular/common";
import { Injector, inject, runInInjectionContext } from "@angular/core";
import {
  BaseRouteReuseStrategy,
  Router,
  type ActivatedRouteSnapshot,
  type CanActivateFn,
  type CanDeactivateFn,
  type GuardResult,
  type Route,
  type Routes,
  type RouterStateSnapshot,
} from "@angular/router";
import { UnsentWork } from "./workbench/unsent-work";
import { from, isObservable, of, switchMap, take, type Observable } from "rxjs";
import { BrowserSessionState } from "./auth/browser-session-state";
import { requireSignIn, signInEntry } from "./auth/sign-in-guards";
import { SignInPageComponent } from "./auth/sign-in-page.component";
import { CatalogShellComponent } from "./catalog/catalog-shell.component";
import { CatalogContextStore } from "./catalog/catalog-context";
import { OrganizationSelection } from "./catalog/organization-selection";
import { isCatalogId } from "./catalog/catalog-types";
import { CreateProjectPageComponent } from "./catalog/projects/create-project-page.component";
import type { EditMappingPageComponent } from "./catalog/metadata/edit-mapping-page.component";
import type { UploadMappingPageComponent } from "./catalog/uploads/upload-mapping-page.component";
import type { AppendRevisionPageComponent } from "./catalog/uploads/append-revision-page.component";
import type { OperationsPageComponent } from "./catalog/operations/operations-page.component";
import type { SandboxPageComponent } from "./catalog/sandbox/sandbox-page.component";
import type { AutomationPageComponent } from "./catalog/automation/automation-page.component";
import { NotFoundComponent } from "./not-found.component";
import type { ChatPageComponent } from "./chat/chat-page.component";
import type { RevisionPageComponent } from "./catalog/revision-page.component";
import type { SpecPageComponent } from "./catalog/specs/spec-page.component";
import type { CreationPageComponent } from "./catalog/creation/creation-page.component";

const leaveChat: CanDeactivateFn<ChatPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => component.requestLeave());
/** Auth entry replaces the mounted workspace; ordinary content navigation does not. */
const leaveWorkspaceEntry: CanActivateFn = () => requestAggregateLeave();
const enterSignIn: CanActivateFn = (route, state) =>
  afterGuard(signInEntry(route, state), requestAggregateLeave);

/** Guard arrays and ancestor/child activation are not a sequencing primitive.
 * Keep auth redirects ahead of the decision and selection in one leaf guard. */
function afterGuard(
  result: ReturnType<CanActivateFn>,
  next: () => ReturnType<CanActivateFn>,
): ReturnType<CanActivateFn> {
  const injector = inject(Injector);
  const proceed = (value: GuardResult) =>
    value === true ? runInInjectionContext(injector, next) : value;
  const observable = (value: ReturnType<CanActivateFn>) =>
    isObservable(value)
      ? value
      : typeof (value as Promise<unknown>)?.then === "function"
        ? from(value as Promise<GuardResult>)
        : of(value as GuardResult);
  if (
    isObservable(result) ||
    typeof (result as Promise<unknown>)?.then === "function"
  )
    return observable(result).pipe(
      take(1),
      switchMap((value) => observable(proceed(value))),
    );
  return proceed(result as GuardResult);
}
function requestAggregateLeave(): boolean {
  if (!inject(UnsentWork).present()) return true;
  try {
    return (
      inject(DOCUMENT).defaultView?.confirm?.(
        "Leave this workspace? Unsent work and recovery information kept only in this tab may be lost. A sent request may already have committed.",
      ) ?? false
    );
  } catch {
    return false;
  }
}
const leaveRevisionChat: CanDeactivateFn<RevisionPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => component.requestChatLeave());

const leaveMappingEditor: CanDeactivateFn<EditMappingPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => requestWorkspaceLeave(component));

const leaveMappingUpload: CanDeactivateFn<UploadMappingPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => requestWorkspaceLeave(component));

const leaveRevisionAppend: CanDeactivateFn<AppendRevisionPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => requestWorkspaceLeave(component));
const leaveOperations: CanDeactivateFn<OperationsPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => requestWorkspaceLeave(component));

const leaveAutomation: CanDeactivateFn<AutomationPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => requestWorkspaceLeave(component));
const leaveSandbox: CanDeactivateFn<SandboxPageComponent> = (
  component,
  _route,
  _state,
  next,
) => pageLeave(next, () => requestWorkspaceLeave(component));

function organizationChange(organizationId: string | null): boolean {
  const selection = inject(OrganizationSelection);
  const root = selection.rootOrganizationId();
  return (selection.organizationId() ?? root) !== (organizationId ?? root);
}

function routeOrganization(route: ActivatedRouteSnapshot): string | null {
  return (
    route.pathFromRoot
      .map((item) => item.paramMap.get("organizationId"))
      .find((value) => value !== null) ?? null
  );
}

/** Deactivation precedes activation. Defer only to a destination that owns the
 * aggregate decision, while every dirty source is still mounted and untouched. */
function pageLeave(
  next: RouterStateSnapshot,
  leave: () => boolean | Observable<boolean>,
): ReturnType<CanActivateFn> {
  let route = next.root;
  for (;;) {
    const child = route.children.find((item) => item.outlet === "primary");
    if (!child) break;
    route = child;
  }
  if (
    route.routeConfig?.canActivate?.some(
      (guard) =>
        guard === leaveWorkspaceEntry ||
        guard === enterSignIn ||
        guard === enterPrivateWorkspace,
    )
  )
    return true;
  if (!route.routeConfig?.canActivate?.includes(enterOrganization))
    return route.routeConfig?.canActivate?.includes(requireSignIn)
      ? afterGuard(requireSignIn(route, next), leave)
      : leave();
  // Even a same-organization destination may redirect to sign-in. Resolve that
  // before a page decision can discard work; the redirect target owns its prompt.
  return afterGuard(requireSignIn(route, next), () => {
    const organizationId = routeOrganization(route);
    if (
      organizationId !== null &&
      !isCatalogId(organizationId) &&
      !route.pathFromRoot.some((item) => item.routeConfig?.path === "catalog")
    )
      return true;
    return (
      organizationChange(
        organizationId !== null && !isCatalogId(organizationId)
          ? null
          : organizationId,
      ) || leave()
    );
  });
}

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

/**
 * Runs after the leave decision: the URL is the tab's authoritative organization, and
 * unqualified standalone routes are root-only. A malformed standalone selection never falls
 * back to the root; catalog pages report their own invalid links.
 */
const enterOrganization: CanActivateFn = (route, state) =>
  afterGuard(requireSignIn(route, state), () => selectOrganization(route));

function withOrganizationAdmission(route: Route): Route {
  return {
    ...route,
    canActivate: [
      route.canActivate?.includes(leaveWorkspaceEntry)
        ? enterPrivateWorkspace
        : enterOrganization,
    ],
  };
}

const enterPrivateWorkspace: CanActivateFn = (route, state) =>
  afterGuard(
    requireSignIn(route, state),
    () => requestAggregateLeave() && selectOrganization(route, true),
  );

function selectOrganization(
  route: ActivatedRouteSnapshot,
  confirmed = false,
): ReturnType<CanActivateFn> {
  const organizationId = routeOrganization(route);
  const valid = organizationId === null || isCatalogId(organizationId);
  if (
    !valid &&
    !route.pathFromRoot.some((item) => item.routeConfig?.path === "catalog")
  )
    return inject(Router).parseUrl("/catalog");
  const destination = valid ? organizationId : null;
  if (!confirmed && organizationChange(destination) && !requestAggregateLeave())
    return false;
  inject(CatalogContextStore).select(destination);
  return true;
}

/** A mounted page belongs to one organization: another organization's URL remounts it. */
export class OrganizationRouteReuseStrategy extends BaseRouteReuseStrategy {
  override shouldReuseRoute(
    future: ActivatedRouteSnapshot,
    current: ActivatedRouteSnapshot,
  ): boolean {
    return (
      super.shouldReuseRoute(future, current) &&
      future.params["organizationId"] === current.params["organizationId"]
    );
  }
}

/** One definition for the root-only routes and their organization-qualified forms. */
const standalone: Route[] = [
  {
    path: "retention",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./administration/retention-page.component").then(
        (m) => m.RetentionPageComponent,
      ),
    title: "Data retention · Braid",
  },
  {
    path: "quotas",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./administration/ai-quota-page.component").then(
        (m) => m.AiQuotaPageComponent,
      ),
    title: "AI quotas · Braid",
  },
  {
    path: "usage",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./administration/ai-usage-page.component").then(
        (m) => m.AiUsagePageComponent,
      ),
    title: "AI usage · Braid",
  },
  {
    path: "audit",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./administration/audit-page.component").then(
        (m) => m.AuditPageComponent,
      ),
    title: "Audit log · Braid",
  },
  {
    path: "chat/resources",
    loadComponent: () =>
      import("./chat/chat-resource-page.component").then(
        (module) => module.ChatResourcePageComponent,
      ),
    title: "Workspace reference · braid",
  },
  {
    path: "chat",
    loadComponent: () =>
      import("./chat/chat-page.component").then(
        (module) => module.ChatPageComponent,
      ),
    canDeactivate: [leaveChat],
    title: "Chat · braid",
  },
  {
    path: "notifications",
    loadComponent: () =>
      import("./catalog/automation/notification-centre.component").then(
        (m) => m.NotificationCentreComponent,
      ),
    title: "Notifications · braid",
  },
  {
    path: "administration",
    loadComponent: () =>
      import("./administration/administration-page.component").then(
        (m) => m.AdministrationPageComponent,
      ),
    title: "Administration · braid",
  },
].map(withOrganizationAdmission);

export const routes: Routes = [
  ...standalone,
  {
    path: "organizations/:organizationId/invitations",
    canActivate: [leaveWorkspaceEntry],
    loadComponent: () =>
      import("./administration/invitation-page.component").then(
        (m) => m.InvitationPageComponent,
      ),
    title: "Organization invitation · braid",
  },
  {
    path: "organizations/:organizationId",
    children: standalone,
  },
  {
    path: "invitations",
    canActivate: [leaveWorkspaceEntry],
    loadComponent: () =>
      import("./administration/invitation-page.component").then(
        (m) => m.InvitationPageComponent,
      ),
    title: "Organization invitation · braid",
  },
  { path: "", pathMatch: "full", redirectTo: "catalog" },
  {
    path: "sign-in",
    component: SignInPageComponent,
    canActivate: [enterSignIn],
    title: "Sign in · braid",
  },
  {
    path: "catalog",
    component: CatalogShellComponent,
    children: (
      [
        {
          path: "organizations/:organizationId/projects/:projectId/creation",
          loadComponent: () =>
            import("./catalog/creation/creation-library.component").then(
              (m) => m.CreationLibraryComponent,
            ),
          title: "Your new mapping proposals · braid",
        },
        {
          path: "organizations/:organizationId/projects/:projectId/creation/:proposalId",
          loadComponent: () =>
            import("./catalog/creation/creation-page.component").then(
              (m) => m.CreationPageComponent,
            ),
          canDeactivate: [
            (component: CreationPageComponent, _route, _state, next) =>
              pageLeave(next, () => requestWorkspaceLeave(component)),
          ],
          canActivate: [leaveWorkspaceEntry],
          runGuardsAndResolvers: "paramsOrQueryParamsChange",
          title: "Review new mapping · braid",
        },
        ...[
          "organizations/:organizationId/projects/:projectId/specs",
          "organizations/:organizationId/projects/:projectId/specs/:bundleId",
        ].map((path) => ({
          path,
          loadComponent: () =>
            import("./catalog/specs/spec-page.component").then(
              (m) => m.SpecPageComponent,
            ),
          canDeactivate: [
            (
              component: SpecPageComponent,
              _route: ActivatedRouteSnapshot,
              _state: RouterStateSnapshot,
              next: RouterStateSnapshot,
            ) => pageLeave(next, () => requestWorkspaceLeave(component)),
          ],
          canActivate: [leaveWorkspaceEntry],
          runGuardsAndResolvers: "paramsOrQueryParamsChange" as const,
          title: "Specification intake · braid",
        })),
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/sandbox",
          loadComponent: () =>
            import("./catalog/sandbox/sandbox-page.component").then(
              (module) => module.SandboxPageComponent,
            ),
          canDeactivate: [leaveSandbox],
          title: "Local sandbox · braid",
        },
        { path: "", pathMatch: "full" as const, children: [] },
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/operations",
          loadComponent: () =>
            import("./catalog/operations/operations-page.component").then(
              (module) => module.OperationsPageComponent,
            ),
          canDeactivate: [leaveOperations],
          title: "Operations · braid",
        },
        {
          path: "organizations/:organizationId/projects/:projectId/automation",
          canDeactivate: [leaveAutomation],
          loadComponent: () =>
            import("./catalog/automation/automation-page.component").then(
              (module) => module.AutomationPageComponent,
            ),
          title: "Automation and notifications · braid",
        },
        ...["search", "organizations/:organizationId/search"].map((path) => ({
          path,
          loadComponent: () =>
            import("./catalog/search/search-page.component").then(
              (module) => module.SearchPageComponent,
            ),
          title: "Search · braid",
        })),
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/new",
          loadComponent: () =>
            import("./catalog/uploads/upload-mapping-page.component").then(
              (module) => module.UploadMappingPageComponent,
            ),
          canDeactivate: [leaveMappingUpload],
          runGuardsAndResolvers: "paramsOrQueryParamsChange",
          title: "Upload mapping · braid",
        },
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/edit",
          loadComponent: () =>
            import("./catalog/metadata/edit-mapping-page.component").then(
              (module) => module.EditMappingPageComponent,
            ),
          canDeactivate: [leaveMappingEditor],
          runGuardsAndResolvers: "paramsOrQueryParamsChange",
          title: "Edit mapping metadata · braid",
        },
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/revisions/new",
          loadComponent: () =>
            import("./catalog/uploads/append-revision-page.component").then(
              (module) => module.AppendRevisionPageComponent,
            ),
          canDeactivate: [leaveRevisionAppend],
          runGuardsAndResolvers: "paramsOrQueryParamsChange",
          title: "Upload revision · braid",
        },
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/comparisons",
          loadComponent: () =>
            import("./catalog/comparison/revision-comparison-page.component").then(
              (module) => module.RevisionComparisonPageComponent,
            ),
          title: "Compare revisions · braid",
        },
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId/revisions/:revisionId",
          loadComponent: () =>
            import("./catalog/revision-page.component").then(
              (module) => module.RevisionPageComponent,
            ),
          title: "Revision · braid",
          canDeactivate: [leaveRevisionChat],
        },
        {
          path: "organizations/:organizationId/projects/:projectId/mappings/:mappingId",
          loadComponent: () =>
            import("./catalog/mapping-page.component").then(
              (module) => module.MappingPageComponent,
            ),
          title: "Mapping · braid",
        },
        {
          path: "organizations/:organizationId/projects/new",
          component: CreateProjectPageComponent,
          title: "Create project · braid",
        },
        {
          path: "organizations/:organizationId/projects/:projectId",
          loadComponent: () =>
            import("./catalog/project-page.component").then(
              (module) => module.ProjectPageComponent,
            ),
          title: "Project · braid",
        },
        {
          path: "organizations/:organizationId/projects",
          loadComponent: () =>
            import("./catalog/projects-page.component").then(
              (module) => module.ProjectsPageComponent,
            ),
          title: "Projects · braid",
        },
      ] satisfies Routes
    ).map(withOrganizationAdmission),
    title: "Catalog · braid",
  },
  {
    path: "help",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./help/help-page.component").then(
        (module) => module.HelpPageComponent,
      ),
    title: "Help · braid",
  },
  {
    path: "help/:sectionId",
    canActivate: [requireSignIn],
    loadComponent: () =>
      import("./help/help-page.component").then(
        (module) => module.HelpPageComponent,
      ),
    title: "Help · braid",
  },
  {
    path: "**",
    component: NotFoundComponent,
    title: "Page unavailable · braid",
  },
];
