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
import { UnsentWork } from "../workbench/unsent-work";
import { firstValueFrom, Subject, takeUntil } from "rxjs";
import { CatalogContextStore } from "../catalog/catalog-context";
import { createCatalogRead } from "../catalog/catalog-state";
import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
} from "../catalog/catalog-types";
import {
  AdministrationFailure,
  type AdministrationSnapshot,
} from "./administration-api";
import {
  AiQuotaApi,
  quotaCount,
  quotaPeriods,
  type AiQuotaSnapshot,
  type QuotaPeriod,
} from "./ai-quota-api";

type Edit = {
  context: CatalogContext;
  principal: string | null;
  period: QuotaPeriod;
  version: string;
  used: boolean;
};

@Component({
  selector: "braid-ai-quota-page",
  imports: [FormsModule],
  templateUrl: "./ai-quota-page.component.html",
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
        padding: 6px;
      }
      header,
      form,
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
      label {
        display: grid;
        gap: 4px;
        font-size: 0.8125rem;
      }
      input {
        min-width: 0;
        max-width: 100%;
      }
      p {
        font-size: 0.8125rem;
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
      @media (min-width: 641px) {
        table button {
          min-height: 24px;
          height: 24px;
          padding: 0 6px;
          line-height: 22px;
        }
      }
    `,
  ],
})
export class AiQuotaPageComponent {
  private readonly api = inject(AiQuotaApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly stop = new Subject<void>();
  readonly read = createCatalogRead<AdministrationSnapshot<AiQuotaSnapshot>>();
  readonly periods = quotaPeriods;
  readonly snapshot = computed(() => {
    const state = this.read.state();
    return state.kind === "ready" ? state.data.body : null;
  });
  readonly saving = signal(false);
  readonly problem = signal("");
  readonly receipt = signal("");
  readonly dirty = signal(false);
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
    const state = this.read.state();
    return (
      !this.busy() &&
      edit !== null &&
      !edit.used &&
      state.kind === "ready" &&
      state.data.body.principalId === edit.principal &&
      this.contexts.canSend(edit.context)
    );
  });
  principal = "";
  maximumTurns = "";
  maximumTokens = "";
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
        untracked(() => {
          this.clear();
          this.principal = "";
        });
      this.owner = owner;
    });
    this.load();
  }

  scopeChanged(): void {
    this.clear();
    this.read.clear();
  }
  load(): void {
    if (this.saving()) return;
    const principal = this.principal || null;
    if (principal !== null && !isCatalogId(principal)) {
      this.problem.set(
        "Enter an exact principal UUID, or leave it empty for the organization.",
      );
      return;
    }
    this.problem.set("");
    this.read.select(null, (context) => this.api.read(context, principal));
  }
  edit(period: QuotaPeriod): void {
    const state = this.read.state();
    if (this.busy() || state.kind !== "ready") return;
    ++this.generation;
    this.stop.next();
    const current = state.data.body.limits.find(
      (limit) => limit.period === period,
    );
    this.maximumTurns = current?.maximumTurns ?? "";
    this.maximumTokens = current?.maximumTokens ?? "";
    this.dirty.set(false);
    this.editing.set({
      context: state.context,
      principal: state.data.body.principalId,
      period,
      version: state.data.body.version,
      used: false,
    });
    this.problem.set("");
    this.receipt.set("");
  }
  async save(): Promise<void> {
    const edit = this.editing();
    if (!edit || !this.canSave()) return;
    if (
      (this.maximumTurns !== "" && !quotaCount(this.maximumTurns)) ||
      (this.maximumTokens !== "" && !quotaCount(this.maximumTokens))
    ) {
      this.problem.set(
        "Limits must be positive whole numbers no greater than 9223372036854775807. Leave a limit empty to remove it.",
      );
      return;
    }
    const generation = ++this.generation;
    const body = {
      maximumTurns: this.maximumTurns || null,
      maximumTokens: this.maximumTokens || null,
    };
    this.saving.set(true);
    this.problem.set("");
    this.receipt.set("");
    this.editing.set({ ...edit, used: true });
    try {
      const outcome = await firstValueFrom(
        this.api
          .write(edit.context, edit.principal, edit.period, edit.version, body)
          .pipe(takeUntil(this.stop)),
      );
      if (
        generation !== this.generation ||
        !this.contexts.isCurrent(edit.context)
      )
        return;
      this.dirty.set(false);
      this.receipt.set(
        outcome === "updated"
          ? "Quota policy saved. Load current policy before another edit."
          : "Quota policy was already unchanged. Load current policy before another edit.",
      );
      this.read.clear();
    } catch (error) {
      if (
        generation !== this.generation ||
        !this.contexts.isCurrent(edit.context)
      )
        return;
      if (error instanceof AdministrationFailure && error.code === "notFound") {
        this.clear();
        this.read.clear();
        this.problem.set(
          "Quota access is unavailable. Load current policy after administrator access is restored.",
        );
        return;
      }
      this.problem.set(
        error instanceof AdministrationFailure &&
          error.code === "preconditionFailed"
          ? "The policy changed after you read it. Your entered limits are retained. Load current policy and review before another edit."
          : "The quota write was not confirmed. Your entered limits are retained. Load current policy before another edit; nothing is resent automatically.",
      );
      this.read.clear();
    } finally {
      if (generation === this.generation) this.saving.set(false);
    }
  }
  private clear(): void {
    ++this.generation;
    this.stop.next();
    this.saving.set(false);
    this.editing.set(null);
    this.dirty.set(false);
    this.maximumTurns = this.maximumTokens = "";
    this.problem.set("");
    this.receipt.set("");
  }
  limit(period: QuotaPeriod, field: "maximumTurns" | "maximumTokens"): string {
    return (
      this.snapshot()?.limits.find((limit) => limit.period === period)?.[
        field
      ] ?? "Uncapped"
    );
  }
}
