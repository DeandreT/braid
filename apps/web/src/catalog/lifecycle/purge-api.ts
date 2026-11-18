import {
  HttpBackend,
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  map,
  switchMap,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import type { components } from "../../api/generated";
import { CatalogContextStore } from "../catalog-context";
import { isCatalogId, type CatalogContext } from "../catalog-types";
import type { LifecycleScope } from "./lifecycle-api";

export type PurgePreview = components["schemas"]["ResourcePurgePreviewV1"];
export type DeletionJob = components["schemas"]["DeletionJobV1"];
export type PurgeSnapshot = {
  readonly body: PurgePreview;
  readonly etag: string;
};

/** Closed categories; "unconfirmed" means the outcome is unknown and must be read, never resent. */
export class PurgeFailure extends Error {
  constructor(readonly code: string) {
    super("The deletion request was not confirmed.");
    this.name = "PurgeFailure";
  }
}

@Injectable({ providedIn: "root" })
export class PurgeApi {
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly contexts = inject(CatalogContextStore);

  /** A read-only preview; its confirmation stays in memory only. */
  preview(
    context: CatalogContext,
    scope: LifecycleScope,
  ): Observable<PurgeSnapshot> {
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(`${this.path(fresh, scope)}/deletion-preview`, {
          observe: "response",
        }),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body as PurgePreview | null;
        const etag = response.headers.get("ETag");
        if (
          response.status !== 200 ||
          !value ||
          value.projectId !== scope.projectId ||
          !etag
        )
          throw new PurgeFailure("unavailable");
        return { body: value, etag };
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  /** Exactly one request; the caller keeps the operation ID to resolve an unknown outcome. */
  request(
    context: CatalogContext,
    scope: LifecycleScope,
    snapshot: PurgeSnapshot,
    operationId: string,
  ): Observable<DeletionJob> {
    const confirmation = snapshot.body.confirmation;
    if (!isCatalogId(operationId) || !confirmation)
      throw new PurgeFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.delete<unknown>(this.path(fresh, scope), {
          observe: "response",
          headers: new HttpHeaders({
            "If-Match": snapshot.etag,
            "Braid-Operation-Id": operationId,
            "Braid-Confirmation": confirmation,
            "X-XSRF-TOKEN": fresh.requestToken,
          }),
        }),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body as DeletionJob | null;
        if (response.status !== 202 || !value || !isCatalogId(value.jobId))
          throw new PurgeFailure("unconfirmed");
        return value;
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  /** Reads the original operation's job; this never repeats the request. */
  operation(
    context: CatalogContext,
    operationId: string,
  ): Observable<DeletionJob> {
    return this.read(context, `operations/${operationId}`, operationId);
  }

  job(context: CatalogContext, jobId: string): Observable<DeletionJob> {
    return this.read(context, jobId, jobId);
  }

  private read(
    context: CatalogContext,
    suffix: string,
    id: string,
  ): Observable<DeletionJob> {
    if (!isCatalogId(id)) throw new PurgeFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(
          `/api/v1/organizations/${fresh.allowedOrganizationId}/deletion-jobs/${suffix}`,
          { observe: "response" },
        ),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body as DeletionJob | null;
        if (response.status !== 200 || !value || !isCatalogId(value.jobId))
          throw new PurgeFailure("unavailable");
        return value;
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  private path(context: CatalogContext, scope: LifecycleScope): string {
    if (
      scope.organizationId !== context.allowedOrganizationId ||
      !isCatalogId(scope.projectId) ||
      ("mappingId" in scope && !isCatalogId(scope.mappingId))
    )
      throw new PurgeFailure("invalidRequest");
    const project = `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}`;
    return "mappingId" in scope
      ? `${project}/mappings/${scope.mappingId}`
      : project;
  }

  private failure(error: unknown, context: CatalogContext): Observable<never> {
    if (error instanceof PurgeFailure) return throwError(() => error);
    if (error instanceof HttpErrorResponse) {
      this.contexts.reject(error.status, context);
      const code: unknown = error.error?.code;
      if (
        typeof code === "string" &&
        [
          "notFound",
          "conflict",
          "preconditionFailed",
          "activeRelease",
          "activeWork",
          "confirmationExpired",
          "invalidRequest",
        ].includes(code)
      )
        return throwError(() => new PurgeFailure(code));
    }
    return throwError(() => new PurgeFailure("unconfirmed"));
  }
}
