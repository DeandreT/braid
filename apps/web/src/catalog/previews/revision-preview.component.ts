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
import { map, type Subscription } from "rxjs";
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
  readonly context = input.required<CatalogContext>();
  readonly mapping = input.required<CatalogMapping>();
  readonly revision = input.required<CatalogRevision>();
  readonly workbench = input(false);
  readonly outputSelected = output<void>();
  private outputEpoch = 0;
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
    inject(UnsentWork).track(() =>
      hasUnsentPreview({
        inputs: this.preview.inputs(),
        parameters: this.preview.parameters(),
        targetKind: this.preview.targetKind(),
        targetName: this.preview.targetName(),
        documentProfile: this.preview.documentProfile(),
        busy: this.preview.busy(),
        pendingKey: this.preview.pendingKey(),
      }),
    );
    effect(() => {
      const revision = this.revision();
      const allowed = this.allowed();
      untracked(() => {
        if (!allowed) {
          this.preview.unbind();
          return;
        }
        this.preview.bind({
          scope: revision.scope,
          admit: () =>
            this.contexts
              .admitted(this.context())
              .pipe(map((context) => context.requestToken)),
          ready: () => this.contexts.canSend(this.context()),
        });
      });
    });
    const outputRun = computed(() => this.preview.run()?.runId ?? null);
    effect(() => {
      this.context();
      this.mapping();
      this.revision();
      outputRun();
      untracked(() => this.clearOutput());
    });
    const invalidation = this.contexts.invalidated.subscribe(() =>
      this.clearOutput(),
    );
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.downloads.forEach((download) => download.unsubscribe());
      this.preview.destroy();
    });
  }

  setTarget(kind: string): void {
    if (kind === "all" || kind === "primary" || kind === "named")
      this.preview.targetKind.set(kind as PreviewTargetKind);
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
    if (file.size > previewLimits.inputBytes) {
      element.value = "";
      this.preview.updateInput(id, { file: null });
      return;
    }
    const bytes = new Uint8Array(await file.arrayBuffer());
    this.preview.updateInput(id, { file: { name: file.name, bytes } });
  }

  clearFile(id: number, element: HTMLInputElement): void {
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
            display: displayBytes(bytes, output.targetName, output.mediaType),
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
        : "json";
  return `preview-${output.ordinal}-${base}.${extension}`;
}
