import { DOCUMENT, DatePipe } from "@angular/common";
import {
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { RouterLink } from "@angular/router";
import { Observable, combineLatest, forkJoin } from "rxjs";
import { CatalogContextStore } from "./catalog-context";
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
import { CatalogReadError, isCatalogId } from "./catalog-types";
import { RevisionReportApi } from "./reports/revision-report-api";
import { revisionReportSelectors } from "./reports/revision-report-selection";
import {
  RevisionReportState,
  type RevisionReportAdmission,
  type RevisionReportSelectors,
} from "./reports/revision-report-state";
import { RevisionReportsComponent } from "./reports/revision-reports.component";
import { RevisionPreviewComponent } from "./previews/revision-preview.component";
import { RevisionRegressionComponent } from "./regression/revision-regression.component";
import { RevisionBuildsComponent } from "./builds/revision-builds.component";
import { BuildViewRecovery } from "./builds/build-view-recovery";

interface RevisionScreen {
  project: CatalogProject;
  mapping: CatalogMapping;
  revision: CatalogRevision;
}

@Component({
  selector: "weft-revision-page",
  imports: [
    DatePipe,
    RouterLink,
    CatalogStatusComponent,
    RevisionReportsComponent,
    RevisionPreviewComponent,
    RevisionRegressionComponent,
    RevisionBuildsComponent,
  ],
  providers: [BuildViewRecovery],
  templateUrl: "./revision-page.component.html",
})
export class RevisionPageComponent extends CatalogRoutePage<
  RevisionScreen,
  CatalogRevisionScope
> {
  private readonly contexts = inject(CatalogContextStore);
  private readonly buildRecovery = inject(BuildViewRecovery);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  private readonly reportDestroyRef = inject(DestroyRef);
  private readonly reportSelection = signal<{
    scope: CatalogRevisionScope;
    selectors: RevisionReportSelectors;
  } | null>(null);
  readonly reports = new RevisionReportState(inject(RevisionReportApi), {
    refresh: () => this.refreshReportAdmission(),
    isCurrent: (admission) => this.isCurrentAdmission(admission),
  });

  constructor() {
    super();
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.reports.unbind();
      this.buildRecovery.clear();
    });
    effect(() => {
      this.contexts.identityVersion();
      untracked(() => this.buildRecovery.clear());
    });
    this.reportDestroyRef.onDestroy(() => invalidation.unsubscribe());
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
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        // Route changes always cancel actions, including an admission refresh in progress.
        this.reports.unbind();
        const selectors = revisionReportSelectors(query);
        const organizationId = params.get("organizationId");
        const projectId = params.get("projectId");
        const mappingId = params.get("mappingId");
        const revisionId = params.get("revisionId");
        this.reportSelection.set(
          selectors &&
            isCatalogId(organizationId) &&
            isCatalogId(projectId) &&
            isCatalogId(mappingId) &&
            isCatalogId(revisionId)
            ? {
                scope: { organizationId, projectId, mappingId, revisionId },
                selectors,
              }
            : null,
        );
        this.buildRecovery.routeScope(this.reportSelection()?.scope ?? null);
      });
    effect(() => {
      const selection = this.reportSelection();
      const view = this.data();
      const context = this.context();
      const state = this.read.state();
      const refreshing = this.reports.refreshingAdmission();
      untracked(() => {
        if (!selection) this.reports.unbind();
        else if (
          view &&
          context &&
          sameRevision(selection.scope, view.revision.scope)
        ) {
          if (!refreshing)
            this.reports.bind(
              { context, mapping: view.mapping, revision: view.revision },
              selection.selectors,
            );
        } else if (!refreshing || state.kind !== "loading")
          this.reports.unbind();
      });
    });
    this.reportDestroyRef.onDestroy(() => this.reports.destroy());
  }

  private isCurrentAdmission(admission: RevisionReportAdmission): boolean {
    const selection = this.reportSelection();
    const view = this.data();
    return (
      selection !== null &&
      this.contexts.context() === admission.context &&
      this.context() === admission.context &&
      view?.mapping === admission.mapping &&
      view.revision === admission.revision &&
      sameRevision(selection.scope, admission.revision.scope)
    );
  }

  /** Reuse the page's one metadata/context refresh; report requests never compete for admission. */
  private refreshReportAdmission(): Observable<RevisionReportAdmission> {
    return new Observable((subscriber) => {
      const selection = this.reportSelection();
      if (!selection) {
        subscriber.error(new CatalogReadError("invalidLink"));
        return;
      }
      const previous = this.read.state();
      const heading = this.document.activeElement
        ?.closest(".report-panel")
        ?.getAttribute("aria-labelledby");
      const watcher = effect(
        () => {
          const state = this.read.state();
          if (this.reportSelection() !== selection) {
            subscriber.error(new CatalogReadError("notFound"));
            return;
          }
          if (
            state === previous ||
            state.kind === "idle" ||
            state.kind === "loading"
          )
            return;
          if (state.kind !== "ready") {
            subscriber.error(new CatalogReadError(state.kind));
            return;
          }
          if (!sameRevision(selection.scope, state.data.revision.scope)) {
            subscriber.error(new CatalogReadError("notFound"));
            return;
          }
          subscriber.next({
            context: state.context,
            mapping: state.data.mapping,
            revision: state.data.revision,
          });
          subscriber.complete();
          if (
            heading === "saved-explanation-title" ||
            heading === "saved-validation-title"
          ) {
            // The access refresh replaces private content, including the initiating button.
            // Restore its report heading only when that removal left focus on the body.
            afterNextRender(
              () => {
                if (
                  !this.reportDestroyRef.destroyed &&
                  this.reportSelection() === selection &&
                  this.document.activeElement === this.document.body
                )
                  this.document.getElementById(heading)?.focus();
              },
              { injector: this.injector },
            );
          }
        },
        { injector: this.injector },
      );
      this.read.retry();
      return () => watcher.destroy();
    });
  }
}

function sameRevision(
  a: CatalogRevisionScope,
  b: CatalogRevisionScope,
): boolean {
  return (
    a.organizationId === b.organizationId &&
    a.projectId === b.projectId &&
    a.mappingId === b.mappingId &&
    a.revisionId === b.revisionId
  );
}
