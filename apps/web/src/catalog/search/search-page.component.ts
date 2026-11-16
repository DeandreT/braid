import { Component, computed, inject, signal } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { ActivatedRoute, Router, RouterLink } from "@angular/router";
import { CatalogApi } from "../catalog-api";
import { createCatalogRead } from "../catalog-state";
import { CatalogStatusComponent } from "../catalog-shell.component";
import {
  isSearchCursor,
  isSearchUnicode,
  searchResultLink,
  searchTextParts,
  type CatalogSearchPage,
} from "./catalog-search";

@Component({
  selector: "braid-search-page",
  imports: [RouterLink, CatalogStatusComponent],
  template: `
    <section
      class="catalog-page catalog-search-page"
      aria-labelledby="search-heading"
    >
      <h1 id="search-heading">Search catalog</h1>
      <p class="catalog-muted">
        Projects, mapping metadata, revisions, and field and rule names and
        paths from saved reports. No sample payloads are searched.
      </p>
      <form
        class="catalog-search-form"
        (submit)="$event.preventDefault(); search(queryInput.value)"
      >
        <label for="catalog-query">Search text</label>
        <input
          id="catalog-query"
          #queryInput
          type="search"
          autocomplete="off"
          spellcheck="false"
          [value]="query()"
          maxlength="256"
          required
        />
        <button class="catalog-button" type="submit">Search</button>
      </form>
      @if (query()) {
        @if (page(); as result) {
          <p role="status">
            {{ result.hits.length }} result{{
              result.hits.length === 1 ? "" : "s"
            }}
            on this page for “{{ result.query }}”
          </p>
          @if (result.hits.length === 0) {
            <p>No matching resources you can read.</p>
          }
          <ol class="catalog-search-results">
            @for (hit of result.hits; track $index) {
              <li>
                <span class="search-kind">{{ hit.kind }}</span>
                <a
                  [routerLink]="resultLink(hit)"
                  [queryParams]="
                    hit.projectionDerivationId
                      ? { projectionDerivationId: hit.projectionDerivationId }
                      : {}
                  "
                  [fragment]="
                    hit.resourceId ? hit.kind + ':' + hit.resourceId : undefined
                  "
                >
                  @for (part of parts(hit.title); track $index) {
                    @if (part.highlight) {
                      <mark>{{ part.text }}</mark>
                    } @else {
                      <span>{{ part.text }}</span>
                    }
                  }
                </a>
                <p>
                  @for (part of parts(hit.description); track $index) {
                    @if (part.highlight) {
                      <mark>{{ part.text }}</mark>
                    } @else {
                      <span>{{ part.text }}</span>
                    }
                  }
                </p>
              </li>
            }
          </ol>
          @if (result.nextCursor) {
            <a
              class="catalog-page-link"
              routerLink="/catalog/search"
              [queryParams]="{ q: query(), cursor: result.nextCursor }"
              >Next page</a
            >
          }
          @if (cursor()) {
            <a
              class="catalog-page-link"
              routerLink="/catalog/search"
              [queryParams]="{ q: query() }"
              >First page</a
            >
          }
        } @else {
          @if (read.state().kind === "unavailable") {
            <p>
              Search may still be indexing saved reports, or the catalog may be
              unavailable. Retry continues a bounded indexing batch; incomplete
              results are never shown.
            </p>
          }
          <braid-catalog-status
            [kind]="read.state().kind"
            (retry)="read.retry()"
          />
        }
      }
    </section>
  `,
  styles: `
    .catalog-search-page {
      max-width: 64rem;
      margin: auto;
      padding: 1.5rem;
    }
    .catalog-search-form {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      flex-wrap: wrap;
    }
    input {
      min-width: 10rem;
      flex: 1;
      color: inherit;
      background: var(--surface);
      border: 1px solid var(--wb-control-border);
      border-radius: 0.4rem;
      padding: 0.65rem;
    }
    .catalog-search-results {
      list-style: none;
      padding: 0;
    }
    li {
      padding: 1rem 0;
      border-bottom: 1px solid var(--border-13);
      overflow-wrap: anywhere;
    }
    li a {
      font-weight: 600;
    }
    li p {
      white-space: pre-wrap;
    }
    .search-kind {
      display: inline-block;
      min-width: 5rem;
      text-transform: capitalize;
      opacity: 0.7;
    }
    mark {
      background: var(--mark);
      color: var(--mark-text);
      border-radius: 0.1rem;
    }
  `,
})
export class SearchPageComponent {
  private readonly api = inject(CatalogApi);
  private readonly router = inject(ActivatedRoute);
  private readonly navigation = inject(Router);
  readonly query = signal("");
  readonly cursor = signal<string | null>(null);
  readonly read = createCatalogRead<CatalogSearchPage>({
    cursorValidator: isSearchCursor,
  });
  readonly page = computed(() => {
    const state = this.read.state();
    return state.kind === "ready" ? state.data : null;
  });
  readonly resultLink = searchResultLink;
  readonly parts = searchTextParts;

  constructor() {
    this.router.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const query = params.get("q") ?? "",
        cursor = params.get("cursor");
      this.query.set(query);
      this.cursor.set(cursor);
      if (
        params.keys.some((key) => key !== "q" && key !== "cursor") ||
        params.getAll("q").length > 1 ||
        params.getAll("cursor").length > 1 ||
        (cursor !== null && !isSearchCursor(cursor)) ||
        new TextEncoder().encode(query.trim()).length > 256 ||
        !isSearchUnicode(query) ||
        /[\u0000-\u001f\u007f-\u009f]/u.test(query)
      ) {
        this.read.invalidLink();
        return;
      }
      if (!query.trim()) {
        this.read.clear();
        return;
      }
      this.read.select(
        null,
        (context) =>
          this.api.search(context.allowedOrganizationId, query, cursor),
        cursor,
      );
    });
  }

  search(query: string): void {
    if (query.trim() === this.query().trim() && this.cursor() === null) {
      this.read.retry();
      return;
    }
    void this.navigation.navigate(["/catalog/search"], {
      queryParams: { q: query.trim() },
    });
  }
}
