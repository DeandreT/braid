import {
  DestroyRef,
  Injectable,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import type {
  CatalogContext,
  CatalogMappingScope,
  CatalogProjectScope,
} from "../catalog-types";
import { RetainedViewWork } from "../retained-view-work";
import type {
  NativeMappingUploadInput,
  NativeMappingUploadSource,
} from "./native-mapping-upload-types";
import type { NativeIngestionSource } from "./native-ingestion-leaves";
import type { NativeRevisionAppendHead } from "./native-revision-append-types";
import { UnsentWork } from "../../workbench/unsent-work";
import { BrowserSessionState } from "../../auth/browser-session-state";
import type { MfdReview } from "./mfd-interchange";

interface UnsentFile<Source> {
  readonly source: Source | null;
  /** Retained only while an aborted capture needs to be restarted after fresh admission. */
  readonly file: File | null;
  readonly summary: {
    readonly name: string;
    readonly byteCount: number;
  } | null;
}

export interface MappingUploadDraft extends UnsentFile<NativeMappingUploadSource> {
  readonly input: NativeMappingUploadInput;
}

export interface RevisionAppendDraft extends UnsentFile<NativeIngestionSource> {
  readonly filename: string;
  readonly reviewed: NativeRevisionAppendHead | null;
  readonly reviewRequired: boolean;
}

/** Provided by the upload page. Sent requests remain in the existing tab-memory protocol. */
@Injectable()
export class MappingUploadDraftRecovery extends RetainedViewWork<
  MappingUploadDraft,
  CatalogProjectScope
> {}

/** Provided by the append page; never application-wide or persisted. */
@Injectable()
export class RevisionAppendDraftRecovery extends RetainedViewWork<
  RevisionAppendDraft,
  CatalogMappingScope
> {}

export interface MfdImportFileDraft {
  readonly kind: "draft";
  readonly files: readonly File[];
  readonly name: string;
  readonly description: string;
  readonly design: string;
  readonly designs: readonly string[];
}

export type MfdImportDraft =
  | MfdImportFileDraft
  | {
      readonly kind: "review";
      readonly review: MfdReview;
      readonly attempted: boolean;
      readonly retained: boolean;
    };

/** Retains a page's draft or original pending review, never converting a sent review back to a draft. */
@Injectable()
export class MfdImportDraftRecovery extends RetainedViewWork<
  MfdImportDraft,
  CatalogProjectScope | CatalogMappingScope
> {
  private readonly sessions = inject(BrowserSessionState);
  private readonly retainedConfirmation = signal(false);
  readonly protectedReview = computed(
    () => this.hasWork() && this.retainedConfirmation(),
  );
  readonly discardAcknowledged = signal(false);

  constructor() {
    super();
    inject(UnsentWork).track(() => this.hasWork());
    const warn = (event: BeforeUnloadEvent) => {
      if (this.hasWork()) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    globalThis.addEventListener("beforeunload", warn);
    inject(DestroyRef).onDestroy(() =>
      globalThis.removeEventListener("beforeunload", warn),
    );
    effect(() => {
      if (
        this.sessions.state().kind === "disabled" ||
        this.sessions.action().kind === "logoutAcknowledged"
      )
        untracked(() => this.discard());
    });
  }

  override keep(
    context: CatalogContext,
    scope: CatalogProjectScope | CatalogMappingScope,
    work: MfdImportDraft,
  ): void {
    if (
      this.sessions.state().kind === "disabled" ||
      this.sessions.action().kind === "logoutAcknowledged"
    ) {
      this.discard();
      return;
    }
    super.keep(context, scope, work);
    this.retainedConfirmation.set(work.kind === "review" && work.attempted);
    this.discardAcknowledged.set(false);
  }

  requestDiscard(): void {
    if (!this.protectedReview() || this.discardAcknowledged()) {
      this.discard();
      this.discardAcknowledged.set(false);
    }
  }
}
