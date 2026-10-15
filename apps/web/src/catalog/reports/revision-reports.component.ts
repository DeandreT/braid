import { DatePipe } from "@angular/common";
import { Component, computed, input } from "@angular/core";
import { RouterLink } from "@angular/router";
import type { components } from "../../api/generated";
import { NativeEvidenceComponent } from "../../mappings/native-evidence.component";
import { MappingProjectionComponent } from "../../mappings/projection.component";
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
  ],
  templateUrl: "./revision-reports.component.html",
  styleUrl: "./revision-reports.component.css",
})
export class RevisionReportsComponent {
  readonly reports = input.required<RevisionReportState>();
  readonly revision = input.required<CatalogRevision>();
  readonly projection = computed(() => this.reports().projection());
  readonly validation = computed(() => this.reports().validation());
  readonly revisionPath = computed(() =>
    catalogRoutes.revision(this.revision().scope),
  );
  readonly document = computed(() => this.reports().projectionDocument());
  readonly linkedValidation = computed(() => this.reports().linkedValidation());
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
