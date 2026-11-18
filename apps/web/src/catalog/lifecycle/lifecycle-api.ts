import {
  HttpBackend,
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { Injectable, inject, signal } from "@angular/core";
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
import { UnsentWork } from "../../workbench/unsent-work";
import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
  type CatalogProjectScope,
} from "../catalog-types";

export type ResourceLifecycle = components["schemas"]["ResourceLifecycleV1"];
export type ResourceLifecycleReceipt =
  components["schemas"]["ResourceLifecycleReceiptV1"];
export type LifecycleAction = "archive" | "restore";
export type LifecycleScope = CatalogProjectScope | CatalogMappingScope;
export type LifecycleSnapshot = {
  readonly body: ResourceLifecycle;
  readonly etag: string;
};

/** Closed categories; "unconfirmed" means the outcome is unknown and must be read, never resent. */
export class LifecycleFailure extends Error {
  constructor(readonly code: string) {
    super("The lifecycle request was not confirmed.");
    this.name = "LifecycleFailure";
  }
}

export function lifecycleETag(
  scope: LifecycleScope,
  projectVersion: string,
  mappingVersion: string | null | undefined,
): string {
  return "mappingId" in scope
    ? `"lifecycle-${scope.mappingId}-${projectVersion}-${mappingVersion}"`
    : `"lifecycle-${scope.projectId}-${projectVersion}"`;
}

@Injectable({ providedIn: "root" })
export class LifecycleApi {
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly contexts = inject(CatalogContextStore);
  private pendingOwner: string | null = null;
  private readonly pending = signal<ReadonlyMap<string, string>>(new Map());

  constructor() {
    inject(UnsentWork).track(() => this.pending().size > 0);
  }

  /** Public operation identities only, in memory, until their same-owner receipt is read. */
  pendingOperation(
    context: CatalogContext,
    scope: LifecycleScope,
  ): string | null {
    const owner = catalogOwner(context);
    if (owner !== this.pendingOwner) {
      this.pendingOwner = owner;
      this.pending.set(new Map());
    }
    return this.pending().get(this.pendingKey(scope)) ?? null;
  }

  rememberOperation(
    context: CatalogContext,
    scope: LifecycleScope,
    operation: string,
  ): void {
    if (catalogOwner(context) !== this.pendingOwner) return;
    const pending = new Map(this.pending());
    pending.set(this.pendingKey(scope), operation);
    this.pending.set(pending);
  }

  forgetOperation(
    context: CatalogContext,
    scope: LifecycleScope,
    operation: string,
  ): void {
    if (catalogOwner(context) !== this.pendingOwner) return;
    const key = this.pendingKey(scope);
    if (this.pending().get(key) !== operation) return;
    const pending = new Map(this.pending());
    pending.delete(key);
    this.pending.set(pending);
  }

  private pendingKey(scope: LifecycleScope): string {
    return JSON.stringify([
      scope.organizationId,
      scope.projectId,
      "mappingId" in scope ? scope.mappingId : null,
    ]);
  }

  read(
    context: CatalogContext,
    scope: LifecycleScope,
  ): Observable<LifecycleSnapshot> {
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(`${this.path(fresh, scope)}/lifecycle`, {
          observe: "response",
        }),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body;
        const etag = response.headers.get("ETag");
        if (
          response.status !== 200 ||
          !lifecycle(value, scope) ||
          etag !==
            lifecycleETag(scope, value.projectVersion, value.mappingVersion)
        )
          throw new LifecycleFailure("unavailable");
        return { body: value, etag };
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  /** Exactly one request; the caller keeps the operation ID to resolve an unknown outcome. */
  change(
    context: CatalogContext,
    scope: LifecycleScope,
    action: LifecycleAction,
    etag: string,
    operationId: string,
  ): Observable<ResourceLifecycleReceipt> {
    if (
      !isCatalogId(operationId) ||
      (action !== "archive" && action !== "restore")
    )
      throw new LifecycleFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.post<unknown>(
          `${this.path(fresh, scope)}/lifecycle/actions/${operationId}`,
          { action },
          {
            observe: "response",
            headers: new HttpHeaders({
              "If-Match": etag,
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
          !receipt(value, operationId) ||
          value.action !== action
        )
          throw new LifecycleFailure("unconfirmed");
        return value;
      }),
      catchError((error) => this.failure(error, context)),
    );
  }

  /** Reads the original operation's receipt; this never repeats the change. */
  operation(
    context: CatalogContext,
    operationId: string,
  ): Observable<ResourceLifecycleReceipt> {
    if (!isCatalogId(operationId)) throw new LifecycleFailure("invalidRequest");
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(
          `/api/v1/organizations/${fresh.allowedOrganizationId}/lifecycle/operations/${operationId}`,
          { observe: "response" },
        ),
      ),
      timeout(20_000),
      map((response) => {
        const value = response.body;
        if (response.status !== 200 || !receipt(value, operationId))
          throw new LifecycleFailure("unavailable");
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
      throw new LifecycleFailure("invalidRequest");
    const project = `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}`;
    return "mappingId" in scope
      ? `${project}/mappings/${scope.mappingId}`
      : project;
  }

  private failure(error: unknown, context: CatalogContext): Observable<never> {
    if (error instanceof LifecycleFailure) return throwError(() => error);
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
        return throwError(() => new LifecycleFailure(code));
    }
    return throwError(() => new LifecycleFailure("unconfirmed"));
  }
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
function version(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[1-9][0-9]{0,18}$/.test(value) &&
    BigInt(value) <= 9223372036854775807n
  );
}
function lifecycle(
  value: unknown,
  scope: LifecycleScope,
): value is ResourceLifecycle {
  const mapping = "mappingId" in scope;
  return (
    record(value) &&
    value["resourceKind"] === (mapping ? "mapping" : "project") &&
    value["projectId"] === scope.projectId &&
    (mapping
      ? value["mappingId"] === scope.mappingId &&
        version(value["mappingVersion"])
      : value["mappingId"] == null && value["mappingVersion"] == null) &&
    ["active", "archived", "deleting"].includes(value["state"] as string) &&
    ["active", "archived", "deleting"].includes(
      value["effectiveState"] as string,
    ) &&
    version(value["projectVersion"]) &&
    typeof value["canArchive"] === "boolean" &&
    typeof value["canRestore"] === "boolean"
  );
}
function receipt(
  value: unknown,
  operationId: string,
): value is ResourceLifecycleReceipt {
  return (
    record(value) &&
    value["operationId"] === operationId &&
    ["archived", "restored", "replayed"].includes(value["outcome"] as string) &&
    (value["action"] === "archive" || value["action"] === "restore") &&
    isCatalogId(value["projectId"]) &&
    version(value["projectVersion"])
  );
}
