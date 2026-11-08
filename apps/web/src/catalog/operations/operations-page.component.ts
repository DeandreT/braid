import { HttpErrorResponse } from "@angular/common/http";
import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { FormsModule } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { finalize, of, switchMap } from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import { BrowserSessionState } from "../../auth/browser-session-state";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog-types";
import {
  OperationsApi,
  type OperationBody,
  type OperationsPage,
  type OriginalOperation,
} from "./operations-api";

@Component({
  selector: "weft-operations-page",
  imports: [FormsModule, RouterLink],
  templateUrl: "./operations-page.component.html",
  styleUrl: "../catalog.css",
  host: { "(input)": "draftChanged()", "(change)": "draftChanged()" },
})
export class OperationsPageComponent {
  readonly context = inject(CatalogContextStore);
  private readonly api = inject(OperationsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly destroy = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly sessions = inject(BrowserSessionState);
  private generation = 0;
  private retainedOwner: string | null = null;
  private readonly admitted = signal<CatalogContext | null>(null);
  readonly presented = computed(
    () =>
      this.context.isCurrent(this.admitted()) &&
      this.context.canPresent(this.admitted()),
  );
  readonly data = signal<OperationsPage | null>(null);
  readonly summary = signal<Record<string, unknown> | null>(null);
  readonly busy = signal(false);
  readonly message = signal("");
  private readonly pending = signal<OriginalOperation | null>(null);
  private pendingForm: { target: string; action: string } | null = null;
  private readonly mutationPending = signal(false);
  private readonly formChanges = signal(0);
  private readonly savedTarget = signal("");
  private readonly savedAction = signal("");
  private readonly unsent = computed(() => {
    this.formChanges();
    return (
      this.pending() !== null ||
      this.mutationPending() ||
      this.targetSignature() !== this.savedTarget() ||
      this.actionSignature() !== this.savedAction()
    );
  });
  readonly visiblePending = computed(() => {
    const current = this.context.context();
    const pending = this.pending();
    return current &&
      this.context.canPresent(current) &&
      pending?.owner === catalogOwner(current)
      ? pending
      : null;
  });
  section = "targets";
  target = "";
  since = "";
  until = "";
  caller = "";
  outcome = "";
  action = "activate";
  environment = "";
  environmentVersion = "";
  version = "";
  qualification = "";
  source = "";
  confirming = false;
  private scope: CatalogMappingScope | null = null;

  constructor() {
    this.saveFormBaselines();
    inject(UnsentWork).track(() => this.unsent(), this.destroy);
    this.context.invalidated.pipe(takeUntilDestroyed()).subscribe(() => {
      this.generation++;
      this.data.set(null);
      this.summary.set(null);
      this.message.set("");
      this.busy.set(false);
      this.mutationPending.set(false);
      this.admitted.set(null);
    });
    effect(() => {
      const context = this.context.context();
      const ended =
        this.sessions.state().kind === "disabled" ||
        this.sessions.action().kind === "logoutAcknowledged";
      untracked(() => {
        if (ended) {
          this.clearDrafts();
          this.retainedOwner = null;
        } else if (context) this.bindOwner(context);
      });
    });
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const organizationId = params.get("organizationId") ?? "";
      const projectId = params.get("projectId") ?? "";
      const mappingId = params.get("mappingId") ?? "";
      this.scope = [organizationId, projectId, mappingId].every(isCatalogId)
        ? { organizationId, projectId, mappingId }
        : null;
      this.generation++;
      this.busy.set(false);
      this.mutationPending.set(false);
      this.data.set(null);
      this.summary.set(null);
      this.admitted.set(null);
      this.clearDrafts();
      this.load();
    });
  }

  load(offset = 0, confirmAccess = false): void {
    const scope = this.scope;
    if (!scope || this.busy()) return;
    let section = confirmAccess ? "targets" : this.section;
    let target = confirmAccess ? null : this.target || null;
    if (section !== "targets" && !isCatalogId(this.target)) {
      this.message.set("Select a target first.");
      return;
    }
    let filters = new URLSearchParams();
    for (const [name, value] of [
      ["since", this.since],
      ["until", this.until],
      ["caller", this.caller],
      ["outcome", this.outcome],
    ])
      if (value && !confirmAccess) filters.set(name!, value!);
    if (confirmAccess) this.section = "targets";
    const generation = ++this.generation;
    this.busy.set(true);
    this.message.set("");
    this.data.set(null);
    this.summary.set(null);
    this.context
      .refresh()
      .pipe(
        switchMap((context) => {
          if (generation !== this.generation)
            throw new Error("Operations selection changed");
          if (!this.bindOwner(context)) {
            // Do not carry the previous owner's read selection into the new workspace.
            section = "targets";
            target = null;
            offset = 0;
            filters = new URLSearchParams();
          }
          this.admitted.set(context);
          return this.api.read(
            context,
            scope,
            target,
            section,
            offset,
            filters,
          );
        }),
        finalize(() => {
          if (generation === this.generation) this.busy.set(false);
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          if (generation !== this.generation) return;
          if (section === "summary")
            this.summary.set(value as Record<string, unknown>);
          else if (
            value &&
            typeof value === "object" &&
            "items" in value &&
            Array.isArray(value.items) &&
            "nextOffset" in value
          )
            this.data.set(value as OperationsPage);
          else this.message.set("The server returned an invalid response.");
        },
        error: () => {
          if (generation === this.generation)
            this.message.set(
              "Operations are unavailable or your access could not be confirmed.",
            );
        },
      });
  }

  select(row: OperationsPage["items"][number]): void {
    if (row.kind !== "target" || !isCatalogId(row["target_id"])) return;
    this.target = row["target_id"];
    this.environment = String(row["environment_id"]);
    this.environmentVersion = String(row["environment_version"]);
    this.version = String(row["version"]);
    this.savedTarget.set(this.targetSignature());
    this.draftChanged();
    this.section = "history";
    this.load();
  }

  send(): void {
    const scope = this.scope;
    if (!scope || this.busy() || (!this.confirming && !this.pending())) return;
    const recovering = this.pending() !== null;
    const generation = ++this.generation;
    this.busy.set(true);
    this.mutationPending.set(true);
    this.message.set("");
    this.context
      .refresh()
      .pipe(
        switchMap((context) => {
          if (generation !== this.generation)
            throw new Error("Operations selection changed");
          if (!this.bindOwner(context))
            throw new Error("Different workspace owner");
          this.admitted.set(context);
          let original = this.pending();
          if (original && original.owner !== catalogOwner(context))
            throw new Error("Different workspace owner");
          if (!original) {
            if (
              ![this.target, this.environment].every(isCatalogId) ||
              !/^[1-9][0-9]*$/.test(this.version) ||
              !/^[1-9][0-9]*$/.test(this.environmentVersion)
            )
              throw new Error("Invalid target");
            const body: OperationBody = {
              action: this.action,
              environmentId: this.environment,
              environmentVersion: this.environmentVersion,
              qualificationId:
                this.action === "activate" || this.action === "rollback"
                  ? this.qualification
                  : null,
              sourceReleaseId: this.action === "rollback" ? this.source : null,
            };
            original = {
              id: crypto.randomUUID(),
              target: this.target,
              version: this.version,
              owner: catalogOwner(context),
              body: JSON.stringify(body),
            };
            this.pendingForm = {
              target: this.targetSignature(),
              action: this.actionSignature(),
            };
            this.pending.set(original);
          }
          return this.api.apply(context, scope, original);
        }),
        finalize(() => {
          if (generation === this.generation) {
            this.busy.set(false);
            this.mutationPending.set(false);
          }
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (receipt) => {
          if (generation !== this.generation) return;
          const original = this.pending();
          if (
            !original ||
            !receipt ||
            typeof receipt !== "object" ||
            !("operationId" in receipt) ||
            receipt.operationId !== original.id ||
            !("outcome" in receipt) ||
            receipt.outcome !== "applied"
          ) {
            this.message.set(
              "The result is uncertain. Recover using the original operation below; do not create a replacement.",
            );
            return;
          }
          this.pending.set(null);
          // Acknowledging a request is not permission to discard edits made while it ran.
          if (this.pendingForm?.target === this.targetSignature()) {
            this.version = "";
            this.savedTarget.set(this.targetSignature());
          }
          if (this.pendingForm?.action === this.actionSignature()) {
            this.confirming = false;
            this.savedAction.set(this.actionSignature());
          }
          this.pendingForm = null;
          this.message.set(
            "Operation recorded. Refresh the target before another change.",
          );
          this.draftChanged();
        },
        error: (error) => {
          if (generation !== this.generation) return;
          if (
            !recovering &&
            error instanceof HttpErrorResponse &&
            [400, 404, 409, 412, 415, 428].includes(error.status)
          ) {
            this.pending.set(null);
            this.pendingForm = null;
            this.confirming = false;
            this.draftChanged();
            this.message.set(
              error.status === 412
                ? "The target version changed. Refresh and review before trying again."
                : "The operation was refused.",
            );
          } else
            this.message.set(
              "The result is uncertain. Recover using the original operation below; do not create a replacement.",
            );
        },
      });
  }

  draftChanged(): void {
    this.formChanges.update((value) => value + 1);
  }

  private targetSignature(): string {
    return JSON.stringify([
      this.target,
      this.environment,
      this.environmentVersion,
      this.version,
    ]);
  }

  private actionSignature(): string {
    return JSON.stringify([
      this.action,
      this.qualification,
      this.source,
      this.confirming,
    ]);
  }

  private saveFormBaselines(): void {
    this.savedTarget.set(this.targetSignature());
    this.savedAction.set(this.actionSignature());
  }

  // A failed check hides local work; only a confirmed different owner discards it.
  private bindOwner(context: CatalogContext): boolean {
    const owner = catalogOwner(context);
    const changed = this.retainedOwner !== null && owner !== this.retainedOwner;
    if (changed) this.clearDrafts();
    this.retainedOwner = owner;
    return !changed;
  }

  private clearDrafts(): void {
    this.pending.set(null);
    this.pendingForm = null;
    this.target = "";
    this.environment = "";
    this.environmentVersion = "";
    this.version = "";
    this.action = "activate";
    this.qualification = "";
    this.source = "";
    this.since = "";
    this.until = "";
    this.caller = "";
    this.outcome = "";
    this.confirming = false;
    this.section = "targets";
    this.saveFormBaselines();
    this.draftChanged();
  }

  text(value: unknown): string {
    return typeof value === "string" ? value : (JSON.stringify(value) ?? "—");
  }
  entries(value: object): [string, unknown][] {
    return Object.entries(value);
  }
  /** Every field any record on this page has, in first-seen order; "kind" is the same for all. */
  columns(rows: OperationsPage["items"]): string[] {
    const keys = new Set<string>();
    for (const row of rows)
      for (const key of Object.keys(row)) if (key !== "kind") keys.add(key);
    return [...keys];
  }
  cell(row: object, key: string): string {
    return key in row ? this.text((row as Record<string, unknown>)[key]) : "—";
  }
  columnLabel(key: string): string {
    const words = key.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/_/g, " ");
    return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
  }
  requestLeave() {
    return of(
      !this.unsent() ||
        (this.document.defaultView?.confirm(
          "Leave Operations? Unsaved action choices and this tab's recovery request will be lost. An unconfirmed action may already have committed.",
        ) ??
          false),
    );
  }
}
