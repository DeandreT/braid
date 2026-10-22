import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from "@angular/core";
import type { Subscription } from "rxjs";
import type {
  CatalogContext,
  CatalogMapping,
  CatalogRevision,
} from "../catalog-types";
import { PreviewApi } from "./preview-api";
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

const shownBytes = 256 * 1024;
const decoder = new TextDecoder("utf-8", { fatal: false });

@Component({
  selector: "weft-revision-preview",
  imports: [StatusChipComponent],
  templateUrl: "./revision-preview.component.html",
  styleUrl: "./revision-preview.component.css",
})
export class RevisionPreviewComponent {
  readonly runStatus = previewRunStatus;
  private readonly document = inject(DOCUMENT);
  private downloads: Subscription[] = [];
  readonly context = input.required<CatalogContext>();
  readonly mapping = input.required<CatalogMapping>();
  readonly revision = input.required<CatalogRevision>();
  readonly preview = new PreviewState(
    inject(PreviewApi),
    sessionStorageOrNull(this.document),
  );
  readonly limits = previewLimits;
  readonly allowed = computed(() => this.mapping().access.actions.preview);
  readonly shown = signal<{
    ordinal: number;
    text: string;
    truncated: boolean;
  } | null>(null);
  readonly outputError = signal<PreviewErrorKind | null>(null);

  constructor() {
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
          requestToken: () => this.context().requestToken || null,
        });
      });
    });
    inject(DestroyRef).onDestroy(() => {
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
    this.outputError.set(null);
    this.downloads.push(
      this.preview.output(output).subscribe({
        next: (bytes) =>
          this.shown.set({
            ordinal: output.ordinal,
            text: decoder.decode(bytes.subarray(0, shownBytes)),
            truncated: bytes.length > shownBytes,
          }),
        error: (error: unknown) => this.outputError.set(kindOf(error)),
      }),
    );
  }

  download(output: PreviewOutput): void {
    this.outputError.set(null);
    this.downloads.push(
      this.preview.output(output).subscribe({
        next: (bytes) => {
          const url = URL.createObjectURL(
            new Blob([bytes as BlobPart], { type: "application/json" }),
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
  return `preview-${output.ordinal}-${base}.json`;
}
