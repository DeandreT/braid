import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { Component, DestroyRef, computed, inject, signal } from "@angular/core";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { firstValueFrom, timeout } from "rxjs";
import { CatalogApi } from "../catalog-api";
import { CatalogContextStore } from "../catalog-context";
import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
} from "../catalog-types";
import {
  creationCapability,
  creationPage,
  type CreationLink,
} from "./creation-library-model";

@Component({
  selector: "braid-creation-library",
  imports: [RouterLink],
  template: `<header class="catalog-heading">
      <div>
        <h1>Your new mapping proposals</h1>
        <p>
          Private retained proposals for this project. Opening a proposal does
          not approve it.
        </p>
      </div>
      <button type="button" [disabled]="busy()" (click)="refresh()">
        Refresh proposals and access
      </button>
    </header>
    <p role="status" aria-live="polite">{{ message() }}</p>
    @if (visible()) {
      @if (enabled()) {
        <ul aria-label="Retained new mapping proposals">
          @for (proposal of proposals(); track proposal.proposalId) {
            <li>
              <a
                [routerLink]="[
                  '/catalog/organizations',
                  organization,
                  'projects',
                  project,
                  'creation',
                  proposal.proposalId,
                ]"
                [queryParams]="{ fingerprint: proposal.fingerprint }"
                >Review proposal {{ proposal.proposalId }}</a
              >
              <p>
                Exact fingerprint <code>{{ proposal.fingerprint }}</code>
              </p>
            </li>
          }
        </ul>
        @if (!proposals().length && !busy()) {
          <p>
            No retained proposals were found. Ask chat to create a new mapping
            from your specification documents.
          </p>
        }
        @if (next()) {
          <button type="button" [disabled]="busy()" (click)="more()">
            Load more proposals
          </button>
        }
      }
      <a
        [routerLink]="[
          '/catalog/organizations',
          organization,
          'projects',
          project,
        ]"
        >Back to project</a
      >
    }`,
})
export class CreationLibraryComponent {
  readonly contexts = inject(CatalogContextStore);
  private readonly api = inject(CatalogApi);
  private readonly http = inject(HttpClient);
  readonly context = signal<CatalogContext | null>(null);
  readonly proposals = signal<CreationLink[]>([]);
  readonly next = signal<string | null>(null);
  readonly enabled = signal(false);
  readonly busy = signal(false);
  readonly message = signal("");
  readonly visible = computed(
    () =>
      this.contexts.isCurrent(this.context()) &&
      this.contexts.canPresent(this.context()),
  );
  organization = "";
  project = "";
  private generation = 0;
  private alive = true;
  private owner = "";
  private readonly facts = new Map<string, string>();
  constructor() {
    const invalidated = this.contexts.invalidated.subscribe(() => {
      this.clear();
      this.message.set(
        "Identity or content access changed. Refresh to read proposals again.",
      );
    });
    const route = inject(ActivatedRoute).paramMap.subscribe((params) => {
      this.clear();
      this.facts.clear();
      this.owner = "";
      this.organization = params.get("organizationId") ?? "";
      this.project = params.get("projectId") ?? "";
      void this.refresh();
    });
    inject(DestroyRef).onDestroy(() => {
      this.alive = false;
      this.clear();
      invalidated.unsubscribe();
      route.unsubscribe();
    });
  }
  private clear() {
    ++this.generation;
    this.proposals.set([]);
    this.next.set(null);
    this.enabled.set(false);
    this.context.set(null);
    this.busy.set(false);
  }
  async refresh() {
    await this.load(false);
  }
  async more() {
    if (this.next()) await this.load(true);
  }
  private async load(append: boolean) {
    if (this.busy()) return;
    if (![this.organization, this.project].every(isCatalogId)) {
      this.message.set("Invalid project link.");
      return;
    }
    const generation = ++this.generation,
      after = append ? this.next() : null;
    this.busy.set(true);
    try {
      const context = await firstValueFrom(this.contexts.refresh());
      if (!this.alive || generation !== this.generation) return;
      if (context.allowedOrganizationId !== this.organization)
        throw new HttpErrorResponse({ status: 404 });
      const owner = catalogOwner(context);
      if (this.owner && owner !== this.owner) {
        this.facts.clear();
        this.proposals.set([]);
        this.next.set(null);
        if (append) throw new Error("Owner changed");
      }
      this.owner = owner;
      this.context.set(context);
      const current = () =>
        this.alive &&
        generation === this.generation &&
        this.contexts.isCurrent(context);
      const scope = {
        organizationId: this.organization,
        projectId: this.project,
      };
      await firstValueFrom(this.api.project(scope));
      if (!current()) return;
      const root = `/api/v1/organizations/${this.organization}/projects/${this.project}`;
      const available = creationCapability(
        await firstValueFrom(
          this.http
            .get<unknown>(root + "/creation-capabilities")
            .pipe(timeout(30000)),
        ),
      );
      if (!current()) return;
      this.enabled.set(available);
      if (!available) {
        this.proposals.set([]);
        this.next.set(null);
        this.message.set(
          "Mapping creation is not configured for this project.",
        );
        return;
      }
      const response = await firstValueFrom(
        this.http
          .get<unknown>(root + "/creation-proposals", {
            params: after
              ? { afterProposal: after, limit: "20" }
              : { limit: "20" },
          })
          .pipe(timeout(30000)),
      );
      const page = creationPage(
        response,
        this.organization,
        this.project,
        after,
      );
      if (!current()) return;
      for (const row of page.proposals) {
        const prior = this.facts.get(row.proposalId);
        if (prior && prior !== row.fingerprint)
          throw new Error("Proposal facts changed");
      }
      const existing = append ? this.proposals() : [];
      if (
        page.proposals.some((p) =>
          existing.some((e) => e.proposalId === p.proposalId),
        )
      )
        throw new Error("Repeated proposal");
      for (const row of page.proposals)
        this.facts.set(row.proposalId, row.fingerprint);
      this.proposals.set([...existing, ...page.proposals]);
      this.next.set(
        existing.length + page.proposals.length < 200
          ? page.afterProposal
          : null,
      );
      this.message.set(
        "Proposals refreshed under current access. Every proposal requires its own exact approval.",
      );
    } catch (error) {
      if (generation !== this.generation || !this.alive) return;
      this.proposals.set([]);
      this.next.set(null);
      this.enabled.set(false);
      if (
        error instanceof HttpErrorResponse &&
        [401, 403, 404].includes(error.status) &&
        this.contexts.isCurrent(this.context())
      )
        this.contexts.reject(error.status);
      this.message.set(
        "Proposals could not be read under current access. Refresh to retry.",
      );
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
}
