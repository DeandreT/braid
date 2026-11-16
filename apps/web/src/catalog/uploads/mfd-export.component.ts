import {
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
} from "@angular/core";
import { Subscription } from "rxjs";
import { UnsentWork } from "../../workbench/unsent-work";
import { CatalogContextStore } from "../catalog-context";
import type { CatalogRevisionScope } from "../catalog-types";
import { MfdInterchangeApi } from "./mfd-interchange-api";
import type { MfdReview } from "./mfd-interchange";

@Component({
  selector: "braid-mfd-export",
  templateUrl: "./mfd-export.component.html",
  styleUrl: "./mfd-interchange.component.css",
})
export class MfdExportComponent {
  readonly scope = input.required<CatalogRevisionScope>();
  readonly enabled = signal(false);
  readonly busy = signal(false);
  readonly review = signal<MfdReview | null>(null);
  readonly acknowledged = signal(false);
  readonly message = signal("");
  private readonly api = inject(MfdInterchangeApi);
  private readonly contexts = inject(CatalogContextStore);
  private active = new Subscription();
  private generation = 0;
  private owner = "";
  constructor() {
    inject(UnsentWork).track(() => this.busy());
    const invalidation = this.contexts.invalidated.subscribe(() =>
      this.clear(),
    );
    effect(() => {
      const context = this.contexts.context(),
        scope = this.scope();
      const owner = context
        ? `${context.actorPrincipalId}/${scope.organizationId}/${scope.projectId}/${scope.mappingId}/${scope.revisionId}`
        : "";
      if (owner === this.owner) return;
      this.owner = owner;
      this.clear();
      if (!context || context.allowedOrganizationId !== scope.organizationId)
        return;
      const generation = this.generation;
      this.active.add(
        this.api.capabilities().subscribe({
          next: (value) => {
            if (generation === this.generation) this.enabled.set(value);
          },
          error: () => this.enabled.set(false),
        }),
      );
    });
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.active.unsubscribe();
      ++this.generation;
    });
  }
  prepare() {
    const context = this.contexts.context(),
      generation = this.generation;
    if (
      !context ||
      !this.contexts.canSend(context) ||
      !this.enabled() ||
      this.busy()
    )
      return;
    this.busy.set(true);
    this.message.set("");
    this.review.set(null);
    this.acknowledged.set(false);
    this.active.add(
      this.api.review(`${this.base()}/review`, null).subscribe({
        next: (review) => {
          if (
            generation !== this.generation ||
            !this.contexts.canPresent(context)
          )
            return;
          this.review.set(review);
          this.busy.set(false);
        },
        error: () => {
          if (generation === this.generation) {
            this.busy.set(false);
            this.message.set(
              "MFD export is unavailable. No successful conversion is assumed.",
            );
          }
        },
      }),
    );
  }
  download() {
    const review = this.review(),
      context = this.contexts.context(),
      generation = this.generation;
    if (
      !review ||
      !context ||
      !this.contexts.canSend(context) ||
      this.busy() ||
      (review.warnings.length && !this.acknowledged())
    )
      return;
    this.busy.set(true);
    this.message.set("");
    this.active.add(
      this.api
        .download(
          `${this.base()}/${review.reviewId}/download`,
          review.reviewDigest,
          this.acknowledged(),
        )
        .subscribe({
          next: async (response) => {
            try {
              const blob = response.body,
                expected = response.headers.get("X-Content-Sha256"),
                media = response.headers.get("Content-Type");
              if (
                !blob ||
                blob.size > 16 * 1024 * 1024 + 256 * 1024 ||
                !expected ||
                !/^[0-9a-f]{64}$/u.test(expected) ||
                !["application/zip", "application/xml"].includes(media ?? "")
              )
                throw new Error();
              const digest = Array.from(
                new Uint8Array(
                  await crypto.subtle.digest(
                    "SHA-256",
                    await blob.arrayBuffer(),
                  ),
                ),
                (byte) => byte.toString(16).padStart(2, "0"),
              ).join("");
              if (digest !== expected) throw new Error();
              if (
                generation !== this.generation ||
                !this.contexts.canPresent(context)
              )
                return;
              const url = URL.createObjectURL(blob),
                anchor = document.createElement("a");
              anchor.href = url;
              anchor.download =
                media === "application/zip" ? "mapping.zip" : "mapping.mfd";
              anchor.click();
              setTimeout(() => URL.revokeObjectURL(url), 0);
              this.message.set("Reviewed export downloaded.");
              this.busy.set(false);
            } catch {
              if (generation === this.generation) {
                this.message.set(
                  "Export download could not be verified. Nothing was downloaded.",
                );
                this.busy.set(false);
              }
            }
          },
          error: () => {
            if (generation === this.generation) {
              this.message.set(
                "Export conditions were not met or access changed. Review again before downloading.",
              );
              this.busy.set(false);
            }
          },
        }),
    );
  }
  private base() {
    const s = this.scope();
    return `/api/v1/organizations/${s.organizationId}/projects/${s.projectId}/mappings/${s.mappingId}/revisions/${s.revisionId}/mfd-exports`;
  }
  private clear() {
    ++this.generation;
    this.active.unsubscribe();
    this.active = new Subscription();
    this.enabled.set(false);
    this.busy.set(false);
    this.review.set(null);
    this.acknowledged.set(false);
    this.message.set("");
  }
}
