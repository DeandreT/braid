import { HttpErrorResponse } from "@angular/common/http";
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from "@angular/core";
import { RouterLink } from "@angular/router";
import { Subscription } from "rxjs";
import { UnsentWork } from "../../workbench/unsent-work";
import { CatalogContextStore } from "../catalog-context";
import {
  catalogRoutes,
  isCatalogId,
  type CatalogProjectScope,
  type CatalogRevisionScope,
  type CatalogContext,
} from "../catalog-types";
import { MfdInterchangeApi } from "./mfd-interchange-api";
import { mfdPackage, type MfdReview } from "./mfd-interchange";
import { MfdImportDraftRecovery } from "./unsent-upload-recovery";

@Component({
  selector: "braid-mfd-import",
  imports: [RouterLink],
  templateUrl: "./mfd-import.component.html",
  styleUrl: "./mfd-interchange.component.css",
  host: { "(input)": "draftChanged()", "(change)": "draftChanged()" },
})
export class MfdImportComponent {
  readonly project = input.required<CatalogProjectScope>();
  readonly mappingId = input<string | null>(null);
  readonly expectedHead = input<string | null>(null);
  readonly blocked = input(false);
  readonly enabled = signal(false);
  readonly busy = signal(false);
  readonly review = signal<MfdReview | null>(null);
  readonly recoveryPending = signal(false);
  readonly acknowledged = signal(false);
  readonly forgetWarning = signal(false);
  readonly forgetAcknowledged = signal(false);
  private readonly confirmationAttempted = signal(false);
  readonly message = signal("");
  readonly saved = signal<CatalogRevisionScope | null>(null);
  readonly designs = signal<string[]>([]);
  readonly routes = catalogRoutes;
  name = "";
  description = "";
  design = "";
  private files: File[] = [];
  private readonly api = inject(MfdInterchangeApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly recovery = inject(MfdImportDraftRecovery);
  private admittedContext: CatalogContext | null = null;
  private generation = 0;
  private owner = "";
  private retained = false;
  private active = new Subscription();
  private readonly draftRevision = signal(0);
  private restoredGeneration = -1;
  readonly selectedFiles = computed(() => {
    this.draftRevision();
    return this.files.map((file) => file.webkitRelativePath || file.name);
  });

  constructor() {
    inject(UnsentWork).track(() => {
      this.draftRevision();
      return this.hasLeaveRisk();
    });
    const invalidation = this.contexts.invalidated.subscribe(() => {
      this.retainDraft();
      this.resetVisible();
    });
    effect(() => {
      const context = this.contexts.context(),
        project = this.project(),
        mapping = this.mappingId();
      const owner = context
        ? `${context.actorPrincipalId}/${project.organizationId}/${project.projectId}/${mapping ?? "create"}`
        : "";
      if (context) this.admittedContext = context;
      if (owner === this.owner) return;
      this.owner = owner;
      this.resetVisible();
      if (!context || context.allowedOrganizationId !== project.organizationId)
        return;
      const generation = this.generation;
      this.active.add(
        this.api.capabilities().subscribe({
          next: (enabled) => {
            if (generation !== this.generation) return;
            this.enabled.set(enabled);
          },
          error: () => {
            if (generation === this.generation) this.enabled.set(false);
          },
        }),
      );
    });
    effect(() => {
      const admitted = this.enabled() && !this.blocked();
      const context = this.contexts.context();
      const project = this.project();
      const mapping = this.mappingId();
      if (!admitted || !context || !this.contexts.canSend(context)) return;
      untracked(() => {
        if (this.restoredGeneration === this.generation) return;
        this.restoredGeneration = this.generation;
        const scope = mapping ? { ...project, mappingId: mapping } : project;
        const draft = this.recovery.take(context, scope);
        if (draft?.kind === "review") {
          this.review.set(draft.review);
          this.confirmationAttempted.set(draft.attempted);
          this.acknowledged.set(false);
          this.retained = draft.retained;
          this.message.set(
            "Recovered the original review. Retry only this review to resolve an uncertain confirmation.",
          );
        } else {
          if (draft) {
            this.files = [...draft.files];
            this.name = draft.name;
            this.description = draft.description;
            this.design = draft.design;
            this.designs.set([...draft.designs]);
            this.draftChanged();
          }
          this.restore();
        }
      });
    });
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (this.hasLeaveRisk()) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    globalThis.addEventListener("beforeunload", beforeUnload);
    inject(DestroyRef).onDestroy(() => {
      this.retainDraft();
      invalidation.unsubscribe();
      this.active.unsubscribe();
      ++this.generation;
      globalThis.removeEventListener("beforeunload", beforeUnload);
    });
  }

  requestLeave(): boolean {
    if (!this.hasLeaveRisk()) return true;
    this.message.set(
      "Wait for the request, or clear the unsent MFD files before leaving. A sent confirmation may still finish.",
    );
    return false;
  }

  draftChanged(): void {
    this.draftRevision.update((revision) => revision + 1);
  }

  private hasLeaveRisk(): boolean {
    return (
      this.busy() ||
      (!(this.review() && this.retained) &&
        Boolean(
          this.files.length || this.name || this.description || this.review(),
        ))
    );
  }

  private retainDraft(): void {
    if (!this.admittedContext || !this.hasLeaveRisk()) return;
    const project = this.project();
    const mapping = this.mappingId();
    const review = this.review();
    if (!review && (this.confirmationAttempted() || this.recoveryPending()))
      return;
    this.recovery.keep(
      this.admittedContext,
      mapping ? { ...project, mappingId: mapping } : project,
      review
        ? {
            kind: "review",
            review,
            attempted: this.confirmationAttempted(),
            retained: this.retained,
          }
        : {
            kind: "draft",
            files: [...this.files],
            name: this.name,
            description: this.description,
            design: this.design,
            designs: [...this.designs()],
          },
    );
  }

  selectFiles(event: Event) {
    if (
      this.busy() ||
      this.blocked() ||
      this.review() ||
      this.recoveryPending()
    )
      return;
    this.files = Array.from((event.target as HTMLInputElement).files ?? []);
    if (this.files.length > 128) {
      this.files = [];
      this.designs.set([]);
      this.design = "";
      this.message.set("Choose no more than 128 files.");
      return;
    }
    const designs = this.files
      .map((file) => file.webkitRelativePath || file.name)
      .filter((name) => name.toLowerCase().endsWith(".mfd"));
    this.designs.set(designs);
    this.design = designs[0] ?? "";
    this.message.set("");
    this.saved.set(null);
  }

  async prepare() {
    if (!this.canSend() || this.review() || this.recoveryPending()) return;
    const context = this.contexts.context()!,
      generation = this.generation;
    const project = this.project(),
      mapping = this.mappingId(),
      head = this.expectedHead();
    if (!mapping && !this.name.trim()) {
      this.message.set("Enter a mapping name.");
      return;
    }
    if (
      !mapping &&
      (Array.from(this.name.trim()).length > 200 ||
        Array.from(this.description).length > 4000)
    ) {
      this.message.set(
        "Use at most 200 characters for the name and 4000 for the description.",
      );
      return;
    }
    if (mapping && !isCatalogId(head)) {
      this.message.set("Refresh the mapping to select its current head.");
      return;
    }
    this.busy.set(true);
    this.message.set("");
    try {
      const body = await mfdPackage(this.files, this.design);
      if (generation !== this.generation || !this.contexts.canPresent(context))
        return;
      const path = mapping
        ? `${this.base()}/mappings/${mapping}/mfd-imports/review?expectedHeadRevisionId=${head}`
        : `${this.base()}/mfd-imports/review?${new URLSearchParams({ name: this.name, description: this.description, backendId: "ferrule" })}`;
      this.active.add(
        this.api.review(path, body).subscribe({
          next: (review) => {
            if (
              generation !== this.generation ||
              !this.contexts.canPresent(context)
            )
              return;
            if (
              review.engine.backendId !== "ferrule" ||
              review.targetMappingId !== this.mappingId()
            ) {
              this.message.set(
                "The review destination does not match the requested mapping.",
              );
              this.busy.set(false);
              return;
            }
            this.review.set(review);
            this.acknowledged.set(false);
            this.busy.set(false);
            this.persist(review);
          },
          error: (error: unknown) => this.failed(error, generation, false),
        }),
      );
    } catch (error) {
      if (generation === this.generation) {
        this.busy.set(false);
        this.message.set(
          error instanceof Error
            ? error.message
            : "Package preparation failed.",
        );
      }
    }
  }

  confirm() {
    const review = this.review(),
      context = this.contexts.context(),
      generation = this.generation;
    if (
      !review ||
      !context ||
      !this.canSend() ||
      (review.warnings.length && !this.acknowledged())
    )
      return;
    this.busy.set(true);
    this.message.set("");
    this.confirmationAttempted.set(true);
    this.persist(review);
    this.active.add(
      this.api
        .confirm(
          `${this.base()}/mfd-imports/${review.reviewId}/confirm`,
          review.reviewId,
          review.reviewDigest,
          this.acknowledged(),
        )
        .subscribe({
          next: (raw) => {
            if (
              generation !== this.generation ||
              !this.contexts.canPresent(context)
            )
              return;
            this.busy.set(false);
            try {
              const value = raw as {
                outcome?: unknown;
                receipt?: {
                  project?: CatalogProjectScope;
                  futureRevision?: CatalogRevisionScope;
                  phase?: unknown;
                };
              };
              const outcome = value?.outcome;
              if (
                typeof outcome !== "string" ||
                ![
                  "committedValid",
                  "committedDecodedInvalid",
                  "inProgress",
                  "needsSource",
                  "retryableFailure",
                  "rejected",
                  "headConflict",
                  "attemptExhausted",
                  "idempotencyConflict",
                  "claimLost",
                  "notAuthorizedOrNotFound",
                ].includes(outcome)
              )
                throw new Error();
              const scope = value.receipt?.futureRevision;
              if (
                value.receipt &&
                (!scope ||
                  scope.organizationId !== this.project().organizationId ||
                  scope.projectId !== this.project().projectId ||
                  !isCatalogId(scope.mappingId) ||
                  !isCatalogId(scope.revisionId) ||
                  (this.mappingId() && scope.mappingId !== this.mappingId()))
              )
                throw new Error();
              if (
                outcome === "committedValid" ||
                outcome === "committedDecodedInvalid"
              ) {
                if (!scope || value.receipt?.phase !== "committed")
                  throw new Error();
                this.saved.set(scope);
                this.clearRecovery();
                this.review.set(null);
                this.files = [];
                this.name = "";
                this.description = "";
                this.message.set(
                  outcome === "committedValid"
                    ? "Imported revision saved."
                    : "Imported revision saved with validation findings.",
                );
              } else
                this.message.set(
                  `Import result: ${outcome}. Retry this same review for recovery; a head conflict requires a new review.`,
                );
            } catch {
              this.message.set(
                "Confirmation outcome is unverified. Retry this same review; do not create a new import.",
              );
            }
          },
          error: (error: unknown) => this.failed(error, generation, true),
        }),
    );
  }

  clear() {
    if (this.busy() || this.blocked()) return;
    if (
      (this.review() || this.recoveryPending()) &&
      this.confirmationAttempted()
    ) {
      this.forgetWarning.set(true);
      return;
    }
    this.clearRecovery();
    this.recovery.discard();
    this.resetVisible(false);
  }
  forget() {
    if (this.busy() || this.blocked() || !this.forgetAcknowledged()) return;
    this.clearRecovery();
    this.recovery.discard();
    this.resetVisible(false);
  }
  retryRecovery() {
    if (this.canSend() && this.recoveryPending()) this.restore();
  }
  canSend() {
    return (
      this.enabled() &&
      !this.busy() &&
      !this.blocked() &&
      this.contexts.canSend(this.contexts.context())
    );
  }
  private base() {
    const p = this.project();
    return `/api/v1/organizations/${p.organizationId}/projects/${p.projectId}`;
  }
  private key() {
    return `braid.mfd.review.v1/${this.owner}`;
  }
  private persist(review: MfdReview) {
    try {
      sessionStorage.setItem(
        this.key(),
        JSON.stringify({
          id: review.reviewId,
          digest: review.reviewDigest,
          attempted: this.confirmationAttempted(),
        }),
      );
      this.retained = true;
    } catch {
      this.retained = false;
      this.message.set(
        "Review ready. This browser cannot retain recovery; keep this page open until confirmation finishes.",
      );
    }
  }
  private clearRecovery() {
    try {
      sessionStorage.removeItem(this.key());
    } catch {
      /* unavailable browser storage */
    }
  }
  private restore() {
    try {
      const stored = JSON.parse(
        sessionStorage.getItem(this.key()) ?? "null",
      ) as { id?: string; digest?: string; attempted?: boolean } | null;
      if (
        !stored ||
        !isCatalogId(stored.id) ||
        !/^[0-9a-f]{64}$/u.test(stored.digest ?? "")
      )
        return;
      const generation = this.generation,
        context = this.contexts.context();
      this.recoveryPending.set(true);
      this.confirmationAttempted.set(true);
      this.busy.set(true);
      this.active.add(
        this.api.restore(`${this.base()}/mfd-imports/${stored.id}`).subscribe({
          next: (review) => {
            if (
              generation !== this.generation ||
              !this.contexts.canPresent(context)
            )
              return;
            this.busy.set(false);
            if (
              review.reviewDigest !== stored.digest ||
              review.targetMappingId !== this.mappingId()
            ) {
              this.message.set(
                "Stored review identity or destination changed; import is unavailable.",
              );
              return;
            }
            this.retained = true;
            this.recoveryPending.set(false);
            // A previous tab may have dispatched immediately before persistence
            // failed; restored reviews always require safe-forget acknowledgement.
            this.confirmationAttempted.set(true);
            this.review.set(review);
            this.message.set(
              "Recovered your server-owned import review. Confirm again to recover an uncertain request without duplication.",
            );
          },
          error: (error: unknown) => this.failed(error, generation, true),
        }),
      );
    } catch {
      this.clearRecovery();
    }
  }
  private failed(error: unknown, generation: number, confirmation: boolean) {
    if (generation !== this.generation) return;
    this.busy.set(false);
    this.message.set(
      error instanceof HttpErrorResponse && error.status === 412
        ? "Review conditions were not met. Check warning acknowledgement, expiry and the selected head."
        : confirmation
          ? "The outcome is unknown or access changed. Retry this same review after refreshing access; do not start a duplicate import."
          : "MFD conversion is unavailable or the package was refused. No successful conversion is assumed.",
    );
  }
  private resetVisible(disable = true) {
    ++this.generation;
    this.active.unsubscribe();
    this.active = new Subscription();
    this.files = [];
    this.retained = false;
    this.name = "";
    this.description = "";
    this.designs.set([]);
    this.design = "";
    this.review.set(null);
    this.recoveryPending.set(false);
    this.acknowledged.set(false);
    this.confirmationAttempted.set(false);
    this.forgetWarning.set(false);
    this.forgetAcknowledged.set(false);
    this.busy.set(false);
    this.message.set("");
    this.saved.set(null);
    this.draftChanged();
    if (disable) this.enabled.set(false);
  }
}
