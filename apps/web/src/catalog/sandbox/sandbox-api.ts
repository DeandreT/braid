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

export type SandboxState = components["schemas"]["LocalSandboxStateV1"];
export type SandboxTarget = components["schemas"]["LocalSandboxTargetV1"];
export type SandboxDeployment =
  components["schemas"]["LocalSandboxDeploymentV1"];
export type SandboxMessage = components["schemas"]["LocalSandboxMessageV1"];
export type SandboxMessagePage =
  components["schemas"]["LocalSandboxMessagePageV1"];
export type SandboxSend = components["schemas"]["LocalSandboxSendV1"];

/** The local deployment sandbox API. Release changes reuse the Operations API. */
@Injectable({ providedIn: "root" })
export class SandboxApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);
  private readonly catalog = inject(CatalogApi);

  state(context: CatalogContext, scope: CatalogMappingScope) {
    return this.request<SandboxState>(context, scope, "GET", "");
  }

  deploy(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    buildId: string,
  ) {
    return this.request<SandboxDeployment>(
      context,
      scope,
      "POST",
      `/deployments/${this.id(id)}`,
      { buildId: this.id(buildId) },
      true,
    );
  }

  messages(
    context: CatalogContext,
    scope: CatalogMappingScope,
    target: string,
  ) {
    return this.request<SandboxMessagePage>(
      context,
      scope,
      "GET",
      `/${this.id(target)}/messages?limit=20`,
    );
  }

  message(
    context: CatalogContext,
    scope: CatalogMappingScope,
    target: string,
    id: string,
  ) {
    return this.request<SandboxMessage>(
      context,
      scope,
      "GET",
      `/${this.id(target)}/messages/${this.id(id)}`,
    );
  }

  send(
    context: CatalogContext,
    scope: CatalogMappingScope,
    target: string,
    id: string,
    body: SandboxSend,
  ) {
    return this.request<SandboxMessage>(
      context,
      scope,
      "POST",
      `/${this.id(target)}/messages/${this.id(id)}`,
      body,
    );
  }

  private id(value: string): string {
    if (!isCatalogId(value)) throw new Error("Invalid identifier");
    return value;
  }

  private request<T>(
    context: CatalogContext,
    scope: CatalogMappingScope,
    method: "GET" | "POST",
    suffix: string,
    body?: unknown,
    administrator = false,
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
        (method === "POST" && !context.requestToken)
      )
        throw new Error("Workspace access unavailable");
      const generation = this.sessions.generation();
      const headers =
        method === "POST"
          ? new HttpHeaders({
              "Content-Type": "application/json",
              "X-XSRF-TOKEN": context.requestToken!,
            })
          : new HttpHeaders();
      // The server checks the project role again; this avoids sending work without it.
      return this.catalog.project(selected).pipe(
        switchMap((project) => {
          if (
            generation !== this.sessions.generation() ||
            !this.sessions.canDispatch(context) ||
            !["publisher", "administrator"].includes(project.access.role) ||
            (administrator && project.access.role !== "administrator")
          )
            throw new Error("Current project authority is required");
          return this.http
            .request<T>(
              method,
              `/api/v1/organizations/${selected.organizationId}/projects/${selected.projectId}/mappings/${selected.mappingId}/sandbox${suffix}`,
              {
                headers,
                body: body === undefined ? undefined : JSON.stringify(body),
              },
            )
            .pipe(
              // Deployment qualifies a build in the compiled runtime first.
              timeout(method === "POST" ? 180000 : 45000),
              takeUntil(this.sessions.invalidated),
              map((value) => {
                if (
                  generation !== this.sessions.generation() ||
                  !this.sessions.presents(context)
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
