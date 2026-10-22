import { DatePipe } from "@angular/common";
import {
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from "@angular/core";
import { FormsModule } from "@angular/forms";
import { StatusChipComponent } from "../../workbench/status-chip.component";
import { MetadataValueComponent } from "../../workbench/metadata-value.component";
import { ConfirmationComponent } from "../../workbench/confirmation.component";
import type { ConfirmationView } from "../../workbench/confirmation";
import {
  buildConfirmation,
  type BuildConfirmationContext,
} from "./build-confirmation";
import { buildStatus } from "../../workbench/presentation";
import { buildLabels, buildRequestLabels } from "./build-presentation";
import { RouterLink } from "@angular/router";
import { Subscription, map, switchMap, type Observable } from "rxjs";
import { BrowserSessionState } from "../../auth/browser-session-state";
import { CatalogApi } from "../catalog-api";
import {
  CatalogReadError,
  catalogRoutes,
  type CatalogContext,
  type CatalogMapping,
  type CatalogRevision,
} from "../catalog-types";
import {
  RegressionApi,
  RegressionError,
  exactPolicy,
  type RunPage,
  type RunSummary,
} from "../regression/regression-api";
import { BuildApi } from "./build-api";
import { BuildViewRecovery } from "./build-view-recovery";
import {
  BuildError,
  active,
  type ApprovalPage,
  type BuildApproval,
  type BuildCapabilities,
  type BuildEnvironment,
  type BuildProfile,
  type BuildRun,
  type BuildRunPage,
  type EnvironmentCapabilities,
  type EnvironmentPage,
} from "./build-values";
import {
  buildOwner,
  captureApproval,
  captureCancellation,
  captureEnvironment,
  captureRevocation,
  captureSubmission,
  recoveredApproval,
  recoveredEnvironment,
  recoveredRun,
  sameOwner,
  sameRevision,
  type BuildOwner,
  type PendingBuildMutation,
} from "./build-recovery";

/** No durable browser storage: uncertain requests keep their original immutable frame until explicit recovery or invalidation. */
@Component({
  selector: "weft-revision-builds",
  imports: [
    FormsModule,
    DatePipe,
    RouterLink,
    StatusChipComponent,
    MetadataValueComponent,
    ConfirmationComponent,
  ],
  templateUrl: "./revision-builds.component.html",
  styleUrl: "./revision-builds.component.css",
})
export class RevisionBuildsComponent {
  readonly copy = buildLabels;
  readonly requestLabels = buildRequestLabels;
  readonly buildStatus = buildStatus;
  readonly context = input.required<CatalogContext>();
  readonly mapping = input.required<CatalogMapping>();
  readonly revision = input.required<CatalogRevision>();
  readonly workbench = input(false);
  private readonly api = inject(BuildApi);
  private readonly regression = inject(RegressionApi);
  private readonly catalog = inject(CatalogApi);
  private readonly sessions = inject(BrowserSessionState);
  private readonly recovery = inject(BuildViewRecovery);
  private readonly admission = signal<CatalogContext | null>(null);
  private readonly observedAccess = signal<CatalogMapping["access"] | null>(
    null,
  );
  private requests = new Subscription();
  private epoch = 0;
  private operation = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  readonly capabilities = signal<BuildCapabilities | null>(null);
  readonly environmentCapabilities = signal<EnvironmentCapabilities | null>(
    null,
  );
  readonly environments = signal<EnvironmentPage | null>(null);
  readonly approvals = signal<ApprovalPage | null>(null);
  readonly history = signal<BuildRunPage | null>(null);
  readonly evidencePage = signal<RunPage | null>(null);
  readonly evidence = signal<RunSummary | null>(null);
  readonly selectedEnvironment = signal<BuildEnvironment | null>(null);
  readonly selectedApproval = signal<BuildApproval | null>(null);
  readonly selectedRun = signal<BuildRun | null>(null);
  readonly pending = this.recovery.pending;
  readonly busy = signal(false);
  readonly blocked = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly confirmation = signal<{
    view: ConfirmationView;
    operation: PendingBuildMutation;
    epoch: number;
    forget: boolean;
  } | null>(null);
  readonly routes = catalogRoutes;

  constructor() {
    effect(() => {
      const context = this.context(),
        mapping = this.mapping(),
        revision = this.revision();
      this.sessions.generation();
      untracked(() => {
        this.recovery.bind(buildOwner(context, revision), mapping.access);
        this.clear(false);
      });
    });
    // A failed or negative check is not a new owner: hide build metadata but keep the frame.
    const invalidation = this.sessions.invalidated.subscribe(() =>
      this.clear(false),
    );
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.clear(false);
    });
  }
  load(): void {
    this.send(
      this.freshAdmission().pipe(
        switchMap((ctx) => this.api.capabilities(ctx, this.mapping().scope)),
      ),
      (cap) => {
        if (!cap.readOwnMetadata) {
          this.clear();
          this.error.set(
            "Your current access cannot read private build metadata.",
          );
          return;
        }
        this.capabilities.set(cap);
        this.blocked.set(false);
        this.loadEnvironments();
      },
    );
  }
  loadEnvironments(next = false): void {
    if (!this.capabilities()?.readOwnMetadata) return;
    const page = this.environments();
    const after =
      next && page?.nextEnvironmentId && page.nextVersion
        ? { environmentId: page.nextEnvironmentId, version: page.nextVersion }
        : undefined;
    this.send(
      this.api.environments(this.admitted(), this.mapping().scope, after),
      (value) => {
        this.environments.set(value);
        if (!this.selectedEnvironment())
          this.selectedEnvironment.set(
            value.environments.find(
              (item) =>
                item.active &&
                sameProfile(item.profile, this.capabilities()!.profile),
            ) || null,
          );
      },
    );
  }
  selectEnvironment(key: string): void {
    if (this.busy() || this.pending()) return;
    this.selectedEnvironment.set(
      this.environments()?.environments.find(
        (item) => environmentKey(item) === key,
      ) || null,
    );
  }
  environmentKey(value: BuildEnvironment): string {
    return environmentKey(value);
  }
  loadEnvironmentAdministration(): void {
    this.send(
      this.api.environmentCapabilities(this.admitted(), this.mapping().scope),
      (cap) => this.environmentCapabilities.set(cap),
    );
  }
  registerEnvironment(nextVersion = false): void {
    if (!this.environmentCapabilities()?.canCreateVersions || !this.canMutate())
      return;
    if (nextVersion && !this.selectedEnvironment()) return;
    this.capture(() =>
      captureEnvironment(
        this.owner(),
        this.environmentCapabilities()!.profile,
        nextVersion ? this.selectedEnvironment() : null,
      ),
    );
  }
  loadEvidence(next = false): void {
    const after = next ? this.evidencePage()?.nextRunId : undefined;
    this.send(
      this.regression.runs(
        this.admitted(),
        this.mapping().scope,
        after || undefined,
      ),
      (value) => this.evidencePage.set(value),
      "evidence",
    );
  }
  selectEvidence(id: string): void {
    if (!this.canMutate()) return;
    this.evidence.set(null);
    this.send(
      this.regression.run(this.admitted(), this.mapping().scope, id),
      (value) => {
        if (
          !sameRevision(value.run.revision, this.revision().scope) ||
          value.run.state !== "completed" ||
          value.executionOutcome !== "completed" ||
          value.previewStatus !== "succeeded" ||
          value.comparison?.policy !== exactPolicy ||
          value.comparison.outcome !== "passed"
        ) {
          this.error.set(
            "This run did not pass exact-byte comparison for the displayed revision.",
          );
          return;
        }
        // Retain identifiers only. Fixture outputs returned by the authorized regression reader are not copied into build state.
        this.evidence.set(value.run);
      },
      "evidence",
    );
  }
  approve(): void {
    const environment = this.selectedEnvironment(),
      evidence = this.evidence(),
      cap = this.capabilities();
    if (!cap?.approveBuilds || !environment || !evidence || !this.canMutate())
      return;
    if (!sameProfile(environment.profile, cap.profile)) {
      this.error.set(
        "Select an active environment matching the offered runtime profile.",
      );
      return;
    }
    this.capture(() =>
      captureApproval(this.owner(), environment, evidence.runId),
    );
  }
  loadApprovals(next = false): void {
    const after = next ? this.approvals()?.nextApprovalId : undefined;
    this.send(
      this.api.approvals(
        this.admitted(),
        this.mapping().scope,
        after || undefined,
      ),
      (value) => this.approvals.set(value),
    );
  }
  openApproval(id: string): void {
    if (this.pending()) return;
    this.send(
      this.api.approval(this.admitted(), this.mapping().scope, id),
      (value) => this.selectedApproval.set(value),
    );
  }
  submit(): void {
    const value = this.selectedApproval();
    if (!value || !this.capabilities()?.submitBuilds || !this.canMutate())
      return;
    this.capture(
      () => captureSubmission(this.owner(), value),
      value.environment,
    );
  }
  revoke(): void {
    const value = this.selectedApproval();
    if (value && this.canMutate())
      this.capture(
        () => captureRevocation(this.owner(), value),
        value.environment,
      );
  }
  loadHistory(next = false): void {
    const after = next ? this.history()?.nextBuildId : undefined;
    this.send(
      this.api.runs(this.admitted(), this.mapping().scope, after || undefined),
      (value) => this.history.set(value),
    );
  }
  openRun(id: string): void {
    if (this.pending()) return;
    this.stopTimer();
    this.send(
      this.api.run(this.admitted(), this.mapping().scope, id),
      (value) => this.showRun(value),
    );
  }
  recoverApprovalRun(): void {
    const value = this.selectedApproval();
    if (value) this.openRun(value.buildId);
  }
  cancel(): void {
    const value = this.selectedRun();
    if (value && this.canMutate()) {
      const approval = this.selectedApproval();
      this.capture(
        () => captureCancellation(this.owner(), value),
        approval?.buildId === value.buildId ? approval.environment : undefined,
        buildStatus(value.state).label,
      );
    }
  }
  retryPending(): void {
    const pending = this.pending();
    if (
      !pending ||
      this.busy() ||
      this.blocked() ||
      !sameOwner(pending.owner, this.owner())
    )
      return;
    this.stopTimer();
    this.send(
      this.freshAdmission(pending.owner).pipe(
        switchMap((ctx) => {
          const scope = pending.owner.scope;
          switch (pending.kind) {
            case "environment":
              return this.api
                .putEnvironment(
                  ctx,
                  scope,
                  pending.id,
                  pending.version,
                  pending.body,
                )
                .pipe(map((value) => ({ environment: value.environment })));
            case "approval":
              return this.api
                .putApproval(ctx, scope, pending.id, pending.body)
                .pipe(map((value) => ({ approval: value.approval })));
            case "submit":
              return this.api
                .submit(ctx, scope, pending.id, pending.body)
                .pipe(map((value) => ({ run: value.run })));
            case "cancel":
              return this.api
                .run(ctx, scope, pending.id, true)
                .pipe(map((value) => ({ run: value })));
            case "revoke":
              return this.api
                .approval(ctx, scope, pending.id, true)
                .pipe(map((value) => ({ approval: value })));
          }
        }),
      ),
      (value) => this.acceptRecovery(pending, value),
    );
  }
  recoverPending(): void {
    const pending = this.pending();
    if (!pending || this.busy() || this.blocked()) return;
    this.send(
      this.freshAdmission(pending.owner).pipe(
        switchMap((ctx) => {
          const scope = pending.owner.scope;
          if (pending.kind === "environment")
            return this.api
              .environment(ctx, scope, pending.id, pending.version)
              .pipe(map((value) => ({ environment: value })));
          if (pending.kind === "approval" || pending.kind === "revoke")
            return this.api
              .approval(ctx, scope, pending.id)
              .pipe(map((value) => ({ approval: value })));
          return this.api
            .run(ctx, scope, pending.id)
            .pipe(map((value) => ({ run: value })));
        }),
      ),
      (value) => this.acceptRecovery(pending, value),
    );
  }
  abandon(): void {
    const operation = this.pending();
    if (this.busy() || !operation || this.confirmation()) return;
    this.confirmation.set({
      operation,
      epoch: this.epoch,
      forget: true,
      view: {
        title: "Stop tracking this request?",
        action: "Stop tracking",
        effect:
          "This clears only the local recovery details. It does not cancel the request, which may already have succeeded. Check the saved results before creating another request.",
        afterLeaving:
          "The server request may still finish after tracking stops or you leave this page.",
        facts: [
          { label: "Mapping version", value: String(this.revision().sequence) },
          { label: "Request reference", value: operation.id },
        ],
      },
    });
  }
  dismissConfirmation(): void {
    this.confirmation.set(null);
  }
  confirmAction(): void {
    const captured = this.confirmation();
    this.confirmation.set(null);
    if (
      !captured ||
      captured.epoch !== this.epoch ||
      !sameOwner(captured.operation.owner, this.owner())
    )
      return;
    if (captured.forget) {
      if (!this.busy() && this.pending() === captured.operation)
        this.pending.set(null);
      return;
    }
    if (!this.canMutate() || !this.sessions.canDispatch(this.admitted()))
      return;
    this.pending.set(captured.operation);
    this.retryPending();
  }
  isCurrent(value: { revision: CatalogRevision["scope"] }): boolean {
    return sameRevision(value.revision, this.revision().scope);
  }
  isActive(): boolean {
    const value = this.selectedRun();
    return !!value && active(value);
  }
  canQueue(): boolean {
    const value = this.selectedApproval();
    return (
      !!value &&
      this.isCurrent(value) &&
      !value.revoked &&
      value.environmentActive &&
      this.capabilities()?.submitBuilds === true
    );
  }
  label(value: string): string {
    return value
      .replace(/([A-Z])/g, " $1")
      .replace(/^./, (char) => char.toUpperCase());
  }
  canAdminister(): boolean {
    return (
      (this.observedAccess()?.role ?? this.mapping().access.role) ===
      "administrator"
    );
  }
  private capture(
    factory: () => PendingBuildMutation,
    environment?: BuildConfirmationContext["environment"],
    previousState?: string,
  ): void {
    if (this.confirmation()) return;
    try {
      const operation = factory();
      this.confirmation.set({
        operation,
        epoch: this.epoch,
        forget: false,
        view: buildConfirmation(operation, {
          sequence: String(this.revision().sequence),
          environment,
          previousState,
        }),
      });
    } catch (error: unknown) {
      this.error.set(message(error));
    }
  }
  private canMutate(): boolean {
    return (
      !this.busy() &&
      !this.pending() &&
      !this.blocked() &&
      this.capabilities()?.readOwnMetadata === true
    );
  }
  private owner(): BuildOwner {
    return buildOwner(this.context(), this.revision());
  }
  private admitted(): CatalogContext {
    return this.admission() || this.context();
  }
  private freshAdmission(owner = this.owner()): Observable<CatalogContext> {
    return this.catalog.context().pipe(
      switchMap((ctx) => {
        if (!sameOwner(owner, buildOwner(ctx, this.revision())))
          throw new BuildError("rejected");
        return this.catalog.mapping(owner.scope).pipe(
          map((mapping) => {
            // Catalog upload actions also depend on ingestion enablement; builds have their own default-off capability.
            if (!["publisher", "administrator"].includes(mapping.access.role))
              throw new BuildError("rejected");
            if (this.recovery.bind(owner, mapping.access))
              throw new BuildError("rejected");
            this.observedAccess.set(mapping.access);
            this.admission.set(ctx);
            return ctx;
          }),
        );
      }),
    );
  }
  private acceptRecovery(
    pending: PendingBuildMutation,
    value: {
      environment?: BuildEnvironment;
      approval?: BuildApproval;
      run?: BuildRun;
    },
  ): void {
    if (this.pending() !== pending || !sameOwner(pending.owner, this.owner()))
      return;
    if (value.environment && recoveredEnvironment(pending, value.environment)) {
      this.pending.set(null);
      this.selectedEnvironment.set(value.environment);
      this.notice.set(
        value.environment.active
          ? "The original environment version is confirmed."
          : "The original environment version is retained but inactive. It has not been reactivated.",
      );
    } else if (value.approval && recoveredApproval(pending, value.approval)) {
      this.pending.set(null);
      this.selectedApproval.set(value.approval);
      this.notice.set(
        value.approval.revoked || !value.approval.environmentActive
          ? "The original approval is retained; fresh execution is not admitted."
          : "The original immutable approval is confirmed. Queue it explicitly when ready.",
      );
    } else if (value.run && recoveredRun(pending, value.run)) {
      this.pending.set(null);
      this.showRun(value.run);
    } else {
      this.notice.set(
        "The requested change is not confirmed. Its original IDs and body have been retained for another explicit read or retry.",
      );
    }
  }
  private showRun(value: BuildRun): void {
    this.selectedRun.set(value);
    this.notice.set(
      value.state === "uncertain"
        ? "The result is not confirmed. Check the previous request before preparing again."
        : `${buildStatus(value.state).label}.`,
    );
    if (active(value)) this.schedulePoll(value.buildId);
  }
  private send<T>(
    request: Observable<T>,
    success: (value: T) => void,
    source: "build" | "evidence" = "build",
  ): void {
    if (this.busy() || !this.sessions.canDispatch(this.admitted())) return;
    const epoch = this.epoch,
      operation = ++this.operation;
    this.busy.set(true);
    this.error.set(null);
    this.requests.add(
      request.subscribe({
        next: (value) => {
          if (epoch === this.epoch && operation === this.operation) {
            this.busy.set(false);
            success(value);
          }
        },
        error: (error: unknown) => {
          if (epoch !== this.epoch || operation !== this.operation) return;
          this.busy.set(false);
          if (
            (error instanceof BuildError && error.kind === "rejected") ||
            (error instanceof RegressionError && error.kind === "rejected") ||
            (error instanceof CatalogReadError &&
              ["notFound", "rejected"].includes(error.kind))
          )
            this.clear();
          else if (
            source === "build" &&
            error instanceof BuildError &&
            error.kind === "notFound"
          ) {
            this.blocked.set(true);
            this.hideMetadata();
            this.confirmAccess(epoch);
          } else if (
            source === "evidence" &&
            error instanceof RegressionError &&
            error.kind === "notFound"
          ) {
            this.confirmAccess(epoch);
          }
          this.error.set(
            source === "evidence" &&
              error instanceof RegressionError &&
              error.kind === "notFound"
              ? "Saved regression runs are unavailable. Existing build metadata can still be recovered."
              : message(error),
          );
        },
        complete: () => {
          if (epoch === this.epoch && operation === this.operation)
            this.busy.set(false);
        },
      }),
    );
  }
  private confirmAccess(epoch: number): void {
    // An opaque build 404 is not proof a mutation never committed. Keep its hidden frame until access is classified.
    this.requests.add(
      this.freshAdmission().subscribe({
        error: (error) => {
          if (
            epoch === this.epoch &&
            ((error instanceof BuildError && error.kind === "rejected") ||
              (error instanceof CatalogReadError &&
                ["notFound", "rejected"].includes(error.kind)))
          ) {
            this.clear();
            this.error.set(
              "Your access changed. Private build state has been cleared.",
            );
          }
        },
      }),
    );
  }
  private stopTimer(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
  private schedulePoll(id: string): void {
    this.stopTimer();
    const epoch = this.epoch;
    this.timer = setTimeout(() => {
      if (
        epoch !== this.epoch ||
        this.blocked() ||
        this.pending() ||
        this.selectedRun()?.buildId !== id ||
        !this.isActive()
      )
        return;
      if (this.busy()) this.schedulePoll(id);
      else this.openRun(id);
    }, 2000);
  }
  private hideMetadata(): void {
    this.confirmation.set(null);
    this.stopTimer();
    this.capabilities.set(null);
    this.environmentCapabilities.set(null);
    this.environments.set(null);
    this.approvals.set(null);
    this.history.set(null);
    this.evidencePage.set(null);
    this.evidence.set(null);
    this.selectedEnvironment.set(null);
    this.selectedApproval.set(null);
    this.selectedRun.set(null);
  }
  private clear(forgetPending = true): void {
    ++this.epoch;
    this.requests.unsubscribe();
    this.requests = new Subscription();
    this.hideMetadata();
    if (forgetPending) this.recovery.clear();
    this.admission.set(null);
    this.observedAccess.set(null);
    this.busy.set(false);
    this.blocked.set(false);
    this.error.set(null);
    this.notice.set(null);
  }
}
function environmentKey(value: BuildEnvironment): string {
  return value.reference.environmentId + "/" + value.reference.version;
}
function sameProfile(left: BuildProfile, right: BuildProfile): boolean {
  return (
    left.id === right.id &&
    left.version === right.version &&
    left.runtime === right.runtime &&
    left.runtimeVersion === right.runtimeVersion &&
    left.artifactFormat === right.artifactFormat &&
    left.maximumArtifactBytes === right.maximumArtifactBytes
  );
}
function message(error: unknown): string {
  const kind =
    error instanceof BuildError
      ? error.kind
      : error instanceof CatalogReadError &&
          ["notFound", "rejected"].includes(error.kind)
        ? "rejected"
        : "unavailable";
  return {
    unavailable:
      "The result could not be confirmed. If a previous request is still being tracked, check it before sending another.",
    notFound:
      "Preparation controls or the requested record are unavailable. Check access before continuing.",
    rejected: "Your access changed. Refresh the catalog before continuing.",
    conflict:
      "This request reference already belongs to different settings. The original request has been kept.",
    invalid:
      "The request was refused. The original recovery details have been kept.",
    precondition:
      "The required permission, runtime, environment or passing test result could not be confirmed. No new report was generated.",
    busy: "Too many preparations are active. Finish or cancel one, then resend the same request.",
    incompatible:
      "The returned details could not be verified. No successful change or preparation is assumed.",
  }[kind];
}
