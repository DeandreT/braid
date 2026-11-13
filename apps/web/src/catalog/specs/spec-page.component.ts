import { DOCUMENT } from "@angular/common";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { Component, DestroyRef, computed, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";
import { ActivatedRoute, Router, RouterLink } from "@angular/router";
import { firstValueFrom, of, timeout } from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import { CatalogApi } from "../catalog-api";
import {
  catalogOwner,
  CatalogReadError,
  isCatalogId,
  type CatalogContext,
} from "../catalog-types";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  capabilities,
  formatLabels,
  type IntakeCapabilities,
} from "./spec-capabilities";
import {
  readEvidence,
  inventory,
  outcome,
  recovery,
  type Evidence,
  type Inventory,
  type SpecObject,
  type Upload,
  type Status,
  type Receipt,
} from "./spec-model";

type Intent =
  | { kind: "upload"; upload: Upload; version: number; body: File | string }
  | { kind: "delete"; key: string; version: number; suffix: string }
  | { kind: "create" };
@Component({
  selector: "weft-spec-page",
  imports: [FormsModule, RouterLink],
  templateUrl: "./spec-page.component.html",
  styleUrl: "./spec-page.component.css",
})
export class SpecPageComponent {
  readonly contexts = inject(CatalogContextStore);
  private readonly http = inject(HttpClient);
  private readonly catalog = inject(CatalogApi);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  readonly context = signal<CatalogContext | null>(null);
  readonly data = signal<Inventory | null>(null);
  readonly chunks = signal<Evidence | null>(null);
  readonly selected = signal<SpecObject | null>(null);
  readonly busy = signal(false);
  readonly reading = signal(false);
  readonly message = signal("");
  readonly projectName = signal("Specifications");
  readonly intent = signal<Intent | null>(null);
  readonly draft = signal("");
  readonly file = signal<File | null>(null);
  readonly intake = signal<IntakeCapabilities | null>(null);
  readonly formatLabels = formatLabels;
  readonly fileAccept = computed(() =>
    (this.intake()?.formats ?? [])
      .map((f) =>
        f.format === "text"
          ? ".txt"
          : f.format === "openapi-json"
            ? ".json"
            : `.${f.format}`,
      )
      .join(","),
  );
  readonly outcome = outcome;
  readonly visible = computed(
    () =>
      this.contexts.isCurrent(this.context()) &&
      this.contexts.canPresent(this.context()),
  );
  readonly dirty = computed(
    () => !!this.draft() || !!this.file() || !!this.intent() || this.busy(),
  );
  organization = "";
  project = "";
  bundle = "";
  format = "text";
  private owner = "";
  private generation = 0;
  private alive = true;
  get projectLink() {
    return [
      "/catalog/organizations",
      this.organization,
      "projects",
      this.project,
    ];
  }
  private get path() {
    return `/api/v1/organizations/${this.organization}/projects/${this.project}/spec-bundles/${this.bundle}`;
  }
  constructor() {
    const destroy = inject(DestroyRef);
    inject(UnsentWork).track(() => this.dirty(), destroy);
    const invalidation = this.contexts.invalidated.subscribe(() => {
      ++this.generation;
      this.reading.set(false);
      this.busy.set(false);
      this.data.set(null);
      this.intake.set(null);
      this.chunks.set(null);
      this.selected.set(null);
      this.message.set(
        "Identity changed or needs checking. Refresh before continuing; a sent request may have committed.",
      );
    });
    const route = inject(ActivatedRoute).paramMap.subscribe((params) => {
      if (
        this.project &&
        (this.project !== params.get("projectId") ||
          this.organization !== params.get("organizationId") ||
          this.bundle !== (params.get("bundleId") ?? ""))
      ) {
        this.intent.set(null);
        this.file.set(null);
        this.draft.set("");
      }
      ++this.generation;
      this.busy.set(false);
      this.organization = params.get("organizationId") ?? "";
      this.reading.set(false);
      this.project = params.get("projectId") ?? "";
      this.bundle = params.get("bundleId") ?? "";
      this.data.set(null);
      this.intake.set(null);
      this.chunks.set(null);
      this.selected.set(null);
      void this.refresh();
    });
    const unload = (e: BeforeUnloadEvent) => {
      if (this.dirty()) {
        e.preventDefault();
        e.returnValue = "";
      }
    };
    this.document.defaultView?.addEventListener("beforeunload", unload);
    destroy.onDestroy(() => {
      this.alive = false;
      ++this.generation;
      route.unsubscribe();
      invalidation.unsubscribe();
      this.document.defaultView?.removeEventListener("beforeunload", unload);
    });
  }
  requestLeave() {
    return of(
      !this.dirty() ||
        (this.document.defaultView?.confirm(
          "Leave specification intake? Unsent content and exact retry information will be lost. A sent request may already have committed. The bundle URL can be reopened to inspect inventory.",
        ) ??
          false),
    );
  }
  async start() {
    if (
      this.dirty() &&
      !this.document.defaultView?.confirm(
        "Discard in-tab work and start another bundle?",
      )
    )
      return;
    this.intent.set(null);
    this.file.set(null);
    this.draft.set("");
    await this.router.navigate([
      ...this.projectLink,
      "specs",
      crypto.randomUUID(),
    ]);
  }
  private valid() {
    return (
      isCatalogId(this.organization) &&
      isCatalogId(this.project) &&
      (!this.bundle || isCatalogId(this.bundle))
    );
  }
  private async admit() {
    if (!this.valid()) throw new Error("Invalid route");
    const context = await firstValueFrom(this.contexts.refresh());
    if (
      context.allowedOrganizationId !== this.organization ||
      !this.contexts.canSend(context)
    )
      throw new Error("Stale context");
    const owner = catalogOwner(context);
    if (this.owner && this.owner !== owner) {
      this.intent.set(null);
      this.file.set(null);
      this.draft.set("");
      this.data.set(null);
      this.chunks.set(null);
      this.selected.set(null);
      this.owner = owner;
      this.intake.set(null);
      this.context.set(context);
      throw new Error("Owner changed");
    }
    this.owner = owner;
    this.context.set(context);
    return context;
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
  private headers(context: CatalogContext, version?: number) {
    return {
      ...(context.requestToken ? { "X-XSRF-TOKEN": context.requestToken } : {}),
      ...(version ? { "If-Match": `"${version}"` } : {}),
    };
  }
  private fail(error: unknown) {
    const status =
      error instanceof HttpErrorResponse
        ? error.status
        : error instanceof CatalogReadError
          ? error.kind === "notFound"
            ? 404
            : error.kind === "rejected"
              ? 403
              : null
          : null;
    if (status === 401 || status === 403 || status === 404) {
      this.intake.set(null);
      ++this.generation;
      this.busy.set(false);
      this.reading.set(false);
      this.data.set(null);
      this.chunks.set(null);
      this.selected.set(null);
    }
    // A late refusal from an old lineage must not invalidate a replacement owner's session.
    if (status === 503) this.intake.set(null);
    if (this.contexts.isCurrent(this.context())) this.contexts.reject(status);
    this.message.set(
      status === 503
        ? "Specification intake is unavailable on this server. Schema, storage or feature admission has not succeeded. A sent request may still have committed; refresh inventory before retrying."
        : status === 404
          ? "Bundle unavailable or content access denied. For a new bundle, choose Create this bundle; otherwise check access and retry."
          : status === 409
            ? "Version conflict. Refresh inventory before retrying the retained request."
            : status === 413
              ? "Content exceeds the server limit. Inspect inventory before abandoning this request."
              : status === 401 || status === 403
                ? "Sign-in or content authorization needs attention. No private result is displayed."
                : "Request could not be confirmed. Refresh inventory before retrying; sent work may have committed.",
    );
  }
  async refresh() {
    // Access refresh supersedes an evidence read; its late response has no authority.
    if (this.reading()) {
      ++this.generation;
      this.reading.set(false);
      this.busy.set(false);
      this.chunks.set(null);
      this.selected.set(null);
    }
    if (this.busy()) return;
    this.busy.set(true);
    const generation = this.generation;
    this.intake.set(null);
    try {
      const context = await this.admit();
      const project = await firstValueFrom(
        this.catalog.project({
          organizationId: this.organization,
          projectId: this.project,
        }),
      );
      if (!this.current(generation, context)) return;
      this.projectName.set(project.name);
      if (!this.bundle) {
        this.message.set(
          "Start a bundle to capture specifications. Availability and content access are checked by the server.",
        );
        return;
      }
      const result = inventory(
        await firstValueFrom(
          this.http.get<unknown>(this.path).pipe(timeout(30000)),
        ),
        this.bundle,
      );
      if (!this.current(generation, context)) return;
      this.data.set(result);
      let intakeUnavailable = false;
      try {
        const admitted = capabilities(
          await firstValueFrom(
            this.http
              .get<unknown>(this.path + "/capabilities")
              .pipe(timeout(30000)),
          ),
        );
        if (!this.current(generation, context)) return;
        this.intake.set(admitted);
        if (!admitted.formats.some((f) => f.format === this.format))
          this.format = admitted.formats[0]?.format ?? "";
      } catch (error) {
        if (!this.current(generation, context)) return;
        // Capability availability is independent of the just-authorized inventory.
        // Authentication/content denials still clear private state through the outer boundary.
        if (
          error instanceof HttpErrorResponse &&
          [401, 403, 404].includes(error.status)
        )
          throw error;
        this.intake.set(null);
        intakeUnavailable = true;
      }
      this.chunks.set(null);
      this.selected.set(null);
      const intent = this.intent();
      if (intent?.kind === "create") this.intent.set(null);
      if (intent?.kind === "upload") {
        const state = recovery(intent.upload, result);
        if (state === "complete") {
          this.intent.set(null);
          this.clearUploaded(intent);
          this.message.set("Both publications confirmed in inventory.");
        } else
          this.message.set(
            state === "conflict"
              ? "Retained request conflicts with retired or different inventory. Do not replay; discard the local request explicitly."
              : "Original/extraction is incomplete. Retry uses the retained IDs and exact bytes. No new upload is started.",
          );
      } else if (intent?.kind === "delete") {
        const source = intent.suffix.split("/")[2];
        const version = Number(intent.suffix.split("/")[4]);
        const sources = result.objects.filter(
          (o) => o.sourceId === source && o.sourceVersion === version,
        );
        if (
          source &&
          sources.length > 0 &&
          sources.every((o) => o.state === "retiring" || o.state === "retired")
        ) {
          this.intent.set(null);
          this.message.set(
            "Source deletion confirmed; physical retirement may still be pending.",
          );
        } else
          this.message.set(
            "Deletion not confirmed. Retry the exact deletion key/version; absence alone does not prove deletion.",
          );
      } else
        this.message.set(
          "Inventory refreshed. No mapping has been generated and no AI-sharing approval is implied.",
        );
      if (intakeUnavailable)
        this.message.update(
          (message) =>
            message +
            " New intake is unavailable because server capabilities could not be confirmed. Existing inventory and extraction reads remain available. Refresh to retry capabilities.",
        );
    } catch (error) {
      if (generation === this.generation) {
        this.data.set(null);
        this.fail(error);
      }
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
  choose(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    const suffix = file?.name.split(".").at(-1)?.toLowerCase();
    const suggested =
      suffix === "txt"
        ? "text"
        : suffix && ["json", "docx", "xlsx", "pdf"].includes(suffix)
          ? suffix
          : this.format;
    const selected = this.intake()?.formats.find((f) => f.format === suggested);
    if (file && (!selected || file.size > selected.maximumSourceBytes)) {
      this.file.set(null);
      this.message.set(
        "This file format is unavailable or exceeds its admitted byte limit. Refresh server capabilities before choosing a supported format. The file was not sent.",
      );
      return;
    }
    this.file.set(file);
    if (selected) this.format = selected.format;
  }
  create() {
    if (!this.bundle || this.intent() || this.busy()) return;
    this.intent.set({ kind: "create" });
    void this.send();
  }
  upload(paste: boolean) {
    const data = this.data();
    const body = paste ? this.draft() : this.file();
    if (!data || !body || this.busy() || this.intent()) return;
    const admitted = this.intake()?.formats.find(
      (f) => f.format === (paste ? "text" : this.format),
    );
    if (
      !admitted ||
      (body instanceof File && body.size > admitted.maximumSourceBytes)
    ) {
      this.message.set(
        "Selected format is unavailable or exceeds its admitted byte limit. Refresh server capabilities.",
      );
      return;
    }
    if (
      paste &&
      new TextEncoder().encode(JSON.stringify({ text: body })).length > 1047000
    ) {
      this.message.set(
        "Pasted JSON request must fit within 1 MiB. Use a text file for larger content.",
      );
      return;
    }
    const upload: Upload = {
      sourceId: crypto.randomUUID(),
      sourceVersion: 1,
      originalObjectId: crypto.randomUUID(),
      uploadKey: crypto.randomUUID(),
      extractionObjectId: crypto.randomUUID(),
      extractionKey: crypto.randomUUID(),
      extractionExpectedVersion: data.bundle.version + 1,
      format: paste ? "text" : this.format,
    };
    this.intent.set({
      kind: "upload",
      upload,
      version: data.bundle.version,
      body,
    });
    void this.send();
  }
  remove(object?: SpecObject) {
    const data = this.data();
    if (
      !data ||
      this.busy() ||
      this.intent() ||
      !this.document.defaultView?.confirm(
        object
          ? "Delete this source version and its extraction?"
          : "Delete this entire bundle?",
      )
    )
      return;
    this.intent.set({
      kind: "delete",
      key: crypto.randomUUID(),
      version: data.bundle.version,
      suffix: object
        ? `/sources/${object.sourceId}/versions/${object.sourceVersion}`
        : "",
    });
    void this.send();
  }
  discard() {
    if (
      !this.busy() &&
      this.document.defaultView?.confirm(
        "Discard local retry information? This does not undo any server publication. Inspect inventory before starting another upload.",
      )
    ) {
      this.intent.set(null);
      this.file.set(null);
      this.draft.set("");
    }
  }
  async send() {
    const intent = this.intent();
    if (!intent || this.busy()) return;
    this.busy.set(true);
    const generation = this.generation;
    try {
      const context = await this.admit();
      if (!this.current(generation, context) || this.intent() !== intent)
        return;
      if (intent.kind === "create") {
        const response = await firstValueFrom(
          this.http
            .put<Status>(
              this.path,
              { creationKey: this.bundle },
              { headers: this.headers(context) },
            )
            .pipe(timeout(30000)),
        );
        if (
          response.bundleId !== this.bundle ||
          !Number.isSafeInteger(response.version) ||
          response.version < 1 ||
          response.tombstoned !== false
        )
          throw new Error("Invalid receipt");
      } else if (intent.kind === "delete") {
        const response = await firstValueFrom(
          this.http
            .delete<Status>(this.path + intent.suffix, {
              headers: {
                ...this.headers(context, intent.version),
                "Idempotency-Key": intent.key,
              },
            })
            .pipe(timeout(30000)),
        );
        if (
          response.bundleId !== this.bundle ||
          !Number.isSafeInteger(response.version) ||
          response.version <= intent.version ||
          response.tombstoned !== !intent.suffix
        )
          throw new Error("Invalid receipt");
      } else {
        // Reconcile before every send, including after a lost response. Never silently replace an attempt.
        const latest = inventory(
          await firstValueFrom(
            this.http.get<unknown>(this.path).pipe(timeout(30000)),
          ),
          this.bundle,
        );
        if (!this.current(generation, context)) return;
        this.data.set(latest);
        const state = recovery(intent.upload, latest);
        if (state === "conflict") throw new Error("Conflicting intent");
        if (state !== "complete") {
          const admitted = capabilities(
            await firstValueFrom(
              this.http
                .get<unknown>(this.path + "/capabilities")
                .pipe(timeout(30000)),
            ),
          );
          if (!this.current(generation, context)) return;
          this.intake.set(admitted);
          const format = admitted.formats.find(
            (f) => f.format === intent.upload.format,
          );
          if (
            !format ||
            (intent.body instanceof File &&
              intent.body.size > format.maximumSourceBytes)
          )
            throw new HttpErrorResponse({ status: 503 });
          const original = latest.objects.find(
            (o) => o.objectId === intent.upload.originalObjectId,
          );
          const extracted = latest.objects.find(
            (o) => o.objectId === intent.upload.extractionObjectId,
          );
          if (!extracted)
            intent.upload.extractionExpectedVersion =
              latest.bundle.version + (original ? 0 : 1);
          if (!this.contexts.canSend(context)) throw new Error("Stale context");
          const paste = typeof intent.body === "string";
          const response = await firstValueFrom(
            this.http
              .post<Receipt>(
                this.path + (paste ? "/text" : "/uploads"),
                paste
                  ? { upload: intent.upload, text: intent.body }
                  : intent.body,
                {
                  headers: {
                    ...this.headers(context, intent.version),
                    ...(paste
                      ? {}
                      : {
                          "Content-Type": "application/octet-stream",
                          "X-Weft-Spec-Upload": JSON.stringify(intent.upload),
                        }),
                  },
                },
              )
              .pipe(timeout(60000)),
          );
          if (
            response.originalObjectId !== intent.upload.originalObjectId ||
            response.extractionObjectId !== intent.upload.extractionObjectId ||
            !Number.isSafeInteger(response.reservationVersion) ||
            response.reservationVersion !==
              intent.upload.extractionExpectedVersion + 1 ||
            ![
              "Complete",
              "Partial",
              "Malformed",
              "Unsupported",
              "LimitExceeded",
            ].includes(response.outcome) ||
            (["docx", "xlsx", "pdf"].includes(intent.upload.format)
              ? response.outcome === "Complete"
              : response.outcome === "Partial")
          )
            throw new Error("Invalid receipt");
        }
      }
      if (!this.current(generation, context)) return;
      this.intent.set(null);
      if (intent.kind === "upload") this.clearUploaded(intent);
      this.chunks.set(null);
      this.selected.set(null);
      if (intent.kind === "delete" && !intent.suffix) {
        this.data.set(null);
        this.message.set(
          "Bundle deletion acknowledged. Physical retirement may still be pending.",
        );
        return;
      }
      this.busy.set(false);
      await this.refresh();
    } catch (error) {
      if (generation === this.generation) this.fail(error);
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
  async read(object: SpecObject) {
    if (this.busy()) return;
    this.busy.set(true);
    this.reading.set(true);
    this.chunks.set(null);
    this.selected.set(null);
    const generation = this.generation;
    try {
      const context = await this.admit();
      if (!this.current(generation, context)) return;
      const bytes = await firstValueFrom(
        this.http
          .get(`${this.path}/extractions/${object.objectId}`, {
            responseType: "arraybuffer",
          })
          .pipe(timeout(30000)),
      );
      const value = await readEvidence(bytes, object);
      if (this.current(generation, context)) {
        this.selected.set(object);
        this.chunks.set(value);
      }
    } catch (error) {
      if (generation === this.generation) this.fail(error);
    } finally {
      if (generation === this.generation) {
        this.busy.set(false);
        this.reading.set(false);
      }
    }
  }

  private clearUploaded(intent: Extract<Intent, { kind: "upload" }>) {
    // A file upload must not discard text the user typed but did not send (or vice versa).
    if (typeof intent.body === "string") {
      if (this.draft() === intent.body) this.draft.set("");
    } else if (this.file() === intent.body) this.file.set(null);
  }
}
