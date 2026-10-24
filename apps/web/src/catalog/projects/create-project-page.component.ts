import { DatePipe, DOCUMENT } from "@angular/common";
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
import { combineLatest } from "rxjs";
import { catalogRoutes, isCatalogId } from "../catalog-types";
import { CatalogContextStore } from "../catalog-context";
import { ProjectCreationState } from "./project-creation-state";
import type {
  ProjectCreationErrorKind,
  ProjectCreationFieldError,
} from "./project-creation-types";

@Component({
  selector: "weft-create-project-page",
  imports: [DatePipe, ReactiveFormsModule, RouterLink],
  templateUrl: "./create-project-page.component.html",
  styleUrl: "./create-project-page.component.css",
})
export class CreateProjectPageComponent {
  readonly state = inject(ProjectCreationState);
  readonly routes = catalogRoutes;
  readonly abandonDecision = signal(false);
  readonly abandonAcknowledged = signal(false);
  readonly form = new FormGroup({
    name: new FormControl("", { nonNullable: true }),
    description: new FormControl("", { nonNullable: true }),
  });
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly contexts = inject(CatalogContextStore);
  private lastFocus = "";
  private lastEditingOwner = "";
  private destroyed = false;
  private focusGeneration = 0;

  constructor() {
    const route = inject(ActivatedRoute);
    const destroyRef = inject(DestroyRef);
    let focusTimer: ReturnType<typeof setTimeout> | null = null;
    const invalidation = this.contexts.invalidated.subscribe(() => {
      if (focusTimer !== null) clearTimeout(focusTimer);
      focusTimer = null;
      ++this.focusGeneration;
      this.abandonDecision.set(false);
      this.abandonAcknowledged.set(false);
    });
    combineLatest([route.paramMap, route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        this.abandonDecision.set(false);
        this.abandonAcknowledged.set(false);
        const organizationId = params.get("organizationId");
        this.state.select(
          query.keys.length === 0 && isCatalogId(organizationId)
            ? organizationId
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
      const context = this.state.context();
      const errors = this.state.fieldErrors();
      const attempt = this.state.attemptSummary();
      const confirmation = this.state.confirmation();
      // Focus waits for the access check to settle; a boundary clears the queued request.
      if (!this.contexts.settled()) return;
      let focus = "";
      let target = "";
      let editingOwner = "";
      if (errors.name || errors.description || errors.body) {
        focus = `errors/${errors.name}/${errors.description}/${errors.body}`;
        target = "creation-errors";
      } else if (context && confirmation) {
        focus = `confirmed/${confirmation.body.receipt.project.projectId}`;
        target = "creation-result";
      } else if (context && this.state.quarantined()) {
        focus = `quarantine/${context.actorPrincipalId}`;
        target = "creation-quarantine";
      } else if (context && attempt) {
        focus = `attempt/${attempt.uncertain}/${attempt.error?.kind}/${attempt.stopped}`;
        target = "creation-attempt";
      } else if (context && this.state.canEdit()) {
        const owner = `${context.allowedOrganizationId}/${context.actorPrincipalId}`;
        if (owner !== this.lastEditingOwner) {
          editingOwner = owner;
          focus = `editing/${owner}`;
          target = "project-name";
        }
      }
      if (focus && focus !== this.lastFocus) {
        this.queueFocus(target, () => {
          this.lastFocus = focus;
          if (editingOwner) this.lastEditingOwner = editingOwner;
        });
      }
    });
    const window = this.document.defaultView;
    const refresh = () => {
      if (
        this.document.visibilityState !== "visible" ||
        focusTimer !== null ||
        this.state.busy()
      )
        return;
      focusTimer = setTimeout(() => {
        focusTimer = null;
        if (this.document.visibilityState === "visible" && !this.state.busy())
          this.state.refresh();
      }, 100);
    };
    window?.addEventListener("focus", refresh);
    this.document.addEventListener("visibilitychange", refresh);
    destroyRef.onDestroy(() => {
      invalidation.unsubscribe();
      this.destroyed = true;
      window?.removeEventListener("focus", refresh);
      this.document.removeEventListener("visibilitychange", refresh);
      if (focusTimer !== null) clearTimeout(focusTimer);
      this.state.leave();
    });
  }

  submit(): void {
    const value = this.form.getRawValue();
    this.state.setDraft(value.name, value.description);
    this.state.create();
  }

  abandon(): void {
    this.state.abandonOriginal(this.abandonAcknowledged());
    this.abandonDecision.set(false);
    this.abandonAcknowledged.set(false);
    this.queueFocus("project-name");
  }

  editRefused(): void {
    this.state.editRefused();
    this.queueFocus("project-name");
  }

  startAnother(): void {
    this.state.startAnother();
    this.queueFocus("project-name");
  }

  focusField(name: "name" | "description"): void {
    this.document
      .getElementById(name === "name" ? "project-name" : "project-description")
      ?.focus();
  }

  private queueFocus(target: string, focused?: () => void): void {
    const generation = ++this.focusGeneration;
    afterNextRender(
      () => {
        if (
          this.destroyed ||
          generation !== this.focusGeneration ||
          !this.contexts.settled()
        )
          return;
        const element = this.document.getElementById(target);
        element?.focus();
        if (element && this.document.activeElement === element) focused?.();
      },
      { injector: this.injector },
    );
  }

  fieldError(
    error: ProjectCreationFieldError,
    field: "name" | "description",
  ): string {
    if (error === "required") return "Enter a project name.";
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

  errorMessage(kind: ProjectCreationErrorKind): string {
    return {
      invalidInput: "Check the project fields before sending this request.",
      identityChanged:
        "Your identity or organization changed. The original request was not sent as this identity.",
      featureDisabled: "Project creation is not enabled for this workspace.",
      identityRejected:
        "Your current identity could not be admitted. Refresh admission before trying again.",
      permissionDenied:
        "You do not currently have permission to create projects in this organization.",
      invalidRequest: "The service refused this request as invalid.",
      notFound:
        "This organization or creation request is unavailable to your current identity.",
      idempotencyConflict:
        "The original request identity conflicts with a saved request. No different project was adopted.",
      bodyLimit: "The service refused the size of this request.",
      unsupportedMediaType: "The service did not accept the request format.",
      admissionBusy: "Project creation is busy. No automatic retry was made.",
      persistenceFailure: "The service could not confirm the creation outcome.",
      internalFailure: "The service could not confirm the creation outcome.",
      timedOut: "The request timed out. The project may already exist.",
      unavailable:
        "The request could not be confirmed. The project may already exist if it was sent.",
      incompatible:
        "The reply could not be verified as the original creation receipt.",
    }[kind];
  }
}
