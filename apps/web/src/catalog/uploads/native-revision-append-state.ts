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
import { Observable, Subject, Subscription, map, switchMap, take } from "rxjs";
import { CatalogApi } from "../catalog-api";
import { CatalogContextStore } from "../catalog-context";
import {
  CatalogReadError,
  isCatalogId,
  type CatalogContext,
  type CatalogFailureKind,
  type CatalogMapping,
  type CatalogMappingScope,
  type CatalogRevisionScope,
} from "../catalog-types";
import {
  NativeIngestionSource,
  NativeIngestionSourceError,
  nativeIngestionMaximumBytes,
  type NativeIngestionSourceErrorKind,
} from "./native-ingestion-leaves";
import { NativeRevisionAppendApi } from "./native-revision-append-api";
import type { RevisionAppendDraftRecovery } from "./unsent-upload-recovery";
import {
  NativeRevisionAppendError,
  NativeRevisionAppendHead,
  NativeRevisionAppendIntent,
  captureNativeRevisionAppendAdmission,
  validateNativeRevisionAppendInput,
  type NativeRevisionAppendAdmission,
  type NativeRevisionAppendAdmissionClassification,
  type NativeRevisionAppendErrorKind,
  type NativeRevisionAppendFailure,
  type NativeRevisionAppendInput,
  type NativeRevisionAppendInputErrors,
  type NativeRevisionAppendOperation,
  type NativeRevisionAppendOutcome,
  type NativeRevisionAppendPhase,
  type NativeRevisionAppendRejection,
  type NativeRevisionAppendResponse,
} from "./native-revision-append-types";
import { UnsentWork } from "../../workbench/unsent-work";

export type NativeRevisionAppendLeaveRisk = "draft" | "recovery" | null;
export type NativeRevisionAppendWork =
  "idle" | "admitting" | "append" | "resume" | "receipt" | "revision";
export interface NativeRevisionAppendHeadView {
  readonly scope: Readonly<CatalogRevisionScope>;
  readonly sequence: string;
  readonly admission: NativeRevisionAppendAdmissionClassification;
}
export interface NativeRevisionAppendMappingView {
  readonly scope: Readonly<CatalogMappingScope>;
  readonly name: string;
  readonly backendId: string;
  readonly head: NativeRevisionAppendHeadView;
}
export type NativeRevisionAppendRevisionAccess =
  | { readonly kind: "idle" | "loading" }
  | { readonly kind: CatalogFailureKind }
  | {
      readonly kind: "ready";
      readonly revision: NativeRevisionAppendHeadView & {
        readonly originalFilename: string;
        readonly nativeByteCount: number;
      };
    };
export interface NativeRevisionAppendView {
  readonly admission:
    "idle" | "loading" | "ready" | "readOnly" | CatalogFailureKind;
  readonly work: NativeRevisionAppendWork;
  readonly preparingFile: boolean;
  readonly quarantined: boolean;
  readonly mapping: NativeRevisionAppendMappingView | null;
  readonly reviewedHead: NativeRevisionAppendHeadView | null;
  readonly headReviewRequired: boolean;
  readonly draft: { readonly originalFilename: string };
  readonly file: { readonly name: string; readonly byteCount: number } | null;
  readonly fieldErrors: NativeRevisionAppendInputErrors;
  readonly sourceError: NativeIngestionSourceErrorKind | null;
  readonly error: NativeRevisionAppendErrorKind | null;
  readonly outcomeUnknown: boolean;
  readonly waitingStopped: boolean;
  readonly locked: boolean;
  readonly canEdit: boolean;
  /** Mount-only presentation; this flag grants no edit or upload action. */
  readonly formVisible: boolean;
  readonly canChooseFile: boolean;
  readonly canAppend: boolean;
  readonly canReviewHead: boolean;
  readonly canResend: boolean;
  readonly canCheckStatus: boolean;
  readonly canResume: boolean;
  readonly canRetryRevisionAccess: boolean;
  readonly canStartNew: boolean;
  readonly canAbandon: boolean;
  readonly canStopWaiting: boolean;
  readonly response: {
    readonly outcome: NativeRevisionAppendOutcome;
    readonly failure: NativeRevisionAppendFailure;
    readonly rejection: NativeRevisionAppendRejection;
  } | null;
  readonly operationStatus: {
    readonly phase: NativeRevisionAppendPhase;
    readonly claimCount: number;
    readonly failure: NativeRevisionAppendFailure;
    readonly terminal: boolean;
    readonly committed: boolean;
    readonly headConflict: boolean;
  } | null;
  readonly terminalOutcome:
    "headConflict" | "rejected" | "attemptExhausted" | null;
  readonly acknowledgement: {
    readonly detailed: boolean;
    readonly revision: NativeRevisionAppendHeadView | null;
    readonly completedAtUtc: string | null;
  } | null;
  readonly currentAccess: NativeRevisionAppendRevisionAccess;
}
type Owner = {
  readonly mode: "development" | "session";
  readonly scope: Readonly<CatalogMappingScope>;
  readonly actorPrincipalId: string;
};
type AdmissionState = NativeRevisionAppendView["admission"];
type FileSummary = { readonly name: string; readonly byteCount: number };
type ResponseSummary = NonNullable<NativeRevisionAppendView["response"]>;
const blank = Object.freeze({ originalFilename: "" });
const noErrors: NativeRevisionAppendInputErrors = Object.freeze({
  originalFilename: null,
  request: null,
});

/** One F-only tab-memory slot. Its private request material is never a view model. */
@Injectable({ providedIn: "root" })
export class NativeRevisionAppendState {
  private readonly contexts = inject(CatalogContextStore);
  private readonly catalog = inject(CatalogApi);
  private readonly api = inject(NativeRevisionAppendApi);
  private readonly document = inject(DOCUMENT);
  private readonly active = signal(false);
  private readonly target = signal<Readonly<CatalogMappingScope> | null>(null);
  private readonly admitted = signal<NativeRevisionAppendAdmission | null>(
    null,
  );
  private readonly presentation = signal<{
    readonly admission: NativeRevisionAppendAdmission;
    readonly identityVersion: number;
  } | null>(null);
  private readonly admissionState = signal<AdmissionState>("idle");
  private readonly work = signal<NativeRevisionAppendWork>("idle");
  private readonly filename = signal("");
  private readonly selectedFile = signal<FileSummary | null>(null);
  private readonly source = signal<NativeIngestionSource | null>(null);
  private readonly draftOwner = signal<Owner | null>(null);
  private readonly reviewed = signal<NativeRevisionAppendHead | null>(null);
  private readonly reviewRequired = signal(false);
  private readonly original = signal<NativeRevisionAppendIntent | null>(null);
  private readonly known = signal<NativeRevisionAppendOperation | null>(null);
  private readonly latest = signal<ResponseSummary | null>(null);
  private readonly unknown = signal(false);
  private readonly stopped = signal(false);
  private readonly foreign = signal(false);
  private readonly ownerReadStamp = signal<{
    readonly owner: Owner;
    readonly identityVersion: number;
  } | null>(null);
  private readonly failure = signal<NativeRevisionAppendErrorKind | null>(null);
  private readonly sourceFailure =
    signal<NativeIngestionSourceErrorKind | null>(null);
  private readonly errors = signal<NativeRevisionAppendInputErrors>(noErrors);
  private readonly access = signal<NativeRevisionAppendRevisionAccess>({
    kind: "idle",
  });
  private readonly preparing = signal(false);
  private readonly pendingLeave = signal<NativeRevisionAppendLeaveRisk>(null);
  private readonly focus = signal<{
    readonly sequence: number;
    readonly target: "summary" | "status" | "review" | "filename";
  } | null>(null);
  private leaveDecision: Subject<boolean> | null = null;
  private focusSequence = 0;
  private networkGeneration = 0;
  private fileGeneration = 0;
  private observedIdentityVersion = this.contexts.identityVersion();
  private network: Subscription | null = null;
  private captureAbort: AbortController | null = null;
  private draftRecovery: RevisionAppendDraftRecovery | null = null;
  private draftContext: CatalogContext | null = null;
  private sourceFile: File | null = null;
  private captureRunning = false;
  private queuedFile: {
    readonly file: File;
    readonly owner: Owner;
    readonly generation: number;
    readonly identityVersion: number;
  } | null = null;

  readonly view = computed<NativeRevisionAppendView>(() => this.projectView());
  readonly leaveRisk = computed<NativeRevisionAppendLeaveRisk>(() => {
    if (this.original() || (this.known() && !this.known()!.terminal))
      return "recovery";
    return this.dirty() || this.draftRecovery?.hasWork() ? "draft" : null;
  });
  readonly leavePrompt = this.pendingLeave.asReadonly();
  readonly focusRequest = this.focus.asReadonly();

  constructor() {
    inject(UnsentWork).track(() => !!this.leaveRisk());
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.suspendDraft();
      this.observedIdentityVersion = this.contexts.identityVersion();
      this.cancelNetwork();
      this.invalidateFile();
      if (!this.locked()) this.clearDraft();
      this.foreign.set(true);
      this.ownerReadStamp.set(null);
      this.admitted.set(null);
      this.presentation.set(null);
      this.access.set({ kind: "idle" });
      this.focus.set(null);
      this.failure.set(null);
      this.resolveLeave(false);
      if (this.active()) this.admissionState.set("unavailable");
    });
    inject(DestroyRef).onDestroy(() => invalidation.unsubscribe());
    effect(() => {
      const version = this.contexts.identityVersion();
      const owner = this.owner();
      // admit() quarantines and acknowledges its context before reading the mapping.
      // Cancel only identity changes not yet handled by that admission.
      if (owner && version !== this.observedIdentityVersion) {
        untracked(() => {
          this.suspendDraft();
          this.observedIdentityVersion = version;
          this.cancelNetwork();
          this.invalidateFile();
          if (!this.locked()) this.clearDraft();
          this.foreign.set(true);
          this.admitted.set(null);
          this.presentation.set(null);
          this.access.set({ kind: "idle" });
          this.resolveLeave(false);
          if (this.active()) this.admissionState.set("unavailable");
        });
      }
    });
    effect(() => {
      const prompt = this.pendingLeave(),
        risk = this.leaveRisk();
      if (prompt && prompt !== risk)
        untracked(() => {
          if (risk === null) this.resolveLeave(true);
          else this.pendingLeave.set(risk);
        });
    });
    const warn = (event: BeforeUnloadEvent) => {
      if (!this.leaveRisk()) return;
      event.preventDefault();
      event.returnValue = "";
    };
    this.document.defaultView?.addEventListener("beforeunload", warn);
    inject(DestroyRef).onDestroy(() => {
      this.deactivate();
      this.clearSlot();
      this.resolveLeave(false);
      this.document.defaultView?.removeEventListener("beforeunload", warn);
    });
  }

  select(
    scope: CatalogMappingScope | null,
    recovery: RevisionAppendDraftRecovery,
  ): void {
    this.draftRecovery = recovery;
    recovery.routeScope(scope);
    const previous = this.target();
    if (this.active() && scope && previous && sameScope(scope, previous))
      return;
    this.cancelNetwork();
    this.invalidateFile();
    if (!this.locked()) this.clearDraft();
    this.active.set(true);
    this.admitted.set(null);
    this.presentation.set(null);
    this.failure.set(null);
    this.access.set({ kind: "idle" });
    this.focus.set(null);
    if (
      !scope ||
      !isCatalogId(scope.organizationId) ||
      !isCatalogId(scope.projectId) ||
      !isCatalogId(scope.mappingId)
    ) {
      this.target.set(null);
      this.contexts.clear();
      this.admissionState.set("invalidLink");
      return;
    }
    this.target.set(copyScope(scope));
    this.refreshAccess();
  }

  /** Route destruction is not server cancellation and never discards an unfinished original. */
  deactivate(): void {
    this.cancelNetwork();
    this.invalidateFile();
    if (!this.locked()) this.clearDraft();
    this.draftRecovery?.discard();
    this.draftRecovery = null;
    this.active.set(false);
    this.resolveLeave(false);
    this.admitted.set(null);
    this.presentation.set(null);
    this.admissionState.set("idle");
    this.access.set({ kind: "idle" });
    this.focus.set(null);
  }

  refreshAccess(): void {
    if (this.busy() || this.preparing() || this.pendingLeave()) return;
    this.admit(false, null, (admission) => {
      const operation = this.known();
      if (operation?.committed && this.safeSlot())
        this.readRevision(admission, operation);
    });
  }

  reviewCurrentHead(): void {
    if (!this.view().canReviewHead) return;
    // Reading a terminal conflict's current head never changes the original condition.
    if (this.locked()) {
      this.refreshAccess();
      return;
    }
    this.admit(false, this.draftOwner(), (admission) => {
      if (this.locked()) return;
      this.reviewed.set(NativeRevisionAppendHead.fromAdmission(admission));
      this.reviewRequired.set(false);
      this.failure.set(null);
      this.requestFocus("review");
    });
  }

  setFilename(originalFilename: string): void {
    if (!this.view().canEdit || typeof originalFilename !== "string") return;
    this.draftContext = this.readAdmission()!.context;
    this.filename.set(originalFilename);
    this.errors.set(noErrors);
  }

  selectFile(file: File | null): void {
    if (!this.view().canChooseFile) return;
    this.invalidateFile();
    this.source.set(null);
    this.selectedFile.set(null);
    this.sourceFailure.set(null);
    this.errors.set(noErrors);
    const admission = this.readAdmission();
    if (!file || !admission) return;
    if (
      !(file instanceof File) ||
      file.size < 1 ||
      file.size > nativeIngestionMaximumBytes
    ) {
      this.sourceFailure.set(
        !(file instanceof File)
          ? "invalidInput"
          : file.size < 1
            ? "emptyNative"
            : "nativeLimit",
      );
      this.requestFocus("summary");
      return;
    }
    const owner = admissionOwner(admission);
    this.draftContext = admission.context;
    this.draftOwner.set(owner);
    this.filename.set(file.name);
    this.selectedFile.set(
      Object.freeze({ name: file.name, byteCount: file.size }),
    );
    this.sourceFile = file;
    this.preparing.set(true);
    this.queuedFile = {
      file,
      owner,
      generation: this.fileGeneration,
      identityVersion: this.contexts.identityVersion(),
    };
    void this.captureNext();
  }

  append(): void {
    const admission = this.readAdmission(),
      source = this.source(),
      head = this.reviewed(),
      scope = this.target();
    if (!admission || !source || !head || !scope || !this.view().canAppend)
      return;
    const input: NativeRevisionAppendInput = Object.freeze({
      originalFilename: this.filename(),
    });
    const validation = validateNativeRevisionAppendInput(scope, head, input);
    if (!validation.valid) {
      this.errors.set(Object.freeze({ ...validation.errors }));
      this.requestFocus("summary");
      return;
    }
    const owner = admissionOwner(admission),
      fileGeneration = this.fileGeneration;
    this.errors.set(noErrors);
    this.admit(true, owner, (fresh) => {
      if (
        source !== this.source() ||
        head !== this.reviewed() ||
        fileGeneration !== this.fileGeneration
      )
        return;
      if (!head.matchesMapping(fresh.mapping)) {
        this.reviewRequired.set(true);
        this.failure.set("headReviewRequired");
        this.requestFocus("review");
        return;
      }
      try {
        // This is the only key-allocation point; fresh head equality has already passed.
        const intent = NativeRevisionAppendIntent.create(
          fresh,
          head,
          input,
          source,
          crypto.randomUUID(),
        );
        this.original.set(intent);
        this.draftRecovery?.discard();
        this.source.set(null);
        this.selectedFile.set(null);
        this.filename.set("");
        this.draftOwner.set(null);
        this.queuedFile = null;
        this.sourceFile = null;
        this.draftContext = null;
        this.sendOriginal(fresh, intent);
      } catch (error: unknown) {
        const failure = appendError(error);
        if (failure.kind === "headReviewRequired")
          this.reviewRequired.set(true);
        this.recordFailure(failure);
        this.requestFocus(
          failure.kind === "headReviewRequired" ? "review" : "summary",
        );
      }
    });
  }

  resendOriginal(): void {
    const intent = this.original();
    if (!intent || !this.view().canResend) return;
    this.admit(true, boundOwner(intent), (admission) => {
      // Recovery deliberately does not compare today's head with the original head.
      if (this.original() === intent) this.sendOriginal(admission, intent);
    });
  }

  checkAppendStatus(): void {
    const operation = this.known();
    if (!operation || !this.view().canCheckStatus) return;
    this.admit(true, boundOwner(operation), (admission) => {
      this.request(
        "receipt",
        () => this.api.receipt(admission, operation),
        (next) => {
          this.latest.set(null);
          this.acceptOperation(next);
        },
      );
    });
  }

  resumeAppend(): void {
    const operation = this.known();
    if (!operation || !this.view().canResume) return;
    this.admit(true, boundOwner(operation), (admission) =>
      this.request(
        "resume",
        () => this.api.resume(admission, operation),
        (reply) => this.acceptResponse(reply),
      ),
    );
  }

  retryRevisionAccess(): void {
    const operation = this.known();
    if (!operation?.committed || !this.view().canRetryRevisionAccess) return;
    this.admit(false, boundOwner(operation), (admission) =>
      this.readRevision(admission, operation),
    );
  }

  startNewAppend(): void {
    if (!this.view().canStartNew) return;
    this.clearSlot();
    this.admit(false, null, (admission) => {
      this.draftOwner.set(admissionOwner(admission));
      this.reviewed.set(NativeRevisionAppendHead.fromAdmission(admission));
      this.requestFocus("filename");
    });
  }

  abandonOriginal(acknowledgedRisk: boolean): void {
    if (!acknowledgedRisk || !this.view().canAbandon) return;
    this.clearSlot();
    this.refreshAccess();
    this.requestFocus("filename");
  }

  stopWaiting(): void {
    if (this.preparing() && !this.busy()) {
      this.invalidateFile();
      this.source.set(null);
      this.sourceFailure.set("canceled");
    } else if (this.busy()) {
      const previousWork = this.work();
      this.cancelNetwork();
      this.failure.set("unavailable");
      if (previousWork === "revision") this.access.set({ kind: "unavailable" });
      if (previousWork === "admitting") {
        this.admitted.set(null);
        this.admissionState.set("unavailable");
      }
    }
    this.requestFocus("status");
  }

  requestLeave(): Observable<boolean> {
    return new Observable<boolean>((subscriber) => {
      const risk = this.leaveRisk();
      if (!risk) {
        subscriber.next(true);
        subscriber.complete();
        return;
      }
      if (!this.leaveDecision) this.leaveDecision = new Subject<boolean>();
      this.pendingLeave.set(risk);
      const subscription = this.leaveDecision.subscribe(subscriber);
      return () => subscription.unsubscribe();
    });
  }
  stay(): void {
    this.resolveLeave(false);
  }
  discardDraftAndLeave(): void {
    if (this.pendingLeave() !== "draft" || this.leaveRisk() !== "draft") return;
    this.draftRecovery?.discard();
    this.cancelNetwork();
    this.invalidateFile();
    this.clearDraft();
    this.resolveLeave(true);
  }
  keepRecoveryAndLeave(): void {
    if (this.pendingLeave() !== "recovery") return;
    this.cancelNetwork();
    this.resolveLeave(true);
  }

  private owner(): Owner | null {
    const original = this.original(),
      operation = this.known();
    return original
      ? boundOwner(original)
      : operation
        ? boundOwner(operation)
        : this.draftOwner();
  }
  private locked(): boolean {
    return this.original() !== null || this.known() !== null;
  }
  private busy(): boolean {
    return this.work() !== "idle";
  }
  private dirty(): boolean {
    return (
      !this.locked() &&
      (this.source() !== null || this.preparing() || this.filename() !== "")
    );
  }
  private readAdmission(): NativeRevisionAppendAdmission | null {
    const admission = this.admitted(),
      scope = this.target();
    return this.active() &&
      admission &&
      scope &&
      sameScope(admission.mapping.scope, scope) &&
      this.contexts.isCurrent(admission.context)
      ? admission
      : null;
  }
  private safeSlot(): boolean {
    const owner = this.owner(),
      scope = this.target(),
      context = this.contexts.context();
    return (
      this.active() &&
      !!scope &&
      !this.foreign() &&
      (!owner || sameScope(owner.scope, scope)) &&
      !!context &&
      (!owner || (owns(context, owner) && this.hasOwnerReadStamp(owner)))
    );
  }
  /** Only the already displayed original-owner graph survives a safe admission. */
  private presentedAdmission(): NativeRevisionAppendAdmission | null {
    const presentation = this.presentation(),
      scope = this.target(),
      owner = this.owner(),
      observed = this.contexts.context();
    if (
      !this.active() ||
      this.work() !== "admitting" ||
      !presentation ||
      !scope ||
      !owner ||
      this.foreign() ||
      presentation.identityVersion !== this.contexts.identityVersion() ||
      !sameScope(presentation.admission.mapping.scope, scope) ||
      !sameScope(owner.scope, scope) ||
      !owns(presentation.admission.context, owner) ||
      !this.hasOwnerReadStamp(owner)
    )
      return null;
    const context = presentation.admission.context;
    return !observed ||
      (owns(observed, owner) &&
        (observed.mode === "session" ? observed.identityContext : null) ===
          (context.mode === "session" ? context.identityContext : null))
      ? presentation.admission
      : null;
  }
  private hasOwnerReadStamp(owner: Owner): boolean {
    const stamp = this.ownerReadStamp();
    return (
      stamp !== null &&
      stamp.identityVersion === this.contexts.identityVersion() &&
      stamp.owner.mode === owner.mode &&
      stamp.owner.actorPrincipalId === owner.actorPrincipalId &&
      sameScope(stamp.owner.scope, owner.scope)
    );
  }
  private retainedCommitVisible(
    operation: NativeRevisionAppendOperation | null,
  ): boolean {
    const scope = this.target(),
      context = this.contexts.context();
    if (
      !operation?.committed ||
      !this.active() ||
      !scope ||
      !sameScope(scope, operation.scope) ||
      this.foreign()
    )
      return false;
    const owner = boundOwner(operation);
    return (
      this.hasOwnerReadStamp(owner) &&
      (context === null || owns(context, owner))
    );
  }
  private uploadAdmitted(): boolean {
    const admission = this.readAdmission();
    return admission !== null && this.safeSlot() && canUpload(admission);
  }
  private projectView(): NativeRevisionAppendView {
    const admission = this.readAdmission(),
      presentation = this.presentedAdmission(),
      displayed = admission ?? presentation,
      safe = this.safeSlot(),
      operation = this.known();
    const read = admission !== null && safe,
      upload = this.uploadAdmitted(),
      available = this.contexts.canPresent(admission?.context ?? null),
      dispatchable = this.contexts.canDispatch(admission?.context ?? null);
    const busy = this.busy(),
      blocked = busy || this.pendingLeave() !== null;
    const locked = this.locked(),
      privateDraft =
        (read && upload) || (presentation !== null && canUpload(presentation));
    const canEdit = read && upload && available && !locked && !blocked;
    const original = this.original(),
      response = this.latest();
    const head = locked
      ? (operation?.reviewedHead ?? original?.reviewedHead ?? null)
      : this.reviewed();
    const committed = read && operation?.committed === true;
    const retainedCommit = this.retainedCommitVisible(operation);
    return Object.freeze({
      admission: this.admissionState(),
      work: this.work(),
      preparingFile: this.preparing(),
      quarantined:
        this.active() && locked && !safe && !presentation && !retainedCommit,
      mapping: displayed ? mappingView(displayed.mapping) : null,
      reviewedHead:
        (read || presentation !== null) && head ? headView(head) : null,
      headReviewRequired:
        (read || presentation !== null) && !locked && this.reviewRequired(),
      draft: privateDraft
        ? Object.freeze({
            originalFilename: original?.originalFilename ?? this.filename(),
          })
        : blank,
      file: privateDraft
        ? original
          ? Object.freeze({
              name: original.originalFilename,
              byteCount: original.byteCount,
            })
          : this.selectedFile()
        : null,
      fieldErrors: privateDraft ? this.errors() : noErrors,
      sourceError: privateDraft ? this.sourceFailure() : null,
      error: !this.owner() || safe ? this.failure() : null,
      outcomeUnknown: read && this.unknown(),
      waitingStopped: read && this.unknown() && this.stopped() && !busy,
      locked,
      canEdit,
      formVisible: (read || presentation !== null) && (privateDraft || locked),
      canChooseFile: canEdit,
      canAppend:
        canEdit &&
        dispatchable &&
        this.source() !== null &&
        !this.preparing() &&
        this.reviewed() !== null &&
        !this.reviewRequired(),
      canReviewHead:
        read &&
        available &&
        !blocked &&
        !this.preparing() &&
        (!locked || operation?.headConflict === true),
      canResend:
        upload &&
        dispatchable &&
        !blocked &&
        original !== null &&
        !operation?.terminal &&
        (!operation || operation.receipt.latestFailure === "needsSource"),
      canCheckStatus: upload && available && !blocked && operation !== null,
      canResume:
        upload &&
        available &&
        !blocked &&
        operation !== null &&
        !operation.terminal &&
        operation.receipt.latestFailure !== "needsSource",
      canRetryRevisionAccess:
        this.contexts.visible() && (committed || retainedCommit) && !blocked,
      canStartNew:
        upload && available && !blocked && operation?.terminal === true,
      canAbandon: read && available && !blocked && locked,
      canStopWaiting: busy || this.preparing(),
      response: upload && response ? Object.freeze({ ...response }) : null,
      operationStatus:
        upload && operation
          ? Object.freeze({
              phase: operation.receipt.phase,
              claimCount: operation.receipt.claimCount,
              failure: operation.receipt.latestFailure,
              terminal: operation.terminal,
              committed: operation.committed,
              headConflict: operation.headConflict,
            })
          : null,
      terminalOutcome:
        read && operation?.terminal && !operation.committed
          ? operation.headConflict
            ? "headConflict"
            : operation.receipt.phase === "rejected"
              ? "rejected"
              : "attemptExhausted"
          : null,
      acknowledgement:
        committed && operation
          ? Object.freeze({
              detailed: true,
              revision: committedHeadView(operation),
              completedAtUtc: operation.receipt.completedAtUtc,
            })
          : retainedCommit
            ? Object.freeze({
                detailed: false,
                revision: null,
                completedAtUtc: null,
              })
            : null,
      currentAccess: committed
        ? this.access()
        : Object.freeze({ kind: "idle" as const }),
    });
  }

  private async captureNext(): Promise<void> {
    if (this.captureRunning || !this.queuedFile) return;
    const capture = this.queuedFile;
    this.queuedFile = null;
    this.captureRunning = true;
    const controller = new AbortController();
    this.captureAbort = controller;
    try {
      const source = await NativeIngestionSource.capture(
        capture.file,
        controller.signal,
      );
      if (
        this.fileCurrent(
          capture.generation,
          capture.owner,
          capture.identityVersion,
        )
      ) {
        this.source.set(source);
        this.sourceFailure.set(null);
      }
    } catch (error: unknown) {
      if (
        this.fileCurrent(
          capture.generation,
          capture.owner,
          capture.identityVersion,
        )
      ) {
        this.sourceFailure.set(
          error instanceof NativeIngestionSourceError
            ? error.kind
            : "fileReadUnavailable",
        );
        this.requestFocus("summary");
      }
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
    next: (admission: NativeRevisionAppendAdmission) => void,
  ): void {
    const scope = this.target();
    if (!this.active() || !scope || this.busy()) return;
    const presented = this.readAdmission();
    this.presentation.set(
      presented && this.safeSlot()
        ? {
            admission: presented,
            identityVersion: this.contexts.identityVersion(),
          }
        : null,
    );
    this.cancelNetwork();
    const generation = this.networkGeneration;
    const subscription = new Subscription();
    this.network = subscription;
    this.work.set("admitting");
    this.admitted.set(null);
    this.access.set({ kind: "idle" });
    this.admissionState.set("loading");
    this.failure.set(null);
    let emitted = false;
    let admissionIdentityVersion: number | null = null;
    subscription.add(
      this.contexts
        .refresh()
        .pipe(
          take(1),
          switchMap((context) => {
            if (
              !this.current(generation, scope) ||
              this.contexts.context() !== context
            )
              throw new NativeRevisionAppendError("unavailable");
            const owner = this.owner();
            if (owner && !owns(context, owner)) {
              this.foreign.set(true);
              this.invalidateFile();
              if (!this.locked()) {
                this.draftRecovery?.discard();
                this.clearDraft();
              }
            }
            this.observedIdentityVersion = this.contexts.identityVersion();
            admissionIdentityVersion = this.observedIdentityVersion;
            if (
              (context.mode !== "development" && context.mode !== "session") ||
              !isCatalogId(context.actorPrincipalId)
            )
              throw new NativeRevisionAppendError("identityChanged");
            if (context.allowedOrganizationId !== scope.organizationId)
              throw new NativeRevisionAppendError("notFound");
            if (
              expected &&
              (!owns(context, expected) || !sameScope(scope, expected.scope))
            )
              throw new NativeRevisionAppendError("identityChanged");
            return this.catalog.mapping(scope).pipe(
              take(1),
              map((mapping): NativeRevisionAppendAdmission => ({
                context,
                mapping,
              })),
            );
          }),
          take(1),
        )
        .subscribe({
          next: (admission) => {
            emitted = true;
            if (!this.current(generation, scope)) return;
            this.presentation.set(null);
            this.work.set("idle");
            try {
              const identityVersion = this.contexts.identityVersion();
              if (
                !this.contexts.isCurrent(admission.context) ||
                !sameScope(admission.mapping.scope, scope) ||
                admissionIdentityVersion === null ||
                admissionIdentityVersion !== identityVersion
              )
                throw new NativeRevisionAppendError("unavailable");
              const candidate =
                NativeRevisionAppendHead.fromAdmission(admission);
              const owner = this.owner();
              if (
                owner &&
                (!owns(admission.context, owner) ||
                  !sameScope(scope, owner.scope))
              ) {
                this.foreign.set(true);
              } else {
                this.foreign.set(false);
                const admittedOwner = admissionOwner(admission);
                this.ownerReadStamp.set(
                  Object.freeze({ owner: admittedOwner, identityVersion }),
                );
                if (!owner) this.draftOwner.set(admittedOwner);
              }
              this.admitted.set(admission);
              this.admissionState.set(
                canUpload(admission) ? "ready" : "readOnly",
              );
              if (!this.locked()) {
                this.draftContext = admission.context;
                this.restoreDraft(admission);
              }
              if (!this.locked() && !this.reviewed() && this.safeSlot())
                this.reviewed.set(candidate);
              if (
                !this.locked() &&
                this.reviewed() &&
                !this.reviewed()!.matchesMapping(admission.mapping)
              )
                this.reviewRequired.set(true);
              if (requireUpload) {
                if (!this.safeSlot())
                  throw new NativeRevisionAppendError("identityChanged");
                captureNativeRevisionAppendAdmission(
                  admission,
                  this.original() ?? this.known() ?? undefined,
                  this.known() ?? undefined,
                );
              }
              if (this.contexts.canDispatch(admission.context)) next(admission);
            } catch (error: unknown) {
              this.recordFailure(appendError(error));
              this.requestFocus("summary");
            }
          },
          error: (error: unknown) => {
            if (!this.current(generation, scope)) return;
            this.presentation.set(null);
            this.work.set("idle");
            this.admitted.set(null);
            const failure = appendError(error);
            this.failure.set(failure.kind);
            this.admissionState.set(
              failure.kind === "notFound"
                ? "notFound"
                : failure.kind === "identityRejected"
                  ? "rejected"
                  : "unavailable",
            );
            this.requestFocus("summary");
          },
          complete: () => {
            if (!emitted && this.current(generation, scope)) {
              this.presentation.set(null);
              this.work.set("idle");
              this.admitted.set(null);
              this.admissionState.set("unavailable");
            }
          },
        }),
    );
  }

  private sendOriginal(
    admission: NativeRevisionAppendAdmission,
    intent: NativeRevisionAppendIntent,
  ): void {
    if (this.original() !== intent) return;
    this.request(
      "append",
      () => this.api.append(admission, intent, this.known()),
      (reply) => this.acceptResponse(reply),
    );
  }
  private request<T>(
    kind: "append" | "resume" | "receipt",
    send: () => Observable<T>,
    accept: (value: T) => void,
  ): void {
    const scope = this.target();
    if (!scope || !this.active()) return;
    this.cancelNetwork();
    const generation = this.networkGeneration,
      identityVersion = this.contexts.identityVersion();
    const subscription = new Subscription();
    this.network = subscription;
    this.work.set(kind);
    this.failure.set(null);
    this.stopped.set(false);
    let emitted = false;
    try {
      subscription.add(
        send()
          .pipe(take(1))
          .subscribe({
            next: (value) => {
              emitted = true;
              if (!this.current(generation, scope, identityVersion)) return;
              this.work.set("idle");
              try {
                accept(value);
              } catch (error: unknown) {
                this.recordFailure(appendError(error, kind !== "receipt"));
              }
              this.requestFocus("status");
            },
            error: (error: unknown) => {
              if (!this.current(generation, scope, identityVersion)) return;
              this.work.set("idle");
              this.recordFailure(appendError(error, kind !== "receipt"));
              this.requestFocus("status");
            },
            complete: () => {
              if (
                !emitted &&
                this.current(generation, scope, identityVersion)
              ) {
                this.work.set("idle");
                this.recordFailure(
                  new NativeRevisionAppendError(
                    "unavailable",
                    null,
                    kind !== "receipt",
                  ),
                );
                this.requestFocus("status");
              }
            },
          }),
      );
    } catch (error: unknown) {
      if (this.current(generation, scope, identityVersion)) {
        this.work.set("idle");
        this.recordFailure(appendError(error, kind !== "receipt"));
        this.requestFocus("status");
      }
    }
  }

  private acceptResponse(reply: NativeRevisionAppendResponse): void {
    this.latest.set(
      Object.freeze({
        outcome: reply.outcome,
        failure: reply.failure,
        rejection: reply.rejection,
      }),
    );
    if (reply.operation) this.acceptOperation(reply.operation);
    // A later refusal never replaces an operation or clears prior dispatched uncertainty.
    if (reply.outcome === "notAuthorizedOrNotFound")
      this.recordFailure(new NativeRevisionAppendError("notFound"));
  }
  private acceptOperation(operation: NativeRevisionAppendOperation): void {
    this.known.set(operation);
    if (!operation.terminal) return;
    this.unknown.set(false);
    this.stopped.set(false);
    this.original.set(null);
    this.invalidateFile();
    this.source.set(null);
    this.selectedFile.set(null);
    this.filename.set("");
    this.draftOwner.set(null);
    if (operation.committed) this.retryRevisionAccess();
  }

  private readRevision(
    admission: NativeRevisionAppendAdmission,
    operation: NativeRevisionAppendOperation,
  ): void {
    const scope = this.target();
    if (
      !scope ||
      !operation.committed ||
      !owns(admission.context, boundOwner(operation)) ||
      !this.safeSlot()
    )
      return;
    this.cancelNetwork();
    const generation = this.networkGeneration,
      identityVersion = this.contexts.identityVersion();
    const subscription = new Subscription();
    this.network = subscription;
    this.work.set("revision");
    this.access.set({ kind: "loading" });
    let emitted = false;
    subscription.add(
      this.catalog
        .revision(operation.receipt.futureRevision)
        .pipe(take(1))
        .subscribe({
          next: (revision) => {
            emitted = true;
            if (!this.current(generation, scope, identityVersion)) return;
            this.work.set("idle");
            if (
              this.readAdmission() !== admission ||
              this.known() !== operation ||
              !operation.matchesRevision(revision)
            ) {
              this.access.set({ kind: "unavailable" });
            } else {
              this.access.set(
                Object.freeze({
                  kind: "ready",
                  revision: Object.freeze({
                    scope: copyRevisionScope(revision.scope),
                    sequence: revision.sequence,
                    admission: revision.admission,
                    originalFilename: revision.originalFilename,
                    nativeByteCount: revision.nativeByteCount,
                  }),
                }),
              );
            }
          },
          error: (error: unknown) => {
            if (!this.current(generation, scope, identityVersion)) return;
            this.work.set("idle");
            this.access.set({
              kind:
                error instanceof CatalogReadError ? error.kind : "unavailable",
            });
          },
          complete: () => {
            if (!emitted && this.current(generation, scope, identityVersion)) {
              this.work.set("idle");
              this.access.set({ kind: "unavailable" });
            }
          },
        }),
    );
  }

  private current(
    generation: number,
    scope: CatalogMappingScope,
    identityVersion?: number,
  ): boolean {
    const selected = this.target();
    return (
      this.active() &&
      generation === this.networkGeneration &&
      selected !== null &&
      sameScope(selected, scope) &&
      (identityVersion === undefined ||
        identityVersion === this.contexts.identityVersion())
    );
  }
  private fileCurrent(
    generation: number,
    owner: Owner,
    identityVersion: number,
  ): boolean {
    const admission = this.readAdmission(),
      scope = this.target();
    return (
      this.active() &&
      generation === this.fileGeneration &&
      !this.locked() &&
      scope !== null &&
      sameScope(scope, owner.scope) &&
      admission !== null &&
      owns(admission.context, owner) &&
      !this.foreign() &&
      canUpload(admission) &&
      identityVersion === this.contexts.identityVersion()
    );
  }
  private invalidateFile(): void {
    ++this.fileGeneration;
    this.captureAbort?.abort();
    this.queuedFile = null;
    this.sourceFile = null;
    this.preparing.set(false);
  }
  private cancelNetwork(): void {
    ++this.networkGeneration;
    if (
      (this.work() === "append" || this.work() === "resume") &&
      !this.known()?.terminal
    ) {
      this.unknown.set(true);
      this.stopped.set(true);
    }
    this.network?.unsubscribe();
    this.network = null;
    this.work.set("idle");
  }
  private recordFailure(error: NativeRevisionAppendError): void {
    this.failure.set(error.kind);
    if (error.mayHaveCommitted && !this.known()?.terminal)
      this.unknown.set(true);
    if (
      error.kind === "notFound" ||
      error.kind === "identityRejected" ||
      error.kind === "identityChanged"
    ) {
      this.admitted.set(null);
      this.access.set({ kind: "idle" });
      this.admissionState.set(
        error.kind === "notFound"
          ? "notFound"
          : error.kind === "identityRejected"
            ? "rejected"
            : "unavailable",
      );
      if (error.kind === "identityChanged") this.foreign.set(true);
    }
    this.contexts.reject(error.status);
  }
  private clearDraft(): void {
    this.draftContext = null;
    this.sourceFile = null;
    this.filename.set("");
    this.source.set(null);
    this.selectedFile.set(null);
    this.draftOwner.set(null);
    this.reviewed.set(null);
    this.reviewRequired.set(false);
    this.errors.set(noErrors);
    this.sourceFailure.set(null);
  }
  private clearSlot(): void {
    this.draftRecovery?.discard();
    this.cancelNetwork();
    this.invalidateFile();
    this.clearDraft();
    this.original.set(null);
    this.known.set(null);
    this.ownerReadStamp.set(null);
    this.latest.set(null);
    this.unknown.set(false);
    this.stopped.set(false);
    this.foreign.set(false);
    this.failure.set(null);
    this.access.set({ kind: "idle" });
  }

  private suspendDraft(): void {
    const context = this.draftContext,
      scope = this.target();
    if (this.locked() || !this.dirty() || !context || !scope) return;
    this.draftRecovery?.keep(context, scope, {
      filename: this.filename(),
      source: this.source(),
      file: this.source() ? null : this.sourceFile,
      summary: this.selectedFile(),
      reviewed: this.reviewed(),
      reviewRequired: this.reviewRequired(),
    });
  }

  private restoreDraft(admission: NativeRevisionAppendAdmission): void {
    if (!canUpload(admission) || !this.contexts.canPresent(admission.context))
      return;
    const kept = this.draftRecovery?.take(
      admission.context,
      admission.mapping.scope,
    );
    if (!kept) return;
    this.filename.set(kept.filename);
    this.source.set(kept.source);
    this.selectedFile.set(kept.summary);
    this.reviewed.set(kept.reviewed);
    this.reviewRequired.set(kept.reviewRequired);
    const owner = admissionOwner(admission);
    this.draftOwner.set(owner);
    if (!kept.source && kept.file) {
      this.sourceFile = kept.file;
      this.preparing.set(true);
      this.queuedFile = {
        file: kept.file,
        owner,
        generation: this.fileGeneration,
        identityVersion: this.contexts.identityVersion(),
      };
      void this.captureNext();
    }
  }
  private resolveLeave(allow: boolean): void {
    const decision = this.leaveDecision;
    this.leaveDecision = null;
    this.pendingLeave.set(null);
    decision?.next(allow);
    decision?.complete();
  }
  private requestFocus(
    target: "summary" | "status" | "review" | "filename",
  ): void {
    if (this.active())
      this.focus.set(Object.freeze({ sequence: ++this.focusSequence, target }));
  }
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
function copyScope(scope: CatalogMappingScope): Readonly<CatalogMappingScope> {
  return Object.freeze({
    organizationId: scope.organizationId,
    projectId: scope.projectId,
    mappingId: scope.mappingId,
  });
}
function copyRevisionScope(
  scope: CatalogRevisionScope,
): Readonly<CatalogRevisionScope> {
  return Object.freeze({
    organizationId: scope.organizationId,
    projectId: scope.projectId,
    mappingId: scope.mappingId,
    revisionId: scope.revisionId,
  });
}
function owns(context: CatalogContext, owner: Owner): boolean {
  return (
    context.mode === owner.mode &&
    context.actorPrincipalId === owner.actorPrincipalId &&
    context.allowedOrganizationId === owner.scope.organizationId
  );
}
function admissionOwner(admission: NativeRevisionAppendAdmission): Owner {
  const mode = admission.context.mode;
  if (mode !== "development" && mode !== "session")
    throw new NativeRevisionAppendError("identityRejected");
  return Object.freeze({
    mode,
    scope: copyScope(admission.mapping.scope),
    actorPrincipalId: admission.context.actorPrincipalId,
  });
}
function boundOwner(
  binding: NativeRevisionAppendIntent | NativeRevisionAppendOperation,
): Owner {
  return Object.freeze({
    mode: binding.mode,
    scope: copyScope(binding.scope),
    actorPrincipalId: binding.actorPrincipalId,
  });
}
function canUpload(admission: NativeRevisionAppendAdmission): boolean {
  return (
    admission.context.features.uploadRevisions === true &&
    admission.mapping.access.actions.uploadRevisions === true &&
    admission.mapping.backendId === "ferrule"
  );
}
function headView(
  head: NativeRevisionAppendHead,
): NativeRevisionAppendHeadView {
  return Object.freeze({
    scope: copyRevisionScope(head.scope),
    sequence: head.sequence,
    admission: head.admission,
  });
}
function mappingView(mapping: CatalogMapping): NativeRevisionAppendMappingView {
  return Object.freeze({
    scope: copyScope(mapping.scope),
    name: mapping.name,
    backendId: mapping.backendId,
    head: Object.freeze({
      scope: copyRevisionScope(mapping.head.scope),
      sequence: mapping.head.sequence,
      admission: mapping.head.admission,
    }),
  });
}
function committedHeadView(
  operation: NativeRevisionAppendOperation,
): NativeRevisionAppendHeadView | null {
  const receipt = operation.receipt;
  return receipt.committedSequence !== null &&
    receipt.committedAdmission !== null
    ? Object.freeze({
        scope: copyRevisionScope(receipt.futureRevision),
        sequence: receipt.committedSequence,
        admission: receipt.committedAdmission,
      })
    : null;
}
function appendError(
  error: unknown,
  writing = false,
): NativeRevisionAppendError {
  if (error instanceof NativeRevisionAppendError) return error;
  if (error instanceof CatalogReadError) {
    if (error.kind === "notFound")
      return new NativeRevisionAppendError("notFound");
    if (error.kind === "rejected")
      return new NativeRevisionAppendError("identityRejected");
    if (error.kind === "invalidLink")
      return new NativeRevisionAppendError("invalidInput");
  }
  return new NativeRevisionAppendError("unavailable", null, writing);
}
