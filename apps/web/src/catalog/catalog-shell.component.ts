import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import {
  ActivatedRoute,
  NavigationEnd,
  Router,
  RouterLink,
  RouterOutlet,
  type ParamMap,
} from "@angular/router";
import {
  combineLatest,
  filter,
  startWith,
  take,
  type Observable,
  type Subscription,
} from "rxjs";
import {
  CatalogApi,
  catalogFailure,
  type OrganizationPage,
} from "./catalog-api";
import { BrowserSessionState } from "../auth/browser-session-state";
import { fileStatusLabel } from "../workbench/presentation";
import { CatalogContextStore } from "./catalog-context";
import { createCatalogRead } from "./catalog-state";
import { InitiatingControl } from "./initiating-control";
import { revisionReportSelectors } from "./reports/revision-report-selection";
import {
  catalogRoutes,
  isCatalogCursor,
  type CatalogFailureKind,
  isCatalogId,
  type CatalogMapping,
  type CatalogMappingScope,
  type CatalogScope,
} from "./catalog-types";

type CatalogStatusKind =
  | "idle"
  | "loading"
  | "ready"
  | "notFound"
  | "rejected"
  | "invalidLink"
  | "unavailable";
type CatalogPageKind = "projects" | "project" | "mapping" | "revision";

@Component({
  selector: "braid-catalog-status",
  imports: [RouterLink],
  template: `
    @switch (kind()) {
      @case ("idle") {
        <div class="catalog-state" role="status">Loading catalog…</div>
      }
      @case ("loading") {
        <div class="catalog-state" role="status">Loading catalog…</div>
      }
      @case ("notFound") {
        <section class="catalog-state catalog-state-error" role="alert">
          <h2>Not found</h2>
          <p>
            This view was not found or is no longer available to your workspace.
          </p>
          <button class="catalog-button" type="button" (click)="retry.emit()">
            Retry
          </button>
        </section>
      }
      @case ("rejected") {
        <section class="catalog-state catalog-state-error" role="alert">
          <h2>Access rejected</h2>
          <p>
            {{
              sessions.state().kind === "development"
                ? "Your Development identity could not access this workspace. Check your local workspace access, then retry."
                : "Your workspace access could not be confirmed. Retry to check access."
            }}
          </p>
          <button class="catalog-button" type="button" (click)="retry.emit()">
            Retry
          </button>
        </section>
      }
      @case ("invalidLink") {
        <section class="catalog-state catalog-state-error" role="alert">
          <h2>Invalid link</h2>
          <p>
            This catalog link is incomplete or invalid. Open the catalog to
            choose a project.
          </p>
          @if (firstPage()) {
            <a
              class="catalog-page-link"
              [routerLink]="firstPage()"
              [queryParams]="{}"
              >First page</a
            >
          }
          <a class="catalog-page-link" routerLink="/catalog">Open catalog</a>
        </section>
      }
      @case ("unavailable") {
        <section class="catalog-state catalog-state-error" role="alert">
          <h2>Catalog unavailable</h2>
          <p>
            We couldn’t load this view. Check that your workspace is available,
            then try again.
          </p>
          <button class="catalog-button" type="button" (click)="retry.emit()">
            Retry
          </button>
        </section>
      }
    }
  `,
})
export class CatalogStatusComponent {
  readonly sessions = inject(BrowserSessionState);
  readonly kind = input.required<CatalogStatusKind>();
  readonly firstPage = input<string | null>(null);
  readonly retry = output<void>();
}

/** Small route binding shared by the four read-only screens; transport owns admission and cancellation. */
export abstract class CatalogRoutePage<T, TScope extends CatalogScope> {
  protected readonly api = inject(CatalogApi);
  protected readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  readonly read = createCatalogRead<T>();
  /** The displayed view is being read again in place. */
  readonly refreshing = computed(() => this.read.refreshing());
  private readonly head = createCatalogRead<CatalogMapping>({
    refreshOnFocus: false,
  });
  readonly routes = catalogRoutes;
  readonly cursor = signal<string | null>(null);
  private readonly selectedScope = signal<TScope | null>(null);
  private headNavigationStarted = false;
  readonly kind = computed<CatalogStatusKind>(() => {
    const head = this.head.state();
    return head.kind === "idle"
      ? this.read.state().kind
      : head.kind === "ready"
        ? "loading"
        : head.kind;
  });
  readonly data = computed(() => {
    if (this.head.state().kind !== "idle") return null;
    const state = this.read.state();
    return state.kind === "ready" ? state.data : null;
  });
  readonly context = computed(() => {
    if (this.head.state().kind !== "idle") return null;
    const state = this.read.state();
    return state.kind === "ready" ? state.context : null;
  });
  readonly firstPage = computed(() => {
    const scope = this.selectedScope();
    if (!scope || !this.cursor() || "revisionId" in scope) return null;
    if ("mappingId" in scope) return catalogRoutes.mapping(scope);
    if ("projectId" in scope) return catalogRoutes.project(scope);
    return catalogRoutes.projects(scope.organizationId);
  });

  protected constructor() {
    effect(() => {
      if (this.read.state().kind === "loading") {
        this.head.clear();
        this.headNavigationStarted = false;
      }
    });
    effect(() => {
      const state = this.head.state();
      const selected = this.selectedScope();
      if (
        state.kind !== "ready" ||
        !selected ||
        !("mappingId" in selected) ||
        !sameMapping(state.data.scope, selected) ||
        this.headNavigationStarted
      )
        return;
      this.headNavigationStarted = true;
      // Navigating to the already selected concrete revision is ignored by the router.
      // Refresh that exact saved view instead of leaving the head action loading forever.
      if (
        "revisionId" in selected &&
        selected.revisionId === state.data.head.scope.revisionId
      ) {
        this.head.clear();
        this.read.retry();
        return;
      }
      void this.router.navigateByUrl(
        catalogRoutes.revision(state.data.head.scope),
      );
    });
  }

  protected bind(
    kind: CatalogPageKind,
    load: (scope: TScope, cursor: string | null) => Observable<T>,
  ): void {
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(([params, query]) => {
        this.head.clear();
        this.headNavigationStarted = false;
        const selection = routeSelection(kind, params, query);
        if (!selection) {
          this.cursor.set(null);
          this.selectedScope.set(null);
          this.read.invalidLink();
          return;
        }
        this.cursor.set(selection.cursor);
        const scope = selection.scope as TScope;
        this.selectedScope.set(scope);
        this.read.select(
          scope,
          () => load(scope, selection.cursor),
          selection.cursor,
        );
      });
  }

  retry(): void {
    this.head.clear();
    this.headNavigationStarted = false;
    this.read.retry();
  }

  viewCurrentHead(scope: CatalogMappingScope): void {
    const selected = this.selectedScope();
    if (
      !selected ||
      !("mappingId" in selected) ||
      !sameMapping(scope, selected)
    )
      return;
    this.headNavigationStarted = false;
    this.head.select(scope, () => this.api.mapping(scope));
  }

  roleLabel(role: string): string {
    return role.charAt(0).toUpperCase() + role.slice(1);
  }

  admissionLabel(admission: "valid" | "decodedInvalid"): string {
    return fileStatusLabel(admission);
  }
}

function sameMapping(a: CatalogMappingScope, b: CatalogScope): boolean {
  return (
    "projectId" in b &&
    "mappingId" in b &&
    a.organizationId === b.organizationId &&
    a.projectId === b.projectId &&
    a.mappingId === b.mappingId
  );
}

function routeSelection(
  kind: CatalogPageKind,
  params: ParamMap,
  query: ParamMap,
): { scope: CatalogScope; cursor: string | null } | null {
  const organizationId = params.get("organizationId");
  const projectId = params.get("projectId");
  const mappingId = params.get("mappingId");
  const revisionId = params.get("revisionId");
  if (!isCatalogId(organizationId)) return null;
  const canPage = kind !== "revision";
  if (
    canPage
      ? query.keys.some((key) => key !== "cursor")
      : revisionReportSelectors(query) === null
  )
    return null;
  const cursors = query.getAll("cursor");
  if (
    cursors.length > 1 ||
    (cursors.length === 1 && !isCatalogCursor(cursors[0]))
  )
    return null;
  const cursor = cursors[0] ?? null;
  if (kind === "projects") return { scope: { organizationId }, cursor };
  if (!isCatalogId(projectId)) return null;
  if (kind === "project")
    return { scope: { organizationId, projectId }, cursor };
  if (!isCatalogId(mappingId)) return null;
  if (kind === "mapping")
    return { scope: { organizationId, projectId, mappingId }, cursor };
  if (!isCatalogId(revisionId)) return null;
  return {
    scope: { organizationId, projectId, mappingId, revisionId },
    cursor,
  };
}

@Component({
  selector: "braid-catalog-shell",
  imports: [RouterLink, RouterOutlet, CatalogStatusComponent],
  providers: [InitiatingControl],
  templateUrl: "./catalog-shell.component.html",
})
export class CatalogShellComponent {
  readonly sessions = inject(BrowserSessionState);
  readonly contextStore = inject(CatalogContextStore);
  readonly routes = catalogRoutes;
  /** `/catalog` is the chooser of discovered admissions, never a business workspace. */
  readonly chooser = signal<Chooser>({ kind: "idle" });
  readonly landing = signal(false);
  private readonly api = inject(CatalogApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private discovery: Subscription | null = null;

  constructor() {
    inject(InitiatingControl);
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        startWith(null),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        const landing =
          this.route.snapshot.firstChild?.routeConfig?.path === "";
        // The initial emission and its NavigationEnd are one arrival: discover once.
        const entered = landing && !this.landing();
        this.landing.set(landing);
        if (entered) {
          this.discover();
        } else if (!landing) {
          this.discovery?.unsubscribe();
          this.chooser.set({ kind: "idle" });
        }
      });
    this.destroyRef.onDestroy(() => this.discovery?.unsubscribe());
  }

  retry(): void {
    if (this.landing()) this.discover();
  }

  more(): void {
    const state = this.chooser();
    if (state.kind === "ready" && state.next)
      this.discover(state.next, state.organizations);
  }

  retryAccess(): void {
    if (this.sessions.state().kind === "checking" || this.sessions.checking())
      return;
    // First reobserve identity and organization only. The mounted child still owns
    // its separate explicit complete resource admission, with no automatic write.
    this.contextStore
      .refresh()
      .pipe(take(1), takeUntilDestroyed(this.destroyRef))
      .subscribe({ error: () => {} });
  }

  private discover(
    cursor: string | null = null,
    earlier: readonly OrganizationChoice[] = [],
  ): void {
    this.discovery?.unsubscribe();
    if (cursor === null) this.chooser.set({ kind: "loading" });
    this.discovery = this.api.organizations(cursor).subscribe({
      next: (page) => {
        const organizations = [...earlier, ...page.organizations];
        const next = page.nextCursor ?? null;
        // Only complete discovery may treat one admission as the sole choice.
        if (next === null && organizations.length === 1)
          void this.router.navigateByUrl(
            catalogRoutes.projects(organizations[0].organizationId),
            { replaceUrl: true },
          );
        else this.chooser.set({ kind: "ready", organizations, next });
      },
      error: (error: unknown) =>
        this.chooser.set({ kind: catalogFailure(error).kind }),
    });
  }
}

type OrganizationChoice = OrganizationPage["organizations"][number];
type Chooser =
  | { kind: "idle" | "loading" | CatalogFailureKind }
  | {
      kind: "ready";
      organizations: readonly OrganizationChoice[];
      next: string | null;
    };
