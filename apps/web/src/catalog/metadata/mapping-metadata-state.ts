import { DOCUMENT } from "@angular/common";
import {
  DestroyRef,
  Injectable,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { map, switchMap, take, type Subscription } from "rxjs";
import { CatalogApi } from "../catalog-api";
import { CatalogContextStore } from "../catalog-context";
import {
  CatalogReadError,
  isCatalogId,
  type CatalogFailureKind,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog-types";
import { MappingMetadataApi } from "./mapping-metadata-api";
import {
  MappingMetadataAttempt,
  MappingMetadataError,
  validateMappingMetadataInput,
  type CatalogMappingMetadata,
  type MappingMetadataAdmission,
  type MappingMetadataBaseline,
  type MappingMetadataInputErrors,
} from "./mapping-metadata-types";

type Draft = { readonly name: string; readonly description: string };
type AdmissionState =
  "idle" | "loading" | "ready" | "readOnly" | CatalogFailureKind;
type Review = {
  readonly base: MappingMetadataBaseline;
  readonly current: MappingMetadataBaseline;
};
export interface MappingMetadataComparison {
  readonly base: CatalogMappingMetadata;
  readonly current: CatalogMappingMetadata;
  readonly mine: Draft;
}
export type MappingMetadataReconciliation =
  | { readonly kind: "idle" }
  | {
      readonly kind: "matches" | "differs";
      readonly metadata: CatalogMappingMetadata;
    };

const emptyDraft: Draft = { name: "", description: "" };
const emptyErrors: MappingMetadataInputErrors = {
  name: null,
  description: null,
  body: null,
};

/** One route-owned, memory-only editor; a read can never acknowledge a lost PATCH. */
@Injectable()
export class MappingMetadataState {
  private readonly contexts = inject(CatalogContextStore);
  private readonly catalog = inject(CatalogApi);
  private readonly api = inject(MappingMetadataApi);
  private readonly document = inject(DOCUMENT);
  private readonly selectedScope = signal<CatalogMappingScope | null>(null);
  private readonly owner = signal<string | null>(null);
  private readonly ownerMode = signal<CatalogContext["mode"] | null>(null);
  private readonly admitted = signal<MappingMetadataAdmission | null>(null);
  private readonly presentingEditor = signal(false);
  private readonly admissionState = signal<AdmissionState>("idle");
  private readonly operation = signal<
    "idle" | "admitting" | "reading" | "saving"
  >("idle");
  private readonly base = signal<MappingMetadataBaseline | null>(null);
  private readonly rawDraft = signal<Draft>(emptyDraft);
  private readonly attempt = signal<MappingMetadataAttempt | null>(null);
  private readonly review = signal<Review | null>(null);
  private readonly needsReview = signal(false);
  private readonly unknown = signal(false);
  private readonly earlierUnknown = signal(false);
  private readonly failure = signal<MappingMetadataError | null>(null);
  private readonly errors = signal<MappingMetadataInputErrors>(emptyErrors);
  private readonly acknowledged = signal<{
    readonly changed: boolean;
    readonly metadata: CatalogMappingMetadata;
  } | null>(null);
  private readonly checked = signal<MappingMetadataReconciliation>({
    kind: "idle",
  });
  private admissionSubscription: Subscription | null = null;
  private requestSubscription: Subscription | null = null;
  private generation = 0;

  readonly admission = this.admissionState.asReadonly();
  readonly busy = computed(() => this.operation() !== "idle");
  readonly saving = computed(() => this.operation() === "saving");
  private readonly view = computed(() => {
    const admission = this.admitted(),
      scope = this.selectedScope();
    return admission &&
      scope &&
      this.contexts.isCurrent(admission.context) &&
      sameScope(admission.mapping.scope, scope) &&
      this.owner() === admission.context.actorPrincipalId &&
      this.ownerMode() === admission.context.mode
      ? admission
      : null;
  });
  readonly context = computed(() => this.view()?.context ?? null);
  readonly mapping = computed(() => this.view()?.mapping ?? null);
  private readonly editorVisible = computed(() => {
    const context = this.contexts.context(),
      scope = this.selectedScope(),
      owner = this.owner();
    const ownerUnchanged =
      scope &&
      owner &&
      (!context ||
        (context.actorPrincipalId === owner &&
          context.mode === this.ownerMode() &&
          context.allowedOrganizationId === scope.organizationId));
    return (
      !!ownerUnchanged &&
      this.presentingEditor() &&
      ((this.view() !== null && this.admissionState() === "ready") ||
        this.operation() === "admitting")
    );
  });
  readonly metadata = computed(() =>
    this.editorVisible() ? (this.base()?.metadata ?? null) : null,
  );
  readonly draft = computed(() =>
    this.editorVisible() ? this.rawDraft() : emptyDraft,
  );
  readonly fieldErrors = computed(() =>
    this.editorVisible() ? this.errors() : emptyErrors,
  );
  readonly error = this.failure.asReadonly();
  readonly dirty = computed(() => {
    const base = this.base(),
      draft = this.rawDraft();
    return (
      base !== null &&
      (draft.name !== base.metadata.name ||
        draft.description !== base.metadata.description)
    );
  });
  readonly outcomeUnknown = computed(
    () => this.editorVisible() && this.unknown(),
  );
  readonly priorUnknown = computed(
    () => this.editorVisible() && this.earlierUnknown(),
  );
  readonly conflict = computed(
    () => this.editorVisible() && this.needsReview(),
  );
  readonly comparison = computed<MappingMetadataComparison | null>(() => {
    const review = this.review();
    return this.editorVisible() && review
      ? {
          base: review.base.metadata,
          current: review.current.metadata,
          mine: this.rawDraft(),
        }
      : null;
  });
  readonly reconciliation = computed<MappingMetadataReconciliation>(() =>
    this.view() ? this.checked() : { kind: "idle" },
  );
  readonly saved = computed(() =>
    this.editorVisible() ? this.acknowledged() : null,
  );
  readonly canEdit = computed(
    () =>
      this.view() !== null &&
      this.contexts.canPresent(this.context()) &&
      this.editorVisible() &&
      this.base() !== null &&
      !this.busy(),
  );
  readonly canSave = computed(
    () =>
      this.canEdit() &&
      this.contexts.canDispatch(this.context()) &&
      !this.needsReview(),
  );
  readonly needsLeaveDecision = computed(
    () => this.dirty() || this.unknown() || this.saving(),
  );
  readonly hasRetainedWork = computed(() => this.dirty() || this.unknown());

  constructor() {
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.cancelActive();
      this.presentingEditor.set(false);
      this.admitted.set(null);
      this.review.set(null);
      this.checked.set({ kind: "idle" });
      this.acknowledged.set(null);
      this.failure.set(null);
      this.errors.set(emptyErrors);
      if (this.base()) this.needsReview.set(true);
      this.admissionState.set("unavailable");
    });
    // Another admitted identity must never inherit this editor's private draft or validator.
    effect(() => {
      const context = this.contexts.context(),
        owner = this.owner(),
        scope = this.selectedScope();
      if (
        context &&
        owner &&
        scope &&
        (context.actorPrincipalId !== owner ||
          context.mode !== this.ownerMode() ||
          context.allowedOrganizationId !== scope.organizationId)
      ) {
        untracked(() => {
          this.cancelActive();
          this.clearUnsentPrivate();
          this.presentingEditor.set(false);
          this.review.set(null);
          this.checked.set({ kind: "idle" });
          this.acknowledged.set(null);
          if (this.base()) this.needsReview.set(true);
          this.admitted.set(null);
          this.admissionState.set("unavailable");
          this.failure.set(new MappingMetadataError("identityChanged"));
        });
      }
    });
    const warn = (event: BeforeUnloadEvent) => {
      if (!this.needsLeaveDecision()) return;
      event.preventDefault();
      event.returnValue = "";
    };
    this.document.defaultView?.addEventListener("beforeunload", warn);
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.leave();
      this.document.defaultView?.removeEventListener("beforeunload", warn);
    });
  }

  select(scope: CatalogMappingScope | null): void {
    if (
      scope &&
      this.selectedScope() &&
      sameScope(scope, this.selectedScope()!)
    )
      return;
    this.cancelActive();
    this.clearPrivate();
    this.owner.set(null);
    this.ownerMode.set(null);
    this.admitted.set(null);
    if (
      !scope ||
      !isCatalogId(scope.organizationId) ||
      !isCatalogId(scope.projectId) ||
      !isCatalogId(scope.mappingId)
    ) {
      this.selectedScope.set(null);
      this.contexts.clear();
      this.admissionState.set("invalidLink");
      return;
    }
    this.selectedScope.set(
      Object.freeze({
        organizationId: scope.organizationId,
        projectId: scope.projectId,
        mappingId: scope.mappingId,
      }),
    );
    this.refreshAdmission();
  }

  refreshAdmission(): void {
    if (this.busy()) return;
    this.admit(false, (admission) => {
      if (!this.base() && editable(admission)) this.read(admission, "initial");
    });
  }

  setDraft(name: string, description: string): void {
    if (!this.canEdit()) return;
    this.rawDraft.set({ name, description });
    this.errors.set(emptyErrors);
    this.acknowledged.set(null);
  }

  save(): void {
    const base = this.base();
    if (!base || !this.canSave()) return;
    const draft = this.rawDraft();
    const validated = validateMappingMetadataInput(
      draft.name,
      draft.description,
    );
    if (!validated.valid) {
      this.errors.set(validated.errors);
      return;
    }
    this.errors.set(emptyErrors);
    this.admit(true, (admission) => {
      // Only the explicit review choice changes this base; fresh mapping/head data never does.
      if (this.base() !== base) return;
      try {
        const attempt = MappingMetadataAttempt.create(base, draft);
        if (this.unknown()) this.earlierUnknown.set(true);
        this.attempt.set(attempt);
        this.review.set(null);
        this.checked.set({ kind: "idle" });
        this.acknowledged.set(null);
        this.send(admission, attempt);
      } catch (error: unknown) {
        this.failure.set(metadataError(error));
      }
    });
  }

  reviewLatest(): void {
    if (
      !this.base() ||
      !this.contexts.canPresent(this.context()) ||
      this.busy()
    )
      return;
    this.admit(false, (admission) => this.read(admission, "review"));
  }

  keepMine(): void {
    const review = this.review();
    if (!review || !this.canEdit()) return;
    this.base.set(review.current);
    this.needsReview.set(false);
    this.failure.set(null);
  }

  editMine(): void {
    this.keepMine();
  }

  useCurrent(): void {
    const review = this.review();
    if (!review || !this.canEdit()) return;
    if (this.unknown()) this.earlierUnknown.set(true);
    this.base.set(review.current);
    this.rawDraft.set({
      name: review.current.metadata.name,
      description: review.current.metadata.description,
    });
    this.attempt.set(null);
    this.unknown.set(false);
    this.needsReview.set(false);
    this.review.set(null);
    this.checked.set({ kind: "idle" });
    this.failure.set(null);
    this.errors.set(emptyErrors);
    this.acknowledged.set(null);
  }

  checkCurrent(): void {
    if (
      !this.attempt() ||
      !this.contexts.canPresent(this.context()) ||
      this.busy()
    )
      return;
    this.admit(false, (admission) => this.read(admission, "check"));
  }

  stopWaiting(): void {
    const wasAdmitting = this.operation() === "admitting";
    this.cancelActive();
    this.failure.set(new MappingMetadataError("unavailable"));
    if (wasAdmitting) this.admissionState.set("unavailable");
  }

  discard(): void {
    this.cancelActive();
    this.clearPrivate();
  }

  leave(): void {
    this.discard();
    this.selectedScope.set(null);
    this.owner.set(null);
    this.ownerMode.set(null);
    this.admitted.set(null);
    this.admissionState.set("idle");
  }

  private admit(
    requireEdit: boolean,
    next: (admission: MappingMetadataAdmission) => void,
  ): void {
    const scope = this.selectedScope();
    if (!scope || this.busy()) return;
    this.cancelActive();
    const generation = this.generation;
    const originalOwner = this.owner();
    const originalMode = this.ownerMode();
    this.operation.set("admitting");
    this.admitted.set(null);
    this.admissionState.set("loading");
    this.failure.set(null);
    let emitted = false;
    this.admissionSubscription = this.contexts
      .refresh()
      .pipe(
        take(1),
        switchMap((context) => {
          if (
            !this.current(generation, scope) ||
            this.contexts.context() !== context
          )
            throw new MappingMetadataError("unavailable");
          if (
            context.allowedOrganizationId !== scope.organizationId ||
            (originalOwner !== null &&
              (context.actorPrincipalId !== originalOwner ||
                context.mode !== originalMode))
          ) {
            this.clearUnsentPrivate();
            this.presentingEditor.set(false);
            if (this.base()) this.needsReview.set(true);
            throw new MappingMetadataError(
              context.allowedOrganizationId !== scope.organizationId
                ? "notFound"
                : "identityChanged",
            );
          }
          this.owner.set(context.actorPrincipalId);
          this.ownerMode.set(context.mode);
          return this.catalog.mapping(scope).pipe(
            take(1),
            map((mapping): MappingMetadataAdmission => ({ context, mapping })),
          );
        }),
        take(1),
      )
      .subscribe({
        next: (admission) => {
          emitted = true;
          if (!this.current(generation, scope)) return;
          this.operation.set("idle");
          if (
            !this.contexts.isCurrent(admission.context) ||
            !sameScope(admission.mapping.scope, scope)
          ) {
            this.presentingEditor.set(false);
            this.admissionState.set("unavailable");
            return;
          }
          this.admitted.set(admission);
          this.presentingEditor.set(editable(admission));
          this.admissionState.set(editable(admission) ? "ready" : "readOnly");
          if (requireEdit && !editable(admission)) {
            this.failure.set(
              new MappingMetadataError(
                admission.context.features.editMetadata
                  ? "editUnavailable"
                  : "featureDisabled",
              ),
            );
            return;
          }
          if (this.contexts.canDispatch(admission.context)) next(admission);
        },
        error: (error: unknown) => {
          if (!this.current(generation, scope)) return;
          this.operation.set("idle");
          this.admitted.set(null);
          this.presentingEditor.set(false);
          this.failure.set(metadataError(error));
          this.admissionState.set(admissionFailure(error));
        },
        complete: () => {
          if (!emitted && this.current(generation, scope)) {
            this.operation.set("idle");
            this.presentingEditor.set(false);
            this.admissionState.set("unavailable");
          }
        },
      });
  }

  private read(
    admission: MappingMetadataAdmission,
    purpose: "initial" | "review" | "check",
  ): void {
    const scope = this.selectedScope();
    if (!scope) return;
    const generation = this.generation;
    this.operation.set("reading");
    let emitted = false;
    this.requestSubscription = this.api
      .read(admission)
      .pipe(take(1))
      .subscribe({
        next: (baseline) => {
          emitted = true;
          if (!this.current(generation, scope)) return;
          this.operation.set("idle");
          if (this.view() !== admission) {
            this.admitted.set(null);
            this.presentingEditor.set(false);
            this.admissionState.set("unavailable");
            return;
          }
          this.failure.set(null);
          if (purpose === "initial") {
            this.base.set(baseline);
            this.rawDraft.set({
              name: baseline.metadata.name,
              description: baseline.metadata.description,
            });
          } else if (purpose === "review") {
            const base = this.base();
            if (base) this.review.set({ base, current: baseline });
          } else {
            const attempt = this.attempt();
            if (attempt)
              this.checked.set({
                kind:
                  baseline.metadata.name === attempt.acceptedName &&
                  baseline.metadata.description === attempt.description
                    ? "matches"
                    : "differs",
                metadata: baseline.metadata,
              });
          }
        },
        error: (error: unknown) => {
          if (!this.current(generation, scope)) return;
          this.operation.set("idle");
          this.recordFailure(metadataError(error));
        },
        complete: () => {
          if (!emitted && this.current(generation, scope)) {
            this.operation.set("idle");
            this.recordFailure(new MappingMetadataError("unavailable"));
          }
        },
      });
  }

  private send(
    admission: MappingMetadataAdmission,
    attempt: MappingMetadataAttempt,
  ): void {
    const generation = this.generation,
      scope = attempt.baseline.scope;
    this.operation.set("saving");
    let emitted = false;
    this.requestSubscription = this.api
      .patch(admission, attempt)
      .pipe(take(1))
      .subscribe({
        next: (response) => {
          emitted = true;
          if (!this.current(generation, scope) || this.attempt() !== attempt)
            return;
          this.operation.set("idle");
          if (this.unknown()) this.earlierUnknown.set(true);
          this.base.set(response.baseline);
          this.rawDraft.set({
            name: response.baseline.metadata.name,
            description: response.baseline.metadata.description,
          });
          this.acknowledged.set({
            changed: response.changed,
            metadata: response.baseline.metadata,
          });
          this.attempt.set(null);
          this.unknown.set(false);
          this.needsReview.set(false);
          this.failure.set(null);
          this.review.set(null);
        },
        error: (error: unknown) => {
          if (!this.current(generation, scope) || this.attempt() !== attempt)
            return;
          this.operation.set("idle");
          this.recordFailure(metadataError(error));
        },
        complete: () => {
          if (
            !emitted &&
            this.current(generation, scope) &&
            this.attempt() === attempt
          ) {
            this.operation.set("idle");
            this.recordFailure(
              new MappingMetadataError("unavailable", null, true),
            );
          }
        },
      });
  }

  private recordFailure(error: MappingMetadataError): void {
    this.failure.set(error);
    if (error.mayHaveCommitted) this.unknown.set(true);
    if (
      this.unknown() ||
      error.kind === "preconditionFailed" ||
      error.kind === "preconditionRequired"
    )
      this.needsReview.set(true);
    if (error.kind === "notFound" || error.kind === "identityRejected") {
      this.admitted.set(null);
      this.presentingEditor.set(false);
      this.admissionState.set(
        error.kind === "notFound" ? "notFound" : "rejected",
      );
    }
    this.contexts.reject(error.status);
  }

  private cancelActive(): void {
    ++this.generation;
    if (this.saving()) {
      this.unknown.set(true);
      this.needsReview.set(true);
    }
    this.admissionSubscription?.unsubscribe();
    this.requestSubscription?.unsubscribe();
    this.admissionSubscription = this.requestSubscription = null;
    this.operation.set("idle");
  }

  private clearUnsentPrivate(): void {
    // A confirmed new owner may start a fresh editor, but cannot discard a
    // retained dispatch or uncertainty belonging to the original owner.
    if (this.attempt() || this.unknown() || this.earlierUnknown()) return;
    this.clearPrivate();
    this.owner.set(null);
    this.ownerMode.set(null);
  }

  private clearPrivate(): void {
    this.presentingEditor.set(false);
    this.base.set(null);
    this.rawDraft.set(emptyDraft);
    this.attempt.set(null);
    this.review.set(null);
    this.needsReview.set(false);
    this.unknown.set(false);
    this.earlierUnknown.set(false);
    this.failure.set(null);
    this.errors.set(emptyErrors);
    this.acknowledged.set(null);
    this.checked.set({ kind: "idle" });
  }

  private current(generation: number, scope: CatalogMappingScope): boolean {
    const selected = this.selectedScope(),
      context = this.contexts.context(),
      owner = this.owner();
    return (
      generation === this.generation &&
      selected !== null &&
      sameScope(scope, selected) &&
      (!context ||
        !owner ||
        (context.actorPrincipalId === owner &&
          context.mode === this.ownerMode() &&
          context.allowedOrganizationId === scope.organizationId))
    );
  }
}

function editable(admission: MappingMetadataAdmission): boolean {
  return (
    admission.context.features.editMetadata &&
    admission.mapping.access.actions.editMetadata
  );
}
function sameScope(
  left: CatalogMappingScope,
  right: CatalogMappingScope,
): boolean {
  return (
    left.organizationId === right.organizationId &&
    left.projectId === right.projectId &&
    left.mappingId === right.mappingId
  );
}
function metadataError(error: unknown): MappingMetadataError {
  if (error instanceof MappingMetadataError) return error;
  if (error instanceof CatalogReadError) {
    if (error.kind === "notFound") return new MappingMetadataError("notFound");
    if (error.kind === "rejected")
      return new MappingMetadataError("identityRejected");
    if (error.kind === "invalidLink")
      return new MappingMetadataError("invalidInput");
  }
  return new MappingMetadataError("unavailable");
}
function admissionFailure(error: unknown): CatalogFailureKind {
  if (error instanceof CatalogReadError) return error.kind;
  const kind = metadataError(error).kind;
  return kind === "notFound"
    ? "notFound"
    : kind === "identityRejected"
      ? "rejected"
      : "unavailable";
}
