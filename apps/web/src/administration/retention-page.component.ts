import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { FormsModule } from "@angular/forms";
import { firstValueFrom, Subject, takeUntil } from "rxjs";
import { UnsentWork } from "../workbench/unsent-work";
import { CatalogContextStore } from "../catalog/catalog-context";
import { createCatalogRead } from "../catalog/catalog-state";
import { catalogOwner, type CatalogContext } from "../catalog/catalog-types";
import {
  AdministrationFailure,
  type AdministrationSnapshot,
} from "./administration-api";
import {
  RetentionApi,
  retentionFamilies,
  retentionSeconds,
  type RetentionFamily,
  type RetentionMode,
  type RetentionPolicies,
  type RetentionPolicyWrite,
} from "./retention-api";

type Unit = "hours" | "days";
type Row = {
  family: RetentionFamily;
  configured: boolean;
  mode: RetentionMode;
  amount: string;
  unit: Unit;
  applyToExisting: boolean;
};
type Edit = { context: CatalogContext; version: string; used: boolean };

export const retentionLabels: Record<RetentionFamily, string> = {
  preview: "Preview runs",
  regressionRun: "Regression runs",
  conversation: "Conversations",
  invocationHistory: "Invocation history",
};

@Component({
  selector: "braid-retention-page",
  imports: [FormsModule],
  templateUrl: "./retention-page.component.html",
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
        padding: 6px;
      }
      header,
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: end;
        gap: 8px;
        margin-bottom: 8px;
      }
      h1 {
        margin: 0;
        font-size: 1.1rem;
      }
      h2 {
        font-size: 1rem;
      }
      p,
      li,
      label {
        font-size: 0.8125rem;
      }
      label.inline {
        display: inline-flex;
        align-items: center;
        gap: 4px;
      }
      input[type="text"] {
        width: 6rem;
      }
      .scroll {
        overflow: auto;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 0.8125rem;
      }
      th,
      td {
        height: 28px;
        padding: 0 6px;
        line-height: 28px;
        white-space: nowrap;
        text-align: left;
        border-bottom: 1px solid var(--border);
      }
    `,
  ],
})
export class RetentionPageComponent {
  private readonly api = inject(RetentionApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly stop = new Subject<void>();
  readonly read =
    createCatalogRead<AdministrationSnapshot<RetentionPolicies>>();
  readonly labels = retentionLabels;
  readonly snapshot = computed(() => {
    const state = this.read.state();
    return state.kind === "ready" ? state.data.body : null;
  });
  readonly saving = signal(false);
  readonly problem = signal("");
  readonly receipt = signal("");
  readonly dirty = signal(false);
  readonly reviewing = signal(false);
  readonly editing = signal<Edit | null>(null);
  readonly visibleEdit = computed(() => {
    const edit = this.editing();
    const state = this.read.state();
    if (!edit || !this.contexts.isCurrent(edit.context)) return null;
    return state.kind === "ready" || (state.kind === "idle" && edit.used)
      ? edit
      : null;
  });
  readonly busy = computed(
    () =>
      this.saving() ||
      this.read.state().kind === "loading" ||
      this.read.refreshing(),
  );
  readonly canSave = computed(() => {
    const edit = this.editing();
    return (
      !this.busy() &&
      edit !== null &&
      !edit.used &&
      this.read.state().kind === "ready" &&
      this.contexts.canSend(edit.context)
    );
  });
  rows: Row[] = [];
  private owner: string | null = null;
  private generation = 0;

  constructor() {
    const lifetime = inject(DestroyRef);
    inject(UnsentWork).track(() => this.saving() || this.dirty(), lifetime);
    const invalidated = this.contexts.invalidated.subscribe(() => this.clear());
    lifetime.onDestroy(() => {
      this.clear();
      this.stop.complete();
      invalidated.unsubscribe();
    });
    effect(() => {
      const context = this.contexts.context();
      if (!context) return;
      const owner = catalogOwner(context);
      if (this.owner !== null && owner !== this.owner)
        untracked(() => this.clear());
      this.owner = owner;
    });
    // Each loaded policy opens for editing; after a save attempt the fresh version
    // keeps any entries that were not confirmed, so nothing is resent unseen.
    effect(() => {
      if (this.read.state().kind !== "ready") return;
      untracked(() => {
        const edit = this.editing();
        if (!edit || (edit.used && !this.dirty())) this.edit();
        else if (edit.used) this.rebase();
      });
    });
    this.load();
  }

  private rebase(): void {
    const state = this.read.state();
    if (state.kind !== "ready") return;
    this.reviewing.set(false);
    this.editing.set({
      context: state.context,
      version: state.data.body.version,
      used: false,
    });
  }

  load(keepMessages = false): void {
    if (this.saving()) return;
    if (!keepMessages) this.problem.set("");
    this.read.select(null, (context) => this.api.read(context));
  }

  edit(): void {
    const state = this.read.state();
    if (this.busy() || state.kind !== "ready") return;
    ++this.generation;
    this.stop.next();
    this.rows = state.data.body.policies.map((policy) => {
      const seconds = Number(policy.durationSeconds ?? 0);
      const days = seconds > 0 && seconds % 86400 === 0;
      return {
        family: policy.family as RetentionFamily,
        configured: policy.mode !== "notConfigured",
        mode: policy.mode as RetentionMode,
        amount:
          seconds > 0 ? String(days ? seconds / 86400 : seconds / 3600) : "",
        unit: days || seconds === 0 ? "days" : "hours",
        applyToExisting: false,
      };
    });
    this.dirty.set(false);
    this.reviewing.set(false);
    this.editing.set({
      context: state.context,
      version: state.data.body.version,
      used: false,
    });
  }

  /** The first step only validates and shows what saving will do. */
  review(): void {
    const body = this.body();
    if (!body) {
      this.problem.set(
        "Each expiring family needs a whole number of hours or days between 1 hour and 3650 days.",
      );
      return;
    }
    this.problem.set("");
    this.reviewing.set(true);
  }

  async save(): Promise<void> {
    const edit = this.editing();
    const body = this.body();
    if (!edit || !body || !this.reviewing() || !this.canSave()) return;
    const generation = ++this.generation;
    let reload = false;
    this.saving.set(true);
    this.problem.set("");
    this.receipt.set("");
    this.editing.set({ ...edit, used: true });
    try {
      const outcome = await firstValueFrom(
        this.api
          .write(edit.context, edit.version, body)
          .pipe(takeUntil(this.stop)),
      );
      if (
        generation !== this.generation ||
        !this.contexts.isCurrent(edit.context)
      )
        return;
      this.dirty.set(false);
      this.reviewing.set(false);
      this.receipt.set(
        outcome === "updated"
          ? "Retention policy saved."
          : "Retention policy was already unchanged.",
      );
      reload = true;
    } catch (error) {
      if (
        generation !== this.generation ||
        !this.contexts.isCurrent(edit.context)
      )
        return;
      const code = error instanceof AdministrationFailure ? error.code : "";
      if (code === "notFound") {
        this.clear();
        this.read.clear();
        this.problem.set(
          "Retention access is unavailable. Open this page again after administrator access is restored.",
        );
        return;
      }
      reload = true;
      this.problem.set(
        code === "preconditionFailed"
          ? "The policy changed after you read it. Your entries are kept against the current policy; review them before saving."
          : code === "conflict"
            ? "A configured family can't go back to not configured. Choose keep instead."
            : "The retention change was not confirmed. Your entries are kept against the current policy; review them before saving. Nothing is resent automatically.",
      );
    } finally {
      if (generation === this.generation) {
        this.saving.set(false);
        // The fresh version is read only once the save has settled.
        if (reload) this.load(true);
      }
    }
  }

  changed(): void {
    this.dirty.set(true);
    this.reviewing.set(false);
  }

  label(family: string): string {
    return retentionLabels[family as RetentionFamily] ?? family;
  }

  describe(row: Row): string {
    if (row.mode === "notConfigured") return "Not configured: data is kept";
    if (row.mode === "retain") return "Keep";
    return `Delete ${row.amount} ${row.unit} after it is created or finished${row.applyToExisting ? ", including existing data" : ", new data only"}`;
  }

  current(policy: RetentionPolicies["policies"][number]): string {
    if (policy.mode === "notConfigured") return "Not configured (kept)";
    if (policy.mode === "retain") return "Keep";
    const seconds = Number(policy.durationSeconds);
    return seconds % 86400 === 0
      ? `Delete after ${seconds / 86400} days`
      : `Delete after ${seconds / 3600} hours`;
  }

  existingRows(): Row[] {
    return this.rows.filter(
      (row) => row.mode === "expire" && row.applyToExisting,
    );
  }

  private body(): RetentionPolicyWrite[] | null {
    const body = this.rows.map((row, index) => {
      const amount = /^[1-9][0-9]{0,5}$/.test(row.amount)
        ? Number(row.amount)
        : 0;
      const seconds = String(amount * (row.unit === "days" ? 86400 : 3600));
      return {
        family: retentionFamilies[index],
        mode: row.mode,
        durationSeconds: row.mode === "expire" ? seconds : null,
        applyToExisting: row.mode === "expire" && row.applyToExisting,
      };
    });
    return body.every(
      (policy) =>
        policy.durationSeconds === null ||
        retentionSeconds(policy.durationSeconds),
    )
      ? body
      : null;
  }

  private clear(): void {
    ++this.generation;
    this.stop.next();
    this.saving.set(false);
    this.editing.set(null);
    this.reviewing.set(false);
    this.dirty.set(false);
    this.rows = [];
    this.problem.set("");
    this.receipt.set("");
  }
}
