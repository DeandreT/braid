import {
  Component,
  effect,
  inject,
  input,
  output,
  signal,
} from "@angular/core";
import { Subscription as RxSubscription, finalize } from "rxjs";
import { catalogOwner, type CatalogContext } from "../catalog-types";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  DeletionApi,
  DeletionFailure,
  type DeletionTarget,
  type ResourceDeletionReferences,
} from "./deletion-api";

/**
 * Permanent deletion of one item after an explicit confirmation. The request is sent once;
 * an unknown outcome is resolved by reading its operation, never by sending it again.
 */
@Component({
  selector: "braid-delete-item",
  template: `
    @if (confirming()) {
      <section
        class="catalog-state"
        role="alertdialog"
        aria-labelledby="delete-item-title"
        data-delete-confirmation
      >
        <h2 id="delete-item-title">Permanently delete {{ label() }}?</h2>
        <p>{{ consequence() }}</p>
        <p>This cannot be undone. {{ retained() }}</p>
        <button
          type="button"
          class="catalog-button"
          data-delete-confirm
          [disabled]="saving()"
          (click)="confirm()"
        >
          {{ saving() ? "Deleting…" : "Delete permanently" }}
        </button>
        <button
          type="button"
          class="catalog-button"
          [disabled]="saving()"
          (click)="confirming.set(false)"
        >
          Cancel
        </button>
      </section>
    } @else if (unknownOperation()) {
      <button type="button" class="catalog-button" (click)="resolve()">
        Check deletion status
      </button>
    } @else if (!done()) {
      <button
        type="button"
        class="catalog-button"
        data-delete-item
        (click)="confirming.set(true)"
      >
        Delete {{ label() }}
      </button>
    }
    @if (message()) {
      <p role="status" data-delete-message>{{ message() }}</p>
    }
  `,
})
export class DeleteItemComponent {
  readonly context = input.required<CatalogContext>();
  readonly target = input.required<DeletionTarget>();
  readonly label = input.required<string>();
  readonly deleted = output<void>();
  readonly confirming = signal(false);
  readonly saving = signal(false);
  readonly done = signal(false);
  readonly message = signal("");
  /** An operation whose outcome is unknown; it is resolved by reading, never resent. */
  readonly unknownOperation = signal<string | null>(null);
  private readonly api = inject(DeletionApi);
  private actions = new Set<RxSubscription>();
  private key: string | null = null;

  constructor() {
    inject(UnsentWork).track(
      () => this.saving() || this.unknownOperation() !== null,
    );
    effect((onCleanup) => {
      const key = JSON.stringify([catalogOwner(this.context()), this.target()]);
      if (key !== this.key) {
        // A different item or signed-in owner never inherits another's pending state.
        this.key = key;
        this.confirming.set(false);
        this.done.set(false);
        this.unknownOperation.set(null);
        this.message.set("");
      }
      onCleanup(() => {
        this.actions.forEach((action) => action.unsubscribe());
        this.actions.clear();
      });
    });
  }

  consequence(): string {
    switch (this.target().kind) {
      case "preview":
        return "Its inputs, outputs and failure details are erased, and its request ID can never run again.";
      case "conversation":
        return "Its messages, model responses and citations are erased. Its identifiers stay retired.";
      case "regressionCase":
        return "Every saved version, including its sample inputs and expected outputs, is erased. Its case ID cannot be reused.";
      case "buildEnvironment":
        return "Every version of the environment is erased. Its environment ID cannot be reused.";
    }
  }

  retained(): string {
    return this.target().kind === "conversation"
      ? "AI usage and quota records and the audit log keep their own retention."
      : "The audit log keeps a record of the deletion.";
  }

  confirm(): void {
    if (this.saving() || this.unknownOperation()) return;
    const operationId = crypto.randomUUID();
    this.saving.set(true);
    this.unknownOperation.set(null);
    this.actions.add(
      this.api
        .delete(this.context(), this.target(), operationId)
        .pipe(finalize(() => this.saving.set(false)))
        .subscribe({
          next: () => this.applied(),
          error: (error: unknown) => {
            this.confirming.set(false);
            const failure =
              error instanceof DeletionFailure
                ? error
                : new DeletionFailure("unconfirmed");
            if (failure.code === "unconfirmed") {
              this.unknownOperation.set(operationId);
              this.message.set(
                "The deletion was not confirmed. Check its status before trying again.",
              );
            } else this.message.set(refusal(failure.code, failure.references));
          },
        }),
    );
  }

  resolve(): void {
    const operationId = this.unknownOperation();
    if (!operationId) return;
    this.actions.add(
      this.api.operation(this.context(), operationId).subscribe({
        next: () => this.applied(),
        // Not-found also covers lost authority; it does not prove the deletion failed.
        error: () =>
          this.message.set("The status is still unknown. Try again later."),
      }),
    );
  }

  private applied(): void {
    this.confirming.set(false);
    this.unknownOperation.set(null);
    this.done.set(true);
    this.message.set("Deleted permanently.");
    this.deleted.emit();
  }
}

function refusal(
  code: string,
  references: ResourceDeletionReferences | null,
): string {
  switch (code) {
    case "activeWork":
      return "It is still running. Cancel it or wait for it to finish, then delete it.";
    case "referencedResource": {
      const counts = [
        [references?.regressionRuns, "regression runs"],
        [references?.automationEvaluations, "automation evaluations"],
        [references?.buildApprovals, "build approvals"],
        [references?.releaseTargets, "release targets"],
      ]
        .filter(([count]) => count && count !== "0")
        .map(([count, name]) => `${count} ${name}`);
      return `It was not deleted because other records refer to it${counts.length ? ": " + counts.join(", ") : ""}.`;
    }
    case "preconditionFailed":
    case "conflict":
      return "It changed since it was read. Refresh, review it and try again.";
    case "notFound":
      return "It is no longer available to you.";
    default:
      return "The deletion was refused.";
  }
}
