import { Component, inject, input, output, signal } from "@angular/core";
import type { CatalogContext } from "../catalog-types";
import type { LifecycleScope } from "./lifecycle-api";
import {
  PurgeApi,
  PurgeFailure,
  type DeletionJob,
  type PurgeSnapshot,
} from "./purge-api";

/**
 * Permanent deletion review for an archived project or mapping. The typed name is a
 * deliberate pause, not security; the server checks authority, versions and blockers.
 * The confirmation stays in memory only and an unknown outcome is read, never resent.
 */
@Component({
  selector: "braid-purge-panel",
  template: `
    @if (job(); as current) {
      <section class="catalog-state" role="status" data-purge-status>
        <h2>Permanent deletion: {{ stateText(current) }}</h2>
        <p>
          Catalog removed: {{ current.catalogRemoved ? "yes" : "no" }}. Private
          objects: {{ current.objectCount }} ({{
            current.objectsPending
          }}
          pending, {{ current.objectsBlocked }} blocked).
        </p>
        <button type="button" class="catalog-button" (click)="refresh()">
          Refresh status
        </button>
      </section>
    } @else if (unknownOperation()) {
      <button type="button" class="catalog-button" (click)="resolve()">
        Check deletion status
      </button>
    } @else if (snapshot(); as review) {
      <section
        class="catalog-state"
        role="alertdialog"
        aria-labelledby="purge-confirm-title"
        data-purge-review
      >
        <h2 id="purge-confirm-title">
          Permanently delete {{ kind() }} {{ name() }}?
        </h2>
        <p>
          This cannot be undone. It removes
          {{ review.body.impact.mappings }} mappings,
          {{ review.body.impact.revisions }} revisions,
          {{ review.body.impact.conversations }} conversations,
          {{ review.body.impact.previews }} previews,
          {{ review.body.impact.regressionCases }} regression cases,
          {{ review.body.impact.regressionRuns }} regression runs,
          {{ review.body.impact.builds }} builds and
          {{ review.body.impact.releaseTargets }} release targets, then erases
          their private files.
        </p>
        <p>
          Kept: audit and AI usage records,
          {{ review.body.impact.retainedArtifacts }}
          retained build artifacts, copies of release and invocation audit, and
          identifiers that stop old requests running again. Backups and storage
          version history follow their own retention.
        </p>
        @if (review.body.activeReleases !== "0") {
          <p role="alert">
            {{ review.body.activeReleases }} release targets still retain this
            scope through an active release, pinned order, trigger, or replica.
            Retirement alone may not clear this hold.
          </p>
        } @else if (review.body.confirmation) {
          <label>
            Type the name to confirm
            <input
              data-purge-name
              [value]="typed()"
              (input)="typed.set($any($event.target).value)"
            />
          </label>
          <button
            type="button"
            class="catalog-button"
            data-purge-confirm
            [disabled]="saving() || typed() !== name()"
            (click)="confirm(review)"
          >
            Delete permanently
          </button>
        }
        <button
          type="button"
          class="catalog-button"
          [disabled]="saving()"
          (click)="snapshot.set(null)"
        >
          Cancel
        </button>
      </section>
    } @else {
      <button
        type="button"
        class="catalog-button"
        data-purge-start
        (click)="review()"
      >
        Delete {{ kind() }} permanently…
      </button>
    }
    @if (message()) {
      <p role="status" data-purge-message>{{ message() }}</p>
    }
  `,
})
export class PurgePanelComponent {
  readonly context = input.required<CatalogContext>();
  readonly scope = input.required<LifecycleScope>();
  readonly name = input.required<string>();
  readonly jobId = input<string | null>(null);
  readonly changed = output<void>();
  readonly snapshot = signal<PurgeSnapshot | null>(null);
  readonly job = signal<DeletionJob | null>(null);
  readonly typed = signal("");
  readonly saving = signal(false);
  readonly message = signal("");
  readonly unknownOperation = signal<string | null>(null);
  private readonly api = inject(PurgeApi);

  kind(): string {
    return "mappingId" in this.scope() ? "mapping" : "project";
  }

  stateText(job: DeletionJob): string {
    return job.state === "blocked"
      ? `blocked (${job.blocker})`
      : job.state === "completed"
        ? "completed"
        : `in progress (${job.state})`;
  }

  review(): void {
    this.message.set("");
    this.typed.set("");
    const existing = this.jobId();
    if (existing) {
      this.loadJob(existing);
      return;
    }
    this.api.preview(this.context(), this.scope()).subscribe({
      next: (value) => {
        if (value.body.deletion) this.job.set(value.body.deletion);
        else this.snapshot.set(value);
      },
      error: () => this.message.set("The deletion review is unavailable."),
    });
  }

  confirm(review: PurgeSnapshot): void {
    if (this.saving()) return;
    const operationId = crypto.randomUUID();
    this.saving.set(true);
    this.api
      .request(this.context(), this.scope(), review, operationId)
      .subscribe({
        next: (job) => {
          this.saving.set(false);
          this.snapshot.set(null);
          this.job.set(job);
          this.changed.emit();
        },
        error: (error: unknown) => {
          this.saving.set(false);
          this.snapshot.set(null);
          const code =
            error instanceof PurgeFailure ? error.code : "unconfirmed";
          if (code === "unconfirmed") {
            this.unknownOperation.set(operationId);
            this.message.set(
              "The deletion was not confirmed. Check its status before trying again.",
            );
          } else
            this.message.set(
              code === "activeRelease"
                ? "A release or retained order still uses this scope."
                : code === "confirmationExpired" ||
                    code === "preconditionFailed" ||
                    code === "conflict"
                  ? "The review is out of date. Review it again."
                  : "The deletion was refused.",
            );
        },
      });
  }

  resolve(): void {
    const operationId = this.unknownOperation();
    if (!operationId) return;
    this.api.operation(this.context(), operationId).subscribe({
      next: (job) => {
        this.unknownOperation.set(null);
        this.message.set("");
        this.job.set(job);
        this.changed.emit();
      },
      error: () =>
        this.message.set("The status is still unknown. Try again later."),
    });
  }

  refresh(): void {
    const current = this.job();
    if (current) this.loadJob(current.jobId);
  }

  private loadJob(jobId: string): void {
    this.api.job(this.context(), jobId).subscribe({
      next: (job) => this.job.set(job),
      error: () => this.message.set("The deletion status is unavailable."),
    });
  }
}
