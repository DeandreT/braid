import { computed, signal, type WritableSignal } from "@angular/core";
import { take, timeout, type Observable, type Subscription } from "rxjs";
import type { components } from "../../api/generated";
import {
  isCatalogId,
  CatalogReadError,
  snapshotCatalogScope,
  type CatalogContext,
  type CatalogMapping,
  type CatalogRevision,
} from "../catalog-types";
import { RevisionReportApi } from "./revision-report-api";
import {
  RevisionReportError,
  type CatalogProjectionReport,
  type CatalogValidationReport,
  type RevisionReport,
  type RevisionReportOperation,
  type RevisionReportResponse,
} from "./revision-report-types";

export interface RevisionReportAdmission {
  readonly context: CatalogContext;
  readonly mapping: CatalogMapping;
  readonly revision: CatalogRevision;
}

export interface RevisionReportSelectors {
  readonly projectionDerivationId: string | null;
  readonly validationDerivationId: string | null;
}

export interface RevisionReportPane<T extends RevisionReport> {
  readonly phase:
    | "idle"
    | "loading"
    | "ready"
    | "refreshing"
    | "ensuring"
    | "polling"
    | "error";
  readonly response: RevisionReportResponse<T> | null;
  readonly error: RevisionReportError | null;
  readonly pinId: string | null;
  readonly pinned: boolean;
  readonly canRepair: boolean;
  readonly checkAgain: boolean;
}

export interface RevisionReportStateOptions {
  /** The parent refreshes its existing metadata controller once and returns its admitted screen. */
  readonly refresh: () => Observable<RevisionReportAdmission>;
  /** Compare the parent's current context identity and exact visible revision scope. */
  readonly isCurrent: (admission: RevisionReportAdmission) => boolean;
}

type PaneValue<T extends RevisionReport> = Omit<
  RevisionReportPane<T>,
  "canRepair"
>;
type Pane<T extends RevisionReport> = {
  readonly operation: RevisionReportOperation;
  value: WritableSignal<PaneValue<T>>;
  subscription: Subscription | null;
  timer: ReturnType<typeof setTimeout> | null;
  generation: number;
};
type Observation = { readonly deadline: number; remaining: number };
type ProjectionDocument = components["schemas"]["ProjectionDocumentV1"];
type ValidationReport = components["schemas"]["ValidationReportV1"];

const observationLimitMs = 15000;
const observationReadLimit = 5;
const minimumObservationDelayMs = 1000;
const admissionRefreshLimitMs = 20000;
const noSelectors: RevisionReportSelectors = {
  projectionDerivationId: null,
  validationDerivationId: null,
};

/** One concrete revision owns the facade. It never refreshes the global context itself. */
export class RevisionReportState {
  private readonly admitted = signal<RevisionReportAdmission | null>(null);
  private readonly projectionPane = pane<CatalogProjectionReport>("projection");
  private readonly validationPane = pane<CatalogValidationReport>("validation");
  private readonly refreshing = signal(false);
  private readonly working = signal(false);
  private selectors: RevisionReportSelectors = noSelectors;
  private selectionKey: string | null = null;
  private selectionGeneration = 0;
  private refreshSubscription: Subscription | null = null;
  private linking = true;
  private attemptedLink: string | null = null;
  private unlinkedValidationId: string | null = null;
  private destroyed = false;

  readonly refreshingAdmission = this.refreshing.asReadonly();
  readonly busy = this.working.asReadonly();
  readonly canRead = computed(() => {
    const admitted = this.admitted();
    return admitted !== null && this.current(admitted) && readAllowed(admitted);
  });
  readonly canEnsure = computed(() => {
    const admitted = this.admitted();
    return (
      this.canRead() &&
      admitted !== null &&
      admitted.context.features.deriveReports &&
      admitted.mapping.access.actions.deriveReports &&
      !this.working()
    );
  });
  readonly projection = computed(() =>
    this.present(this.projectionPane.value()),
  );
  readonly validation = computed(() =>
    this.present(this.validationPane.value()),
  );
  readonly projectionDocument = computed<ProjectionDocument | null>(() => {
    const body = this.projection().response?.body;
    return body?.state === "available" && body.readOutcome === "observed"
      ? (body.report?.document ?? null)
      : null;
  });
  readonly linkedProjectionId = computed<string | null>(() => {
    const projection = this.projection();
    const validation = this.validation().response?.body;
    return this.projectionDocument() !== null &&
      projection.pinId !== null &&
      validation?.report !== null &&
      validation?.readOutcome === "observed" &&
      validation.projectionDerivationId === projection.pinId
      ? projection.pinId
      : null;
  });
  readonly linkedValidation = computed<ValidationReport | null>(() =>
    this.linkedProjectionId() === null
      ? null
      : (this.validation().response?.body.report ?? null),
  );

  constructor(
    private readonly api: RevisionReportApi,
    private readonly options: RevisionReportStateOptions,
  ) {}

  bind(
    admission: RevisionReportAdmission,
    selectors: RevisionReportSelectors,
  ): void {
    if (this.destroyed) return;
    let copy: RevisionReportAdmission;
    let selected: RevisionReportSelectors;
    let key: string;
    try {
      copy = snapshotAdmission(admission);
      selected = snapshotSelectors(selectors);
      key = selectionIdentity(copy, selected);
    } catch {
      this.unbind();
      this.failPane(
        this.projectionPane,
        new RevisionReportError("invalidLink"),
      );
      this.failPane(
        this.validationPane,
        new RevisionReportError("invalidLink"),
      );
      return;
    }
    // The parent's own refresh emits a new admitted screen before its callback settles.
    // It must not cancel the same explicit action that requested that refresh.
    if (this.refreshing() && key === this.selectionKey) {
      this.admitted.set(copy);
      return;
    }
    const sameSelection = key === this.selectionKey;
    const previous = this.admitted();
    if (
      sameSelection &&
      previous?.context === copy.context &&
      previous.mapping === copy.mapping &&
      previous.revision === copy.revision
    )
      return;
    this.cancelAll();
    this.admitted.set(copy);
    this.selectors = selected;
    this.selectionKey = key;
    if (!sameSelection) {
      this.linking = true;
      this.unlinkedValidationId = null;
    }
    this.attemptedLink = null;
    this.resetPanes();
    if (!this.canRead()) return;
    this.readProjection(copy);
    this.readValidation(copy);
  }

  unbind(): void {
    this.cancelAll();
    this.admitted.set(null);
    this.selectors = noSelectors;
    this.selectionKey = null;
    this.linking = true;
    this.attemptedLink = null;
    this.unlinkedValidationId = null;
    this.resetPanes();
  }

  destroy(): void {
    this.destroyed = true;
    this.unbind();
  }

  readAgain(operation: RevisionReportOperation): void {
    this.refreshReads(operation);
  }

  readValidationWithoutLinks(): void {
    if (!this.admitted() || !this.canRead() || this.working()) return;
    this.linking = false;
    this.refreshReads("validation");
  }

  ensure(operation: RevisionReportOperation): void {
    const value = this.value(operation);
    const state = value.response?.body.state;
    if (value.pinned || (state !== "notDerived" && state !== "failed")) return;
    this.mutate(operation, null);
  }

  repair(operation: RevisionReportOperation): void {
    const value = this.value(operation);
    if (value.pinned || !repairable(value.response?.body)) return;
    const id = value.response?.body.result?.derivationId;
    if (id) this.mutate(operation, id);
  }

  private mutate(
    operation: RevisionReportOperation,
    repairId: string | null,
  ): void {
    if (!this.canEnsure()) return;
    this.refreshAdmission(operation, true, (admitted) => {
      // The other report still settles independently under the fresh admission.
      if (operation === "projection") {
        this.readValidation(admitted);
        this.receive(
          this.projectionPane,
          admitted,
          this.api.ensureProjection(
            admitted.revision,
            admitted.context.requestToken,
            repairId,
          ),
          "ensuring",
          (response) => this.ensured(this.projectionPane, admitted, response),
        );
      } else {
        this.readProjection(admitted);
        this.receive(
          this.validationPane,
          admitted,
          this.api.ensureValidation(
            admitted.revision,
            admitted.context.requestToken,
            repairId,
          ),
          "ensuring",
          (response) => this.ensured(this.validationPane, admitted, response),
        );
      }
    });
  }

  private refreshReads(operation: RevisionReportOperation): void {
    // Capture the exact fallback before clearing either pane for fresh admission.
    const validationId =
      this.selectors.validationDerivationId ??
      (this.linking ? null : this.unlinkedValidationId);
    this.refreshAdmission(operation, false, (admitted) => {
      this.readProjection(admitted);
      this.readValidation(admitted, undefined, validationId);
      this.finishAction();
    });
  }

  private refreshAdmission(
    operation: RevisionReportOperation,
    derive: boolean,
    admittedAction: (admitted: RevisionReportAdmission) => void,
  ): void {
    if (!this.admitted() || !this.canRead() || this.working()) return;
    const generation = this.selectionGeneration;
    const key = this.selectionKey;
    this.attemptedLink = null;
    this.cancelPane(this.projectionPane);
    this.cancelPane(this.validationPane);
    this.resetPanes();
    this.setPhase(operation, "refreshing");
    this.working.set(true);
    this.refreshing.set(true);
    let emitted = false;
    try {
      this.refreshSubscription = this.options
        .refresh()
        .pipe(timeout(admissionRefreshLimitMs), take(1))
        .subscribe({
          next: (fresh) => {
            emitted = true;
            if (
              generation !== this.selectionGeneration ||
              key !== this.selectionKey
            )
              return;
            let admitted: RevisionReportAdmission;
            try {
              admitted = snapshotAdmission(fresh);
              if (
                selectionIdentity(admitted, this.selectors) !== key ||
                !this.current(admitted)
              )
                throw new RevisionReportError("rejected");
            } catch (error: unknown) {
              this.actionFailed(operation, failure(error));
              return;
            }
            this.admitted.set(admitted);
            this.refreshing.set(false);
            if (
              !readAllowed(admitted) ||
              (derive &&
                (!admitted.context.features.deriveReports ||
                  !admitted.mapping.access.actions.deriveReports))
            ) {
              this.actionFailed(operation, new RevisionReportError("rejected"));
              return;
            }
            admittedAction(admitted);
          },
          error: (error: unknown) => {
            if (generation === this.selectionGeneration)
              this.actionFailed(operation, failure(error));
          },
          complete: () => {
            if (!emitted && generation === this.selectionGeneration)
              this.actionFailed(
                operation,
                new RevisionReportError("unavailable"),
              );
          },
        });
    } catch (error: unknown) {
      if (generation === this.selectionGeneration)
        this.actionFailed(operation, failure(error));
    }
  }

  private readProjection(
    admitted: RevisionReportAdmission,
    observation?: Observation,
  ): void {
    let request = this.api.projection(
      admitted.revision,
      this.selectors.projectionDerivationId,
    );
    if (observation)
      request = request.pipe(
        timeout(Math.max(1, observation.deadline - Date.now())),
      );
    this.receive(
      this.projectionPane,
      admitted,
      request,
      observation ? "polling" : "loading",
      (response) =>
        this.observed(this.projectionPane, admitted, response, observation),
      observation,
    );
  }

  private readValidation(
    admitted: RevisionReportAdmission,
    observation?: Observation,
    retainedId = this.selectors.validationDerivationId,
    projectionId: string | null = null,
  ): void {
    let request = this.api.validation(
      admitted.revision,
      retainedId,
      projectionId,
    );
    if (observation)
      request = request.pipe(
        timeout(Math.max(1, observation.deadline - Date.now())),
      );
    this.receive(
      this.validationPane,
      admitted,
      request,
      observation ? "polling" : "loading",
      (response) =>
        this.observed(this.validationPane, admitted, response, observation),
      observation,
    );
  }

  private receive<T extends RevisionReport>(
    controller: Pane<T>,
    admitted: RevisionReportAdmission,
    request: Observable<RevisionReportResponse<T>>,
    phase: PaneValue<T>["phase"],
    next: (response: RevisionReportResponse<T>) => void,
    observation?: Observation,
  ): void {
    this.cancelPane(controller);
    const generation = this.selectionGeneration;
    const paneGeneration = controller.generation;
    const prior = controller.value();
    controller.value.set({
      ...empty<T>(prior.pinned),
      phase,
      ...(observation ? { response: prior.response, pinId: prior.pinId } : {}),
    });
    let emitted = false;
    controller.subscription = request.pipe(take(1)).subscribe({
      next: (response) => {
        emitted = true;
        if (!this.accepts(controller, generation, paneGeneration, admitted))
          return;
        next(response);
      },
      error: (error: unknown) => {
        if (!this.accepts(controller, generation, paneGeneration, admitted))
          return;
        if (observation && Date.now() >= observation.deadline) {
          this.stopObservation(controller);
          return;
        }
        const actual = failure(error);
        const authorityRefused =
          actual.kind === "notFound" || actual.kind === "rejected";
        if (authorityRefused) {
          this.cancelPane(this.projectionPane);
          this.cancelPane(this.validationPane);
          this.resetPanes();
        }
        this.failPane(controller, actual);
        if (authorityRefused || phase === "ensuring" || observation)
          this.finishAction();
      },
      complete: () => {
        if (
          !emitted &&
          this.accepts(controller, generation, paneGeneration, admitted)
        ) {
          this.failPane(controller, new RevisionReportError("unavailable"));
          if (phase === "ensuring" || observation) this.finishAction();
        }
      },
    });
  }

  private ensured<T extends RevisionReport>(
    controller: Pane<T>,
    admitted: RevisionReportAdmission,
    response: RevisionReportResponse<T>,
  ): void {
    this.publish(controller, response);
    if (response.status === 202 && response.body.state === "pending") {
      this.schedule(
        controller,
        admitted,
        {
          deadline: Date.now() + observationLimitMs,
          remaining: observationReadLimit,
        },
        (response.retryAfterSeconds ?? 1) * 1000,
      );
      return;
    }
    if (response.status === 202)
      controller.value.update((value) => ({ ...value, checkAgain: true }));
    this.finishAction();
  }

  private observed<T extends RevisionReport>(
    controller: Pane<T>,
    admitted: RevisionReportAdmission,
    response: RevisionReportResponse<T>,
    observation?: Observation,
  ): void {
    this.publish(controller, response);
    if (observation) {
      if (response.body.state === "pending") {
        const next = response.body.nextAttemptAtUtc;
        const delay =
          next === null
            ? minimumObservationDelayMs
            : Date.parse(next) - Date.now();
        this.schedule(
          controller,
          admitted,
          observation,
          Math.max(minimumObservationDelayMs, delay),
        );
      } else {
        if (response.body.state === "failed")
          controller.value.update((value) => ({ ...value, checkAgain: true }));
        this.finishAction();
      }
    } else {
      if (response.body.state === "pending")
        controller.value.update((value) => ({ ...value, checkAgain: true }));
      this.maybeLinkValidation();
    }
  }

  private schedule<T extends RevisionReport>(
    controller: Pane<T>,
    admitted: RevisionReportAdmission,
    observation: Observation,
    requestedDelay: number,
  ): void {
    const delay = Math.max(minimumObservationDelayMs, requestedDelay);
    if (
      !Number.isFinite(delay) ||
      observation.remaining === 0 ||
      Date.now() + delay >= observation.deadline
    ) {
      this.stopObservation(controller);
      return;
    }
    controller.value.update((value) => ({ ...value, phase: "polling" }));
    const generation = this.selectionGeneration;
    const paneGeneration = controller.generation;
    controller.timer = setTimeout(() => {
      controller.timer = null;
      if (!this.accepts(controller, generation, paneGeneration, admitted))
        return;
      if (Date.now() >= observation.deadline) {
        this.stopObservation(controller);
        return;
      }
      --observation.remaining;
      if (controller.operation === "projection")
        this.readProjection(admitted, observation);
      else this.readValidation(admitted, observation);
    }, delay);
  }

  private stopObservation<T extends RevisionReport>(controller: Pane<T>): void {
    controller.value.update((value) => ({
      ...value,
      phase: "ready",
      checkAgain: true,
    }));
    this.finishAction();
  }

  private publish<T extends RevisionReport>(
    controller: Pane<T>,
    response: RevisionReportResponse<T>,
  ): void {
    controller.value.set({
      ...empty<T>(controller.value().pinned),
      phase: "ready",
      response,
      pinId:
        response.body.state === "available"
          ? (response.body.result?.derivationId ?? null)
          : null,
    });
  }

  private maybeLinkValidation(): void {
    if (!this.linking || this.working() || !this.canRead()) return;
    const admitted = this.admitted();
    const projection = this.projectionPane.value();
    const validation = this.validationPane.value();
    const p = projection.pinId;
    const v = validation.pinId;
    if (
      !admitted ||
      !p ||
      !v ||
      !projection.response?.body.report?.document ||
      !validation.response?.body.report ||
      validation.phase !== "ready"
    )
      return;
    if (validation.response.body.projectionDerivationId === p) return;
    const link = `${v}/${p}`;
    if (this.attemptedLink === link) return;
    this.attemptedLink = link;
    this.unlinkedValidationId = v;
    this.readValidation(admitted, undefined, v, p);
  }

  private accepts<T extends RevisionReport>(
    controller: Pane<T>,
    generation: number,
    paneGeneration: number,
    admitted: RevisionReportAdmission,
  ): boolean {
    if (
      this.destroyed ||
      generation !== this.selectionGeneration ||
      paneGeneration !== controller.generation
    )
      return false;
    if (!this.current(admitted)) {
      this.unbind();
      return false;
    }
    return true;
  }

  private current(admitted: RevisionReportAdmission): boolean {
    try {
      return !this.destroyed && this.options.isCurrent(admitted);
    } catch {
      return false;
    }
  }

  private present<T extends RevisionReport>(
    value: PaneValue<T>,
  ): RevisionReportPane<T> {
    if (this.admitted() !== null && !this.canRead())
      return {
        ...empty<T>(value.pinned),
        phase: this.refreshing() ? "refreshing" : "idle",
        canRepair: false,
      };
    return {
      ...value,
      canRepair:
        this.canEnsure() && !value.pinned && repairable(value.response?.body),
    };
  }

  private value(operation: RevisionReportOperation): PaneValue<RevisionReport> {
    return operation === "projection"
      ? this.projectionPane.value()
      : this.validationPane.value();
  }

  private setPhase(
    operation: RevisionReportOperation,
    phase: PaneValue<RevisionReport>["phase"],
  ): void {
    if (operation === "projection")
      this.projectionPane.value.update((value) => ({ ...value, phase }));
    else this.validationPane.value.update((value) => ({ ...value, phase }));
  }

  private failPane<T extends RevisionReport>(
    controller: Pane<T>,
    error: RevisionReportError,
  ): void {
    controller.value.set({
      ...empty<T>(controller.value().pinned),
      phase: "error",
      error,
    });
  }

  private actionFailed(
    operation: RevisionReportOperation,
    error: RevisionReportError,
  ): void {
    if (operation === "projection") this.failPane(this.projectionPane, error);
    else this.failPane(this.validationPane, error);
    this.finishAction();
  }

  private finishAction(): void {
    this.refreshing.set(false);
    this.working.set(false);
    this.maybeLinkValidation();
  }

  private cancelPane<T extends RevisionReport>(controller: Pane<T>): void {
    ++controller.generation;
    controller.subscription?.unsubscribe();
    controller.subscription = null;
    if (controller.timer !== null) clearTimeout(controller.timer);
    controller.timer = null;
  }

  private cancelAll(): void {
    ++this.selectionGeneration;
    this.refreshSubscription?.unsubscribe();
    this.refreshSubscription = null;
    this.cancelPane(this.projectionPane);
    this.cancelPane(this.validationPane);
    this.refreshing.set(false);
    this.working.set(false);
  }

  private resetPanes(): void {
    this.projectionPane.value.set(
      empty(this.selectors.projectionDerivationId !== null),
    );
    this.validationPane.value.set(
      empty(this.selectors.validationDerivationId !== null),
    );
  }
}

function pane<T extends RevisionReport>(
  operation: RevisionReportOperation,
): Pane<T> {
  return {
    operation,
    value: signal(empty<T>(false)),
    subscription: null,
    timer: null,
    generation: 0,
  };
}

function empty<T extends RevisionReport>(pinned: boolean): PaneValue<T> {
  return {
    phase: "idle",
    response: null,
    error: null,
    pinId: null,
    pinned,
    checkAgain: false,
  };
}

function repairable(body: RevisionReport | undefined): boolean {
  return (
    body?.state === "available" &&
    body.result !== null &&
    (body.readOutcome === "objectUnavailable" ||
      body.readOutcome === "storageIntegrity")
  );
}

function readAllowed(admission: RevisionReportAdmission): boolean {
  return (
    admission.context.features.readReports &&
    admission.mapping.access.actions.readReports
  );
}

function snapshotSelectors(
  value: RevisionReportSelectors,
): RevisionReportSelectors {
  if (
    !value ||
    (value.projectionDerivationId !== null &&
      !isCatalogId(value.projectionDerivationId)) ||
    (value.validationDerivationId !== null &&
      !isCatalogId(value.validationDerivationId))
  )
    throw new RevisionReportError("invalidLink");
  return {
    projectionDerivationId: value.projectionDerivationId,
    validationDerivationId: value.validationDerivationId,
  };
}

function snapshotAdmission(
  value: RevisionReportAdmission,
): RevisionReportAdmission {
  const scope = snapshotCatalogScope(value.revision.scope);
  if (
    !("revisionId" in scope) ||
    value.context.allowedOrganizationId !== scope.organizationId ||
    value.mapping.scope.organizationId !== scope.organizationId ||
    value.mapping.scope.projectId !== scope.projectId ||
    value.mapping.scope.mappingId !== scope.mappingId
  )
    throw new RevisionReportError("invalidLink");
  // These are the parent's admitted DTOs; reference identity is part of its late-reply guard.
  // The transport separately captures all immutable revision facts before each request.
  return {
    context: value.context,
    mapping: value.mapping,
    revision: value.revision,
  };
}

function selectionIdentity(
  admission: RevisionReportAdmission,
  selectors: RevisionReportSelectors,
): string {
  const context = admission.context;
  const scope = admission.revision.scope;
  return [
    context.mode,
    context.actorPrincipalId,
    context.allowedOrganizationId,
    context.mode === "session" ? context.identityContext : "",
    scope.organizationId,
    scope.projectId,
    scope.mappingId,
    scope.revisionId,
    selectors.projectionDerivationId ?? "",
    selectors.validationDerivationId ?? "",
  ].join("/");
}

function failure(error: unknown): RevisionReportError {
  if (error instanceof RevisionReportError) return error;
  if (error instanceof CatalogReadError)
    return new RevisionReportError(error.kind);
  if (error instanceof Error && error.name === "TimeoutError")
    return new RevisionReportError("timedOut");
  return new RevisionReportError("unavailable");
}
