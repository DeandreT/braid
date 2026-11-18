import {
  HttpClient,
  HttpErrorResponse,
  HttpParams,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  map,
  switchMap,
  takeUntil,
  throwError,
  timeout,
} from "rxjs";
import { BrowserSessionState } from "../auth/browser-session-state";
import { CatalogContextStore } from "../catalog/catalog-context";
import { isCatalogId, type CatalogContext } from "../catalog/catalog-types";
import type { components } from "../api/generated";

export type AuditPage = components["schemas"]["AuditLogPageV1"];
export type AuditEntry = components["schemas"]["AuditLogEntryV1"];

@Injectable({ providedIn: "root" })
export class AuditApi {
  private readonly http = inject(HttpClient);
  private readonly contexts = inject(CatalogContextStore);
  private readonly sessions = inject(BrowserSessionState);

  read(context: CatalogContext, query: Readonly<Record<string, string>>) {
    return this.contexts.admitted(context).pipe(
      switchMap((current) => {
        const generation = this.sessions.generation();
        return this.http
          .get<AuditPage>(
            `/api/v1/organizations/${current.allowedOrganizationId}/audit`,
            {
              params: new HttpParams({ fromObject: query }),
            },
          )
          .pipe(
            timeout(20_000),
            takeUntil(this.sessions.invalidated),
            map((page) => {
              if (
                generation !== this.sessions.generation() ||
                !this.contexts.isCurrent(current) ||
                !this.contexts.canPresent(current) ||
                !Array.isArray(page.entries) ||
                page.entries.length > 100 ||
                JSON.stringify(page).length > 512 * 1024 ||
                !page.entries.every(
                  (e) => isCatalogId(e.eventId) && isCatalogId(e.resourceId),
                )
              )
                throw new Error("Audit response is unavailable.");
              return page;
            }),
            catchError((error: unknown) => {
              if (
                error instanceof HttpErrorResponse &&
                [401, 403].includes(error.status)
              )
                this.sessions.rejectProtectedRequest(generation, current);
              return throwError(() => error);
            }),
          );
      }),
    );
  }
}
