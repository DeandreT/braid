import { DOCUMENT, DatePipe } from "@angular/common";
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
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { firstValueFrom, takeUntil } from "rxjs";
import { CatalogContextStore } from "../catalog/catalog-context";
import { catalogOwner, type CatalogContext } from "../catalog/catalog-types";
import { BrowserSessionState } from "../auth/browser-session-state";
import { AuditApi, type AuditEntry, type AuditPage } from "./audit-api";

@Component({
  selector: "braid-audit-page",
  imports: [FormsModule, DatePipe],
  templateUrl: "./audit-page.component.html",
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
        margin-block-end: 8px;
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
        max-width: 100%;
        min-width: 0;
      }
      .table-scroll {
        overflow: auto;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 0.8125rem;
      }
      th,
      td {
        padding: 0 6px;
        height: 28px;
        line-height: 28px;
        white-space: nowrap;
        text-align: left;
        border-bottom: 1px solid var(--wb-border);
      }
      td {
        max-width: 20rem;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      p {
        font-size: 0.8125rem;
      }
    `,
  ],
})
export class AuditPageComponent {
  private readonly api = inject(AuditApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly sessions = inject(BrowserSessionState);
  private readonly document = inject(DOCUMENT);
  private readonly destroy = inject(DestroyRef);
  readonly page = signal<AuditPage | null>(null);
  private readonly owner = signal<CatalogContext | null>(null);
  readonly presented = computed(
    () =>
      this.contexts.isCurrent(this.owner()) &&
      this.contexts.canPresent(this.owner()),
  );
  readonly busy = signal(false);
  readonly error = signal("");
  readonly filtersDirty = signal(false);
  readonly kinds = [
    "administration",
    "release",
    "build",
    "approval",
    "request",
    "payload",
    "grant",
    "invocation",
  ];
  projectId = "";
  actorId = "";
  kind = "";
  since = "";
  until = "";
  private query: Record<string, string> = {};
  private generation = 0;

  constructor() {
    this.contexts.invalidated.pipe(takeUntilDestroyed()).subscribe(() => {
      ++this.generation;
      this.page.set(null);
      this.owner.set(null);
      this.projectId = this.actorId = this.kind = this.since = this.until = "";
      this.busy.set(false);
      this.error.set("");
    });
    this.destroy.onDestroy(() => ++this.generation);
    let focusTimer: ReturnType<typeof setTimeout> | null = null;
    const refreshIdentity = () => {
      if (this.document.visibilityState !== "visible" || focusTimer !== null)
        return;
      focusTimer = setTimeout(() => {
        focusTimer = null;
        if (this.document.visibilityState === "visible") void this.revalidate();
      }, 100);
    };
    this.document.defaultView?.addEventListener("focus", refreshIdentity);
    this.document.addEventListener("visibilitychange", refreshIdentity);
    this.destroy.onDestroy(() => {
      this.document.defaultView?.removeEventListener("focus", refreshIdentity);
      this.document.removeEventListener("visibilitychange", refreshIdentity);
      if (focusTimer !== null) clearTimeout(focusTimer);
    });
    effect(() => {
      const current = this.contexts.context();
      const owner = this.owner();
      if (current && owner && catalogOwner(current) !== catalogOwner(owner))
        untracked(() => {
          ++this.generation;
          this.page.set(null);
          this.owner.set(null);
          this.busy.set(false);
          this.projectId =
            this.actorId =
            this.kind =
            this.since =
            this.until =
              "";
        });
    });
    void this.load();
  }

  private async revalidate(): Promise<void> {
    const generation = this.generation;
    const query = this.page() ? { ...this.query } : null;
    try {
      const context = await firstValueFrom(
        this.contexts.refresh().pipe(takeUntilDestroyed(this.destroy)),
      );
      if (!query || generation !== this.generation) return;
      const page = await firstValueFrom(
        this.api.read(context, query).pipe(takeUntilDestroyed(this.destroy)),
      );
      if (generation !== this.generation) return;
      this.owner.set(context);
      this.page.set(page);
    } catch {
      if (generation !== this.generation) return;
      ++this.generation;
      this.page.set(null);
      this.owner.set(null);
      this.busy.set(false);
      this.error.set(
        "Audit access is unavailable. Refresh your access before continuing.",
      );
    }
  }

  async load(next = false): Promise<void> {
    if (
      this.busy() ||
      (next &&
        (!this.page()?.nextCursor || this.filtersDirty() || !this.presented()))
    )
      return;
    const generation = ++this.generation;
    this.busy.set(true);
    this.error.set("");
    const query = next
      ? { ...this.query, cursor: this.page()?.nextCursor ?? "" }
      : this.filters();
    try {
      const context = await firstValueFrom(this.contexts.refresh());
      if (generation !== this.generation) return;
      const page = await firstValueFrom(
        this.api
          .read(context, query)
          .pipe(takeUntil(this.contexts.invalidated)),
      );
      if (generation !== this.generation) return;
      this.query = { ...query, since: page.since, until: page.until };
      this.page.set(null);
      this.owner.set(context);
      this.page.set(page);
      this.filtersDirty.set(false);
    } catch {
      if (generation === this.generation) {
        this.page.set(null);
        this.error.set(
          "Audit access is unavailable. Organization administrators and administrators of the selected project can read this view.",
        );
      }
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }

  async exportCsv(): Promise<void> {
    if (this.busy() || !this.page() || !this.presented() || this.filtersDirty())
      return;
    const originalOwner = this.owner()!;
    const generation = ++this.generation;
    this.busy.set(true);
    this.error.set("");
    try {
      const context = await firstValueFrom(this.contexts.refresh());
      if (
        generation !== this.generation ||
        catalogOwner(context) !== catalogOwner(originalOwner)
      )
        return;
      const query = { ...this.query };
      delete query["cursor"];
      const rows: AuditEntry[] = [];
      let bytes = 0;
      const seen = new Set<string>();
      for (let count = 0; count < 100; count++) {
        if (generation !== this.generation) return;
        const page = await firstValueFrom(
          this.api.read(context, { ...query, limit: "100" }),
        );
        rows.push(...page.entries);
        bytes += new TextEncoder().encode(JSON.stringify(page.entries)).length;
        if (bytes > 8 * 1024 * 1024)
          throw new Error("Audit export exceeded its byte bound.");
        if (!page.nextCursor) {
          if (
            generation !== this.generation ||
            !this.contexts.isCurrent(context) ||
            !this.contexts.canPresent(context) ||
            !this.sessions.presents(context)
          )
            return;
          const header =
            "eventId,occurredAtUtc,kind,action,actorKind,actorId,projectId,mappingId,resourceId,outcome,historicalObservation\r\n";
          const csv =
            header +
            rows
              .map((e) =>
                [
                  e.eventId,
                  e.occurredAtUtc,
                  e.kind,
                  e.action,
                  e.actorKind,
                  e.actorId ?? "",
                  e.projectId ?? "",
                  e.mappingId ?? "",
                  e.resourceId,
                  e.outcome,
                  String(e.historicalObservation),
                ]
                  .map(
                    (value) =>
                      `"${(/^[=+\-@\t\r\n]/.test(value) ? "'" : "") + value.replaceAll('"', '""')}"`,
                  )
                  .join(","),
              )
              .join("\r\n") +
            (rows.length ? "\r\n" : "");
          const url = URL.createObjectURL(
            new Blob([csv], { type: "text/csv;charset=utf-8" }),
          );
          try {
            const link = this.document.createElement("a");
            link.href = url;
            link.download = "braid-audit.csv";
            link.click();
          } finally {
            setTimeout(() => URL.revokeObjectURL(url), 0);
          }
          return;
        }
        if (seen.has(page.nextCursor))
          throw new Error("Repeated audit cursor.");
        seen.add(page.nextCursor);
        query["cursor"] = page.nextCursor;
      }
      throw new Error(
        "Narrow the audit filters before exporting more than 10,000 events.",
      );
    } catch {
      if (generation === this.generation)
        this.error.set(
          "Audit export is unavailable or too large. Narrow the filters and try again.",
        );
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }

  private filters(): Record<string, string> {
    return Object.fromEntries(
      Object.entries({
        projectId: this.projectId,
        actorId: this.actorId,
        kind: this.kind,
        since: this.since,
        until: this.until,
        limit: "50",
      }).filter(([, value]) => value !== ""),
    );
  }
}
