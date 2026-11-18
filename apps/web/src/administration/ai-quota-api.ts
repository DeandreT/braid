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
import type { components } from "../api/generated";
import { CatalogContextStore } from "../catalog/catalog-context";
import { isCatalogId, type CatalogContext } from "../catalog/catalog-types";
import {
  AdministrationFailure,
  type AdministrationSnapshot,
} from "./administration-api";

export type AiQuotaSnapshot = components["schemas"]["AiQuotaSnapshotV1"];
export type AiQuotaWrite = components["schemas"]["AiQuotaWriteV1"];
export type QuotaPeriod = "hour" | "day" | "month";
export const quotaPeriods: readonly QuotaPeriod[] = ["hour", "day", "month"];
export function quotaCount(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[1-9][0-9]{0,18}$/.test(value) &&
    BigInt(value) <= 9223372036854775807n
  );
}
export function quotaETag(
  org: string,
  principal: string | null,
  version: string,
): string {
  return `"ai-quota-${org}-${principal ?? "organization"}-${version}"`;
}

@Injectable({ providedIn: "root" })
export class AiQuotaApi {
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly contexts = inject(CatalogContextStore);

  read(
    context: CatalogContext,
    principal: string | null,
  ): Observable<AdministrationSnapshot<AiQuotaSnapshot>> {
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(this.path(fresh, principal), {
          observe: "response",
        }),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body;
        const etag = response.headers.get("ETag");
        if (
          response.status !== 200 ||
          !snapshot(value) ||
          value.organizationId !== context.allowedOrganizationId ||
          value.principalId !== principal ||
          etag !==
            quotaETag(context.allowedOrganizationId, principal, value.version)
        )
          throw new AdministrationFailure("unavailable");
        return { body: value, etag };
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  write(
    context: CatalogContext,
    principal: string | null,
    period: QuotaPeriod,
    version: string,
    body: AiQuotaWrite,
  ): Observable<string> {
    if (
      !quotaPeriods.includes(period) ||
      !quotaCount(version) ||
      (body.maximumTurns !== null && !quotaCount(body.maximumTurns)) ||
      (body.maximumTokens !== null && !quotaCount(body.maximumTokens))
    )
      throw new AdministrationFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.put<unknown>(
          `${this.path(fresh, principal)}/${period}`,
          body,
          {
            observe: "response",
            headers: new HttpHeaders({
              "If-Match": quotaETag(
                fresh.allowedOrganizationId,
                principal,
                version,
              ),
              "X-XSRF-TOKEN": fresh.requestToken,
            }),
          },
        ),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body;
        if (
          response.status !== 200 ||
          !record(value) ||
          (value["outcome"] !== "updated" && value["outcome"] !== "unchanged")
        )
          throw new AdministrationFailure("unconfirmed");
        return value["outcome"] as string;
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  private path(context: CatalogContext, principal: string | null): string {
    if (principal !== null && !isCatalogId(principal))
      throw new AdministrationFailure("invalidRequest");
    return `/api/v1/organizations/${context.allowedOrganizationId}/administration${principal ? `/principals/${principal}` : ""}/ai-quotas`;
  }
  private failure(error: unknown, context: CatalogContext): Observable<never> {
    if (error instanceof HttpErrorResponse) {
      this.contexts.reject(error.status, context);
      const code: unknown = error.error?.code;
      if (
        typeof code === "string" &&
        [
          "notFound",
          "preconditionFailed",
          "preconditionRequired",
          "invalidRequest",
        ].includes(code)
      )
        return throwError(() => new AdministrationFailure(code));
    }
    return throwError(() => new AdministrationFailure("unconfirmed"));
  }
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
function snapshot(value: unknown): value is AiQuotaSnapshot {
  if (
    !record(value) ||
    !isCatalogId(value["organizationId"]) ||
    (value["principalId"] !== null && !isCatalogId(value["principalId"])) ||
    !quotaCount(value["version"]) ||
    !Array.isArray(value["limits"]) ||
    value["limits"].length > 3
  )
    return false;
  const seen = new Set<string>();
  return value["limits"].every((limit: unknown) => {
    if (
      !record(limit) ||
      !quotaPeriods.includes(limit["period"] as QuotaPeriod) ||
      seen.has(limit["period"] as string) ||
      (limit["maximumTurns"] === null && limit["maximumTokens"] === null) ||
      (limit["maximumTurns"] !== null && !quotaCount(limit["maximumTurns"])) ||
      (limit["maximumTokens"] !== null && !quotaCount(limit["maximumTokens"]))
    )
      return false;
    seen.add(limit["period"] as string);
    return true;
  });
}
