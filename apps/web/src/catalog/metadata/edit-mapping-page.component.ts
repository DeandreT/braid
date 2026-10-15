import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
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
import { MappingMetadataState } from "./mapping-metadata-state";
import type {
  MappingMetadataErrorKind,
  MappingMetadataFieldError,
} from "./mapping-metadata-types";

@Component({
  selector: "weft-edit-mapping-page",
  providers: [MappingMetadataState],
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: "./edit-mapping-page.component.html",
  styleUrl: "./edit-mapping-page.component.css",
})
export class EditMappingPageComponent {
  readonly state = inject(MappingMetadataState);
  readonly routes = catalogRoutes;
  readonly leaveDecision = signal(false);
  readonly form = new FormGroup({
    name: new FormControl("", { nonNullable: true }),
    description: new FormControl("", { nonNullable: true }),
  });
  private readonly document = inject(DOCUMENT);
  private leaveChoice: Subscriber<boolean> | null = null;
  private returnFocusId = "metadata-title";
  private lastEditingOwner = "";
  private lastFocus = "";
  private destroyed = false;

  constructor() {
    const route = inject(ActivatedRoute);
    const destroyRef = inject(DestroyRef);
    combineLatest([route.paramMap, route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
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
      const value = this.form.getRawValue();
      this.state.setDraft(value.name, value.description);
    });
    effect(() => {
      const draft = this.state.draft();
      untracked(() => {
        const current = this.form.getRawValue();
        if (
          current.name !== draft.name ||
          current.description !== draft.description
        )
          this.form.setValue(draft, { emitEvent: false });
      });
    });
    effect(() => {
      const errors = this.state.fieldErrors(),
        saved = this.state.saved(),
        comparison = this.state.comparison();
      const context = this.state.context(),
        metadata = this.state.metadata();
      if (this.leaveDecision()) return;
      let focus = "",
        target = "";
      if (errors.name || errors.description || errors.body) {
        focus = `errors/${errors.name}/${errors.description}/${errors.body}`;
        target = "metadata-errors";
      } else if (saved) {
        focus = `saved/${saved.metadata.version}/${saved.changed}`;
        target = "metadata-saved";
      } else if (comparison && this.state.conflict()) {
        focus = `review/${comparison.current.version}`;
        target = "metadata-review";
      } else if (context && metadata && this.state.canEdit()) {
        const owner = `${context.actorPrincipalId}/${metadata.scope.organizationId}/${metadata.scope.projectId}/${metadata.scope.mappingId}`;
        if (owner !== this.lastEditingOwner) {
          this.lastEditingOwner = owner;
          focus = `editing/${owner}`;
          target = "metadata-name";
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
        this.leaveDecision()
      )
        return;
      timer = setTimeout(() => {
        timer = null;
        if (
          this.document.visibilityState === "visible" &&
          !this.state.busy() &&
          !this.leaveDecision()
        )
          this.state.refreshAdmission();
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
      this.state.leave();
    });
  }

  /** Root's CanDeactivate guard forwards here, including mapping/query changes on a reused route. */
  requestLeave(): Observable<boolean> {
    if (!this.state.needsLeaveDecision()) return of(true);
    return new Observable<boolean>((subscriber) => {
      this.resolveLeave(false);
      const focusedId = this.document.activeElement?.id;
      this.returnFocusId =
        focusedId === "metadata-name" || focusedId === "metadata-description"
          ? focusedId
          : "metadata-title";
      this.leaveChoice = subscriber;
      this.leaveDecision.set(true);
      this.queueFocus("metadata-leave");
      return () => {
        if (this.leaveChoice === subscriber) {
          this.leaveChoice = null;
          this.leaveDecision.set(false);
        }
      };
    });
  }

  keepEditing(): void {
    const target = this.returnFocusId;
    this.resolveLeave(false);
    this.queueFocus(target);
  }

  discardAndLeave(): void {
    if (!this.leaveChoice) return;
    this.state.discard();
    this.resolveLeave(true);
  }

  submit(): void {
    if (this.leaveDecision()) return;
    const value = this.form.getRawValue();
    this.state.setDraft(value.name, value.description);
    this.state.save();
  }

  editMine(): void {
    this.state.editMine();
    this.queueFocus("metadata-name");
  }

  focusField(name: "name" | "description"): void {
    this.document
      .getElementById(
        name === "name" ? "metadata-name" : "metadata-description",
      )
      ?.focus();
  }

  fieldError(
    error: MappingMetadataFieldError,
    field: "name" | "description",
  ): string {
    if (error === "required") return "Enter a mapping name.";
    if (error === "invalidUnicode")
      return "Use complete Unicode characters; this value contains an unmatched surrogate.";
    if (error === "nul") return "Remove the null character from this value.";
    if (error === "invalidType") return "Enter a text value.";
    if (error === "scalarLimit")
      return field === "name"
        ? "Use no more than 200 Unicode characters after surrounding whitespace is removed."
        : "Use no more than 4,000 Unicode characters.";
    return field === "name"
      ? "The accepted name must fit within 800 UTF-8 bytes."
      : "The description must fit within 16,000 UTF-8 bytes.";
  }

  errorMessage(kind: MappingMetadataErrorKind): string {
    return {
      invalidInput: "Check the metadata fields before saving.",
      identityChanged:
        "Your identity or organization changed. The previous editor information was cleared; refresh admission to start a new edit.",
      identityRejected:
        "Your identity could not access this mapping. Private editor details are hidden.",
      featureDisabled: "Metadata editing is not enabled for this workspace.",
      editUnavailable:
        "You do not currently have permission to edit this mapping.",
      notFound:
        "This mapping is unavailable to your current identity. Private editor details are hidden.",
      invalidRequest: "The service refused this metadata request as invalid.",
      preconditionFailed:
        "The mapping metadata changed. Review latest before deciding what to save.",
      preconditionRequired:
        "A current metadata validator is required. Review latest before saving.",
      versionExhausted:
        "This metadata version cannot advance further. No different version was adopted.",
      bodyLimit: "The service refused the size of this metadata request.",
      unsupportedMediaType:
        "The service did not accept the metadata request format.",
      persistenceFailure:
        "The service could not confirm this metadata operation.",
      internalFailure: "The service could not confirm this metadata operation.",
      timedOut: "Waiting timed out. A sent save may already have committed.",
      unavailable:
        "This operation could not be confirmed. No automatic save or retry was made.",
      incompatible:
        "The reply could not be verified as this mapping's metadata.",
    }[kind];
  }

  private resolveLeave(leave: boolean): void {
    const subscriber = this.leaveChoice;
    this.leaveChoice = null;
    this.leaveDecision.set(false);
    subscriber?.next(leave);
    subscriber?.complete();
  }

  private queueFocus(target: string): void {
    queueMicrotask(() => {
      if (!this.destroyed) this.document.getElementById(target)?.focus();
    });
  }
}
