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
  takeUntil,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import type { components } from "../../api/generated";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogProjectScope,
} from "../catalog-types";

export type Policy = components["schemas"]["AutomationPolicyV1"];
export type Subscription = components["schemas"]["NotificationSubscriptionV1"];
export type Options = components["schemas"]["NotificationOptionsV1"];
export type Inbox = components["schemas"]["NotificationsV1"];
export type Deliveries = components["schemas"]["NotificationDeliveriesV1"];
export type Destinations = components["schemas"]["NotificationDestinationsV1"];
export type Suite = components["schemas"]["RegressionSuiteV1"];
export type Evaluations = components["schemas"]["RegressionEvaluationsV1"];

export function notificationEventLabel(event: string): string {
  const names: Record<string, string> = {
    revisionUploaded: "Revision uploaded",
    regressionFailed: "Regression failed",
    regressionRecovered: "Regression recovered",
    buildQualified: "Build qualified",
    buildFailed: "Build failed",
    releaseActivated: "Release activated",
    releaseRolledBack: "Release rolled back",
    endpointErrorRate: "Endpoint error rate",
    invitation: "Invitation",
  };
  return names[event] ?? "Notification";
}

@Injectable({ providedIn: "root" })
export class AutomationApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  inbox(
    context: CatalogContext,
    after: string | null = null,
  ): Observable<Inbox> {
    if (after !== null && !isCatalogId(after))
      throw new Error("Invalid notification cursor.");
    return this.request(
      context,
      { organizationId: context.allowedOrganizationId },
      "GET",
      "/notifications?limit=20" + (after ? "&afterId=" + after : ""),
    );
  }

  markInboxRead(
    context: CatalogContext,
    id: string,
  ): Observable<components["schemas"]["AutomationSavedV1"]> {
    if (!isCatalogId(id)) throw new Error("Invalid notification ID.");
    return this.request(
      context,
      { organizationId: context.allowedOrganizationId },
      "PUT",
      "/notifications/" + id + "/read",
      {},
    );
  }

  read<T>(
    context: CatalogContext,
    scope: CatalogProjectScope | { organizationId: string },
    path: string,
  ): Observable<T> {
    return this.request<T>(context, scope, "GET", path);
  }

  write(
    context: CatalogContext,
    scope: CatalogProjectScope,
    path: string,
    body: unknown,
  ): Observable<components["schemas"]["AutomationSavedV1"]> {
    return this.request<components["schemas"]["AutomationSavedV1"]>(
      context,
      scope,
      "PUT",
      path,
      body,
    );
  }

  private request<T>(
    context: CatalogContext,
    scope: CatalogProjectScope | { organizationId: string },
    method: string,
    path: string,
    body?: unknown,
  ): Observable<T> {
    const selected = { ...scope };
    return defer(() => {
      if (
        !isCatalogId(selected.organizationId) ||
        ("projectId" in selected && !isCatalogId(selected.projectId)) ||
        context.allowedOrganizationId !== selected.organizationId ||
        !this.sessions.canDispatch(context) ||
        !/^\/[a-z0-9/-]+(?:\?limit=(?:[1-9][0-9]?|100)(?:&afterId=[a-f0-9-]{36})?)?$/.test(
          path,
        )
      )
        throw new Error("Automation unavailable.");
      const generation = this.sessions.generation();
      const headers =
        method === "GET"
          ? new HttpHeaders()
          : new HttpHeaders({
              "Content-Type": "application/json",
              "X-XSRF-TOKEN": context.requestToken,
            });
      return this.http
        .request(
          method,
          `/api/v1/organizations/${selected.organizationId}${"projectId" in selected ? "/projects/" + selected.projectId : ""}${path}`,
          { headers, body, observe: "response", responseType: "text" },
        )
        .pipe(
          timeout(30000),
          takeUntil(this.sessions.invalidated),
          map((response) => {
            if (
              generation !== this.sessions.generation() ||
              !this.sessions.presents(context) ||
              (response.body?.length ?? 0) > 1024 * 1024
            )
              throw new Error("Automation unavailable.");
            const value: unknown = JSON.parse(response.body ?? "null");
            if (
              value === null ||
              (typeof value !== "object" && typeof value !== "boolean")
            )
              throw new Error("Automation unavailable.");
            return value as T;
          }),
          catchError((error: unknown) => {
            if (
              error instanceof HttpErrorResponse &&
              [401, 403].includes(error.status)
            )
              this.sessions.rejectProtectedRequest(generation, context);
            return throwError(() => new Error("Automation unavailable."));
          }),
        );
    });
  }
}
