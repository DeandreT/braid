import { DatePipe } from "@angular/common";
import {
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { FormsModule } from "@angular/forms";
import { createCatalogRead } from "../catalog/catalog-state";
import { CatalogContextStore } from "../catalog/catalog-context";
import { catalogOwner } from "../catalog/catalog-types";
import {
  AiUsageApi,
  isUsageCount,
  isUsageCursor,
  type AiUsageGroup,
  type AiUsagePage,
} from "./ai-usage-api";

@Component({
  selector: "braid-ai-usage-page",
  imports: [DatePipe, FormsModule],
  templateUrl: "./ai-usage-page.component.html",
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
        padding: 6px;
      }
      header,
      form,
      nav {
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
      label {
        display: grid;
        gap: 4px;
        font-size: 0.8125rem;
      }
      input,
      select {
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
      .number {
        text-align: right;
        font-variant-numeric: tabular-nums;
      }
    `,
  ],
})
export class AiUsagePageComponent {
  private readonly api = inject(AiUsageApi);
  private readonly contexts = inject(CatalogContextStore);
  readonly read = createCatalogRead<AiUsagePage>({
    cursorValidator: isUsageCursor,
  });
  readonly page = computed(() => {
    const state = this.read.state();
    return state.kind === "ready" ? state.data : null;
  });
  readonly busy = computed(
    () => this.read.state().kind === "loading" || this.read.refreshing(),
  );
  readonly dirty = signal(false);
  projectId = "";
  principalId = "";
  profileId = "";
  profileVersion = "";
  groupBy = "organization";
  since = "";
  until = "";
  private query: Record<string, string> = {};
  private appliedGrouping = "organization";
  private owner: string | null = null;
  private readonly number = new Intl.NumberFormat(undefined, {
    maximumFractionDigits: 0,
  });

  constructor() {
    effect(() => {
      const context = this.contexts.context();
      if (!context) return;
      const owner = catalogOwner(context);
      if (this.owner !== null && this.owner !== owner)
        untracked(() => {
          this.read.clear();
          this.query = {};
          this.projectId =
            this.principalId =
            this.profileId =
            this.profileVersion =
            this.since =
            this.until =
              "";
          this.groupBy = this.appliedGrouping = "organization";
          this.dirty.set(false);
        });
      this.owner = owner;
    });
    this.apply();
  }

  apply(): void {
    if (this.busy()) return;
    this.query = Object.fromEntries(
      Object.entries({
        projectId: this.projectId,
        principalId: this.principalId,
        profileId: this.profileId,
        profileVersion: this.profileVersion,
        groupBy: this.groupBy,
        since: this.since,
        until: this.until,
        limit: "50",
      }).filter(([, value]) => value !== ""),
    );
    this.appliedGrouping = this.groupBy;
    this.dirty.set(false);
    const query = { ...this.query };
    this.read.select(null, (context) => this.api.read(context, query));
  }

  next(): void {
    const page = this.page();
    if (!page?.nextCursor || this.busy() || this.dirty()) return;
    this.query = {
      ...this.query,
      since: page.since,
      until: page.until,
      cursor: page.nextCursor,
    };
    const query = { ...this.query };
    this.read.select(
      null,
      (context) => this.api.read(context, query),
      page.nextCursor,
    );
  }

  count(value: string | null): string {
    return value !== null && isUsageCount(value)
      ? this.number.format(BigInt(value))
      : "Unknown";
  }

  tokenHint(value: string | null, known: string, missing: string): string {
    return value === null
      ? `Unknown total; known subtotal ${this.count(known)}; ${this.count(missing)} unreported rounds`
      : this.count(value);
  }

  group(row: AiUsageGroup): string {
    return this.appliedGrouping === "project"
      ? (row.projectId ?? "Organization-wide turns")
      : this.appliedGrouping === "principal"
        ? (row.principalId ?? "Unknown principal")
        : this.appliedGrouping === "modelProfile"
          ? `${row.profileId} v${row.profileVersion}`
          : row.organizationId;
  }
}
