import { DatePipe } from "@angular/common";
import {
  Component,
  ElementRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from "@angular/core";
import type { ChatCitation, ChatConversation } from "../../chat/chat-types";
import type { WorkspaceCitationEvent } from "../../chat/workspace-chat-types";
import { sameScope } from "../../chat/chat-types";
import {
  ProjectionView,
  type NodeReference,
} from "../../workbench/projection-view";
import { WorkbenchMappingTableComponent } from "../../workbench/mapping-table.component";
import type { WorkbenchSample } from "../../workbench/workbench-sample";
import { RouterLink } from "@angular/router";
import type { components } from "../../api/generated";
import { NativeEvidenceComponent } from "../../workbench/native-evidence.component";
import { MappingProjectionComponent } from "../../workbench/projection.component";
import { catalogRoutes, type CatalogRevision } from "../catalog-types";
import type {
  RevisionReportPane,
  RevisionReportState,
} from "./revision-report-state";
import type {
  RevisionReport,
  RevisionReportErrorKind,
  RevisionReportOperation,
} from "./revision-report-types";
import { HelpLinkComponent } from "../../help/help-link.component";

type Lifecycle = components["schemas"]["CatalogRevisionReportStateV1"];
type ReadOutcome = components["schemas"]["CatalogRevisionReportReadOutcomeV1"];
type Failure = components["schemas"]["CatalogRevisionReportFailureV1"];

@Component({
  selector: "weft-revision-reports",
  imports: [
    DatePipe,
    RouterLink,
    MappingProjectionComponent,
    NativeEvidenceComponent,
    WorkbenchMappingTableComponent,
    HelpLinkComponent,
  ],
  templateUrl: "./revision-reports.component.html",
  styleUrl: "./revision-reports.component.css",
})
export class RevisionReportsComponent {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly viewer = viewChild(MappingProjectionComponent);
  readonly citationNotice = signal("");
  readonly reports = input.required<RevisionReportState>();
  readonly revision = input.required<CatalogRevision>();
  readonly workbench = input(false);
  readonly sample = input<WorkbenchSample | null>(null);
  readonly sampleMode = input(false);
  readonly sampleModeChange = output<boolean>();
  readonly selectedEntity = input<NodeReference | null>(null);
  readonly entitySelected = output<NodeReference | null>();
  readonly diagnosticSelected = output<{
    validation: boolean;
    ordinal: number;
  }>();
  readonly operations = ["projection", "validation"] as const;
  readonly projection = computed(() => this.reports().projection());
  readonly validation = computed(() => this.reports().validation());
  readonly revisionPath = computed(() =>
    catalogRoutes.revision(this.revision().scope),
  );
  readonly document = computed(() => this.reports().projectionDocument());
  readonly workbenchView = computed(() =>
    this.document() ? new ProjectionView(this.document()!) : null,
  );
  readonly linkedValidation = computed(() => this.reports().linkedValidation());
  private readonly panes = computed(() => [
    { operation: "projection" as const, pane: this.projection() },
    { operation: "validation" as const, pane: this.validation() },
  ]);
  /** A report is being requested, observed or still recorded as in progress. */
  readonly generating = computed(() =>
    this.panes().some(
      ({ pane }) =>
        pane.phase === "ensuring" ||
        pane.phase === "polling" ||
        pane.response?.body.state === "pending",
    ),
  );
  /** Progress is no longer observed automatically; only an explicit read checks it. */
  readonly progressUnobserved = computed(
    () =>
      this.generating() &&
      !this.reports().busy() &&
      this.panes().every(({ pane }) => !this.busy(pane)),
  );
  /** Current reports still missing once reads and automatic generation have settled. */
  readonly missing = computed(() =>
    this.panes()
      .filter(
        ({ pane }) =>
          !pane.pinned &&
          pane.phase === "ready" &&
          (pane.response?.body.state === "notDerived" ||
            pane.response?.body.state === "failed"),
      )
      .map(({ operation, pane }) => ({
        operation,
        failed: pane.response?.body.state === "failed",
      })),
  );
  readonly missingTitle = computed(() => {
    const missing = this.missing(),
      names = missing
        .map((item) =>
          item.operation === "projection" ? "explanation" : "validation",
        )
        .join(" and ");
    return missing.some((item) => item.failed)
      ? `Generating this version's ${names} failed`
      : `This version's ${names} ${missing.length > 1 ? "haven't" : "hasn't"} been generated`;
  });
  readonly pinned = computed(
    () => this.projection().pinned || this.validation().pinned,
  );
  readonly needsCurrentLink = computed(
    () =>
      this.pinned() ||
      this.projection().error?.kind === "invalidLink" ||
      this.validation().error?.kind === "invalidLink",
  );
  readonly projectionPin = computed(() => {
    const id = this.projection().response?.body.result?.derivationId;
    return id ? { projectionDerivationId: id } : null;
  });
  readonly validationPin = computed(() => {
    const id = this.validation().response?.body.result?.derivationId;
    return id ? { validationDerivationId: id } : null;
  });
  readonly linkedPin = computed(() => {
    const projectionId = this.projection().response?.body.result?.derivationId;
    const validationId = this.validation().response?.body.result?.derivationId;
    return this.document() && projectionId && validationId
      ? {
          projectionDerivationId: projectionId,
          validationDerivationId: validationId,
        }
      : null;
  });

  showChatCitation(value: {
    conversation: ChatConversation;
    citation: ChatCitation;
  }): void {
    const { conversation, citation } = value;
    const target = citation.target;
    const validation =
      target.kind === "validationDiagnostic" ||
      target.kind === "validationReport";
    const pin = validation
      ? conversation.validationDerivationId
      : conversation.projectionDerivationId;
    this.showReportTarget(
      target,
      citation.reportDerivationId,
      pin,
      sameScope(conversation.revision, this.revision().scope),
    );
  }

  showWorkspaceChatCitation(value: WorkspaceCitationEvent): void {
    const c = value.citation,
      scope = this.revision().scope;
    if (c.kind !== "report" || !c.target || !c.reportDerivationId) return;
    this.showReportTarget(
      c.target,
      c.reportDerivationId,
      c.reportDerivationId,
      c.organizationId === scope.organizationId &&
        c.projectId === scope.projectId &&
        c.mappingId === scope.mappingId &&
        c.revisionId === scope.revisionId,
    );
  }

  private showReportTarget(
    target: ChatCitation["target"],
    reportDerivationId: string,
    pin: string,
    scopeMatches: boolean,
  ): void {
    const validation =
      target.kind === "validationDiagnostic" ||
      target.kind === "validationReport";
    const pane = validation ? this.validation() : this.projection();
    this.citationNotice.set("");
    if (
      !scopeMatches ||
      reportDerivationId !== pin ||
      pane.response?.body.result?.derivationId !== pin ||
      !pane.response.body.report
    ) {
      this.citationNotice.set(
        "This citation belongs to a saved report that is not displayed. No revision or report selection was changed.",
      );
      return;
    }
    let selector: string | null = null;
    if (target.kind === "projectionReport")
      selector = "#saved-explanation-title";
    else if (target.kind === "validationReport")
      selector = "#saved-validation-title";
    else if (
      target.kind === "projectionDiagnostic" ||
      target.kind === "validationDiagnostic"
    ) {
      if (
        target.diagnosticOrdinal !== null &&
        Number.isInteger(target.diagnosticOrdinal) &&
        target.diagnosticOrdinal >= 0
      )
        selector = `[data-chat-${validation ? "validation" : "projection"}-diagnostic="${target.diagnosticOrdinal}"]`;
      if (
        this.workbench() &&
        target.diagnosticOrdinal !== null &&
        Number.isInteger(target.diagnosticOrdinal) &&
        target.diagnosticOrdinal >= 0
      ) {
        if (
          target.diagnosticOrdinal <
          pane.response.body.report.diagnostics.length
        ) {
          this.diagnosticSelected.emit({
            validation,
            ordinal: target.diagnosticOrdinal,
          });
          return;
        }
      }
    } else {
      const viewer = this.viewer(),
        id = target.resourceId;
      const currentView = this.workbench()
        ? this.workbenchView()
        : viewer?.view();
      if (currentView && id) {
        const view = currentView;
        let reference: NodeReference | null = null;
        if (target.kind === "field" && view.fields.has(id))
          reference = { kind: "field", fieldId: id };
        if (target.kind === "rule" && view.rules.has(id))
          reference = { kind: "rule", ruleId: id };
        if (target.kind === "boundary" && view.boundaries.has(id))
          reference = { kind: "boundary", boundaryId: id };
        if (target.kind === "parameter" && view.parameters.has(id))
          reference = { kind: "parameter", parameterId: id };
        if (target.kind === "control" && view.controls.has(id))
          reference = { kind: "control", controlId: id };
        if (reference) {
          if (this.workbench()) this.entitySelected.emit(reference);
          else viewer?.navigate(reference);
          return;
        }
      }
    }
    const element = selector
      ? this.element.nativeElement.querySelector<HTMLElement>(selector)
      : null;
    if (element) {
      element.focus();
      element.scrollIntoView({ block: "nearest" });
    } else
      this.citationNotice.set(
        "The cited resource is not available in the displayed report.",
      );
  }

  operation(value: components["schemas"]["OperationStatusV1"]): string {
    return {
      succeeded: "Completed",
      invalidMapping: "The native mapping has validation issues",
      unsupportedArtifact: "This mapping format is unsupported",
      resourceLimit: "The mapping operation exceeded its limits",
      protocolFailure: "The mapping engine returned an incompatible response",
      backendUnavailable: "The mapping engine is unavailable",
      backendFailure: "The mapping engine could not finish",
      timedOut: "The mapping operation timed out",
      canceled: "The mapping operation was canceled",
      isolationFailure: "The mapping worker could not be isolated safely",
    }[value];
  }

  validationTitle(report: components["schemas"]["ValidationReportV1"]): string {
    if (report.isValid === true) return "Native mapping is valid";
    if (report.isValid === false) return "Native mapping has validation issues";
    return "Validation has no validity decision";
  }

  busy(pane: RevisionReportPane<RevisionReport>): boolean {
    return ["loading", "refreshing", "ensuring", "polling"].includes(
      pane.phase,
    );
  }

  phase(
    pane: RevisionReportPane<RevisionReport>,
    operation: RevisionReportOperation,
  ): string {
    const name = operation === "projection" ? "explanation" : "validation";
    switch (pane.phase) {
      case "loading":
        return `Reading saved ${name}…`;
      case "refreshing":
        return `Reading saved ${name} again…`;
      case "ensuring":
        return `Requesting ${name}…`;
      case "polling":
        return `Waiting for ${name}. Only reading its progress…`;
      case "error":
        return `The ${name} request could not be completed.`;
      case "ready":
        return `Saved ${name} read completed.`;
      default:
        return `Saved ${name} has not been read.`;
    }
  }

  lifecycle(value: Lifecycle): string {
    return {
      notDerived: "Not generated",
      pending: "In progress",
      available: "Successful result saved",
      failed: "Attempt failed",
    }[value];
  }

  readOutcome(value: ReadOutcome): string {
    return {
      observed: "Read completed",
      objectUnavailable: "Saved object is unavailable",
      storageIntegrity: "Saved object failed its integrity check",
      unsupportedFormat: "Saved object format is unsupported",
      resourceLimit: "Saved object exceeds the read limits",
    }[value];
  }

  failure(value: Failure): string {
    return {
      objectUnavailable: "The saved object is unavailable.",
      storageIntegrity: "The saved object failed its integrity check.",
      storageIo: "The saved object could not be accessed.",
      backendUnavailable: "The mapping engine is unavailable.",
      backendFailure: "The mapping engine could not finish.",
      timedOut: "The mapping operation timed out.",
      canceled: "The mapping operation was canceled.",
      isolationFailure: "The mapping worker could not be isolated safely.",
      protocolFailure: "The mapping engine returned an incompatible response.",
      profileUnavailable: "The configured mapping profile is unavailable.",
      resourceLimit: "The mapping operation exceeded its limits.",
      unsupportedArtifact: "This mapping format is unsupported.",
    }[value];
  }

  error(value: RevisionReportErrorKind): string {
    return {
      notFound:
        "This report is unavailable or you no longer have access. Read again to check current access.",
      rejected:
        "The report request was not accepted. Refresh the catalog before trying again.",
      invalidLink:
        "This report link is invalid. Open the revision without report selectors to read current reports.",
      claimLost:
        "This attempt no longer owns the work. Read again to see its current state.",
      selectionChanged:
        "The selected saved result changed. Read again before choosing another action.",
      generationExhausted:
        "No more report attempts can be created for this selection. Existing saved results can still be read.",
      admissionBusy:
        "The service is busy. Read again before deciding whether to request another attempt.",
      dependencyUnavailable:
        "A report dependency is unavailable. Revision details remain available; read again later.",
      resourceLimit:
        "The report request exceeded a service limit. Revision details remain available.",
      timedOut:
        "The request timed out. Work may have completed; read again before requesting another attempt.",
      incompatible:
        "The service returned a report that could not be verified for this revision. No explanation is shown from that response.",
      unavailable:
        "The report could not be read. Read again to check its current state.",
    }[value];
  }
}
