import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from "@angular/core";
import { map, switchMap, type Subscription } from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import type {
  CatalogContext,
  CatalogMapping,
  CatalogRevision,
} from "../catalog-types";
import { PreviewApi } from "./preview-api";
import { DocumentViewComponent } from "../../workbench/document-view.component";
import {
  displayBytes,
  type DocumentRepresentation,
} from "../../workbench/workbench-documents";
import { PreviewState } from "./preview-state";
import { StatusChipComponent } from "../../workbench/status-chip.component";
import { previewOutcomeStatus, previewRunStatus } from "./preview-presentation";
import {
  PreviewError,
  previewLimits,
  type PreviewErrorKind,
  type PreviewOutput,
  type PreviewParameterKind,
  type PreviewTargetKind,
} from "./preview-types";
import { HelpLinkComponent } from "../../help/help-link.component";
import { UnsentWork } from "../../workbench/unsent-work";
import { hasUnsentPreview } from "./preview-unsent";
import {
  RegressionApi,
  type CaseCapabilities,
} from "../regression/regression-api";
import { RegressionViewRecovery } from "../regression/regression-view-recovery";
import { canonicalDefinition } from "../regression/regression-definition";
import { previewCaseDefinition, type PreviewCasePending } from "./preview-case";
import { csvPreviewProfile } from "./csv-contract";

@Component({
  selector: "weft-revision-preview",
  imports: [StatusChipComponent, HelpLinkComponent, DocumentViewComponent],
  templateUrl: "./revision-preview.component.html",
  styleUrl: "./revision-preview.component.css",
})
export class RevisionPreviewComponent {
  readonly runStatus = previewRunStatus;
  private readonly document = inject(DOCUMENT);
  private readonly contexts = inject(CatalogContextStore);
  private downloads: Subscription[] = [];
  private readonly regression = inject(RegressionApi);
  private capabilityRequest: Subscription | null = null;
  readonly capabilities = signal<CaseCapabilities | null>(null);
  private readonly caseRecovery = inject(RegressionViewRecovery).previewCase;
  private caseRequest: Subscription | null = null;
  private caseOwner: {
    context: CatalogContext;
    scope: CatalogRevision["scope"];
  } | null = null;
  readonly caseName = signal("");
  readonly baselineApproved = signal(false);
  readonly caseBusy = signal(false);
  readonly casePending = signal<PreviewCasePending | null>(null);
  readonly caseNotice = signal<string | null>(null);
  readonly canSaveCase = computed(() => {
    const run = this.preview.run();
    return (
      this.preview.busy() === null &&
      !this.preview.active() &&
      !!this.capabilities()?.publishCases &&
      !!this.capabilities()?.csvRuntime &&
      run?.outcome === "succeeded" &&
      run.provenance?.profile === csvPreviewProfile &&
      run.outputs.length === 1 &&
      !!run.outputs[0]?.csvDialect &&
      this.preview.submitted()?.runId === run.runId
    );
  });
  readonly context = input.required<CatalogContext>();
  readonly mapping = input.required<CatalogMapping>();
  readonly revision = input.required<CatalogRevision>();
  readonly workbench = input(false);
  readonly outputSelected = output<void>();
  private outputEpoch = 0;
  private inputEpoch = 0;
  private readonly fileSequence = new Map<number, number>();
  readonly preview = new PreviewState(
    inject(PreviewApi),
    sessionStorageOrNull(this.document),
  );
  readonly limits = previewLimits;
  readonly allowed = computed(() => this.mapping().access.actions.preview);
  readonly shown = signal<{
    runId: string;
    output: PreviewOutput;
    display: DocumentRepresentation;
  } | null>(null);
  readonly outputError = signal<PreviewErrorKind | null>(null);

  constructor() {
    inject(UnsentWork).track(
      () =>
        this.caseBusy() ||
        !!this.casePending() ||
        this.caseName().length > 0 ||
        hasUnsentPreview({
          inputs: this.preview.inputs(),
          parameters: this.preview.parameters(),
          targetKind: this.preview.targetKind(),
          targetName: this.preview.targetName(),
          documentProfile: this.preview.documentProfile(),
          csvProfile: this.preview.csvProfile(),
          busy: this.preview.busy(),
          pendingKey: this.preview.pendingKey(),
        }),
    );
    effect(() => {
      const revision = this.revision();
      const allowed = this.allowed();
      untracked(() => {
        this.inputEpoch++;
        this.fileSequence.clear();
        this.retainCase();
        if (!allowed) {
          this.preview.unbind();
          this.capabilityRequest?.unsubscribe();
          this.capabilities.set(null);
          return;
        }
        this.preview.bind({
          scope: revision.scope,
          admit: () =>
            this.contexts
              .admitted(this.context())
              .pipe(map((context) => context.requestToken)),
          ready: () => this.contexts.canSend(this.context()),
          csvAvailable: () => this.context().features.csvPreview === true,
        });
        this.capabilityRequest?.unsubscribe();
        this.capabilities.set(null);
        this.capabilityRequest = this.contexts
          .admitted(this.context())
          .pipe(
            switchMap((context) =>
              this.regression.capabilities(context, this.mapping().scope),
            ),
          )
          .subscribe({
            next: (value) => {
              this.capabilities.set(value);
              this.caseOwner = {
                context: this.context(),
                scope: { ...revision.scope },
              };
              const kept = this.caseRecovery.take(
                this.context(),
                revision.scope,
              );
              if (kept) {
                this.caseName.set(value.publishCases ? kept.name : "");
                this.casePending.set(kept.pending);
                this.caseNotice.set(
                  "The original save details were restored. Nothing was sent again.",
                );
              }
            },
            error: () => this.capabilities.set(null),
          });
      });
    });
    const outputRun = computed(() => this.preview.run()?.runId ?? null);
    effect(() => {
      this.context();
      this.mapping();
      this.revision();
      outputRun();
      untracked(() => {
        this.clearOutput();
        this.baselineApproved.set(false);
      });
    });
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.inputEpoch++;
      this.fileSequence.clear();
      this.clearOutput();
      this.retainCase();
      this.capabilities.set(null);
      this.capabilityRequest?.unsubscribe();
    });
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.capabilityRequest?.unsubscribe();
      this.retainCase();
      this.downloads.forEach((download) => download.unsubscribe());
      this.preview.destroy();
    });
  }

  setTarget(kind: string): void {
    if (kind === "all" || kind === "primary" || kind === "named")
      this.preview.targetKind.set(kind as PreviewTargetKind);
  }

  setProfile(value: string): void {
    if (value === "csv" && this.context().features.csvPreview !== true) return;
    this.preview.csvProfile.set(value === "csv");
    this.preview.documentProfile.set(value === "document");
  }

  savePreviewCase(): void {
    const run = this.preview.run();
    const submitted = this.preview.submitted();
    const capabilities = this.capabilities();
    if (
      !this.canSaveCase() ||
      !this.baselineApproved() ||
      this.caseBusy() ||
      this.casePending() ||
      !run ||
      !submitted ||
      !capabilities
    )
      return;
    this.caseBusy.set(true);
    this.caseNotice.set(null);
    const name = this.caseName();
    this.caseRequest = this.preview
      .output(run.outputs[0]!)
      .pipe(
        map((bytes) =>
          previewCaseDefinition(run, submitted.body, capabilities, bytes, name),
        ),
        switchMap((body) =>
          this.contexts.admitted(this.context()).pipe(
            switchMap((context) => {
              if (this.preview.run()?.runId !== run.runId)
                throw new Error(
                  "The preview changed. Review the baseline again.",
                );
              const pending = { id: crypto.randomUUID(), version: "1", body };
              this.casePending.set(pending);
              return this.regression.publish(
                context,
                this.mapping().scope,
                pending.id,
                pending.version,
                body,
              );
            }),
          ),
        ),
      )
      .subscribe({
        next: () => this.caseSaved(),
        error: () => {
          this.caseBusy.set(false);
          this.caseNotice.set(
            this.casePending()
              ? "The save was not confirmed. Check the original version or retry its unchanged request."
              : "The CSV baseline could not be verified. Check the name, runtime and access before saving.",
          );
        },
      });
  }

  checkPreviewCase(): void {
    const pending = this.casePending();
    if (!pending || this.caseBusy()) return;
    this.caseBusy.set(true);
    this.caseRequest = this.contexts
      .admitted(this.context())
      .pipe(
        switchMap((context) =>
          this.regression.case(
            context,
            this.mapping().scope,
            pending.id,
            pending.version,
          ),
        ),
      )
      .subscribe({
        next: (value) => {
          if (
            canonicalDefinition(value.definition) !==
            canonicalDefinition(pending.body)
          ) {
            this.caseBusy.set(false);
            this.caseNotice.set(
              "The saved version differs from the original request. Its recovery details are retained.",
            );
          } else this.caseSaved();
        },
        error: () => {
          this.caseBusy.set(false);
          this.caseNotice.set(
            "The original saved version could not be confirmed. Nothing was sent again.",
          );
        },
      });
  }

  retryPreviewCase(): void {
    const pending = this.casePending();
    if (!pending || this.caseBusy() || !this.capabilities()?.publishCases)
      return;
    this.caseBusy.set(true);
    this.caseRequest = this.contexts
      .admitted(this.context())
      .pipe(
        switchMap((context) =>
          this.regression.publish(
            context,
            this.mapping().scope,
            pending.id,
            pending.version,
            pending.body,
          ),
        ),
      )
      .subscribe({
        next: () => this.caseSaved(),
        error: () => {
          this.caseBusy.set(false);
          this.caseNotice.set(
            "The original save is still unconfirmed. Its request was not changed.",
          );
        },
      });
  }

  private caseSaved(): void {
    this.caseBusy.set(false);
    this.casePending.set(null);
    this.caseName.set("");
    this.baselineApproved.set(false);
    this.caseNotice.set(
      "The exact CSV baseline is saved as a test case. Load regression cases to run it.",
    );
  }

  private retainCase(): void {
    if (this.caseOwner && (this.caseName() || this.casePending()))
      this.caseRecovery.keep(this.caseOwner.context, this.caseOwner.scope, {
        name: this.caseName(),
        pending: this.casePending(),
      });
    this.caseRequest?.unsubscribe();
    this.caseBusy.set(false);
    this.casePending.set(null);
    this.caseName.set("");
    this.baselineApproved.set(false);
    this.caseNotice.set(null);
    this.caseOwner = null;
  }

  setParameterKind(id: number, kind: string): void {
    if (
      kind === "string" ||
      kind === "integer" ||
      kind === "float" ||
      kind === "boolean"
    )
      this.preview.updateParameter(id, { kind: kind as PreviewParameterKind });
  }

  async chooseFile(id: number, event: Event): Promise<void> {
    const element = event.target as HTMLInputElement;
    const file = element.files?.[0];
    if (!file) return;
    const epoch = this.inputEpoch;
    const sequence = (this.fileSequence.get(id) ?? 0) + 1;
    this.fileSequence.set(id, sequence);
    if (file.size > previewLimits.inputBytes) {
      element.value = "";
      this.preview.updateInput(id, { file: null });
      return;
    }
    try {
      const bytes = new Uint8Array(await file.arrayBuffer());
      if (
        epoch !== this.inputEpoch ||
        sequence !== this.fileSequence.get(id) ||
        !this.allowed()
      )
        return;
      this.preview.updateInput(id, { file: { name: file.name, bytes } });
    } catch {
      if (epoch === this.inputEpoch && sequence === this.fileSequence.get(id))
        this.outputError.set("unavailable");
    }
  }

  clearFile(id: number, element: HTMLInputElement): void {
    this.fileSequence.set(id, (this.fileSequence.get(id) ?? 0) + 1);
    element.value = "";
    this.preview.updateInput(id, { file: null });
  }

  show(output: PreviewOutput): void {
    this.clearOutput();
    const epoch = this.outputEpoch;
    const runId = this.preview.run()?.runId;
    if (!runId) return;
    this.outputError.set(null);
    this.downloads.push(
      this.preview.output(output).subscribe({
        next: (bytes) => {
          if (
            epoch !== this.outputEpoch ||
            this.preview.run()?.runId !== runId ||
            !this.allowed()
          )
            return;
          this.shown.set({
            runId,
            output,
            display: displayBytes(
              bytes,
              output.targetName,
              output.mediaType,
              output.csvDialect,
            ),
          });
          this.outputSelected.emit();
        },
        error: (error: unknown) => {
          if (epoch === this.outputEpoch) this.outputError.set(kindOf(error));
        },
      }),
    );
  }

  private clearOutput(): void {
    this.outputEpoch++;
    this.downloads.forEach((download) => download.unsubscribe());
    this.downloads = [];
    this.shown.set(null);
    this.outputError.set(null);
  }

  download(output: PreviewOutput): void {
    this.outputError.set(null);
    this.downloads.push(
      this.preview.output(output).subscribe({
        next: (bytes) => {
          const url = URL.createObjectURL(
            new Blob([bytes as BlobPart], { type: output.mediaType }),
          );
          const anchor = this.document.createElement("a");
          anchor.href = url;
          anchor.download = outputFilename(output);
          anchor.click();
          setTimeout(() => URL.revokeObjectURL(url), 0);
        },
        error: (error: unknown) => this.outputError.set(kindOf(error)),
      }),
    );
  }

  targetLabel(output: PreviewOutput): string {
    return output.targetName
      ? output.targetName + (output.targetNameTruncated ? "…" : "")
      : "Primary output";
  }

  outcomeLabel(outcome: string | null): string {
    return previewOutcomeStatus(outcome).label;
  }

  errorText(kind: PreviewErrorKind): string {
    switch (kind) {
      case "notFound":
        return "This preview is not available. It may have expired.";
      case "rejected":
        return "Your session can no longer run previews. Refresh the catalog and try again.";
      case "invalidRequest":
        return "The server refused the preview request. Check the inputs and parameters.";
      case "conflict":
        return "These inputs differ from an earlier unconfirmed submission. Discard it to submit these inputs.";
      case "bodyLimit":
        return "The preview request is too large.";
      case "unsupportedMediaType":
        return "The preview request format was refused.";
      case "timedOut":
        return "The server did not answer in time. Check the run again.";
      case "integrity":
        return "The output bytes did not match their recorded digest.";
      case "incompatible":
        return "The server response could not be verified.";
      default:
        return "Previews are unavailable right now.";
    }
  }
}

function sessionStorageOrNull(document: Document): Storage | null {
  try {
    return document.defaultView?.sessionStorage ?? null;
  } catch {
    return null;
  }
}

function kindOf(error: unknown): PreviewErrorKind {
  return error instanceof PreviewError ? error.kind : "unavailable";
}

function outputFilename(output: PreviewOutput): string {
  const base =
    (output.targetName || "primary")
      .replace(/[^A-Za-z0-9._-]+/g, "_")
      .slice(0, 80) || "output";
  const extension =
    output.mediaType === "application/edi-x12"
      ? "x12"
      : output.mediaType === "application/edifact"
        ? "edi"
        : output.mediaType === "text/csv"
          ? "csv"
          : output.mediaType === "text/tab-separated-values"
            ? "tsv"
            : "json";
  return `preview-${output.ordinal}-${base}.${extension}`;
}
