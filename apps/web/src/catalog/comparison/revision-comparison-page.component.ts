import { DOCUMENT, DatePipe } from "@angular/common";
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
import { RouterLink, type Params } from "@angular/router";
import {
  catchError,
  combineLatest,
  forkJoin,
  of,
  throwError,
  type Observable,
} from "rxjs";
import type { components } from "../../api/generated";
import { NativeEvidenceComponent } from "../../mappings/native-evidence.component";
import { MappingProjectionComponent } from "../../mappings/projection.component";
import { ProjectionView } from "../../mappings/projection-view";
import { CatalogContextStore } from "../catalog-context";
import {
  CatalogRoutePage,
  CatalogStatusComponent,
} from "../catalog-shell.component";
import {
  CatalogReadError,
  sameAdmittedAccess,
  type CatalogMapping,
  type CatalogMappingScope,
  type CatalogProject,
  type CatalogRevision,
  type CatalogRevisionPage,
} from "../catalog-types";
import { RevisionReportApi } from "../reports/revision-report-api";
import {
  RevisionReportState,
  type RevisionReportAdmission,
  type RevisionReportPane,
} from "../reports/revision-report-state";
import type {
  RevisionReport,
  RevisionReportErrorKind,
  RevisionReportResult,
} from "../reports/revision-report-types";
import { RevisionComparisonApi } from "./revision-comparison-api";
import {
  comparisonPath,
  comparisonQuery,
  comparisonSelection,
  currentSide,
  type ComparisonSelection,
  type ComparisonSideSelection,
} from "./revision-comparison-selection";
import { RevisionComparisonState } from "./revision-comparison-state";
import {
  ComparisonError,
  type ComparisonErrorKind,
  type ComparisonRequest,
  type ComparisonSideCitation,
  type ComparisonSideName,
  type ComparisonValidationSummary,
  type RevisionComparison,
} from "./revision-comparison-types";
import {
  comparisonGroups,
  entityTitle,
  matchLabel,
  type ComparisonCandidateView,
  type ComparisonGroupView,
  type ComparisonItemView,
} from "./revision-comparison-view";

type ProjectionDocument = components["schemas"]["ProjectionDocumentV1"];
type ValidationReport = components["schemas"]["ValidationReportV1"];
type SideRevision = CatalogRevision | "notFound" | null;

interface ComparisonScreen {
  project: CatalogProject;
  mapping: CatalogMapping;
  page: CatalogRevisionPage;
  left: SideRevision;
  right: SideRevision;
}

/** Why one saved report cannot be cited; never inferred from a missing payload. */
export type ReportProblem =
  | { readonly kind: "notDerived" | "pending" | "failed" }
  | {
      readonly kind: "unreadable";
      readonly outcome: components["schemas"]["CatalogRevisionReportReadOutcomeV1"];
    }
  | { readonly kind: "error"; readonly error: RevisionReportErrorKind };

export type SideStatus =
  | { readonly kind: "unselected" | "missing" | "unreadable" | "reading" }
  | { readonly kind: "noExplanation"; readonly problem: ReportProblem }
  | {
      readonly kind: "ready";
      readonly projection: RevisionReportResult;
      readonly validation: RevisionReportResult | null;
      readonly validationProblem: ReportProblem | null;
      readonly linked: boolean;
      readonly document: ProjectionDocument;
      readonly linkedValidation: ValidationReport | null;
    };

export interface SideView {
  readonly name: ComparisonSideName;
  readonly title: string;
  readonly selection: ComparisonSideSelection | null;
  readonly revision: CatalogRevision | null;
  readonly status: SideStatus;
}

const sideNames: readonly ComparisonSideName[] = ["left", "right"];

@Component({
  selector: "weft-revision-comparison-page",
  imports: [
    DatePipe,
    RouterLink,
    CatalogStatusComponent,
    MappingProjectionComponent,
    NativeEvidenceComponent,
  ],
  templateUrl: "./revision-comparison-page.component.html",
  styleUrl: "./revision-comparison-page.component.css",
})
export class RevisionComparisonPageComponent extends CatalogRoutePage<
  ComparisonScreen,
  CatalogMappingScope
> {
  private readonly contexts = inject(CatalogContextStore);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  private readonly pageDestroyRef = inject(DestroyRef);
  private readonly viewer = viewChild(MappingProjectionComponent);
  readonly selection = signal<ComparisonSelection | null>(null);
  readonly leftReports = this.sideReports("left");
  readonly rightReports = this.sideReports("right");
  readonly comparison = new RevisionComparisonState(
    inject(RevisionComparisonApi),
    () => {
      const context = this.context();
      return context
        ? this.contexts.admitted(context)
        : throwError(() => new ComparisonError("rejected"));
    },
  );
  readonly showUnchanged = signal(false);
  readonly viewerSide = signal<ComparisonSideName | null>(null);
  readonly comparisonPath = comparisonPath;
  readonly matchLabel = matchLabel;
  readonly entityTitle = entityTitle;

  readonly sides = computed<readonly SideView[]>(() =>
    sideNames.map((name) => this.side(name)),
  );
  readonly request = computed<ComparisonRequest | null>(() => {
    const [left, right] = this.sides();
    if (
      left.status.kind !== "ready" ||
      right.status.kind !== "ready" ||
      !left.revision ||
      !right.revision
    )
      return null;
    return {
      left: {
        revision: left.revision,
        projectionDerivationId: left.status.projection.derivationId,
        validationDerivationId: left.status.validation?.derivationId ?? null,
      },
      right: {
        revision: right.revision,
        projectionDerivationId: right.status.projection.derivationId,
        validationDerivationId: right.status.validation?.derivationId ?? null,
      },
    };
  });
  readonly result = computed(() => this.comparison.viewFor(this.request()));
  readonly ready = computed<RevisionComparison | null>(() => {
    const result = this.result();
    return result.phase === "ready" ? result.comparison : null;
  });
  readonly groups = computed<readonly ComparisonGroupView[]>(() => {
    const comparison = this.ready();
    const [left, right] = this.sides();
    if (
      !comparison ||
      left.status.kind !== "ready" ||
      right.status.kind !== "ready"
    )
      return [];
    return comparisonGroups(
      comparison,
      new ProjectionView(left.status.document),
      new ProjectionView(right.status.document),
    );
  });
  readonly hiddenUnchanged = computed(() =>
    this.showUnchanged()
      ? 0
      : this.groups().reduce((total, group) => total + group.unchanged, 0),
  );
  readonly visibleGroups = computed(() =>
    this.groups().map((group) => ({
      ...group,
      visible: this.showUnchanged()
        ? group.items
        : group.items.filter((item) => !item.unchanged),
    })),
  );
  readonly sameRevision = computed(() => {
    const [left, right] = this.sides();
    return (
      left.revision !== null &&
      right.revision !== null &&
      left.revision.scope.revisionId === right.revision.scope.revisionId
    );
  });
  readonly viewerDocument = computed<ProjectionDocument | null>(() => {
    const name = this.viewerSide();
    const status = name ? this.sides()[name === "left" ? 0 : 1].status : null;
    return status?.kind === "ready" && this.ready() ? status.document : null;
  });
  readonly viewerValidation = computed<ValidationReport | null>(() => {
    const name = this.viewerSide();
    const status = name ? this.sides()[name === "left" ? 0 : 1].status : null;
    return status?.kind === "ready" ? status.linkedValidation : null;
  });
  readonly candidatesOmitted = computed(
    () => this.ready()?.items.some((item) => item.candidatesTruncated) ?? false,
  );
  readonly headline = computed(() => {
    const comparison = this.ready();
    return comparison ? headline(comparison) : "";
  });
  readonly live = computed(() => {
    const [left, right] = this.sides();
    const result = this.result();
    if (result.phase === "loading") return "Comparing saved explanations…";
    if (result.phase === "error")
      return "The comparison could not be completed.";
    if (result.phase === "ready")
      return `Revision ${left.revision?.sequence} and revision ${right.revision?.sequence} compared. ${this.headline()}`;
    return "";
  });

  constructor() {
    super();
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.leftReports.unbind();
      this.rightReports.unbind();
      this.comparison.clear();
    });
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        // Route changes always cancel report reads and the comparison request.
        this.leftReports.unbind();
        this.rightReports.unbind();
        this.comparison.clear();
        this.viewerSide.set(null);
        const selection = comparisonSelection(params, query);
        this.selection.set(selection);
        if (!selection) {
          this.read.invalidLink();
          return;
        }
        this.cursor.set(selection.cursor);
        this.read.select(
          selection.scope,
          () => this.load(selection),
          selection.cursor,
        );
      });
    effect(() => {
      const selection = this.selection();
      const view = this.data();
      const context = this.context();
      // Each side reads its explanation and validation together, which is the service's
      // per-instance report read capacity, so the right side reads after the left settles.
      this.leftReports.readsSettled();
      untracked(() => {
        let leftReading = false;
        for (const name of sideNames) {
          const reports = this.reports(name);
          const chosen = selection?.[name];
          const revision = view?.[name];
          if (
            !selection ||
            !chosen ||
            !view ||
            !context ||
            !revision ||
            revision === "notFound" ||
            revision.scope.revisionId !== chosen.revisionId
          )
            reports.unbind();
          else if (!leftReading) {
            reports.bind(
              { context, mapping: view.mapping, revision },
              chosen.selectors,
            );
            leftReading = name === "left" && !reports.readsSettled();
          }
        }
      });
    });
    effect(() => {
      const request = this.request();
      untracked(() => this.comparison.select(request));
    });
    this.pageDestroyRef.onDestroy(() => {
      invalidation.unsubscribe();
      this.leftReports.destroy();
      this.rightReports.destroy();
      this.comparison.destroy();
    });
  }

  reports(name: ComparisonSideName): RevisionReportState {
    return name === "left" ? this.leftReports : this.rightReports;
  }

  /** Select a revision for one side; the other side keeps its exact selection. */
  pick(name: ComparisonSideName, revision: CatalogRevision): Params {
    const selection = this.selection();
    const chosen = currentSide(revision.scope.revisionId);
    return comparisonQuery(
      name === "left" ? chosen : (selection?.left ?? null),
      name === "right" ? chosen : (selection?.right ?? null),
      selection?.cursor ?? null,
    );
  }

  pageQuery(cursor: string | null): Params {
    const selection = this.selection();
    return comparisonQuery(
      selection?.left ?? null,
      selection?.right ?? null,
      cursor,
    );
  }

  swapQuery(): Params {
    const selection = this.selection();
    return comparisonQuery(
      selection?.right ?? null,
      selection?.left ?? null,
      selection?.cursor ?? null,
    );
  }

  /** Drop one side's saved-report pins so its current reports are read. */
  currentReportsQuery(name: ComparisonSideName): Params {
    const selection = this.selection();
    const unpin = (side: ComparisonSideSelection | null) =>
      side ? currentSide(side.revisionId) : null;
    return comparisonQuery(
      name === "left"
        ? unpin(selection?.left ?? null)
        : (selection?.left ?? null),
      name === "right"
        ? unpin(selection?.right ?? null)
        : (selection?.right ?? null),
      selection?.cursor ?? null,
    );
  }

  /** The exact saved reports cited by this result, as a stable comparison link. */
  exactQuery(comparison: RevisionComparison): Params {
    const side = (
      citation: ComparisonSideCitation,
    ): ComparisonSideSelection => ({
      revisionId: citation.revision.revisionId,
      selectors: {
        projectionDerivationId: citation.projection.derivationId,
        validationDerivationId: citation.validation?.derivationId ?? null,
      },
    });
    return comparisonQuery(side(comparison.left), side(comparison.right));
  }

  revisionReportsQuery(citation: ComparisonSideCitation): Params {
    return citation.validation
      ? {
          projectionDerivationId: citation.projection.derivationId,
          validationDerivationId: citation.validation.derivationId,
        }
      : { projectionDerivationId: citation.projection.derivationId };
  }

  pinned(name: ComparisonSideName): boolean {
    const selectors = this.selection()?.[name]?.selectors;
    return (
      !!selectors &&
      (selectors.projectionDerivationId !== null ||
        selectors.validationDerivationId !== null)
    );
  }

  retryComparison(): void {
    this.comparison.retry();
  }

  toggleUnchanged(): void {
    this.showUnchanged.update((value) => !value);
  }

  openViewer(name: ComparisonSideName): void {
    this.viewerSide.set(name);
    afterNextRender(
      () => {
        if (this.viewerSide() === name)
          this.document.getElementById("comparison-viewer-title")?.focus();
      },
      { injector: this.injector },
    );
  }

  closeViewer(): void {
    this.viewerSide.set(null);
  }

  /** Open the existing mapping viewer for the candidate's own side and select its node. */
  showCandidate(candidate: ComparisonCandidateView): void {
    const reference = candidate.reference;
    if (!reference) return;
    const current = this.viewer();
    if (this.viewerSide() === candidate.side && current) {
      current.navigate(reference);
      return;
    }
    this.viewerSide.set(candidate.side);
    afterNextRender(
      () => {
        if (this.viewerSide() === candidate.side)
          this.viewer()?.navigate(reference);
      },
      { injector: this.injector },
    );
  }

  focusGroup(group: ComparisonGroupView): void {
    const heading = this.document.getElementById(group.id);
    heading?.focus();
    heading?.scrollIntoView({ block: "start" });
  }

  /** Arrow keys move between comparison items; Tab still reaches every control in order. */
  moveItem(event: KeyboardEvent): void {
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    const target = event.target;
    if (
      !(target instanceof HTMLElement) ||
      target.closest("input, textarea, select, summary")
    )
      return;
    const container = (event.currentTarget as HTMLElement | null)?.closest(
      ".comparison-groups",
    );
    if (!container) return;
    const items = [
      ...container.querySelectorAll<HTMLElement>(".comparison-item"),
    ];
    if (items.length === 0) return;
    const current = target.closest<HTMLElement>(".comparison-item");
    let next: number;
    if (event.key === "Home") next = 0;
    else if (event.key === "End") next = items.length - 1;
    else if (current) {
      const index = items.indexOf(current);
      next =
        event.key === "ArrowDown"
          ? Math.min(items.length - 1, index + 1)
          : Math.max(0, index - 1);
    } else {
      // From a group heading, move to the nearest item in document order.
      const after = items.findIndex(
        (item) =>
          (target.compareDocumentPosition(item) &
            Node.DOCUMENT_POSITION_FOLLOWING) !==
          0,
      );
      next =
        event.key === "ArrowDown"
          ? after < 0
            ? items.length - 1
            : after
          : Math.max(0, (after < 0 ? items.length : after) - 1);
    }
    event.preventDefault();
    items[next].focus();
    items[next].scrollIntoView({ block: "nearest" });
  }

  absenceText(item: ComparisonItemView, sides: readonly SideView[]): string {
    const [left, right] = sides;
    if (item.entity === "validation") return "";
    if (item.match === "onlyLeft")
      return item.changed
        ? `Present in revision ${left.revision?.sequence} and absent from revision ${right.revision?.sequence}'s complete explanation.`
        : `Present in revision ${left.revision?.sequence}. Revision ${right.revision?.sequence}'s explanation is incomplete, so it may still exist there.`;
    if (item.match === "onlyRight")
      return item.changed
        ? `Present in revision ${right.revision?.sequence} and absent from revision ${left.revision?.sequence}'s complete explanation.`
        : `Present in revision ${right.revision?.sequence}. Revision ${left.revision?.sequence}'s explanation is incomplete, so it may still exist there.`;
    if (item.match === "ambiguous")
      return "No pairing was chosen. Review each possible counterpart in its own revision.";
    return "";
  }

  /** Zero categories are omitted; the item total is always stated. */
  groupSummary(group: ComparisonGroupView): string {
    const total = group.items.length;
    return [
      `${total} ${total === 1 ? "item" : "items"}`,
      ...(
        [
          [group.changed, "changed"],
          [group.uncertain, "uncertain"],
          [group.unchanged, "unchanged"],
          [group.onlyLeft, "only in left"],
          [group.onlyRight, "only in right"],
          [group.ambiguous, "ambiguous"],
        ] as const
      )
        .filter(([count]) => count > 0)
        .map(([count, name]) => `${count} ${name}`),
    ].join(" · ");
  }

  validityText(summary: ComparisonValidationSummary | null): string {
    if (!summary) return "Not compared";
    return `${summary.isValid ? "Valid" : "Issues found"} · ${summary.totalIssueCount} notices, ${summary.emittedIssueCount} returned${summary.truncated || summary.hasTruncatedDiagnosticText ? " · shortened" : ""}`;
  }

  coverageText(status: components["schemas"]["CoverageStatus"]): string {
    return {
      complete: "Complete",
      partial: "Partial",
      unavailable: "Unavailable",
    }[status];
  }

  problemText(
    operation: "explanation" | "validation",
    problem: ReportProblem,
  ): string {
    switch (problem.kind) {
      case "notDerived":
        return `No saved ${operation} exists for this revision. Comparison reads saved reports only and never generates one.`;
      case "pending":
        return `The ${operation} for this revision is still in progress. Its saved result is not available yet.`;
      case "failed":
        return `The latest ${operation} attempt for this revision failed. No saved result is available.`;
      case "unreadable":
        return `A saved ${operation} exists, but its payload could not be read (${readOutcome(problem.outcome)}). Nothing is inferred from the missing payload.`;
      default:
        return reportError(operation, problem.error);
    }
  }

  comparisonError(kind: ComparisonErrorKind): string {
    return {
      notFound:
        "A compared revision or saved report is unavailable, or you no longer have access. Refresh the catalog to read the current reports.",
      rejected:
        "The comparison request was not accepted. Refresh the catalog before trying again.",
      invalidLink:
        "The comparison selection is invalid. Choose the revisions again.",
      admissionBusy: "The service is busy. Try the comparison again shortly.",
      dependencyUnavailable:
        "Saved report storage is unavailable. Try the comparison again later.",
      resourceLimit:
        "These explanations are too large to compare within the service limits.",
      timedOut: "The comparison timed out. Try it again.",
      incompatible:
        "The service returned a comparison that could not be verified for these saved reports. Nothing is shown from that response.",
      unavailable: "The comparison could not be completed. Try it again.",
    }[kind];
  }

  canRetryComparison(kind: ComparisonErrorKind): boolean {
    return [
      "admissionBusy",
      "dependencyUnavailable",
      "timedOut",
      "unavailable",
      "incompatible",
    ].includes(kind);
  }

  private load(selection: ComparisonSelection): Observable<ComparisonScreen> {
    const scope = selection.scope;
    return forkJoin({
      project: this.api.project({
        organizationId: scope.organizationId,
        projectId: scope.projectId,
      }),
      mapping: this.api.mapping(scope),
      page: this.api.revisions(scope, selection.cursor),
      left: this.revisionFor(scope, selection.left),
      right: this.revisionFor(scope, selection.right),
    });
  }

  /** A revision outside this mapping is shown on its side; other failures stay page failures. */
  private revisionFor(
    scope: CatalogMappingScope,
    side: ComparisonSideSelection | null,
  ): Observable<SideRevision> {
    if (!side) return of(null);
    return this.api
      .revision({ ...scope, revisionId: side.revisionId })
      .pipe(
        catchError((error: unknown) =>
          error instanceof CatalogReadError && error.kind === "notFound"
            ? of("notFound" as const)
            : throwError(() => error),
        ),
      );
  }

  private sideReports(name: ComparisonSideName): RevisionReportState {
    return new RevisionReportState(inject(RevisionReportApi), {
      // This view never starts a report action; report work belongs to each revision page.
      refresh: () => throwError(() => new CatalogReadError("unavailable")),
      isCurrent: (admission) => this.isCurrentAdmission(name, admission),
      admit: (admission) => this.contexts.admitted(admission.context),
    });
  }

  private isCurrentAdmission(
    name: ComparisonSideName,
    admission: RevisionReportAdmission,
  ): boolean {
    const selection = this.selection();
    const view = this.data();
    const context = this.context();
    const revision = view?.[name];
    // A same-owner re-read keeps an admission current while its revision and access are unchanged.
    return (
      selection?.[name]?.revisionId === admission.revision.scope.revisionId &&
      view !== null &&
      context !== null &&
      this.contexts.isCurrent(admission.context) &&
      this.contexts.isCurrent(context) &&
      sameAdmittedAccess({ context, mapping: view.mapping }, admission) &&
      typeof revision === "object" &&
      revision !== null &&
      revision.scope.revisionId === admission.revision.scope.revisionId
    );
  }

  private side(name: ComparisonSideName): SideView {
    const selection = this.selection()?.[name] ?? null;
    const view = this.data();
    const context = this.context();
    const value = view?.[name] ?? null;
    const revision = value === "notFound" ? null : value;
    const readable =
      context !== null &&
      context.features.readReports &&
      view !== null &&
      view.mapping.access.actions.readReports;
    return {
      name,
      title: name === "left" ? "Left revision" : "Right revision",
      selection,
      revision,
      status: !selection
        ? { kind: "unselected" }
        : value === "notFound"
          ? { kind: "missing" }
          : !revision
            ? { kind: "reading" }
            : !readable
              ? { kind: "unreadable" }
              : status(this.reports(name)),
    };
  }
}

function status(reports: RevisionReportState): SideStatus {
  const projection = reports.projection();
  const validation = reports.validation();
  // Admitted reading that is not yet bound to the current screen is still settling.
  if (!reports.canRead()) return { kind: "reading" };
  const failed = projection.phase === "error" || validation.phase === "error";
  if (
    reports.busy() ||
    busy(projection) ||
    busy(validation) ||
    (!failed && (projection.phase === "idle" || validation.phase === "idle"))
  )
    return { kind: "reading" };
  // An authority refusal on either report clears both; it is that report's problem.
  if (projection.phase !== "ready" || !projection.response) {
    const error = projection.error ?? validation.error;
    return {
      kind: "noExplanation",
      problem: { kind: "error", error: error?.kind ?? "unavailable" },
    };
  }
  const problem = reportProblem(projection);
  const document = reports.projectionDocument();
  const result = projection.response.body.result;
  if (
    problem ||
    !document ||
    !result ||
    projection.pinId !== result.derivationId
  )
    return {
      kind: "noExplanation",
      problem: problem ?? { kind: "unreadable", outcome: "observed" },
    };
  const validationProblem = reportProblem(validation);
  const validationResult = validationProblem
    ? null
    : (validation.response?.body.result ?? null);
  return {
    kind: "ready",
    projection: result,
    validation: validationResult,
    validationProblem,
    linked: reports.linkedProjectionId() === result.derivationId,
    document,
    linkedValidation: reports.linkedValidation(),
  };
}

function reportProblem(
  pane: RevisionReportPane<RevisionReport>,
): ReportProblem | null {
  if (pane.phase === "error" || !pane.response)
    return { kind: "error", error: pane.error?.kind ?? "unavailable" };
  const body = pane.response.body;
  if (body.state === "notDerived" || body.state === "pending")
    return { kind: body.state };
  if (body.state === "failed") return { kind: "failed" };
  if (body.readOutcome !== "observed" || body.report === null)
    return { kind: "unreadable", outcome: body.readOutcome };
  return pane.pinId === body.result?.derivationId
    ? null
    : { kind: "error", error: "incompatible" };
}

function busy(pane: RevisionReportPane<RevisionReport>): boolean {
  return ["loading", "refreshing", "ensuring", "polling"].includes(pane.phase);
}

function readOutcome(
  value: components["schemas"]["CatalogRevisionReportReadOutcomeV1"],
): string {
  return {
    observed: "read completed",
    objectUnavailable: "saved object is unavailable",
    storageIntegrity: "saved object failed its integrity check",
    unsupportedFormat: "saved object format is unsupported",
    resourceLimit: "saved object exceeds the read limits",
  }[value];
}

function reportError(
  operation: "explanation" | "validation",
  kind: RevisionReportErrorKind,
): string {
  switch (kind) {
    case "notFound":
      return `The selected saved ${operation} is unavailable or you no longer have access.`;
    case "rejected":
      return `The ${operation} request was not accepted. Refresh the catalog before trying again.`;
    case "invalidLink":
      return `This saved ${operation} link is invalid.`;
    case "admissionBusy":
      return `The service is busy. Refresh the catalog to read the ${operation} again.`;
    case "dependencyUnavailable":
      return `Saved report storage is unavailable. Refresh the catalog later to read the ${operation} again.`;
    case "resourceLimit":
      return `The saved ${operation} exceeds the read limits.`;
    case "timedOut":
      return `Reading the saved ${operation} timed out. Refresh the catalog to read it again.`;
    case "incompatible":
      return `The service returned a saved ${operation} that could not be verified for this revision.`;
    default:
      return `The saved ${operation} could not be read. Refresh the catalog to read it again.`;
  }
}

/** Partial, truncated or uncertain evidence never yields a "no changes" statement. */
function headline(comparison: RevisionComparison): string {
  const changed = comparison.items.filter((item) =>
    item.facets.some((facet) => facet.hasObservedDifference),
  ).length;
  if (comparison.hasObservedDifferences)
    return changed > 0
      ? `Observed differences in ${changed} of ${comparison.emittedItemCount} items shown.`
      : "Observed differences exist in items that are not shown.";
  const complete =
    !comparison.hasUncertainty &&
    !comparison.truncated &&
    comparison.leftCoverage.status === "complete" &&
    comparison.rightCoverage.status === "complete";
  return complete
    ? "No differences were observed between these saved explanations."
    : "No differences were observed in the available evidence, but the evidence is incomplete. This is not a no-changes result.";
}
