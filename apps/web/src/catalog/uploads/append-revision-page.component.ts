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
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { FormControl, FormGroup, ReactiveFormsModule } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { combineLatest, type Observable } from "rxjs";
import { catalogRoutes, isCatalogId } from "../catalog-types";
import { CatalogContextStore } from "../catalog-context";
import type { NativeIngestionSourceErrorKind } from "./native-ingestion-leaves";
import { NativeRevisionAppendState } from "./native-revision-append-state";
import type {
  NativeRevisionAppendErrorKind,
  NativeRevisionAppendFilenameError,
  NativeRevisionAppendInputErrors,
  NativeRevisionAppendRejection,
} from "./native-revision-append-types";

@Component({
  selector: "weft-append-revision-page",
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: "./append-revision-page.component.html",
  styleUrl: "./append-revision-page.component.css",
})
export class AppendRevisionPageComponent {
  readonly state = inject(NativeRevisionAppendState);
  readonly routes = catalogRoutes;
  readonly abandonDecision = signal(false);
  readonly abandonAcknowledged = signal(false);
  readonly uiBlocked = computed(
    () => this.state.leavePrompt() !== null || this.abandonDecision(),
  );
  readonly form = new FormGroup({
    originalFilename: new FormControl("", { nonNullable: true }),
  });
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly contexts = inject(CatalogContextStore);
  private focusGeneration = 0;
  private lastStateFocus = -1;
  private returnFocusId = "append-title";
  private destroyed = false;

  constructor() {
    const route = inject(ActivatedRoute),
      destroyRef = inject(DestroyRef);
    let timer: ReturnType<typeof setTimeout> | null = null;
    const invalidation = this.contexts.invalidated.subscribe(() => {
      if (timer !== null) clearTimeout(timer);
      timer = null;
      ++this.focusGeneration;
      this.abandonDecision.set(false);
      this.abandonAcknowledged.set(false);
    });
    combineLatest([route.paramMap, route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        ++this.focusGeneration;
        this.abandonDecision.set(false);
        this.abandonAcknowledged.set(false);
        this.form.setValue({ originalFilename: "" }, { emitEvent: false });
        const organizationId = params.get("organizationId"),
          projectId = params.get("projectId"),
          mappingId = params.get("mappingId");
        this.state.select(
          query.keys.length === 0 &&
            isCatalogId(organizationId) &&
            isCatalogId(projectId) &&
            isCatalogId(mappingId)
            ? { organizationId, projectId, mappingId }
            : null,
        );
      });
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.state.setFilename(this.form.getRawValue().originalFilename);
    });
    effect(() => {
      const draft = this.state.view().draft;
      untracked(() => {
        if (this.form.getRawValue().originalFilename !== draft.originalFilename)
          this.form.setValue(draft, { emitEvent: false });
      });
    });
    effect(() => {
      if (this.abandonDecision() && !this.state.view().canAbandon) {
        this.abandonDecision.set(false);
        this.abandonAcknowledged.set(false);
      }
    });
    effect(() => {
      const prompt = this.state.leavePrompt(),
        request = this.state.focusRequest();
      if (!this.contexts.visible()) return;
      if (prompt) {
        this.queueFocus("append-leave");
        return;
      }
      if (
        !this.abandonDecision() &&
        request &&
        request.sequence !== this.lastStateFocus
      ) {
        const target = {
          summary: "append-errors",
          status: "append-status",
          review: "append-review",
          filename: "append-filename",
        }[request.target];
        this.queueFocus(target, request.sequence);
      }
    });
    const window = this.document.defaultView;
    const canRefresh = () => {
      const view = this.state.view();
      return (
        this.document.visibilityState === "visible" &&
        !view.quarantined &&
        view.work === "idle" &&
        !view.preparingFile &&
        !this.uiBlocked()
      );
    };
    const refresh = () => {
      if (timer !== null || !canRefresh()) return;
      timer = setTimeout(() => {
        timer = null;
        if (canRefresh()) this.state.refreshAccess();
      }, 100);
    };
    window?.addEventListener("focus", refresh);
    this.document.addEventListener("visibilitychange", refresh);
    destroyRef.onDestroy(() => {
      invalidation.unsubscribe();
      this.destroyed = true;
      ++this.focusGeneration;
      window?.removeEventListener("focus", refresh);
      this.document.removeEventListener("visibilitychange", refresh);
      if (timer !== null) clearTimeout(timer);
      this.state.deactivate();
    });
  }

  requestLeave(): Observable<boolean> {
    const focused = this.document.activeElement?.id;
    this.returnFocusId =
      focused && ["append-file", "append-filename"].includes(focused)
        ? focused
        : "append-title";
    return this.state.requestLeave();
  }

  stay(): void {
    this.state.stay();
    this.queueFocus(this.returnFocusId);
  }

  selectFile(event: Event): void {
    const input = event.target as HTMLInputElement,
      file = input.files?.item(0) ?? null;
    if (!this.uiBlocked()) {
      this.state.selectFile(file);
      const filename = this.state.view().draft.originalFilename;
      if (this.form.controls.originalFilename.value !== filename)
        this.form.controls.originalFilename.setValue(filename, {
          emitEvent: false,
        });
    }
    // Only the state-owned immutable snapshot may survive this input event.
    input.value = "";
  }

  submit(): void {
    if (this.uiBlocked()) return;
    this.state.setFilename(this.form.getRawValue().originalFilename);
    this.state.append();
  }

  openAbandon(): void {
    if (this.uiBlocked() || !this.state.view().canAbandon) return;
    this.abandonAcknowledged.set(false);
    this.abandonDecision.set(true);
    this.queueFocus("append-abandon");
  }

  keepOriginal(): void {
    this.abandonDecision.set(false);
    this.abandonAcknowledged.set(false);
    this.queueFocus("append-status");
  }

  abandon(): void {
    if (
      this.state.leavePrompt() ||
      !this.abandonAcknowledged() ||
      !this.state.view().canAbandon
    )
      return;
    this.state.abandonOriginal(true);
    this.abandonDecision.set(false);
    this.abandonAcknowledged.set(false);
  }

  focusField(event: Event, field: "filename" | "file" | "review"): void {
    event.preventDefault();
    if (!this.uiBlocked())
      this.queueFocus(
        field === "filename"
          ? "append-filename"
          : field === "file"
            ? "append-file"
            : "append-review",
      );
  }

  filenameError(error: NativeRevisionAppendFilenameError): string {
    return {
      required: "Enter a saved filename.",
      invalidType: "Enter a text value for the saved filename.",
      invalidUnicode:
        "Use complete Unicode characters; the filename contains an unmatched surrogate.",
      nul: "Remove the null character from the filename.",
      unsafeFilename:
        "Use a safe filename without path separators or control characters, and not a single or double dot.",
      byteLimit: "The saved filename must fit within 255 UTF-8 bytes.",
    }[error];
  }

  requestError(error: NativeRevisionAppendInputErrors["request"]): string {
    switch (error) {
      case "invalidScope":
        return "The mapping scope could not be verified. Open the mapping from the catalog again.";
      case "invalidHead":
        return "Review a valid current head before sending a new append.";
      case "queryLimit":
      case "requestLineLimit":
        return "The encoded append request is too long. Shorten the saved filename.";
      default:
        return "";
    }
  }

  sourceError(error: NativeIngestionSourceErrorKind): string {
    return {
      invalidInput: "Choose one native mapping file.",
      emptyNative: "Choose a nonempty native mapping file.",
      nativeLimit:
        "Choose a native file no larger than 4 MiB. This service may have a lower limit.",
      fileReadUnavailable:
        "The selected file could not be read. Choose it again before appending.",
      hashUnavailable:
        "This browser could not verify the selected bytes. File preparation sent no append.",
      canceled: "File preparation stopped. File preparation sent no append.",
    }[error];
  }

  errorMessage(kind: NativeRevisionAppendErrorKind): string {
    return {
      invalidInput: "Check the saved filename and selected file.",
      identityChanged:
        "Your identity or mapping changed. The original append was not sent as another identity.",
      identityRejected:
        "Your identity could not be admitted. Private append details are hidden.",
      featureDisabled:
        "Native revision upload is not enabled for this workspace.",
      uploadUnavailable:
        "You do not currently have permission to upload a revision to this mapping.",
      unsupportedBackend:
        "This form supports mappings whose persisted backend is Ferrule. Open this mapping through the catalog to view it.",
      headReviewRequired:
        "The current head differs from the head you reviewed. Your file draft is retained; review the current head before sending a new append.",
      invalidRequest: "The service refused this append request as invalid.",
      notFound:
        "This mapping or append is unavailable to your current identity. Private append details are hidden.",
      bodyLimit: "The service refused this call's native-file size.",
      unsupportedMediaType: "The service did not accept this append format.",
      ingestionAdmissionBusy:
        "Append admission is busy. No automatic retry was made.",
      persistenceFailure: "The service could not confirm this append call.",
      internalFailure: "The service could not confirm this append call.",
      timedOut: "Waiting timed out. An append already sent may have saved.",
      unavailable:
        "This call could not be confirmed. No automatic append, status check or resume was made.",
      incompatible:
        "The reply could not be verified as the original append operation.",
    }[kind];
  }

  rejectionMessage(rejection: NativeRevisionAppendRejection): string {
    switch (rejection) {
      case "emptyNative":
        return "The service refused an empty native file.";
      case "nativeLimit":
        return "The file exceeds this service's native-file limit, which may be lower than 4 MiB.";
      case "unregisteredBackend":
        return "Ferrule native upload is not registered in this service.";
      case "nativeDecode":
        return "The native file could not be decoded.";
      case "unsupportedArtifact":
        return "The native file uses an unsupported artifact form.";
      case "resourceLimit":
        return "Native admission reached a service resource limit.";
      default:
        return "This own operation ended without a committed revision. A status read does not disclose its private rejection reason.";
    }
  }

  private queueFocus(target: string, stateSequence?: number): void {
    const generation = ++this.focusGeneration;
    afterNextRender(
      () => {
        if (
          !this.destroyed &&
          generation === this.focusGeneration &&
          this.contexts.visible() &&
          (stateSequence === undefined ||
            this.state.focusRequest()?.sequence === stateSequence) &&
          (target === "append-leave"
            ? this.state.leavePrompt() !== null
            : target === "append-abandon"
              ? this.abandonDecision() && this.state.leavePrompt() === null
              : !this.uiBlocked())
        ) {
          const element = this.document.getElementById(target);
          element?.focus();
          if (
            element &&
            this.document.activeElement === element &&
            stateSequence !== undefined
          )
            this.lastStateFocus = stateSequence;
        }
      },
      { injector: this.injector },
    );
  }
}
