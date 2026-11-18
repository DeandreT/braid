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

export type ResourceDeletionReceipt =
  components["schemas"]["ResourceDeletionReceiptV1"];
export type ResourceDeletionReferences =
  components["schemas"]["ResourceDeletionReferencesV1"];

/** One item; cases and environments name the exact version being deleted. */
export type DeletionTarget =
  | {
      readonly kind: "preview";
      readonly organizationId: string;
      readonly runId: string;
    }
  | {
      readonly kind: "conversation";
      readonly organizationId: string;
      readonly conversationId: string;
    }
  | {
      readonly kind: "regressionCase";
      readonly organizationId: string;
      readonly projectId: string;
      readonly mappingId: string;
      readonly caseId: string;
      readonly version: string;
    }
  | {
      readonly kind: "buildEnvironment";
      readonly organizationId: string;
      readonly projectId: string;
      readonly environmentId: string;
      readonly version: string;
    };

/** Closed categories; "unconfirmed" means the outcome is unknown and must be read, never resent. */
export class DeletionFailure extends Error {
  constructor(
    readonly code: string,
    readonly references: ResourceDeletionReferences | null = null,
  ) {
    super("The deletion was not confirmed.");
    this.name = "DeletionFailure";
  }
}

export function deletionResource(target: DeletionTarget): string {
  switch (target.kind) {
    case "preview":
      return target.runId;
    case "conversation":
      return target.conversationId;
    case "regressionCase":
      return target.caseId;
    case "buildEnvironment":
      return target.environmentId;
  }
}

@Injectable({ providedIn: "root" })
export class DeletionApi {
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly contexts = inject(CatalogContextStore);

  /** Exactly one request; the caller keeps the operation ID to resolve an unknown outcome. */
  delete(
    context: CatalogContext,
    target: DeletionTarget,
    operationId: string,
  ): Observable<ResourceDeletionReceipt> {
    if (!isCatalogId(operationId)) throw new DeletionFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) => {
        let headers = new HttpHeaders({
          "Braid-Operation-Id": operationId,
          "X-XSRF-TOKEN": fresh.requestToken,
        });
        if ("version" in target)
          headers = headers.set("If-Match", `"${target.version}"`);
        return this.http.delete<unknown>(this.path(fresh, target), {
          observe: "response",
          headers,
        });
      }),
      timeout(20_000),
      map((response) => {
        const value = response.body;
        if (
          response.status !== 200 ||
          !receipt(value, operationId) ||
          value.resourceKind !== target.kind ||
          value.resourceId !== deletionResource(target)
        )
          throw new DeletionFailure("unconfirmed");
        return value;
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  /** Reads the original operation's receipt; this never repeats the deletion. */
  operation(
    context: CatalogContext,
    operationId: string,
  ): Observable<ResourceDeletionReceipt> {
    if (!isCatalogId(operationId)) throw new DeletionFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(
          `/api/v1/organizations/${fresh.allowedOrganizationId}/deletions/${operationId}`,
          { observe: "response" },
        ),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body;
        if (response.status !== 200 || !receipt(value, operationId))
          throw new DeletionFailure("unavailable");
        return value;
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  private path(context: CatalogContext, target: DeletionTarget): string {
    const ids =
      target.kind === "regressionCase"
        ? [target.projectId, target.mappingId, target.caseId]
        : target.kind === "buildEnvironment"
          ? [target.projectId, target.environmentId]
          : [deletionResource(target)];
    if (
      target.organizationId !== context.allowedOrganizationId ||
      !ids.every(isCatalogId) ||
      ("version" in target && !/^[1-9][0-9]{0,18}$/.test(target.version))
    )
      throw new DeletionFailure("invalidRequest");
    const organization = `/api/v1/organizations/${target.organizationId}`;
    switch (target.kind) {
      case "preview":
        return `${organization}/previews/${target.runId}`;
      case "conversation":
        return `${organization}/chat/conversations/${target.conversationId}`;
      case "regressionCase":
        return `${organization}/projects/${target.projectId}/mappings/${target.mappingId}/regression-cases/${target.caseId}`;
      case "buildEnvironment":
        return `${organization}/projects/${target.projectId}/build-environments/${target.environmentId}`;
    }
  }

  private failure(error: unknown, context: CatalogContext): Observable<never> {
    if (error instanceof DeletionFailure) return throwError(() => error);
    if (error instanceof HttpErrorResponse) {
      this.contexts.reject(error.status, context);
      const code: unknown = error.error?.code;
      if (
        typeof code === "string" &&
        [
          "notFound",
          "conflict",
          "activeWork",
          "referencedResource",
          "preconditionFailed",
          "preconditionRequired",
          "invalidRequest",
        ].includes(code)
      ) {
        const references: unknown = error.error?.references;
        return throwError(
          () =>
            new DeletionFailure(
              code,
              record(references)
                ? (references as ResourceDeletionReferences)
                : null,
            ),
        );
      }
    }
    return throwError(() => new DeletionFailure("unconfirmed"));
  }
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
function receipt(
  value: unknown,
  operationId: string,
): value is ResourceDeletionReceipt {
  return (
    record(value) &&
    value["operationId"] === operationId &&
    ["deleted", "replayed"].includes(value["outcome"] as string) &&
    ["preview", "conversation", "regressionCase", "buildEnvironment"].includes(
      value["resourceKind"] as string,
    ) &&
    isCatalogId(value["resourceId"])
  );
}
