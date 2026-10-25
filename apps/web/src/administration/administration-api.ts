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

export type OrganizationAdministration =
  components["schemas"]["OrganizationAdministrationV1"];
export type ProjectAdministration =
  components["schemas"]["ProjectAdministrationV1"];
export type AdministrationSnapshot<T> = {
  readonly body: T;
  readonly etag: string;
};
export class AdministrationFailure extends Error {
  constructor(readonly code: string) {
    super("The administration request was not confirmed.");
  }
}

@Injectable({ providedIn: "root" })
export class AdministrationApi {
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly contexts = inject(CatalogContextStore);

  organization(
    context: CatalogContext,
  ): Observable<AdministrationSnapshot<OrganizationAdministration>> {
    return this.read(context, "", isOrganization);
  }
  project(
    context: CatalogContext,
    projectId: string,
  ): Observable<AdministrationSnapshot<ProjectAdministration>> {
    if (!isCatalogId(projectId))
      throw new AdministrationFailure("invalidRequest");
    return this.read(context, `/projects/${projectId}`, isProject);
  }
  write(
    context: CatalogContext,
    projectId: string | null,
    suffix: string,
    method: "POST" | "PUT",
    etag: string,
    body: unknown,
  ): Observable<unknown> {
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.request(
          method,
          this.path(fresh, projectId ? `/projects/${projectId}` : "") + suffix,
          {
            body,
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
        if (response.status !== 200)
          throw new AdministrationFailure("unconfirmed");
        return response.body;
      }),
      catchError((error) => this.failure(error)),
    );
  }
  private read<T>(
    context: CatalogContext,
    project: string,
    guard: (value: unknown) => value is T,
  ): Observable<AdministrationSnapshot<T>> {
    return this.contexts.admitted(context).pipe(
      switchMap((fresh) =>
        this.http.get<unknown>(this.path(fresh, project), {
          observe: "response",
        }),
      ),
      timeout(20_000),
      map((response) => {
        const etag = response.headers.get("ETag");
        if (
          response.status !== 200 ||
          !guard(response.body) ||
          !etag ||
          etag !==
            `"admin-${context.allowedOrganizationId}-${(response.body as { version: number }).version}"`
        )
          throw new AdministrationFailure("unavailable");
        return { body: response.body, etag };
      }),
      catchError((error) => this.failure(error)),
    );
  }
  private path(context: CatalogContext, project: string): string {
    return `/api/v1/organizations/${context.allowedOrganizationId}${project}/administration`;
  }
  private failure(error: unknown): Observable<never> {
    if (error instanceof HttpErrorResponse) {
      this.contexts.reject(error.status);
      const code: unknown = error.error?.code;
      if (
        typeof code === "string" &&
        [
          "notFound",
          "lastAdministrator",
          "conflict",
          "preconditionFailed",
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
function snapshot(value: unknown): value is Record<string, unknown> {
  return (
    record(value) &&
    Number.isSafeInteger(value["version"]) &&
    (value["version"] as number) > 0 &&
    Array.isArray(value["members"]) &&
    value["members"].length <= 1000
  );
}
function isOrganization(value: unknown): value is OrganizationAdministration {
  return (
    snapshot(value) &&
    (value["members"] as unknown[]).every(
      (member) =>
        record(member) &&
        isCatalogId(member["principalId"]) &&
        typeof member["active"] === "boolean" &&
        typeof member["principalActive"] === "boolean" &&
        ["member", "administrator"].includes(member["role"] as string) &&
        Array.isArray(member["bindings"]) &&
        member["bindings"].every(
          (binding) =>
            record(binding) &&
            isCatalogId(binding["bindingId"]) &&
            typeof binding["enabled"] === "boolean",
        ),
    ) &&
    Array.isArray(value["invitations"]) &&
    value["invitations"].length <= 1000 &&
    value["invitations"].every(
      (i) => record(i) && isCatalogId(i["invitationId"]),
    ) &&
    Array.isArray(value["audit"]) &&
    value["audit"].length <= 100
  );
}
function isProject(value: unknown): value is ProjectAdministration {
  return (
    snapshot(value) &&
    (value["members"] as unknown[]).every(
      (member) =>
        record(member) &&
        isCatalogId(member["principalId"]) &&
        typeof member["active"] === "boolean" &&
        typeof member["payloadRead"] === "boolean" &&
        ["viewer", "runner", "publisher", "administrator"].includes(
          member["role"] as string,
        ),
    ) &&
    Array.isArray(value["grants"]) &&
    value["grants"].length <= 1000 &&
    value["grants"].every(
      (g) =>
        record(g) &&
        isCatalogId(g["grantId"]) &&
        isCatalogId(g["targetId"]) &&
        isCatalogId(g["objectId"]),
    )
  );
}
