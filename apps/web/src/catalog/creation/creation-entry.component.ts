import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { Component, effect, inject, input, signal } from "@angular/core";
import { RouterLink } from "@angular/router";
import { timeout } from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import type { CatalogContext, CatalogProjectScope } from "../catalog-types";
import { creationCapability } from "./creation-library-model";

@Component({
  selector: "braid-creation-entry",
  imports: [RouterLink],
  template: `@if (available() && contexts.isCurrent(context())) {
    <section aria-label="New mapping proposals">
      <h2>New mapping proposals</h2>
      <p>
        Find your retained proposals, review samples and request a separately
        approved revision.
      </p>
      <a
        class="catalog-button"
        [routerLink]="[
          '/catalog/organizations',
          scope().organizationId,
          'projects',
          scope().projectId,
          'creation',
        ]"
        >Your new mapping proposals</a
      >
    </section>
  }`,
})
export class CreationEntryComponent {
  readonly scope = input.required<CatalogProjectScope>();
  readonly context = input<CatalogContext | null>(null);
  readonly contexts = inject(CatalogContextStore);
  readonly available = signal(false);
  private readonly http = inject(HttpClient);
  constructor() {
    effect((cleanup) => {
      const scope = this.scope(),
        context = this.context();
      this.available.set(false);
      if (
        !context ||
        !this.contexts.isCurrent(context) ||
        scope.organizationId !== context.allowedOrganizationId
      )
        return;
      const subscription = this.http
        .get<unknown>(
          `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/creation-capabilities`,
        )
        .pipe(timeout(10000))
        .subscribe({
          next: (value) => {
            if (!this.contexts.isCurrent(context)) return;
            try {
              this.available.set(creationCapability(value));
            } catch {
              this.available.set(false);
            }
          },
          error: (error) => {
            if (!this.contexts.isCurrent(context)) return;
            this.available.set(false);
            if (
              error instanceof HttpErrorResponse &&
              [401, 403].includes(error.status)
            )
              this.contexts.reject(error.status);
          },
        });
      cleanup(() => subscription.unsubscribe());
    });
  }
}
