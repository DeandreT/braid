import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  defer,
  map,
  switchMap,
  takeUntil,
  throwError,
  timeout,
} from "rxjs";
import { BrowserSessionState } from "../../auth/browser-session-state";
import { CatalogApi } from "../catalog-api";
import type { components } from "../../api/generated";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog-types";

export type OperationsPage = components["schemas"]["ReleaseOperationsPageV1"];
export type OperationBody = components["schemas"]["ReleaseOperationV1"];
export interface OriginalOperation {
  id: string;
  target: string;
  version: string;
  body: string;
  owner: string;
}

@Injectable({ providedIn: "root" })
export class OperationsApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);
  private readonly catalog = inject(CatalogApi);

  read(
    context: CatalogContext,
    scope: CatalogMappingScope,
    target: string | null,
    section: string,
    offset: number,
    filters: URLSearchParams,
  ) {
    if (
      ![
        "targets",
        "releases",
        "history",
        "actions",
        "callers",
        "invocations",
        "summary",
      ].includes(section)
    )
      throw new Error("Invalid section");
    const query = new URLSearchParams(filters);
    query.set("offset", String(offset));
    query.set("limit", "20");
    return this.request(
      context,
      scope,
      target ? `/${target}/${section}?${query}` : `?${query}`,
    );
  }

  apply(
    context: CatalogContext,
    scope: CatalogMappingScope,
    original: OriginalOperation,
  ) {
    return this.request(
      context,
      scope,
      `/${original.target}/actions/${original.id}`,
      original,
    );
  }

  private request(
    context: CatalogContext,
    scope: CatalogMappingScope,
    suffix: string,
    original?: OriginalOperation,
  ) {
    const selected = { ...scope };
    return defer(() => {
      if (
        ![
          selected.organizationId,
          selected.projectId,
          selected.mappingId,
        ].every(isCatalogId) ||
        context.allowedOrganizationId !== selected.organizationId ||
        !this.sessions.canDispatch(context) ||
        (original &&
          (!isCatalogId(original.id) ||
            !isCatalogId(original.target) ||
            !/^[1-9][0-9]*$/.test(original.version) ||
            !context.requestToken))
      )
        throw new Error("Workspace access unavailable");
      const generation = this.sessions.generation();
      const headers = original
        ? new HttpHeaders({
            "Content-Type": "application/json",
            "X-XSRF-TOKEN": context.requestToken!,
            "If-Match": `"${original.version}"`,
          })
        : new HttpHeaders();
      // A catalog context proves identity, not project authority. Refresh the actual
      // project's membership before dispatch; the server checks it again atomically.
      return this.catalog.project(selected).pipe(
        switchMap((project) => {
          if (
            generation !== this.sessions.generation() ||
            !this.sessions.canDispatch(context) ||
            !["publisher", "administrator"].includes(project.access.role) ||
            (original &&
              ["enable", "disable"].includes(
                JSON.parse(original.body).action,
              ) &&
              project.access.role !== "administrator")
          )
            throw new Error("Current project authority is required");
          return this.http
            .request<unknown>(
              original ? "POST" : "GET",
              `/api/v1/organizations/${selected.organizationId}/projects/${selected.projectId}/mappings/${selected.mappingId}/operations${suffix}`,
              { headers, body: original?.body },
            )
            .pipe(
              timeout(45000),
              takeUntil(this.sessions.invalidated),
              map((value) => {
                if (
                  generation !== this.sessions.generation() ||
                  !this.sessions.presents(context) ||
                  JSON.stringify(value).length > 1024 * 1024
                )
                  throw new Error("Workspace access unavailable");
                return value;
              }),
              catchError((error: unknown) => {
                if (
                  error instanceof HttpErrorResponse &&
                  [401, 403].includes(error.status)
                )
                  this.sessions.rejectProtectedRequest(generation, context);
                return throwError(() => error);
              }),
            );
        }),
      );
    });
  }
}
