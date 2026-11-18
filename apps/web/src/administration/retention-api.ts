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
import { quotaCount } from "./ai-quota-api";

export type RetentionPolicies = components["schemas"]["RetentionPoliciesV1"];
export type RetentionPolicyWrite =
  components["schemas"]["RetentionFamilyPolicyWriteV1"];
export type RetentionFamily =
  "preview" | "regressionRun" | "conversation" | "invocationHistory";
export type RetentionMode = "notConfigured" | "retain" | "expire";
export const retentionFamilies: readonly RetentionFamily[] = [
  "preview",
  "regressionRun",
  "conversation",
  "invocationHistory",
];
export const minimumRetentionSeconds = 3600;
export const maximumRetentionSeconds = 315_360_000;
export function retentionETag(org: string, version: string): string {
  return `"retention-${org}-${version}"`;
}
export function retentionSeconds(value: unknown): value is string {
  return (
    quotaCount(value) &&
    BigInt(value) >= BigInt(minimumRetentionSeconds) &&
    BigInt(value) <= BigInt(maximumRetentionSeconds)
  );
}

@Injectable({ providedIn: "root" })
export class RetentionApi {
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly contexts = inject(CatalogContextStore);

  read(
    context: CatalogContext,
  ): Observable<AdministrationSnapshot<RetentionPolicies>> {
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(this.path(fresh), { observe: "response" }),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body;
        const etag = response.headers.get("ETag");
        if (
          response.status !== 200 ||
          !policies(value) ||
          value.organizationId !== context.allowedOrganizationId ||
          etag !== retentionETag(context.allowedOrganizationId, value.version)
        )
          throw new AdministrationFailure("unavailable");
        return { body: value, etag };
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  /** Sends the complete policy once; an unconfirmed write is never resent. */
  write(
    context: CatalogContext,
    version: string,
    body: readonly RetentionPolicyWrite[],
  ): Observable<string> {
    if (
      !quotaCount(version) ||
      body.length !== retentionFamilies.length ||
      body.some(
        (policy, index) =>
          policy.family !== retentionFamilies[index] ||
          (policy.mode === "expire") !== (policy.durationSeconds !== null) ||
          (policy.durationSeconds !== null &&
            !retentionSeconds(policy.durationSeconds)) ||
          (policy.applyToExisting && policy.mode !== "expire"),
      )
    )
      throw new AdministrationFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.put<unknown>(
          this.path(fresh),
          { policies: body },
          {
            observe: "response",
            headers: new HttpHeaders({
              "If-Match": retentionETag(fresh.allowedOrganizationId, version),
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

  private path(context: CatalogContext): string {
    return `/api/v1/organizations/${context.allowedOrganizationId}/administration/retention`;
  }
  private failure(error: unknown, context: CatalogContext): Observable<never> {
    if (error instanceof HttpErrorResponse) {
      this.contexts.reject(error.status, context);
      const code: unknown = error.error?.code;
      if (
        typeof code === "string" &&
        [
          "notFound",
          "conflict",
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
function policies(value: unknown): value is RetentionPolicies {
  if (
    !record(value) ||
    !isCatalogId(value["organizationId"]) ||
    !quotaCount(value["version"]) ||
    !Array.isArray(value["policies"]) ||
    value["policies"].length !== retentionFamilies.length
  )
    return false;
  return value["policies"].every((policy: unknown, index: number) => {
    if (!record(policy) || policy["family"] !== retentionFamilies[index])
      return false;
    const mode = policy["mode"];
    const configured = mode === "retain" || mode === "expire";
    return (
      (configured || mode === "notConfigured") &&
      (mode === "expire"
        ? retentionSeconds(policy["durationSeconds"])
        : policy["durationSeconds"] === null) &&
      (configured
        ? quotaCount(policy["policyVersion"]) &&
          typeof policy["effectiveAtUtc"] === "string"
        : policy["policyVersion"] === null &&
          policy["effectiveAtUtc"] === null) &&
      typeof policy["appliesToExisting"] === "boolean" &&
      (mode === "expire" || policy["appliesToExisting"] === false)
    );
  });
}
