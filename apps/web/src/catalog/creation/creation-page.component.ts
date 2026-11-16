import { DOCUMENT } from "@angular/common";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { Component, DestroyRef, computed, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { combineLatest, firstValueFrom, of, timeout } from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
} from "../catalog-types";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  base64,
  creationOutput,
  creationProposal,
  creationIdentity,
  creationStateFollows,
  digest,
  sha256,
  type CreationOutput,
  type CreationProposal,
  type CreationSelection,
} from "./creation-model";

@Component({
  selector: "braid-creation-page",
  imports: [FormsModule, RouterLink],
  templateUrl: "./creation-page.component.html",
  styleUrl: "./creation-page.component.css",
})
export class CreationPageComponent {
  readonly contexts = inject(CatalogContextStore);
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  readonly context = signal<CatalogContext | null>(null);
  readonly proposal = signal<CreationProposal | null>(null);
  readonly output = signal<CreationOutput | null>(null);
  readonly sample = signal("");
  readonly file = signal<File | null>(null);
  readonly busy = signal(false);
  readonly message = signal("");
  readonly accepted = signal<ReadonlySet<string>>(new Set());
  readonly reviewed = signal(false);
  readonly uncertain = signal(false);
  readonly reconciled = signal(false);
  readonly retryable = computed(() => {
    const p = this.proposal();
    return (
      !!p?.approved &&
      this.reconciled() &&
      (!p.receipt || p.receipt.phase === "Reserved")
    );
  });
  readonly visible = computed(
    () =>
      this.contexts.isCurrent(this.context()) &&
      this.contexts.canPresent(this.context()),
  );
  readonly dirty = computed(
    () => !!this.sample() || !!this.file() || this.busy() || this.uncertain(),
  );
  readonly canApprove = computed(() => {
    const p = this.proposal();
    return (
      !!p &&
      ((!p.approved && !p.receipt) || this.retryable()) &&
      !this.uncertain() &&
      this.reviewed() &&
      !p.questions.some((q) => q.critical) &&
      p.assumptions
        .filter((a) => a.critical)
        .every((a) => this.accepted().has(a.id))
    );
  });
  organization = "";
  selection: CreationSelection = { project: "", proposal: "", fingerprint: "" };
  private generation = 0;
  private owner = "";
  private alive = true;
  // Keep only a digest of private review facts when visible content is cleared.
  private baseline: {
    identity: string;
    approved: boolean;
    receipt: CreationProposal["receipt"];
  } | null = null;
  get projectLink() {
    return [
      "/catalog/organizations",
      this.organization,
      "projects",
      this.selection.project,
    ];
  }
  evidenceLink(bundle: string) {
    return [...this.projectLink, "specs", bundle];
  }
  get feedbackParams() {
    return {
      creationProjectId: this.selection.project,
      creationProposalId: this.selection.proposal,
      creationFingerprint: this.selection.fingerprint,
    };
  }
  get libraryLink() {
    return [...this.projectLink, "creation"];
  }
  revisionLink(mapping: string | null, revision: string | null) {
    return [...this.projectLink, "mappings", mapping, "revisions", revision];
  }
  private get path() {
    return `/api/v1/organizations/${this.organization}/projects/${this.selection.project}/creation-proposals/${this.selection.proposal}`;
  }
  constructor() {
    const destroy = inject(DestroyRef);
    inject(UnsentWork).track(() => this.dirty(), destroy);
    const invalidated = this.contexts.invalidated.subscribe(() => {
      this.clear();
      this.message.set(
        "Identity changed or access needs checking. Refresh to read the exact proposal again; a sent save may have committed.",
      );
    });
    const route = inject(ActivatedRoute);
    const subscription = combineLatest([
      route.paramMap,
      route.queryParamMap,
    ]).subscribe(([params, query]) => {
      this.clear();
      this.organization = params.get("organizationId") ?? "";
      this.selection = {
        project: params.get("projectId") ?? "",
        proposal: params.get("proposalId") ?? "",
        fingerprint: query.get("fingerprint") ?? "",
      };
      if (query.getAll("fingerprint").length !== 1)
        this.selection.fingerprint = "";
      void this.refresh();
    });
    const unload = (event: BeforeUnloadEvent) => {
      if (this.dirty()) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    this.document.defaultView?.addEventListener("beforeunload", unload);
    destroy.onDestroy(() => {
      this.alive = false;
      this.clear();
      invalidated.unsubscribe();
      subscription.unsubscribe();
      this.document.defaultView?.removeEventListener("beforeunload", unload);
    });
  }
  requestLeave() {
    return of(
      !this.dirty() ||
        (this.document.defaultView?.confirm(
          "Leave mapping review? In-tab sample data will be lost. A sent save may have committed; reopen this exact proposal to recover its status.",
        ) ??
          false),
    );
  }
  private clear() {
    ++this.generation;
    this.busy.set(false);
    this.proposal.set(null);
    this.output.set(null);
    this.sample.set("");
    this.file.set(null);
    this.accepted.set(new Set());
    this.reviewed.set(false);
    this.uncertain.set(false);
    this.reconciled.set(false);
    this.baseline = null;
  }
  private valid() {
    return (
      isCatalogId(this.organization) &&
      isCatalogId(this.selection.project) &&
      isCatalogId(this.selection.proposal) &&
      digest(this.selection.fingerprint)
    );
  }
  private current(generation: number, context: CatalogContext) {
    return (
      this.alive &&
      generation === this.generation &&
      this.contexts.isCurrent(context) &&
      this.contexts.canPresent(context) &&
      catalogOwner(context) === this.owner
    );
  }
  private async admit(generation: number) {
    const context = await firstValueFrom(this.contexts.refresh());
    if (
      generation !== this.generation ||
      context.allowedOrganizationId !== this.organization ||
      !this.contexts.canSend(context)
    )
      throw new Error("Stale context");
    if (this.owner && this.owner !== catalogOwner(context)) {
      this.clear();
      this.owner = catalogOwner(context);
      this.context.set(context);
      throw new Error("Owner changed");
    }
    this.owner = catalogOwner(context);
    this.context.set(context);
    return context;
  }
  private fail(error: unknown) {
    const status = error instanceof HttpErrorResponse ? error.status : null;
    if ([401, 403, 404, 409].includes(status ?? 0)) {
      this.proposal.set(null);
      this.output.set(null);
      this.accepted.set(new Set());
      this.reviewed.set(false);
      this.sample.set("");
      this.file.set(null);
    }
    if (this.contexts.isCurrent(this.context())) this.contexts.reject(status);
    this.message.set(
      status === 503
        ? "Mapping creation is unavailable on this server. A sent save may have committed; check its status before continuing."
        : status === 409
          ? "The exact proposal or its source evidence changed. Return to the conversation and review a new proposal."
          : [401, 403, 404].includes(status ?? 0)
            ? "Proposal unavailable or access denied."
            : "The response could not be verified. A sent save may have committed; check its status before continuing.",
    );
  }
  private async observe(
    p: CreationProposal,
    generation: number,
    context: CatalogContext,
  ): Promise<boolean> {
    const identity = await sha256(
      new TextEncoder().encode(creationIdentity(p)),
    );
    if (!this.current(generation, context)) return false;
    if (
      this.baseline &&
      (this.baseline.identity !== identity ||
        !creationStateFollows(this.baseline, p))
    )
      throw new Error("Substituted proposal or receipt");
    this.baseline = {
      identity,
      approved: p.approved,
      receipt: p.receipt ? { ...p.receipt } : null,
    };
    this.proposal.set(p);
    return true;
  }
  async refresh() {
    if (this.busy()) return;
    if (!this.valid()) {
      this.clear();
      this.message.set(
        "Open an exact proposal link from the creation conversation.",
      );
      return;
    }
    const generation = ++this.generation;
    this.busy.set(true);
    this.output.set(null);
    this.accepted.set(new Set());
    this.reviewed.set(false);
    this.message.set("");
    this.proposal.set(null);
    try {
      const context = await this.admit(generation);
      const raw = await firstValueFrom(
        this.http
          .get<unknown>(
            `${this.path}?expectedFingerprint=${this.selection.fingerprint}`,
          )
          .pipe(timeout(65000)),
      );
      const p = await creationProposal(raw, this.selection);
      if (!(await this.observe(p, generation, context))) return;
      // A read alone does not resolve a lost save response. Recovery reconciles its stable key.
      this.message.set(
        p.receipt?.phase === "Committed"
          ? "Mapping saved."
          : p.approved
            ? "Approval is retained. Check save status to reconcile the original operation."
            : "Review this exact mapping and its assumptions before approving.",
      );
    } catch (error) {
      if (generation === this.generation) this.fail(error);
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
  accept(id: string, value: boolean) {
    const next = new Set(this.accepted());
    if (value) next.add(id);
    else next.delete(id);
    this.accepted.set(next);
  }
  setSample(value: string) {
    this.sample.set(value);
    this.file.set(null);
    this.output.set(null);
  }
  chooseFile(event: Event) {
    this.file.set((event.target as HTMLInputElement).files?.[0] ?? null);
    this.sample.set("");
    this.output.set(null);
  }
  async act(action: "approve" | "recover" | "preview") {
    const proposal = this.proposal();
    if (
      !proposal ||
      this.busy() ||
      !this.visible() ||
      (action === "approve" && !this.canApprove())
    )
      return;
    const generation = ++this.generation;
    this.busy.set(true);
    this.message.set("");
    if (action === "preview") this.output.set(null);
    let dispatched = false;
    try {
      let sampleDigest = "";
      let body: object = { fingerprint: proposal.fingerprint };
      if (action === "approve")
        body = {
          fingerprint: proposal.fingerprint,
          acceptedCriticalAssumptions: proposal.assumptions
            .filter((a) => a.critical && this.accepted().has(a.id))
            .map((a) => a.id)
            .sort(),
        };
      if (action === "preview") {
        const file = this.file();
        if ((file?.size ?? this.sample().length) > 1048576)
          throw new Error("Sample exceeds limit");
        const bytes = file
          ? new Uint8Array(await file.arrayBuffer())
          : new TextEncoder().encode(this.sample());
        if (bytes.length > 1048576) throw new Error("Sample exceeds limit");
        sampleDigest = await sha256(bytes);
        body = {
          fingerprint: proposal.fingerprint,
          sampleDigest,
          sampleBase64: base64(bytes),
        };
      }
      const context = await this.admit(generation);
      if (!this.current(generation, context) || this.proposal() !== proposal)
        return;
      dispatched = true;
      if (action === "approve") this.uncertain.set(true);
      const raw = await firstValueFrom(
        this.http
          .post<unknown>(`${this.path}/${action}`, body, {
            headers: context.requestToken
              ? { "X-XSRF-TOKEN": context.requestToken }
              : {},
          })
          .pipe(timeout(65000)),
      );
      if (action === "preview") {
        const output = await creationOutput(raw, proposal, sampleDigest);
        if (!this.current(generation, context)) return;
        this.output.set(output);
        this.message.set(
          output
            ? "Sample executed against this exact proposal."
            : "Sample execution failed. Review the input and mapping requirements.",
        );
      } else {
        const result = await creationProposal(raw, this.selection);
        if (!(await this.observe(result, generation, context))) return;
        this.uncertain.set(false);
        this.reconciled.set(action === "recover");
        if (action === "recover") {
          this.reviewed.set(false);
          this.accepted.set(new Set());
        }
        this.message.set(
          result.receipt?.phase === "Committed"
            ? "Mapping saved."
            : result.approved
              ? "Approval is retained. Check save status to reconcile the original operation."
              : "No retained approval or save was found. Review and approve this exact proposal to save it.",
        );
      }
    } catch (error) {
      if (generation === this.generation) {
        if (dispatched && action === "approve") this.uncertain.set(true);
        this.fail(error);
      }
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
}
