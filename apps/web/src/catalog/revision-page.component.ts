import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Router, RouterLink } from "@angular/router";
import {
  Observable,
  Subscription,
  catchError,
  combineLatest,
  forkJoin,
  of,
  switchMap,
} from "rxjs";
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
  CatalogRevisionPage,
} from "./catalog-types";
import {
  CatalogReadError,
  isCatalogId,
  sameAdmittedAccess,
} from "./catalog-types";
import { RevisionReportApi } from "./reports/revision-report-api";
import { revisionReportSelectors } from "./reports/revision-report-selection";
import {
  RevisionReportState,
  type RevisionReportAdmission,
  type RevisionReportSelectors,
} from "./reports/revision-report-state";
import { RevisionReportsComponent } from "./reports/revision-reports.component";
import { RevisionChatComponent } from "../chat/revision-chat.component";
import { ChatViewRecovery } from "../chat/chat-view-recovery";
import { RevisionPreviewComponent } from "./previews/revision-preview.component";
import { RevisionRegressionComponent } from "./regression/revision-regression.component";
import { RevisionBuildsComponent } from "./builds/revision-builds.component";
import { BuildViewRecovery } from "./builds/build-view-recovery";
import { RegressionViewRecovery } from "./regression/regression-view-recovery";
import { BrowserSessionState } from "../auth/browser-session-state";
import { WorkbenchState } from "../workbench/workbench-state";
import { WorkbenchRecents } from "../workbench/workbench-recents";
import {
  WorkbenchFrameComponent,
  type WorkbenchCommand,
} from "../workbench/workbench-frame.component";
import { WorkbenchEntityInspectorComponent } from "../workbench/entity-inspector.component";
import { WorkbenchSample } from "../workbench/workbench-sample";
import {
  affectedFields,
  compareDocuments,
  displayDocument,
  documentKey,
} from "../workbench/workbench-documents";
import type {
  NodeReference,
  ProjectionView,
} from "../mappings/projection-view";
import type { DockView } from "../workbench/workbench-model";
import { NativeEvidenceComponent } from "../mappings/native-evidence.component";
import { StatusChipComponent } from "../workbench/status-chip.component";
import { MetadataValueComponent } from "../workbench/metadata-value.component";
import { MappingEditorComponent } from "../edit/mapping-editor.component";
import {
  buildStatus,
  comparisonStatus,
  operationStatus,
  uncertainStatus,
} from "../workbench/presentation";

interface RevisionScreen {
  project: CatalogProject;
  mapping: CatalogMapping;
  revision: CatalogRevision;
}

@Component({
  selector: "weft-revision-page",
  imports: [
    RouterLink,
    CatalogStatusComponent,
    RevisionReportsComponent,
    RevisionChatComponent,
    RevisionPreviewComponent,
    RevisionRegressionComponent,
    RevisionBuildsComponent,
    WorkbenchFrameComponent,
    WorkbenchEntityInspectorComponent,
    NativeEvidenceComponent,
    StatusChipComponent,
    MetadataValueComponent,
    MappingEditorComponent,
  ],
  providers: [
    BuildViewRecovery,
    RegressionViewRecovery,
    ChatViewRecovery,
    WorkbenchState,
  ],
  templateUrl: "./revision-page.component.html",
  styleUrl: "./revision-page.component.css",
})
export class RevisionPageComponent extends CatalogRoutePage<
  RevisionScreen,
  CatalogRevisionScope
> {
  readonly workbench = inject(WorkbenchState);
  readonly editMode = signal(false);
  editedRevision(id: string): void {
    const mapping = this.data()?.mapping.scope;
    this.editMode.set(false);
    if (mapping)
      void this.workbenchRouter.navigateByUrl(
        this.routes.revision({ ...mapping, revisionId: id }),
      );
  }
  readonly comparisonStatus = comparisonStatus;
  readonly buildStatus = buildStatus;
  readonly operationStatus = operationStatus;
  readonly uncertainStatus = uncertainStatus;
  readonly investigation = computed(() => {
    const trail = this.workbench.selection().investigation;
    if (!trail) return null;
    const run = this.selectedRun()?.run,
      savedCase = this.selectedCase(),
      projection = this.projection();
    if (
      !run ||
      !savedCase ||
      !projection ||
      trail.run.runId !== run.runId ||
      trail.run.caseId !== savedCase.caseId ||
      trail.run.version !== savedCase.version ||
      !trail.path.every((reference) =>
        this.referenceExists(projection, reference),
      )
    )
      return null;
    return {
      runId: run.runId,
      name: savedCase.definition.name,
      path: trail.path.map((reference) => ({
        reference,
        label: projection.label(reference),
      })),
    };
  });
  private readonly recents = inject(WorkbenchRecents);
  readonly reportView = viewChild(RevisionReportsComponent);
  readonly regressionView = viewChild(RevisionRegressionComponent);
  readonly buildView = viewChild(RevisionBuildsComponent);
  readonly previewView = viewChild(RevisionPreviewComponent);
  readonly frame = viewChild(WorkbenchFrameComponent);
  private readonly sessions = inject(BrowserSessionState);
  private readonly workbenchRouter = inject(Router);
  readonly selectedDocument = signal<string | null>(null);
  readonly documentKey = documentKey;
  readonly diagnostic = signal<{ validation: boolean; ordinal: number } | null>(
    null,
  );
  readonly revisionHistory = signal<CatalogRevisionPage | null>(null);
  readonly historyBusy = signal(false);
  readonly historyError = signal<string | null>(null);
  private historyRequest: Subscription | null = null;
  private switcherRequest: Subscription | null = null;
  readonly switcherChoices = signal<readonly WorkbenchCommand[]>([]);
  readonly switcherPaths = new Map<string, string>();
  readonly projection = computed(
    () => this.reportView()?.workbenchView() ?? null,
  );
  readonly selectedCase = computed(() => {
    const component = this.regressionView(),
      value = component?.selected(),
      view = this.data();
    return component &&
      view &&
      value &&
      this.context() &&
      this.sessions.presents(this.context()!) &&
      component.context() === this.context() &&
      component.capabilities()?.readSamples &&
      sameRevision(value.revision, view.revision.scope)
      ? value
      : null;
  });
  readonly selectedRun = computed(() => {
    const component = this.regressionView(),
      value = component?.run(),
      view = this.data();
    return component &&
      view &&
      value &&
      this.context() &&
      this.sessions.presents(this.context()!) &&
      component.context() === this.context() &&
      component.capabilities()?.readSamples &&
      sameRevision(value.run.revision, view.revision.scope)
      ? value
      : null;
  });
  readonly sample = computed(() => {
    const value = this.selectedCase(),
      view = this.data();
    if (!value || !view || this.regressionView()?.editing()) return null;
    return WorkbenchSample.capture(
      view.revision.scope,
      value,
      this.selectedRun(),
      true,
    );
  });
  readonly inputDocuments = computed(() => {
    const value = this.selectedCase();
    return value
      ? [
          { name: null, bytesBase64Url: value.definition.primaryBase64Url },
          ...value.definition.namedInputs,
        ].map(displayDocument)
      : [];
  });
  readonly outputDocuments = computed(() =>
    (this.selectedRun()?.outputs ?? []).map(displayDocument),
  );
  readonly differences = computed(() => {
    const value = this.selectedCase(),
      run = this.selectedRun();
    return value &&
      run &&
      run.run.state === "completed" &&
      run.executionOutcome === "completed" &&
      run.previewStatus === "succeeded" &&
      value.caseId === run.run.caseId &&
      value.version === run.run.version
      ? compareDocuments(value.definition.expected, run.outputs ?? [])
      : [];
  });
  readonly commands = computed<readonly WorkbenchCommand[]>(() => {
    const value = this.data();
    if (!value) return [];
    return [
      { id: "project", label: value.project.name, detail: "Current project" },
      { id: "mapping", label: value.mapping.name, detail: "Current mapping" },
      ...(this.selectedCase()
        ? [
            {
              id: "selected-case",
              label: this.selectedCase()!.definition.name,
              detail: "Selected test case",
              view: "tests" as const,
            },
          ]
        : []),
      ...(value.mapping.access.actions.uploadRevisions
        ? [
            {
              id: "upload",
              label: "Upload version",
              detail: "Keep this saved version unchanged",
            },
          ]
        : []),
      ...this.switcherChoices(),
    ];
  });
  readonly dockContext = computed(() => {
    const value = this.selectedCase(),
      run = this.selectedRun(),
      revision = this.data()?.revision.sequence;
    return [
      value?.definition.name,
      run ? "Selected run" : null,
      revision ? `Version ${revision}` : null,
    ]
      .filter(Boolean)
      .join(" · ");
  });
  readonly diagnostics = computed(() => {
    const projection = this.reports.projection().response?.body.report;
    const validation = this.reports.validation().response?.body.report;
    return [
      ...(projection
        ? [
            {
              kind: "projection",
              label: "Explanation diagnostics",
              total: projection.diagnostics.length,
              truncated: false,
              items: projection.diagnostics.map((item) => ({
                ...item,
                reference: null as NodeReference | null,
              })),
            },
          ]
        : []),
      ...(validation
        ? [
            {
              kind: "validation",
              label: "Validation diagnostics",
              total: validation.totalIssueCount,
              truncated: validation.truncated,
              items: validation.diagnostics.map((item) => ({
                ...item,
                reference: this.reports.linkedValidation()
                  ? item.projectionLocator
                  : null,
              })),
            },
          ]
        : []),
    ];
  });
  private readonly contexts = inject(CatalogContextStore);
  private readonly buildRecovery = inject(BuildViewRecovery);
  private readonly regressionRecovery = inject(RegressionViewRecovery);
  private readonly chatRecovery = inject(ChatViewRecovery);
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
    admit: (admission) => this.contexts.admitted(admission.context),
  });

  constructor() {
    super();
    effect(() => {
      const context = this.context(),
        value = this.data(),
        generation = this.sessions.generation();
      untracked(() => {
        if (context && value) {
          this.workbench.bind(
            context,
            value.mapping,
            value.revision.scope,
            generation,
          );
          this.recents.remember(context, value.mapping.scope);
        } else this.workbench.clear();
      });
    });
    effect(() => {
      const component = this.regressionView(),
        value = this.selectedCase(),
        run = this.selectedRun(),
        cap = component?.capabilities();
      untracked(() => {
        const {
          binding,
          epoch,
          case: oldCase,
          run: oldRun,
        } = this.workbench.selection();
        if (!binding) return;
        if (cap && !cap.readSamples) {
          this.workbench.dispatch({
            kind: "case",
            binding,
            epoch,
            selection: null,
          });
          this.workbench.dispatch({
            kind: "run",
            binding,
            epoch,
            selection: null,
          });
        } else {
          if (!value && oldCase)
            this.workbench.dispatch({
              kind: "case",
              binding,
              epoch,
              selection: null,
            });
          if (!run && oldRun)
            this.workbench.dispatch({
              kind: "run",
              binding,
              epoch,
              selection: null,
            });
          if (
            value &&
            (oldCase?.caseId !== value.caseId ||
              oldCase.version !== value.version)
          )
            this.workbench.dispatch({
              kind: "case",
              binding,
              epoch,
              selection: { caseId: value.caseId, version: value.version },
            });
          if (run && oldRun?.runId !== run.run.runId)
            this.workbench.dispatch({
              kind: "run",
              binding,
              epoch,
              selection: {
                runId: run.run.runId,
                caseId: run.run.caseId,
                version: run.run.version,
              },
            });
        }
      });
    });
    effect(() => {
      this.workbench.lifetime();
      untracked(() => {
        this.selectedDocument.set(null);
        this.diagnostic.set(null);
        this.historyRequest?.unsubscribe();
        this.historyRequest = null;
        this.revisionHistory.set(null);
        this.historyBusy.set(false);
        this.historyError.set(null);
        this.switcherRequest?.unsubscribe();
        this.switcherRequest = null;
        this.switcherChoices.set([]);
        this.switcherPaths.clear();
      });
    });
    // Recovery providers keep unsent work across boundaries and clear it on a confirmed new owner.
    const invalidation = this.contexts.invalidated.subscribe(() =>
      this.reports.unbind(),
    );
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
        const scope = this.reportSelection()?.scope ?? null;
        this.buildRecovery.routeScope(scope);
        this.regressionRecovery.routeScope(scope);
        this.chatRecovery.routeScope(scope);
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
    this.reportDestroyRef.onDestroy(() => {
      this.reports.destroy();
      this.historyRequest?.unsubscribe();
      this.switcherRequest?.unsubscribe();
    });
  }

  inspect(reference: NodeReference | null): void {
    const value = this.projection(),
      { binding, epoch } = this.workbench.selection();
    if (!binding || !value) return;
    if (reference && !this.referenceExists(value, reference)) return;
    this.workbench.dispatch({
      kind: "entity",
      binding,
      epoch,
      entity: reference,
    });
    if (reference) {
      this.workbench.dispatch({ kind: "view", view: "mapping" });
      this.workbench.updateLayout({ inspectorCollapsed: false });
      this.frame()?.mobileInspector.set(true);
    }
  }

  private referenceExists(
    value: ProjectionView,
    reference: NodeReference,
  ): boolean {
    switch (reference.kind) {
      case "field":
        return value.fields.has(reference.fieldId);
      case "rule":
        return value.rules.has(reference.ruleId);
      case "boundary":
        return value.boundaries.has(reference.boundaryId);
      case "control":
        return value.controls.has(reference.controlId);
      case "parameter":
        return value.parameters.has(reference.parameterId);
    }
  }

  investigateField(fieldId: string): void {
    const run = this.selectedRun()?.run;
    const { binding, epoch } = this.workbench.selection();
    if (
      !binding ||
      !run ||
      !this.affectedFields().some((field) => field.fieldId === fieldId)
    )
      return;
    this.workbench.dispatch({
      kind: "investigate",
      binding,
      epoch,
      run: { runId: run.runId, caseId: run.caseId, version: run.version },
      entity: { kind: "field", fieldId },
    });
    this.inspect({ kind: "field", fieldId });
  }

  returnToTest(): void {
    if (!this.investigation()) return;
    this.workbench.dispatch({ kind: "view", view: "tests" });
    this.frame()?.mobileInspector.set(false);
    afterNextRender(
      () =>
        this.document
          .querySelector<HTMLElement>("#regression-result-title")
          ?.focus(),
      { injector: this.injector },
    );
  }

  showDiagnostic(value: { validation: boolean; ordinal: number }): void {
    this.diagnostic.set(value);
    this.workbench.dispatch({ kind: "dock", dock: "diagnostics" });
    this.workbench.updateLayout({ dockCollapsed: false });
    afterNextRender(
      () =>
        this.document
          .getElementById(
            `wb-diagnostic-${value.validation ? "validation" : "projection"}-${value.ordinal}`,
          )
          ?.focus(),
      { injector: this.injector },
    );
  }

  command(value: WorkbenchCommand): void {
    const view = this.data();
    if (!view) return;
    const url =
      value.id === "project"
        ? this.routes.project(view.project.scope)
        : value.id === "mapping"
          ? this.routes.mapping(view.mapping.scope)
          : value.id === "upload" && view.mapping.access.actions.uploadRevisions
            ? this.routes.appendRevision(view.mapping.scope)
            : (this.switcherPaths.get(value.id) ?? null);
    if (url) void this.workbenchRouter.navigateByUrl(url);
  }

  displayedInput() {
    return (
      this.inputDocuments().find(
        (value) => documentKey(value.name) === this.selectedDocument(),
      ) ??
      this.inputDocuments()[0] ??
      null
    );
  }
  displayedOutput() {
    return (
      this.outputDocuments().find(
        (value) => documentKey(value.name) === this.selectedDocument(),
      ) ??
      this.outputDocuments()[0] ??
      null
    );
  }
  displayedDifference() {
    return (
      this.differences().find(
        (value) => value.key === this.selectedDocument(),
      ) ??
      this.differences()[0] ??
      null
    );
  }
  affectedFields() {
    return affectedFields(this.displayedDifference(), this.projection());
  }

  openDock(dock: DockView): void {
    this.workbench.dispatch({ kind: "dock", dock });
    this.workbench.updateLayout({ dockCollapsed: false });
  }
  sampleMode(enabled: boolean): void {
    const { binding, epoch } = this.workbench.selection();
    if (binding && (!enabled || this.sample()))
      this.workbench.dispatch({ kind: "sample", binding, epoch, enabled });
  }

  loadSwitcher(): void {
    const context = this.context(),
      view = this.data(),
      lifetime = this.workbench.lifetime();
    if (!context || !view || !this.sessions.presents(context)) return;
    this.switcherRequest?.unsubscribe();
    const recent = this.recents
      .scopes(context)
      .map((scope) => this.api.mapping(scope).pipe(catchError(() => of(null))));
    // The reads wait for any pending access check.
    this.switcherRequest = this.contexts
      .admitted(context)
      .pipe(
        switchMap(() =>
          forkJoin({
            projects: this.api.projects(context.allowedOrganizationId),
            mappings: this.api.mappings(view.project.scope),
            recent: recent.length ? forkJoin(recent) : of([]),
          }),
        ),
      )
      .subscribe({
        next: ({ projects, mappings, recent }) => {
          if (this.workbench.lifetime() !== lifetime) return;
          this.switcherPaths.clear();
          const choices: WorkbenchCommand[] = [];
          for (const project of projects.projects) {
            const id = `project:${project.scope.projectId}`;
            this.switcherPaths.set(id, this.routes.project(project.scope));
            choices.push({ id, label: project.name, detail: "Project" });
          }
          for (const mapping of recent) {
            if (!mapping) continue;
            const id = `mapping:${mapping.scope.projectId}/${mapping.scope.mappingId}`;
            this.switcherPaths.set(id, this.routes.mapping(mapping.scope));
            choices.push({
              id,
              label: mapping.name,
              detail: "Recent mapping · access refreshed",
            });
          }
          for (const mapping of mappings.mappings) {
            const id = `mapping:${mapping.scope.projectId}/${mapping.scope.mappingId}`;
            if (this.switcherPaths.has(id)) continue;
            this.switcherPaths.set(id, this.routes.mapping(mapping.scope));
            choices.push({
              id,
              label: mapping.name,
              detail: "Mapping in current project",
            });
          }
          this.switcherChoices.set(choices);
        },
        error: () => {
          if (this.workbench.lifetime() === lifetime) {
            this.switcherChoices.set([]);
            this.switcherPaths.clear();
          }
        },
      });
  }

  loadRevisions(next = false): void {
    const view = this.data(),
      context = this.context(),
      lifetime = this.workbench.lifetime();
    if (
      !view ||
      !context ||
      !this.sessions.presents(context) ||
      this.historyBusy()
    )
      return;
    this.historyBusy.set(true);
    this.historyError.set(null);
    const cursor = next ? (this.revisionHistory()?.nextCursor ?? null) : null;
    // The read waits for any pending access check.
    this.historyRequest = this.contexts
      .admitted(context)
      .pipe(switchMap(() => this.api.revisions(view.mapping.scope, cursor)))
      .subscribe({
        next: (value) => {
          if (this.workbench.lifetime() === lifetime) {
            this.revisionHistory.set(value);
            this.historyBusy.set(false);
          }
        },
        error: () => {
          if (this.workbench.lifetime() === lifetime) {
            this.revisionHistory.set(null);
            this.historyBusy.set(false);
            this.historyError.set(
              "Revision history could not be read. Refresh current access before retrying.",
            );
          }
        },
      });
  }

  /** A same-owner re-read keeps an admission current while its revision and access are unchanged. */
  private isCurrentAdmission(admission: RevisionReportAdmission): boolean {
    const selection = this.reportSelection();
    const view = this.data();
    const context = this.context();
    return (
      selection !== null &&
      view !== null &&
      context !== null &&
      this.contexts.isCurrent(admission.context) &&
      this.contexts.isCurrent(context) &&
      sameAdmittedAccess({ context, mapping: view.mapping }, admission) &&
      sameRevision(view.revision.scope, admission.revision.scope) &&
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
      const operation = this.document.activeElement
        ?.closest("[data-report-operation]")
        ?.getAttribute("data-report-operation");
      const heading = operation
        ? operation === "projection"
          ? "saved-explanation-title"
          : "saved-validation-title"
        : this.document.activeElement
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
