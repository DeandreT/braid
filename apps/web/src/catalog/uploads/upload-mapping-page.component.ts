import { DOCUMENT } from "@angular/common";
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
import { FormControl, FormGroup, ReactiveFormsModule } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { Observable, combineLatest, of, type Subscriber } from "rxjs";
import { catalogRoutes, isCatalogId } from "../catalog-types";
import {
  NativeMappingUploadState,
  type NativeMappingUploadLeaveRisk,
} from "./native-mapping-upload-state";
import type {
  NativeMappingUploadErrorKind,
  NativeMappingUploadFieldError,
  NativeMappingUploadRejection,
} from "./native-mapping-upload-types";

@Component({
  selector: "weft-upload-mapping-page",
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: "./upload-mapping-page.component.html",
  styleUrl: "./upload-mapping-page.component.css",
})
export class UploadMappingPageComponent {
  readonly state = inject(NativeMappingUploadState);
  readonly routes = catalogRoutes;
  readonly leaveDecision = signal<NativeMappingUploadLeaveRisk>(null);
  readonly abandonDecision = signal(false);
  readonly abandonAcknowledged = signal(false);
  readonly form = new FormGroup({
    name: new FormControl("", { nonNullable: true }),
    description: new FormControl("", { nonNullable: true }),
    originalFilename: new FormControl("", { nonNullable: true }),
  });
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private focusRequest = 0;
  private leaveChoice: Subscriber<boolean> | null = null;
  private returnFocusId = "upload-title";
  private lastEditingOwner = "";
  private lastFocus = "";
  private destroyed = false;

  constructor() {
    const route = inject(ActivatedRoute),
      destroyRef = inject(DestroyRef);
    combineLatest([route.paramMap, route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        this.abandonDecision.set(false);
        this.abandonAcknowledged.set(false);
        const organizationId = params.get("organizationId"),
          projectId = params.get("projectId");
        this.state.select(
          query.keys.length === 0 &&
            isCatalogId(organizationId) &&
            isCatalogId(projectId)
            ? { organizationId, projectId }
            : null,
        );
      });
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      const value = this.form.getRawValue();
      this.state.setDraft(
        value.name,
        value.description,
        value.originalFilename,
      );
    });
    effect(() => {
      const draft = this.state.draft();
      untracked(() => {
        const value = this.form.getRawValue();
        if (
          value.name !== draft.name ||
          value.description !== draft.description ||
          value.originalFilename !== draft.originalFilename
        )
          this.form.setValue(draft, { emitEvent: false });
      });
    });
    effect(() => {
      // Admission may finish and dispatch while a draft-only leave decision is open.
      if (
        this.leaveDecision() === "draft" &&
        this.state.leaveRisk() === "recovery"
      )
        this.leaveDecision.set("recovery");
    });
    effect(() => {
      const errors = this.state.fieldErrors(),
        fileError = this.state.fileError();
      const acknowledgement = this.state.acknowledgement(),
        operation = this.state.operationStatus();
      const context = this.state.context(),
        project = this.state.project();
      if (this.leaveDecision()) return;
      let focus = "",
        target = "";
      if (
        errors.name ||
        errors.description ||
        errors.originalFilename ||
        errors.request ||
        fileError
      ) {
        focus = `errors/${errors.name}/${errors.description}/${errors.originalFilename}/${errors.request}/${fileError?.kind}`;
        target = "upload-errors";
      } else if (acknowledgement) {
        focus = "saved";
        target = "upload-saved";
      } else if (this.state.quarantined()) {
        focus = "quarantined";
        target = "upload-quarantine";
      } else if (this.state.outcomeUnknown()) {
        focus = `unknown/${this.state.waitingStopped()}`;
        target = "upload-unknown";
      } else if (operation) {
        focus = `operation/${operation.phase}/${operation.claimCount}/${this.state.response()?.outcome}`;
        target = "upload-operation";
      } else if (context && project && this.state.canEdit()) {
        const owner = `${context.actorPrincipalId}/${project.scope.organizationId}/${project.scope.projectId}`;
        if (owner !== this.lastEditingOwner) {
          this.lastEditingOwner = owner;
          focus = `editing/${owner}`;
          target = "upload-name";
        }
      }
      if (focus && focus !== this.lastFocus) {
        this.lastFocus = focus;
        this.queueFocus(target);
      }
    });
    const window = this.document.defaultView;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const refresh = () => {
      if (
        this.document.visibilityState !== "visible" ||
        timer !== null ||
        this.state.busy() ||
        this.state.preparingFile() ||
        this.leaveDecision()
      )
        return;
      timer = setTimeout(() => {
        timer = null;
        if (
          this.document.visibilityState === "visible" &&
          !this.state.busy() &&
          !this.state.preparingFile() &&
          !this.leaveDecision()
        )
          this.state.refreshAccess();
      }, 100);
    };
    window?.addEventListener("focus", refresh);
    this.document.addEventListener("visibilitychange", refresh);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      window?.removeEventListener("focus", refresh);
      this.document.removeEventListener("visibilitychange", refresh);
      if (timer !== null) clearTimeout(timer);
      this.resolveLeave(false);
      this.state.deactivate();
    });
  }

  requestLeave(): Observable<boolean> {
    const risk = this.state.leaveRisk();
    if (!risk) return of(true);
    return new Observable<boolean>((subscriber) => {
      this.resolveLeave(false);
      const focused = this.document.activeElement?.id;
      this.returnFocusId =
        focused &&
        [
          "upload-name",
          "upload-description",
          "upload-filename",
          "upload-file",
        ].includes(focused)
          ? focused
          : "upload-title";
      this.leaveChoice = subscriber;
      this.leaveDecision.set(risk);
      this.queueFocus("upload-leave");
      return () => {
        if (this.leaveChoice === subscriber) {
          this.leaveChoice = null;
          this.leaveDecision.set(null);
        }
      };
    });
  }

  stay(): void {
    this.resolveLeave(false);
    this.queueFocus(this.returnFocusId);
  }

  confirmLeave(): void {
    if (!this.leaveChoice) return;
    if (
      this.leaveDecision() === "draft" &&
      this.state.leaveRisk() === "recovery"
    ) {
      this.leaveDecision.set("recovery");
      this.queueFocus("upload-leave");
      return;
    }
    if (this.leaveDecision() === "draft") this.state.discardDraft();
    else this.state.stopWaiting();
    this.resolveLeave(true);
  }

  selectFile(event: Event): void {
    this.lastFocus = "";
    const input = event.target as HTMLInputElement;
    const file = input.files?.item(0) ?? null;
    if (!this.leaveDecision()) {
      this.state.selectFile(file);
      const filename = this.state.draft().originalFilename;
      if (this.form.controls.originalFilename.value !== filename)
        this.form.controls.originalFilename.setValue(filename, {
          emitEvent: false,
        });
    }
    // Recovery owns an immutable private snapshot, never a live FileList or path.
    input.value = "";
  }

  submit(): void {
    if (this.leaveDecision()) return;
    this.lastFocus = "";
    const value = this.form.getRawValue();
    this.state.setDraft(value.name, value.description, value.originalFilename);
    this.state.upload();
  }

  abandon(): void {
    this.state.abandonOriginal(this.abandonAcknowledged());
    this.lastFocus = "";
    this.lastEditingOwner = "";
    this.abandonDecision.set(false);
    this.abandonAcknowledged.set(false);
    this.queueFocus("upload-name");
  }

  startAnother(): void {
    this.state.startAnother();
    this.lastFocus = "";
    this.lastEditingOwner = "";
    this.queueFocus("upload-name");
  }
  focusField(
    field: "name" | "description" | "originalFilename" | "file",
  ): void {
    this.queueFocus(
      field === "name"
        ? "upload-name"
        : field === "description"
          ? "upload-description"
          : field === "file"
            ? "upload-file"
            : "upload-filename",
    );
  }

  fieldError(
    error: NativeMappingUploadFieldError,
    field: "name" | "description" | "originalFilename",
  ): string {
    if (error === "required")
      return field === "name"
        ? "Enter a mapping name."
        : "Enter a saved filename.";
    if (error === "invalidUnicode")
      return "Use complete Unicode characters; this value contains an unmatched surrogate.";
    if (error === "nul") return "Remove the null character from this value.";
    if (error === "invalidType") return "Enter a text value.";
    if (error === "unsafeFilename")
      return "Use a safe filename, without path separators or control characters, and not a single or double dot.";
    if (error === "scalarLimit")
      return field === "name"
        ? "Use no more than 200 Unicode characters after surrounding whitespace is removed."
        : "Use no more than 4,000 Unicode characters.";
    return field === "name"
      ? "The accepted name must fit within 800 UTF-8 bytes."
      : field === "description"
        ? "The description must fit within 16,000 UTF-8 bytes."
        : "The saved filename must fit within 255 UTF-8 bytes.";
  }

  rejectionMessage(rejection: NativeMappingUploadRejection): string {
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
        return "This native file uses an unsupported artifact form.";
      case "resourceLimit":
        return "Native admission reached a service resource limit.";
      default:
        return "The operation ended without a committed revision. A status read does not disclose its private rejection reason.";
    }
  }

  errorMessage(kind: NativeMappingUploadErrorKind): string {
    return {
      invalidInput: "Check the upload fields and selected file.",
      emptyNative: "Choose a nonempty native file.",
      nativeLimit:
        "Choose a native file no larger than 4 MiB. This service may have a lower limit.",
      fileReadUnavailable:
        "The selected file could not be read. Choose it again before uploading.",
      hashUnavailable:
        "This browser could not verify the selected file's bytes. No upload was sent.",
      canceled:
        "File preparation was stopped. No upload was sent by file preparation.",
      identityChanged:
        "Your identity or project changed. The original upload was not sent as another identity.",
      identityRejected:
        "Your identity could not be admitted. Private upload information is hidden.",
      featureDisabled:
        "Native mapping upload is not enabled for this workspace.",
      uploadUnavailable:
        "You do not currently have permission to upload a mapping in this project.",
      invalidRequest: "The service refused this upload request as invalid.",
      notFound:
        "This project or upload is unavailable to your current identity. Private upload information is hidden.",
      bodyLimit: "The service refused the upload body size.",
      unsupportedMediaType: "The service did not accept this upload format.",
      ingestionAdmissionBusy:
        "Upload admission is busy. No automatic retry was made.",
      persistenceFailure: "The service could not confirm this upload call.",
      internalFailure: "The service could not confirm this upload call.",
      timedOut: "Waiting timed out. A sent upload may already have saved.",
      unavailable:
        "This operation could not be confirmed. No automatic upload or resume was made.",
      incompatible:
        "The reply could not be verified as the original upload operation.",
    }[kind];
  }

  private resolveLeave(leave: boolean): void {
    const subscriber = this.leaveChoice;
    this.leaveChoice = null;
    this.leaveDecision.set(null);
    subscriber?.next(leave);
    subscriber?.complete();
  }
  private queueFocus(target: string): void {
    const request = ++this.focusRequest;
    // Conditional notices must be in the rendered view before they receive focus.
    afterNextRender(
      () => {
        if (
          !this.destroyed &&
          request === this.focusRequest &&
          (target === "upload-leave"
            ? this.leaveDecision() !== null
            : this.leaveDecision() === null && !this.abandonDecision())
        )
          this.document.getElementById(target)?.focus();
      },
      { injector: this.injector },
    );
  }
}
