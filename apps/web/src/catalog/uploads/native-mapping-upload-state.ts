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
import { map, switchMap, take, type Observable, type Subscription } from "rxjs";
import { CatalogApi } from "../catalog-api";
import { CatalogContextStore } from "../catalog-context";
import {
  CatalogReadError,
  isCatalogId,
  type CatalogContext,
  type CatalogFailureKind,
  type CatalogProjectScope,
  type CatalogRevision,
} from "../catalog-types";
import { NativeMappingUploadApi } from "./native-mapping-upload-api";
import {
  NativeMappingUploadError,
  NativeMappingUploadIntent,
  NativeMappingUploadSource,
  validateNativeMappingUploadInput,
  type NativeMappingUploadAdmission,
  type NativeMappingUploadInput,
  type NativeMappingUploadInputErrors,
  type NativeMappingUploadOperation,
  type NativeMappingUploadResponse,
} from "./native-mapping-upload-types";

type Owner = {
  readonly mode: "development" | "session";
  readonly scope: CatalogProjectScope;
  readonly actorPrincipalId: string;
};
type FileSummary = { readonly name: string; readonly byteCount: number };
type AdmissionState =
  "idle" | "loading" | "ready" | "readOnly" | CatalogFailureKind;
type UploadedRevision = Pick<
  CatalogRevision,
  "scope" | "sequence" | "admission" | "originalFilename" | "nativeByteCount"
>;
type RevisionAccess =
  | { readonly kind: "idle" | "loading" }
  | { readonly kind: "ready"; readonly revision: UploadedRevision }
  | { readonly kind: CatalogFailureKind };
type Work = "idle" | "admitting" | "create" | "resume" | "receipt" | "revision";
export type NativeMappingUploadLeaveRisk = "draft" | "recovery" | null;

const blank: NativeMappingUploadInput = {
  name: "",
  description: "",
  originalFilename: "",
};
const noErrors: NativeMappingUploadInputErrors = {
  name: null,
  description: null,
  originalFilename: null,
  request: null,
};

/** One tab-memory upload slot, independent of catalog read pages and other mutations. */
@Injectable({ providedIn: "root" })
export class NativeMappingUploadState {
  private readonly contexts = inject(CatalogContextStore);
  private readonly catalog = inject(CatalogApi);
  private readonly api = inject(NativeMappingUploadApi);
  private readonly document = inject(DOCUMENT);
  private readonly active = signal(false);
  private readonly scope = signal<CatalogProjectScope | null>(null);
  private readonly admitted = signal<NativeMappingUploadAdmission | null>(null);
  private readonly admissionState = signal<AdmissionState>("idle");
  private readonly work = signal<Work>("idle");
  private readonly rawDraft = signal<NativeMappingUploadInput>(blank);
  private readonly draftOwner = signal<Owner | null>(null);
  private readonly source = signal<NativeMappingUploadSource | null>(null);
  private readonly selectedFile = signal<FileSummary | null>(null);
  private readonly original = signal<NativeMappingUploadIntent | null>(null);
  private readonly known = signal<NativeMappingUploadOperation | null>(null);
  private readonly latest = signal<NativeMappingUploadResponse | null>(null);
  private readonly unknown = signal(false);
  private readonly stopped = signal(false);
  private readonly foreignOwner = signal(false);
  private readonly presenting = signal(false);
  private readonly failure = signal<NativeMappingUploadError | null>(null);
  private readonly inputErrors =
    signal<NativeMappingUploadInputErrors>(noErrors);
  private readonly fileFailure = signal<NativeMappingUploadError | null>(null);
  private readonly access = signal<RevisionAccess>({ kind: "idle" });
  private readonly preparing = signal(false);
  private captureAbort: AbortController | null = null;
  private queuedFile: {
    readonly file: File;
    readonly owner: Owner;
    readonly generation: number;
  } | null = null;
  private captureRunning = false;
  private fileGeneration = 0;
  private requestGeneration = 0;
  private admissionSubscription: Subscription | null = null;
  private requestSubscription: Subscription | null = null;

  readonly admission = this.admissionState.asReadonly();
  readonly busy = computed(() => this.work() !== "idle");
  readonly preparingFile = this.preparing.asReadonly();
  readonly sending = computed(
    () => this.work() === "create" || this.work() === "resume",
  );
  readonly error = this.failure.asReadonly();
  readonly fileError = computed(() =>
    this.presentUpload() ? this.fileFailure() : null,
  );
  readonly outcomeUnknown = computed(
    () =>
      this.active() &&
      this.sameSelectedOwner() &&
      !this.foreignOwner() &&
      this.unknown(),
  );
  readonly waitingStopped = computed(
    () => this.outcomeUnknown() && this.stopped() && !this.sending(),
  );
  private readonly slotOwner = computed<Owner | null>(() => {
    const intent = this.original(),
      operation = this.known();
    if (intent)
      return {
        mode: intent.mode,
        scope: intent.scope,
        actorPrincipalId: intent.actorPrincipalId,
      };
    if (operation)
      return {
        mode: operation.mode,
        scope: operation.scope,
        actorPrincipalId: operation.actorPrincipalId,
      };
    return this.draftOwner();
  });
  private readonly view = computed(() => {
    const admission = this.admitted(),
      selected = this.scope();
    return this.active() &&
      admission &&
      selected &&
      this.contexts.context() === admission.context &&
      sameScope(admission.project.scope, selected)
      ? admission
      : null;
  });
  readonly context = computed(() => this.view()?.context ?? null);
  readonly project = computed(() => this.view()?.project ?? null);
  readonly quarantined = computed(
    () =>
      this.active() &&
      (this.original() !== null || this.known() !== null) &&
      (this.foreignOwner() || !this.sameSelectedOwner()),
  );
  private readonly presentUpload = computed(() => {
    const current = this.contexts.context(),
      owner = this.slotOwner();
    const safe = !owner || !current || owns(current, owner);
    return (
      this.active() &&
      this.presenting() &&
      safe &&
      !this.foreignOwner() &&
      this.sameSelectedOwner() &&
      ((this.view() !== null && this.admissionState() === "ready") ||
        this.work() === "admitting")
    );
  });
  readonly draft = computed<NativeMappingUploadInput>(() => {
    if (!this.presentUpload()) return blank;
    const intent = this.original();
    return intent
      ? {
          name: intent.acceptedName,
          description: intent.description,
          originalFilename: intent.originalFilename,
        }
      : this.rawDraft();
  });
  readonly file = computed<FileSummary | null>(() => {
    if (!this.presentUpload()) return null;
    const intent = this.original();
    return intent
      ? { name: intent.originalFilename, byteCount: intent.byteCount }
      : this.selectedFile();
  });
  readonly fieldErrors = computed(() =>
    this.presentUpload() ? this.inputErrors() : noErrors,
  );
  readonly formVisible = computed(
    () => this.presentUpload() && !this.known()?.terminal,
  );
  readonly hasRetainedUpload = computed(
    () => this.original() !== null || this.known() !== null,
  );
  readonly locked = computed(
    () => this.original() !== null || this.known() !== null,
  );
  readonly canEdit = computed(
    () =>
      this.presentUpload() &&
      this.view() !== null &&
      this.contexts.canPresent(this.context()) &&
      !this.locked() &&
      !this.busy(),
  );
  readonly canChooseFile = this.canEdit;
  readonly canUpload = computed(
    () => this.canEdit() && this.source() !== null && !this.preparingFile(),
  );
  readonly canResend = computed(
    () =>
      this.uploadAdmitted() &&
      this.contexts.canPresent(this.context()) &&
      this.original() !== null &&
      (this.known() === null ||
        this.known()?.receipt.latestFailure === "needsSource") &&
      !this.busy(),
  );
  readonly canCheckStatus = computed(
    () =>
      this.uploadAdmitted() &&
      this.contexts.canPresent(this.context()) &&
      this.known() !== null &&
      !this.busy(),
  );
  readonly canResume = this.canCheckStatus;
  readonly canStartAnother = computed(
    () =>
      this.uploadAdmitted() &&
      this.contexts.canPresent(this.context()) &&
      this.known()?.terminal === true &&
      !this.busy(),
  );
  readonly canAbandon = computed(() => {
    const context = this.contexts.context(),
      owner = this.slotOwner();
    return (
      this.active() &&
      owner !== null &&
      context !== null &&
      this.contexts.canPresent(context) &&
      owns(context, owner) &&
      (this.original() !== null || this.known() !== null) &&
      !this.busy()
    );
  });
  readonly operationStatus = computed(() => {
    const operation = this.known();
    if (!this.uploadAdmitted() || !operation) return null;
    return {
      phase: operation.receipt.phase,
      claimCount: operation.receipt.claimCount,
      failure: operation.receipt.latestFailure,
      terminal: operation.terminal,
      committed: operation.committed,
    };
  });
  readonly response = computed(() => {
    const response = this.latest();
    return this.uploadAdmitted() && response
      ? {
          outcome: response.outcome,
          rejection: response.rejection,
          failure: response.failure,
        }
      : null;
  });
  readonly acknowledgement = computed(() => {
    const operation = this.known(),
      current = this.contexts.context(),
      owner = this.slotOwner();
    if (
      !this.active() ||
      !operation?.committed ||
      !owner ||
      !this.sameSelectedOwner() ||
      this.foreignOwner() ||
      (current && !owns(current, owner))
    )
      return null;
    return {
      detailed: this.view() !== null,
      admission: this.view() ? operation.receipt.committedAdmission : null,
    };
  });
  readonly currentAccess = computed<RevisionAccess>(() =>
    this.acknowledgement()?.detailed ? this.access() : { kind: "idle" },
  );
  readonly canRetryRevisionAccess = computed(
    () =>
      this.contexts.visible() &&
      this.acknowledgement() !== null &&
      !this.busy(),
  );
  readonly dirtyDraft = computed(
    () =>
      !this.locked() &&
      (this.source() !== null ||
        this.preparingFile() ||
        this.rawDraft().name !== "" ||
        this.rawDraft().description !== "" ||
        this.rawDraft().originalFilename !== ""),
  );
  readonly leaveRisk = computed<NativeMappingUploadLeaveRisk>(() => {
    if (
      this.original() !== null ||
      (this.known() !== null && !this.known()!.terminal)
    )
      return "recovery";
    return this.dirtyDraft() ? "draft" : null;
  });

  constructor() {
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.cancelNetwork();
      this.invalidateFile();
      if (!this.locked()) this.clearDraft();
      this.foreignOwner.set(true);
      this.presenting.set(false);
      this.admitted.set(null);
      this.access.set({ kind: "idle" });
      this.failure.set(null);
      if (this.active()) this.admissionState.set("unavailable");
    });
    effect(() => {
      const context = this.contexts.context(),
        owner = this.slotOwner();
      if (context && owner && !owns(context, owner))
        untracked(() => {
          this.cancelNetwork();
          this.invalidateFile();
          if (!this.locked()) this.clearDraft();
          this.foreignOwner.set(true);
          this.presenting.set(false);
          this.admitted.set(null);
          if (this.active()) this.admissionState.set("unavailable");
        });
    });
    const warn = (event: BeforeUnloadEvent) => {
      if (!this.leaveRisk()) return;
      event.preventDefault();
      event.returnValue = "";
    };
    this.document.defaultView?.addEventListener("beforeunload", warn);
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.deactivate();
      this.clearSlot();
      this.document.defaultView?.removeEventListener("beforeunload", warn);
    });
  }

  select(scope: CatalogProjectScope | null): void {
    const selected = this.scope();
    if (this.active() && scope && selected && sameScope(scope, selected))
      return;
    this.cancelNetwork();
    this.invalidateFile();
    const previous = this.scope();
    if (!this.locked() && (!previous || !scope || !sameScope(previous, scope)))
      this.clearDraft();
    this.active.set(true);
    this.admitted.set(null);
    this.presenting.set(false);
    this.failure.set(null);
    if (
      !scope ||
      !isCatalogId(scope.organizationId) ||
      !isCatalogId(scope.projectId)
    ) {
      this.scope.set(null);
      this.contexts.clear();
      this.admissionState.set("invalidLink");
      return;
    }
    this.scope.set(
      Object.freeze({
        organizationId: scope.organizationId,
        projectId: scope.projectId,
      }),
    );
    this.refreshAccess();
  }

  deactivate(): void {
    this.cancelNetwork();
    this.invalidateFile();
    if (!this.locked()) this.clearDraft();
    this.active.set(false);
    this.admitted.set(null);
    this.presenting.set(false);
    this.access.set({ kind: "idle" });
    this.admissionState.set("idle");
  }

  refreshAccess(): void {
    if (this.busy() || this.preparingFile()) return;
    this.admit(false, null, (admission) => {
      if (
        this.known()?.committed &&
        this.sameSelectedOwner() &&
        !this.foreignOwner()
      )
        this.readRevision(admission, this.known()!);
    });
  }

  setDraft(name: string, description: string, originalFilename: string): void {
    if (!this.canEdit()) return;
    this.rawDraft.set({ name, description, originalFilename });
    this.inputErrors.set(noErrors);
  }

  selectFile(file: File | null): void {
    if (!this.canChooseFile()) return;
    this.invalidateFile();
    this.source.set(null);
    this.selectedFile.set(null);
    this.fileFailure.set(null);
    this.inputErrors.set(noErrors);
    const admission = this.view();
    if (!file || !admission) return;
    if (file.size < 1 || file.size > 4194304) {
      this.fileFailure.set(
        new NativeMappingUploadError(
          file.size < 1 ? "emptyNative" : "nativeLimit",
        ),
      );
      return;
    }
    const owner = ownerOf(admission);
    this.draftOwner.set(owner);
    this.rawDraft.update((draft) => ({
      ...draft,
      originalFilename: file.name,
    }));
    this.selectedFile.set({ name: file.name, byteCount: file.size });
    this.preparing.set(true);
    this.queuedFile = { file, owner, generation: this.fileGeneration };
    void this.captureNext();
  }

  upload(): void {
    const admission = this.view(),
      source = this.source(),
      scope = this.scope();
    if (!admission || !source || !scope || !this.canUpload()) return;
    const input = this.rawDraft(),
      validation = validateNativeMappingUploadInput(scope, input);
    if (!validation.valid) {
      this.inputErrors.set(validation.errors);
      return;
    }
    this.inputErrors.set(noErrors);
    const owner = ownerOf(admission),
      fileGeneration = this.fileGeneration;
    this.admit(true, owner, (fresh) => {
      if (source !== this.source() || fileGeneration !== this.fileGeneration)
        return;
      try {
        const intent = NativeMappingUploadIntent.create(
          fresh,
          input,
          source,
          crypto.randomUUID(),
        );
        this.original.set(intent);
        this.source.set(null);
        this.selectedFile.set(null);
        this.rawDraft.set(blank);
        this.draftOwner.set(null);
        this.queuedFile = null;
        this.sendOriginal(fresh, intent);
      } catch (error: unknown) {
        this.failure.set(uploadError(error));
      }
    });
  }

  resendOriginal(): void {
    const intent = this.original();
    if (!intent || !this.canResend()) return;
    this.admit(
      true,
      {
        mode: intent.mode,
        scope: intent.scope,
        actorPrincipalId: intent.actorPrincipalId,
      },
      (admission) => this.sendOriginal(admission, intent),
    );
  }

  checkUploadStatus(): void {
    const operation = this.known();
    if (!operation || !this.canCheckStatus()) return;
    this.admit(true, ownerOfOperation(operation), (admission) => {
      this.request(
        "receipt",
        this.api.receipt(admission, operation),
        (next) => {
          this.latest.set(null);
          this.acceptOperation(next);
        },
      );
    });
  }

  resumeUpload(): void {
    const operation = this.known();
    if (!operation || !this.canResume()) return;
    this.admit(true, ownerOfOperation(operation), (admission) =>
      this.request(
        "resume",
        this.api.resume(admission, operation),
        (response) => this.acceptResponse(response),
      ),
    );
  }

  retryRevisionAccess(): void {
    const operation = this.known();
    if (!operation?.committed || !this.canRetryRevisionAccess()) return;
    this.admit(false, ownerOfOperation(operation), (admission) =>
      this.readRevision(admission, operation),
    );
  }

  startAnother(): void {
    if (!this.canStartAnother()) return;
    this.clearSlot();
    const admission = this.view();
    if (admission) this.draftOwner.set(ownerOf(admission));
  }

  abandonOriginal(acknowledgedRisk: boolean): void {
    if (!acknowledgedRisk || !this.canAbandon()) return;
    this.clearSlot();
    const admission = this.view();
    if (admission) this.draftOwner.set(ownerOf(admission));
  }

  discardDraft(): void {
    if (!this.locked()) {
      this.cancelNetwork();
      this.invalidateFile();
      this.clearDraft();
    }
  }

  stopWaiting(): void {
    if (this.preparingFile() && !this.busy()) {
      this.invalidateFile();
      this.fileFailure.set(new NativeMappingUploadError("canceled"));
      return;
    }
    const admitting = this.work() === "admitting";
    this.cancelNetwork();
    this.failure.set(new NativeMappingUploadError("unavailable"));
    if (admitting) {
      this.presenting.set(false);
      this.admissionState.set("unavailable");
    }
  }

  private async captureNext(): Promise<void> {
    if (this.captureRunning || !this.queuedFile) return;
    const capture = this.queuedFile;
    this.queuedFile = null;
    this.captureRunning = true;
    const controller = new AbortController();
    this.captureAbort = controller;
    try {
      const source = await NativeMappingUploadSource.capture(
        capture.file,
        controller.signal,
      );
      if (this.fileCurrent(capture.generation, capture.owner)) {
        this.source.set(source);
        this.fileFailure.set(null);
      }
    } catch (error: unknown) {
      if (this.fileCurrent(capture.generation, capture.owner))
        this.fileFailure.set(uploadError(error));
    } finally {
      this.captureRunning = false;
      if (this.captureAbort === controller) this.captureAbort = null;
      if (this.queuedFile) void this.captureNext();
      else this.preparing.set(false);
    }
  }

  private admit(
    requireUpload: boolean,
    expected: Owner | null,
    next: (admission: NativeMappingUploadAdmission) => void,
  ): void {
    const scope = this.scope();
    if (!this.active() || !scope || this.busy()) return;
    this.cancelNetwork();
    const generation = this.requestGeneration;
    this.work.set("admitting");
    this.admitted.set(null);
    this.access.set({ kind: "idle" });
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
            throw new NativeMappingUploadError("unavailable");
          const owner = this.slotOwner();
          if (owner && !owns(context, owner)) {
            this.foreignOwner.set(true);
            this.presenting.set(false);
            this.invalidateFile();
            if (!this.locked()) this.clearDraft();
          }
          if (context.allowedOrganizationId !== scope.organizationId)
            throw new NativeMappingUploadError("notFound");
          if (
            expected &&
            (!owns(context, expected) || !sameScope(scope, expected.scope))
          )
            throw new NativeMappingUploadError("identityChanged");
          return this.catalog.project(scope).pipe(
            take(1),
            map((project): NativeMappingUploadAdmission => ({
              context,
              project,
            })),
          );
        }),
        take(1),
      )
      .subscribe({
        next: (admission) => {
          emitted = true;
          if (!this.current(generation, scope)) return;
          this.work.set("idle");
          if (
            this.contexts.context() !== admission.context ||
            !sameScope(admission.project.scope, scope)
          ) {
            this.presenting.set(false);
            this.admissionState.set("unavailable");
            return;
          }
          const owner = this.slotOwner();
          if (
            !owner ||
            (owns(admission.context, owner) && sameScope(scope, owner.scope))
          )
            this.foreignOwner.set(false);
          if (!owner) this.draftOwner.set(ownerOf(admission));
          this.admitted.set(admission);
          this.presenting.set(canUpload(admission));
          this.admissionState.set(canUpload(admission) ? "ready" : "readOnly");
          if (requireUpload && (!canUpload(admission) || this.quarantined())) {
            this.failure.set(
              new NativeMappingUploadError(
                this.quarantined()
                  ? "identityChanged"
                  : admission.context.features.createMappings
                    ? "uploadUnavailable"
                    : "featureDisabled",
              ),
            );
            return;
          }
          if (this.contexts.canPresent(admission.context)) next(admission);
        },
        error: (error: unknown) => {
          if (!this.current(generation, scope)) return;
          this.work.set("idle");
          this.admitted.set(null);
          this.presenting.set(false);
          const failure = uploadError(error);
          this.failure.set(failure);
          this.admissionState.set(
            failure.kind === "notFound"
              ? "notFound"
              : failure.kind === "identityRejected"
                ? "rejected"
                : "unavailable",
          );
        },
        complete: () => {
          if (!emitted && this.current(generation, scope)) {
            this.work.set("idle");
            this.presenting.set(false);
            this.admissionState.set("unavailable");
          }
        },
      });
  }

  private sendOriginal(
    admission: NativeMappingUploadAdmission,
    intent: NativeMappingUploadIntent,
  ): void {
    if (this.original() !== intent) return;
    this.request(
      "create",
      this.api.create(admission, intent, this.known()),
      (response) => this.acceptResponse(response),
    );
  }

  private request<T>(
    kind: "create" | "resume" | "receipt",
    observable: Observable<T>,
    accept: (value: T) => void,
  ): void {
    const scope = this.scope();
    if (!scope) return;
    const generation = this.requestGeneration;
    this.work.set(kind);
    this.stopped.set(false);
    this.failure.set(null);
    let emitted = false;
    this.requestSubscription = observable.pipe(take(1)).subscribe({
      next: (value) => {
        emitted = true;
        if (!this.current(generation, scope)) return;
        this.work.set("idle");
        accept(value);
      },
      error: (error: unknown) => {
        if (!this.current(generation, scope)) return;
        this.work.set("idle");
        this.recordFailure(uploadError(error));
      },
      complete: () => {
        if (!emitted && this.current(generation, scope)) {
          this.work.set("idle");
          this.recordFailure(
            new NativeMappingUploadError(
              "unavailable",
              null,
              kind !== "receipt",
            ),
          );
        }
      },
    });
  }

  private acceptResponse(response: NativeMappingUploadResponse): void {
    this.latest.set(response);
    if (response.outcome === "notAuthorizedOrNotFound") {
      this.recordFailure(new NativeMappingUploadError("notFound"));
      return;
    }
    if (response.operation) this.acceptOperation(response.operation);
  }

  private acceptOperation(operation: NativeMappingUploadOperation): void {
    this.known.set(operation);
    if (!operation.terminal) return;
    this.unknown.set(false);
    this.stopped.set(false);
    this.original.set(null);
    this.source.set(null);
    this.selectedFile.set(null);
    this.rawDraft.set(blank);
    this.draftOwner.set(null);
    this.queuedFile = null;
    if (operation.committed) this.retryRevisionAccess();
  }

  private readRevision(
    admission: NativeMappingUploadAdmission,
    operation: NativeMappingUploadOperation,
  ): void {
    if (
      !operation.committed ||
      !owns(admission.context, ownerOfOperation(operation))
    )
      return;
    const scope = this.scope();
    if (!scope) return;
    const generation = this.requestGeneration;
    this.work.set("revision");
    this.access.set({ kind: "loading" });
    let emitted = false;
    this.requestSubscription = this.catalog
      .revision(operation.receipt.futureRevision)
      .pipe(take(1))
      .subscribe({
        next: (revision) => {
          emitted = true;
          if (!this.current(generation, scope)) return;
          this.work.set("idle");
          if (
            this.view() !== admission ||
            this.known() !== operation ||
            !operation.matchesRevision(revision)
          ) {
            this.access.set({ kind: "unavailable" });
            return;
          }
          this.access.set({
            kind: "ready",
            revision: Object.freeze({
              scope: Object.freeze({ ...revision.scope }),
              sequence: revision.sequence,
              admission: revision.admission,
              originalFilename: revision.originalFilename,
              nativeByteCount: revision.nativeByteCount,
            }),
          });
        },
        error: (error: unknown) => {
          if (!this.current(generation, scope)) return;
          this.work.set("idle");
          this.access.set({
            kind:
              error instanceof CatalogReadError ? error.kind : "unavailable",
          });
        },
        complete: () => {
          if (!emitted && this.current(generation, scope)) {
            this.work.set("idle");
            this.access.set({ kind: "unavailable" });
          }
        },
      });
  }

  private recordFailure(error: NativeMappingUploadError): void {
    this.failure.set(error);
    if (error.mayHaveCommitted && !this.known()?.terminal)
      this.unknown.set(true);
    if (error.kind === "notFound" || error.kind === "identityRejected") {
      this.admitted.set(null);
      this.presenting.set(false);
      this.admissionState.set(
        error.kind === "notFound" ? "notFound" : "rejected",
      );
    }
    this.contexts.reject(error.status);
  }

  private uploadAdmitted(): boolean {
    const admission = this.view();
    return (
      admission !== null &&
      canUpload(admission) &&
      !this.foreignOwner() &&
      this.sameSelectedOwner()
    );
  }

  private sameSelectedOwner(): boolean {
    const owner = this.slotOwner(),
      selected = this.scope();
    return (
      selected !== null && (owner === null || sameScope(selected, owner.scope))
    );
  }

  private current(generation: number, scope: CatalogProjectScope): boolean {
    const selected = this.scope(),
      context = this.contexts.context(),
      owner = this.slotOwner();
    return (
      this.active() &&
      generation === this.requestGeneration &&
      selected !== null &&
      sameScope(scope, selected) &&
      (!context ||
        !owner ||
        owns(context, owner) ||
        this.work() === "admitting")
    );
  }

  private fileCurrent(generation: number, owner: Owner): boolean {
    const admission = this.view(),
      selected = this.scope();
    return (
      this.active() &&
      generation === this.fileGeneration &&
      !this.locked() &&
      selected !== null &&
      sameScope(selected, owner.scope) &&
      admission !== null &&
      owns(admission.context, owner) &&
      canUpload(admission)
    );
  }

  private invalidateFile(): void {
    ++this.fileGeneration;
    this.captureAbort?.abort();
    this.queuedFile = null;
    this.preparing.set(false);
  }

  private cancelNetwork(): void {
    ++this.requestGeneration;
    if (this.sending() && !this.known()?.terminal) {
      this.unknown.set(true);
      this.stopped.set(true);
    }
    this.admissionSubscription?.unsubscribe();
    this.requestSubscription?.unsubscribe();
    this.admissionSubscription = this.requestSubscription = null;
    this.work.set("idle");
  }

  private clearDraft(): void {
    this.rawDraft.set(blank);
    this.draftOwner.set(null);
    this.source.set(null);
    this.selectedFile.set(null);
    this.inputErrors.set(noErrors);
    this.fileFailure.set(null);
  }

  private clearSlot(): void {
    this.invalidateFile();
    this.clearDraft();
    this.original.set(null);
    this.known.set(null);
    this.latest.set(null);
    this.unknown.set(false);
    this.stopped.set(false);
    this.foreignOwner.set(false);
    this.failure.set(null);
    this.access.set({ kind: "idle" });
  }
}

function sameScope(
  left: CatalogProjectScope,
  right: CatalogProjectScope,
): boolean {
  return (
    left.organizationId === right.organizationId &&
    left.projectId === right.projectId
  );
}
function owns(context: CatalogContext, owner: Owner): boolean {
  return (
    context.mode === owner.mode &&
    context.actorPrincipalId === owner.actorPrincipalId &&
    context.allowedOrganizationId === owner.scope.organizationId
  );
}
function ownerOf(admission: NativeMappingUploadAdmission): Owner {
  const mode = admission.context.mode;
  if (mode !== "development" && mode !== "session")
    throw new NativeMappingUploadError("identityRejected");
  return {
    mode,
    scope: Object.freeze({ ...admission.project.scope }),
    actorPrincipalId: admission.context.actorPrincipalId,
  };
}
function ownerOfOperation(operation: NativeMappingUploadOperation): Owner {
  return {
    mode: operation.mode,
    scope: operation.scope,
    actorPrincipalId: operation.actorPrincipalId,
  };
}
function canUpload(admission: NativeMappingUploadAdmission): boolean {
  return (
    admission.context.features.createMappings &&
    admission.project.access.actions.createMappings
  );
}
function uploadError(error: unknown): NativeMappingUploadError {
  if (error instanceof NativeMappingUploadError) return error;
  if (error instanceof CatalogReadError) {
    if (error.kind === "notFound")
      return new NativeMappingUploadError("notFound");
    if (error.kind === "rejected")
      return new NativeMappingUploadError("identityRejected");
    if (error.kind === "invalidLink")
      return new NativeMappingUploadError("invalidInput");
  }
  return new NativeMappingUploadError("unavailable");
}
