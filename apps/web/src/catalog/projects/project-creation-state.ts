import { DOCUMENT } from "@angular/common";
import {
  DestroyRef,
  Injectable,
  computed,
  inject,
  signal,
} from "@angular/core";
import { take, type Subscription } from "rxjs";
import { CatalogApi } from "../catalog-api";
import { CatalogContextStore } from "../catalog-context";
import {
  CatalogReadError,
  isCatalogId,
  type CatalogContext,
  type CatalogFailureKind,
  type CatalogProject,
} from "../catalog-types";
import { ProjectCreationApi } from "./project-creation-api";
import {
  ProjectCreationError,
  ProjectCreationIntent,
  validateProjectCreationInput,
  type ProjectCreationInputErrors,
  type ProjectCreationResponse,
} from "./project-creation-types";

type Owner = {
  readonly mode: CatalogContext["mode"];
  readonly organizationId: string;
  readonly actorPrincipalId: string;
};
type Draft = Owner & { readonly name: string; readonly description: string };
type Attempt = {
  readonly intent: ProjectCreationIntent;
  readonly uncertain: boolean;
  readonly error: ProjectCreationError | null;
  readonly stopped: boolean;
};
type Confirmation = Owner & { readonly response: ProjectCreationResponse };
export type ProjectCreationAdmission =
  "idle" | "loading" | "ready" | CatalogFailureKind;
export type ProjectCreationAccess =
  | { readonly kind: "idle" | "loading" }
  | { readonly kind: "ready"; readonly project: CatalogProject }
  | { readonly kind: CatalogFailureKind };
export interface ProjectCreationAttemptSummary {
  readonly sending: boolean;
  readonly uncertain: boolean;
  readonly error: ProjectCreationError | null;
  readonly stopped: boolean;
  readonly requiresAbandonment: boolean;
}

const emptyErrors: ProjectCreationInputErrors = {
  name: null,
  description: null,
  body: null,
};
const emptyDraft = { name: "", description: "" };

/** One unfinished request and one receipt survive SPA navigation only in this tab's memory. */
@Injectable({ providedIn: "root" })
export class ProjectCreationState {
  private readonly contexts = inject(CatalogContextStore);
  private readonly api = inject(ProjectCreationApi);
  private readonly catalog = inject(CatalogApi);
  private readonly document = inject(DOCUMENT);
  private readonly active = signal(false);
  private readonly organizationId = signal<string | null>(null);
  private readonly admitted = signal<CatalogContext | null>(null);
  private readonly presentation = signal<{
    readonly context: CatalogContext;
    readonly identityVersion: number;
  } | null>(null);
  private readonly storedDraft = signal<Draft | null>(null);
  private readonly pending = signal<Attempt | null>(null);
  private readonly acknowledged = signal<Confirmation | null>(null);
  private readonly receiptOwnerMismatch = signal(false);
  private readonly admissionState = signal<ProjectCreationAdmission>("idle");
  private readonly operation = signal<
    "idle" | "admitting" | "submitting" | "access"
  >("idle");
  private readonly inputErrors =
    signal<ProjectCreationInputErrors>(emptyErrors);
  private readonly actionError = signal<ProjectCreationError | null>(null);
  private readonly accessState = signal<ProjectCreationAccess>({
    kind: "idle",
  });
  private admissionSubscription: Subscription | null = null;
  private postSubscription: Subscription | null = null;
  private accessSubscription: Subscription | null = null;
  private generation = 0;

  readonly admission = this.admissionState.asReadonly();
  readonly busy = computed(() => this.operation() !== "idle");
  readonly submitting = computed(() => this.operation() === "submitting");
  readonly error = this.actionError.asReadonly();
  readonly fieldErrors = this.inputErrors.asReadonly();
  private readonly currentContext = computed(() => {
    const context = this.admitted();
    return this.active() &&
      context !== null &&
      this.contexts.isCurrent(context) &&
      context.allowedOrganizationId === this.organizationId()
      ? context
      : null;
  });
  /** Mount-only original-owner display; no action obtains authority from this fallback. */
  readonly context = computed(() => {
    const current = this.currentContext();
    if (current) return current;
    const presentation = this.presentation(),
      observed = this.contexts.context();
    if (
      !this.active() ||
      this.operation() !== "admitting" ||
      !presentation ||
      presentation.identityVersion !== this.contexts.identityVersion() ||
      presentation.context.allowedOrganizationId !== this.organizationId()
    )
      return null;
    const context = presentation.context;
    return !observed ||
      (observed.mode === context.mode &&
        observed.actorPrincipalId === context.actorPrincipalId &&
        observed.allowedOrganizationId === context.allowedOrganizationId &&
        (observed.mode === "session" ? observed.identityContext : null) ===
          (context.mode === "session" ? context.identityContext : null))
      ? context
      : null;
  });
  readonly quarantined = computed(() => {
    const context = this.context();
    if (!context) return false;
    const pending = this.pending();
    const acknowledged = this.acknowledged();
    return (
      (pending !== null && !owns(context, pending.intent)) ||
      (acknowledged !== null && !owns(context, acknowledged))
    );
  });
  readonly draft = computed(() => {
    const context = this.context();
    const draft = this.storedDraft();
    return context && draft && owns(context, draft) && !this.quarantined()
      ? { name: draft.name, description: draft.description }
      : emptyDraft;
  });
  readonly attemptSummary = computed<ProjectCreationAttemptSummary | null>(
    () => {
      const context = this.context();
      const pending = this.pending();
      return context && pending && owns(context, pending.intent)
        ? {
            sending: this.submitting(),
            uncertain: pending.uncertain,
            error: pending.error,
            stopped: pending.stopped,
            requiresAbandonment:
              pending.uncertain ||
              pending.error?.kind === "idempotencyConflict",
          }
        : null;
    },
  );
  readonly confirmation = computed(() => {
    const context = this.context();
    const acknowledged = this.acknowledged();
    return context && acknowledged && owns(context, acknowledged)
      ? acknowledged.response
      : null;
  });
  readonly retainedConfirmation = computed(() => {
    const acknowledged = this.acknowledged();
    return (
      this.active() &&
      acknowledged !== null &&
      acknowledged.organizationId === this.organizationId() &&
      this.admissionState() !== "invalidLink" &&
      !this.receiptOwnerMismatch() &&
      this.context() === null
    );
  });
  readonly currentAccess = computed<ProjectCreationAccess>(() =>
    this.confirmation() ? this.accessState() : { kind: "idle" },
  );
  readonly hasRetainedRequest = computed(
    () => this.pending() !== null || this.acknowledged() !== null,
  );
  readonly canEdit = computed(
    () =>
      this.currentContext() !== null &&
      this.contexts.canPresent(this.currentContext()) &&
      !this.quarantined() &&
      !this.pending() &&
      !this.confirmation() &&
      !this.busy(),
  );
  readonly canCreate = computed(
    () =>
      this.canEdit() &&
      this.contexts.canDispatch(this.currentContext()) &&
      capability(this.currentContext()),
  );
  readonly canRecover = computed(
    () =>
      this.attemptSummary() !== null &&
      this.contexts.canDispatch(this.currentContext()) &&
      capability(this.currentContext()) &&
      !this.busy(),
  );

  constructor() {
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.cancelActive();
      this.admitted.set(null);
      this.presentation.set(null);
      this.accessState.set({ kind: "idle" });
      this.receiptOwnerMismatch.set(true);
      this.inputErrors.set(emptyErrors);
      this.actionError.set(null);
      this.admissionState.set("unavailable");
    });
    const window = this.document.defaultView;
    const warn = (event: BeforeUnloadEvent) => {
      if (!this.pending()) return;
      event.preventDefault();
      event.returnValue = "";
    };
    window?.addEventListener("beforeunload", warn);
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.leave();
      window?.removeEventListener("beforeunload", warn);
    });
  }

  select(organizationId: string | null): void {
    this.cancelActive();
    this.active.set(true);
    this.organizationId.set(organizationId);
    this.admitted.set(null);
    this.presentation.set(null);
    this.actionError.set(null);
    this.inputErrors.set(emptyErrors);
    if (!isCatalogId(organizationId)) {
      this.contexts.clear();
      this.admissionState.set("invalidLink");
      return;
    }
    this.refresh();
  }

  leave(): void {
    this.cancelActive();
    this.active.set(false);
    this.admitted.set(null);
    this.presentation.set(null);
    this.admissionState.set("idle");
    this.accessState.set({ kind: "idle" });
  }

  refresh(): void {
    if (!this.active() || this.busy()) return;
    this.admit(null, (context) => {
      const receipt = this.acknowledged();
      if (receipt && owns(context, receipt)) this.readProject(context, receipt);
    });
  }

  setDraft(name: string, description: string): void {
    const context = this.context();
    if (!context || !this.canEdit()) return;
    this.storedDraft.set({
      mode: context.mode,
      organizationId: context.allowedOrganizationId,
      actorPrincipalId: context.actorPrincipalId,
      name,
      description,
    });
    this.inputErrors.set(emptyErrors);
    this.actionError.set(null);
  }

  create(): void {
    const context = this.context();
    if (!context || !this.canCreate()) return;
    const draft = this.draft();
    const input = validateProjectCreationInput(draft.name, draft.description);
    if (!input.valid) {
      this.inputErrors.set(input.errors);
      return;
    }
    this.inputErrors.set(emptyErrors);
    const owner = {
      mode: context.mode,
      organizationId: context.allowedOrganizationId,
      actorPrincipalId: context.actorPrincipalId,
    };
    this.admit(owner, (fresh) => {
      if (!this.requireCapability(fresh)) return;
      try {
        const intent = ProjectCreationIntent.create(
          fresh,
          { name: draft.name, description: draft.description },
          crypto.randomUUID(),
        );
        this.pending.set({
          intent,
          uncertain: false,
          error: null,
          stopped: false,
        });
        this.send(fresh, intent);
      } catch (error: unknown) {
        this.actionError.set(creationError(error));
      }
    });
  }

  recover(): void {
    const pending = this.pending();
    if (!pending || !this.canRecover()) return;
    this.admit(pending.intent, (context) => {
      if (this.requireCapability(context)) this.send(context, pending.intent);
    });
  }

  stopWaiting(): void {
    this.cancelActive();
    this.accessState.set({ kind: "idle" });
    if (this.admissionState() === "loading")
      this.admissionState.set("unavailable");
  }

  retryAccess(): void {
    const receipt = this.acknowledged();
    if (
      !receipt ||
      !this.contexts.visible() ||
      (!this.confirmation() && !this.retainedConfirmation()) ||
      this.busy()
    )
      return;
    this.admit(receipt, (context) => this.readProject(context, receipt));
  }

  editRefused(): void {
    const pending = this.pending();
    if (
      !pending ||
      !this.contexts.canPresent(this.currentContext()) ||
      !this.attemptSummary() ||
      this.busy() ||
      pending.uncertain ||
      pending.error?.kind === "idempotencyConflict"
    )
      return;
    this.pending.set(null);
    this.actionError.set(null);
    this.inputErrors.set(emptyErrors);
  }

  abandonOriginal(confirmed: boolean): void {
    if (
      !confirmed ||
      !this.contexts.canPresent(this.currentContext()) ||
      !this.attemptSummary() ||
      this.busy()
    )
      return;
    this.pending.set(null);
    this.actionError.set(null);
    this.inputErrors.set(emptyErrors);
  }

  startAnother(): void {
    if (
      !this.contexts.canPresent(this.currentContext()) ||
      !this.confirmation() ||
      this.busy() ||
      this.pending()
    )
      return;
    this.acknowledged.set(null);
    this.storedDraft.set(null);
    this.accessState.set({ kind: "idle" });
    this.actionError.set(null);
    this.inputErrors.set(emptyErrors);
  }

  private admit(
    expectedOwner: Owner | null,
    next: (context: CatalogContext) => void,
  ): void {
    const organizationId = this.organizationId();
    if (!this.active() || !isCatalogId(organizationId) || this.busy()) return;
    const presented = this.currentContext();
    this.presentation.set(
      presented
        ? {
            context: presented,
            identityVersion: this.contexts.identityVersion(),
          }
        : null,
    );
    this.cancelActive();
    const generation = this.generation;
    this.operation.set("admitting");
    this.admitted.set(null);
    this.admissionState.set("loading");
    this.actionError.set(null);
    let emitted = false;
    this.admissionSubscription = this.contexts
      .refresh()
      .pipe(take(1))
      .subscribe({
        next: (context) => {
          emitted = true;
          if (!this.selected(generation, organizationId)) return;
          this.presentation.set(null);
          this.operation.set("idle");
          const receipt = this.acknowledged();
          if (receipt) this.receiptOwnerMismatch.set(!owns(context, receipt));
          if (
            this.contexts.context() !== context ||
            context.allowedOrganizationId !== organizationId
          ) {
            this.admissionState.set("notFound");
            return;
          }
          this.admitted.set(context);
          this.admissionState.set("ready");
          if (expectedOwner && !owns(context, expectedOwner)) {
            this.actionError.set(new ProjectCreationError("identityChanged"));
            return;
          }
          if (this.contexts.canDispatch(context)) next(context);
        },
        error: (error: unknown) => {
          if (!this.selected(generation, organizationId)) return;
          this.presentation.set(null);
          this.operation.set("idle");
          this.admissionState.set(
            error instanceof CatalogReadError ? error.kind : "unavailable",
          );
          this.actionError.set(creationError(error));
        },
        complete: () => {
          if (!emitted && this.selected(generation, organizationId)) {
            this.presentation.set(null);
            this.operation.set("idle");
            this.admissionState.set("unavailable");
          }
        },
      });
  }

  private send(context: CatalogContext, intent: ProjectCreationIntent): void {
    const generation = this.generation;
    this.pending.update((pending) =>
      pending?.intent === intent ? { ...pending, stopped: false } : pending,
    );
    this.operation.set("submitting");
    let emitted = false;
    this.postSubscription = this.api
      .create(context, intent)
      .pipe(take(1))
      .subscribe({
        next: (response) => {
          emitted = true;
          if (
            !this.selected(generation, intent.organizationId) ||
            this.pending()?.intent !== intent
          )
            return;
          this.operation.set("idle");
          this.acknowledged.set({
            mode: intent.mode,
            organizationId: intent.organizationId,
            actorPrincipalId: intent.actorPrincipalId,
            response,
          });
          this.receiptOwnerMismatch.set(false);
          this.pending.set(null);
          this.actionError.set(null);
          this.accessState.set({ kind: "idle" });
          // A validated receipt is historical fact. Current access is a separate GET-only flow.
          this.admit(intent, (fresh) =>
            this.readProject(fresh, this.acknowledged()!),
          );
        },
        error: (error: unknown) => {
          if (
            !this.selected(generation, intent.organizationId) ||
            this.pending()?.intent !== intent
          )
            return;
          this.operation.set("idle");
          this.refused(intent, creationError(error));
        },
        complete: () => {
          if (
            !emitted &&
            this.selected(generation, intent.organizationId) &&
            this.pending()?.intent === intent
          ) {
            this.operation.set("idle");
            this.refused(intent, new ProjectCreationError("unavailable"));
          }
        },
      });
  }

  private readProject(context: CatalogContext, receipt: Confirmation): void {
    if (!owns(context, receipt) || !this.context()) return;
    const generation = this.generation;
    this.operation.set("access");
    this.accessState.set({ kind: "loading" });
    let emitted = false;
    this.accessSubscription = this.catalog
      .project(receipt.response.body.receipt.project)
      .pipe(take(1))
      .subscribe({
        next: (project) => {
          emitted = true;
          if (!this.selected(generation, receipt.organizationId)) return;
          this.operation.set("idle");
          this.accessState.set(
            this.context() === context && this.acknowledged() === receipt
              ? { kind: "ready", project }
              : { kind: "unavailable" },
          );
        },
        error: (error: unknown) => {
          if (!this.selected(generation, receipt.organizationId)) return;
          this.operation.set("idle");
          this.accessState.set({
            kind:
              error instanceof CatalogReadError ? error.kind : "unavailable",
          });
        },
        complete: () => {
          if (!emitted && this.selected(generation, receipt.organizationId)) {
            this.operation.set("idle");
            this.accessState.set({ kind: "unavailable" });
          }
        },
      });
  }

  private requireCapability(context: CatalogContext): boolean {
    if (capability(context)) return true;
    const error = new ProjectCreationError(
      context.features.createProjects ? "permissionDenied" : "featureDisabled",
    );
    const pending = this.pending();
    if (pending) this.refused(pending.intent, error);
    else this.actionError.set(error);
    return false;
  }

  private refused(
    intent: ProjectCreationIntent,
    error: ProjectCreationError,
  ): void {
    const pending = this.pending();
    if (!pending || pending.intent !== intent) return;
    this.pending.set({
      ...pending,
      uncertain: pending.uncertain || error.mayHaveCommitted,
      error,
      stopped: false,
    });
    this.actionError.set(error);
    this.contexts.reject(error.status);
  }

  private cancelActive(): void {
    ++this.generation;
    if (this.operation() === "submitting") {
      const pending = this.pending();
      if (pending)
        this.pending.set({
          ...pending,
          uncertain: true,
          stopped: true,
          error: new ProjectCreationError("unavailable"),
        });
    }
    this.admissionSubscription?.unsubscribe();
    this.postSubscription?.unsubscribe();
    this.accessSubscription?.unsubscribe();
    this.admissionSubscription =
      this.postSubscription =
      this.accessSubscription =
        null;
    this.operation.set("idle");
  }

  private selected(generation: number, organizationId: string): boolean {
    return (
      this.active() &&
      generation === this.generation &&
      this.organizationId() === organizationId
    );
  }
}

function owns(context: CatalogContext, owner: Owner): boolean {
  return (
    context.mode === owner.mode &&
    context.allowedOrganizationId === owner.organizationId &&
    context.actorPrincipalId === owner.actorPrincipalId
  );
}

function capability(context: CatalogContext | null): boolean {
  return (
    context !== null &&
    context.features.createProjects &&
    context.organizationActions.createProjects
  );
}

function creationError(error: unknown): ProjectCreationError {
  if (error instanceof ProjectCreationError) return error;
  if (error instanceof CatalogReadError) {
    if (error.kind === "rejected")
      return new ProjectCreationError("identityRejected");
    if (error.kind === "notFound") return new ProjectCreationError("notFound");
    if (error.kind === "invalidLink")
      return new ProjectCreationError("invalidInput");
  }
  return new ProjectCreationError("unavailable");
}
