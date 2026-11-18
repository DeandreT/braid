import {
  Component,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from "@angular/core";
import { Subscription as RxSubscription, finalize } from "rxjs";
import { catalogOwner, type CatalogContext } from "../catalog-types";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  LifecycleApi,
  LifecycleFailure,
  type LifecycleAction,
  type LifecycleScope,
  type LifecycleSnapshot,
} from "./lifecycle-api";
import { PurgePanelComponent } from "./purge-panel.component";

/**
 * Archive state of one exact project or mapping. Controls explain state but are not security:
 * the server refuses authoring on archived resources whatever this panel shows.
 */
@Component({
  selector: "braid-lifecycle-panel",
  imports: [PurgePanelComponent],
  template: `
    @if (snapshot(); as current) {
      @if (current.body.effectiveState === "archived") {
        <section
          class="catalog-state catalog-archived"
          role="status"
          data-lifecycle-banner
        >
          <h2>
            <span class="catalog-badge">Archived</span>
            {{
              current.body.state === "archived"
                ? "This " + kind() + " is archived."
                : "Its project is archived."
            }}
          </h2>
          <p>
            It stays readable. New revisions, edits, regression case changes,
            previews, builds and releases are refused. Enabled endpoints that
            are already deployed keep serving.
          </p>
        </section>
      }
      @if (pending(); as action) {
        <section
          class="catalog-state"
          role="alertdialog"
          aria-labelledby="lifecycle-confirm-title"
        >
          <h2 id="lifecycle-confirm-title">
            {{ action === "archive" ? "Archive" : "Restore" }} {{ name() }}?
          </h2>
          @if (action === "archive") {
            <p>
              Authoring and new deployments stop for this {{ kind() }}. Work
              that started before archiving cannot publish. Enabled endpoints
              keep serving; disable them separately when needed.
            </p>
          } @else {
            <p>
              Authoring becomes available again. Work that started before the
              archive stays refused and must be submitted again.
            </p>
          }
          <button
            type="button"
            class="catalog-button"
            data-lifecycle-confirm
            [disabled]="saving()"
            (click)="confirm(action)"
          >
            {{ action === "archive" ? "Archive" : "Restore" }}
          </button>
          <button
            type="button"
            class="catalog-button"
            [disabled]="saving()"
            (click)="pending.set(null)"
          >
            Cancel
          </button>
        </section>
      } @else if (unknownOperation()) {
        <button type="button" class="catalog-button" (click)="resolve()">
          Check change status
        </button>
      } @else {
        @if (current.body.canArchive) {
          <button
            type="button"
            class="catalog-button"
            data-lifecycle-archive
            (click)="pending.set('archive')"
          >
            Archive {{ kind() }}
          </button>
        }
        @if (current.body.canRestore) {
          <button
            type="button"
            class="catalog-button"
            data-lifecycle-restore
            (click)="pending.set('restore')"
          >
            Restore {{ kind() }}
          </button>
        }
        @if (current.body.canRestore || current.body.deletionJobId) {
          <braid-purge-panel
            [context]="context()"
            [scope]="scope()"
            [name]="name()"
            [jobId]="current.body.deletionJobId ?? null"
            (changed)="changed.emit()"
          />
        }
      }
    }
    @if (unknownOperation() && !snapshot()) {
      <button type="button" class="catalog-button" (click)="resolve()">
        Check change status
      </button>
    }
    @if (message()) {
      <p role="status" data-lifecycle-message>{{ message() }}</p>
    }
  `,
})
export class LifecyclePanelComponent {
  readonly context = input.required<CatalogContext>();
  readonly scope = input.required<LifecycleScope>();
  readonly name = input.required<string>();
  readonly changed = output<void>();
  readonly snapshot = signal<LifecycleSnapshot | null>(null);
  readonly pending = signal<LifecycleAction | null>(null);
  readonly saving = signal(false);
  readonly message = signal("");
  /** An operation whose outcome is unknown; it is resolved by reading, never resent. */
  readonly unknownOperation = signal<string | null>(null);
  private readonly api = inject(LifecycleApi);
  private actions = new Set<RxSubscription>();
  private ownerKey: string | null = null;
  private inFlight: string | null = null;

  constructor() {
    inject(UnsentWork).track(
      () => this.saving() || this.unknownOperation() !== null,
    );
    effect((onCleanup) => {
      const context = this.context(),
        scope = this.scope();
      const key = JSON.stringify([catalogOwner(context), scope]);
      if (key !== this.ownerKey) {
        this.ownerKey = key;
        this.pending.set(null);
        const operation = untracked(() =>
          this.api.pendingOperation(context, scope),
        );
        this.unknownOperation.set(operation);
        this.message.set(
          operation
            ? "The change was not confirmed. Check its status before trying again."
            : "",
        );
      }
      this.snapshot.set(null);
      const request = this.api.read(context, scope).subscribe({
        next: (value) => this.observed(value),
        error: () => this.snapshot.set(null),
      });
      onCleanup(() => {
        request.unsubscribe();
        this.actions.forEach((action) => action.unsubscribe());
        this.actions.clear();
        // A change cut off mid-request has an unknown outcome. Keep its operation so
        // the same owner can still resolve it; another owner's next run clears it.
        if (this.inFlight) {
          this.unconfirmed(this.inFlight);
          this.inFlight = null;
        }
      });
    });
  }

  kind(): string {
    return "mappingId" in this.scope() ? "mapping" : "project";
  }

  confirm(action: LifecycleAction): void {
    const current = this.snapshot();
    if (
      !current ||
      this.saving() ||
      this.unknownOperation() ||
      !(action === "archive"
        ? current.body.canArchive
        : current.body.canRestore)
    )
      return;
    const operationId = crypto.randomUUID();
    this.saving.set(true);
    this.inFlight = operationId;
    this.api.rememberOperation(this.context(), this.scope(), operationId);
    this.actions.add(
      this.api
        .change(this.context(), this.scope(), action, current.etag, operationId)
        .pipe(finalize(() => this.saving.set(false)))
        .subscribe({
          next: (receipt) => {
            this.inFlight = null;
            this.applied(receipt.action, receipt.operationId);
          },
          error: (error: unknown) => {
            this.inFlight = null;
            this.pending.set(null);
            const code =
              error instanceof LifecycleFailure ? error.code : "unconfirmed";
            if (code === "unconfirmed") this.unconfirmed(operationId);
            else {
              this.api.forgetOperation(
                this.context(),
                this.scope(),
                operationId,
              );
              this.message.set(
                code === "preconditionFailed" || code === "conflict"
                  ? "The state changed since it was read. Review it and try again."
                  : "The change was refused.",
              );
            }
            this.reload();
          },
        }),
    );
  }

  resolve(): void {
    const operationId = this.unknownOperation();
    if (!operationId) return;
    this.actions.add(
      this.api.operation(this.context(), operationId).subscribe({
        next: (receipt) => this.applied(receipt.action, receipt.operationId),
        // Not-found also covers lost authority, and a canceled transport does
        // not establish that the server transaction has finished. Keep identity.
        error: () =>
          this.message.set("The status is still unknown. Try again later."),
      }),
    );
  }

  private unconfirmed(operationId: string): void {
    this.pending.set(null);
    this.unknownOperation.set(operationId);
    this.message.set(
      "The change was not confirmed. Check its status before trying again.",
    );
  }

  private applied(action: string, operation: string): void {
    this.api.forgetOperation(this.context(), this.scope(), operation);
    this.pending.set(null);
    this.unknownOperation.set(null);
    this.message.set(
      action === "archive"
        ? `The ${this.kind()} is archived.`
        : `The ${this.kind()} is restored.`,
    );
    this.reload();
    this.changed.emit();
  }

  private reload(): void {
    this.actions.add(
      this.api.read(this.context(), this.scope()).subscribe({
        next: (value) => this.observed(value),
        error: () => this.snapshot.set(null),
      }),
    );
  }

  private observed(value: LifecycleSnapshot): void {
    this.snapshot.set(value);
    const action = this.pending();
    if (
      action &&
      !(action === "archive" ? value.body.canArchive : value.body.canRestore)
    )
      this.pending.set(null);
  }
}
