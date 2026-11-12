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
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
  type CatalogProjectScope,
} from "../catalog-types";
import {
  BuildError,
  approval,
  approvalBody,
  backend,
  environment,
  environmentBody,
  invalid,
  matchesApproval,
  nullableId,
  profile,
  csvProfile,
  run,
  shape,
  version,
  type ApprovalPage,
  type ApprovalReceipt,
  type BuildApproval,
  type BuildCapabilities,
  type BuildEnvironment,
  type BuildRun,
  type BuildRunPage,
  type BuildRunReceipt,
  type EnvironmentCapabilities,
  type EnvironmentPage,
  type EnvironmentReceipt,
} from "./build-values";

export interface EnvironmentCursor {
  environmentId: string;
  version: string;
}

/** Metadata-only, session-bound requests. Original mutation bodies are strings and are sent unchanged. */
@Injectable({ providedIn: "root" })
export class BuildApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  capabilities(
    context: CatalogContext,
    scope: CatalogMappingScope,
  ): Observable<BuildCapabilities> {
    scope = { ...scope };
    return this.request(
      context,
      scope,
      scope.mappingId,
      "GET",
      "/build-capabilities",
      undefined,
      (v) => {
        if (
          !shape(
            v,
            [
              "backend",
              "profile",
              "readOwnMetadata",
              "approveBuilds",
              "submitBuilds",
            ],
            [
              "documentBackend",
              "documentProfile",
              "interchangeProfile",
              "csvBackend",
              "csvProfile",
            ],
          ) ||
          !backend(v.backend) ||
          !profile(v.profile) ||
          (v.documentBackend !== undefined && !backend(v.documentBackend)) ||
          (v.documentProfile !== undefined && !profile(v.documentProfile)) ||
          (v.interchangeProfile !== undefined &&
            (!profile(v.interchangeProfile) ||
              v.documentProfile === undefined)) ||
          (v.documentBackend === undefined) !==
            (v.documentProfile === undefined) ||
          (v.csvBackend === undefined) !== (v.csvProfile === undefined) ||
          (v.csvBackend !== undefined && !backend(v.csvBackend)) ||
          (v.csvBackend !== undefined &&
            !["backendId", "engineRevision", "engineBuild"].every(
              (key) =>
                (v.csvBackend as Record<string, unknown>)[key] ===
                (v.backend as Record<string, unknown>)[key],
            )) ||
          (v.csvProfile !== undefined && !csvProfile(v.csvProfile)) ||
          ![v.readOwnMetadata, v.approveBuilds, v.submitBuilds].every(
            (x) => typeof x === "boolean",
          ) ||
          (!v.readOwnMetadata && (v.approveBuilds || v.submitBuilds))
        )
          invalid();
        return v as unknown as BuildCapabilities;
      },
    );
  }
  environmentCapabilities(
    context: CatalogContext,
    scope: CatalogProjectScope,
  ): Observable<EnvironmentCapabilities> {
    scope = { ...scope };
    return this.request(
      context,
      scope,
      null,
      "GET",
      "/build-environment-capabilities",
      undefined,
      (v) => {
        if (
          !shape(
            v,
            ["profile", "canCreateVersions"],
            ["documentProfile", "interchangeProfile", "csvProfile"],
          ) ||
          !profile(v.profile) ||
          (v.documentProfile !== undefined && !profile(v.documentProfile)) ||
          (v.csvProfile !== undefined && !csvProfile(v.csvProfile)) ||
          (v.interchangeProfile !== undefined &&
            (!profile(v.interchangeProfile) ||
              v.documentProfile === undefined)) ||
          typeof v.canCreateVersions !== "boolean"
        )
          invalid();
        return v as unknown as EnvironmentCapabilities;
      },
    );
  }
  environments(
    context: CatalogContext,
    scope: CatalogProjectScope,
    after?: EnvironmentCursor,
  ): Observable<EnvironmentPage> {
    scope = { ...scope };
    if (after && (!isCatalogId(after.environmentId) || !version(after.version)))
      throw new BuildError("invalid");
    const suffix =
      "/build-environments?limit=20" +
      (after
        ? `&afterEnvironmentId=${after.environmentId}&afterVersion=${after.version}`
        : "");
    return this.request(context, scope, null, "GET", suffix, undefined, (v) => {
      if (
        !shape(v, ["environments", "nextEnvironmentId", "nextVersion"]) ||
        !Array.isArray(v.environments) ||
        v.environments.length > 20 ||
        !nullableId(v.nextEnvironmentId) ||
        (v.nextEnvironmentId === null
          ? v.nextVersion !== null
          : !version(v.nextVersion))
      )
        invalid();
      const rows = v.environments.map((x) => environment(x, scope));
      if (
        new Set(
          rows.map(
            (x) => x.reference.environmentId + "/" + x.reference.version,
          ),
        ).size !== rows.length
      )
        invalid();
      const last = rows.at(-1);
      if (
        v.nextEnvironmentId !== null &&
        (!last ||
          last.reference.environmentId !== v.nextEnvironmentId ||
          last.reference.version !== v.nextVersion)
      )
        invalid();
      return {
        environments: rows,
        nextEnvironmentId: v.nextEnvironmentId,
        nextVersion: v.nextVersion,
      } as EnvironmentPage;
    });
  }
  environment(
    context: CatalogContext,
    scope: CatalogProjectScope,
    id: string,
    number: string,
  ): Observable<BuildEnvironment> {
    scope = { ...scope };
    ids(id, number);
    return this.request(
      context,
      scope,
      null,
      "GET",
      `/build-environments/${id}/versions/${number}`,
      undefined,
      (v) => {
        const value = environment(v, scope);
        if (
          value.reference.environmentId !== id ||
          value.reference.version !== number
        )
          invalid();
        return value;
      },
    );
  }
  putEnvironment(
    context: CatalogContext,
    scope: CatalogProjectScope,
    id: string,
    number: string,
    body: string,
  ): Observable<EnvironmentReceipt> {
    scope = { ...scope };
    ids(id, number);
    const selected = environmentBody(body);
    return this.request(
      context,
      scope,
      null,
      "PUT",
      `/build-environments/${id}/versions/${number}`,
      body,
      (v, status) => {
        if (!shape(v, ["outcome", "environment"])) invalid();
        receipt(status, v.outcome);
        const value = environment(v.environment, scope);
        if (
          value.reference.environmentId !== id ||
          value.reference.version !== number ||
          value.profile.id !== selected.profileId ||
          value.profile.version !== selected.profileVersion
        )
          invalid();
        return { outcome: v.outcome, environment: value } as EnvironmentReceipt;
      },
      "environment",
    );
  }
  approvals(
    context: CatalogContext,
    scope: CatalogMappingScope,
    after?: string,
  ): Observable<ApprovalPage> {
    scope = { ...scope };
    if (after && !isCatalogId(after)) throw new BuildError("invalid");
    return this.request(
      context,
      scope,
      scope.mappingId,
      "GET",
      "/build-approvals?limit=20" + (after ? "&afterApprovalId=" + after : ""),
      undefined,
      (v) => {
        if (
          !shape(v, ["approvals", "nextApprovalId"]) ||
          !Array.isArray(v.approvals) ||
          v.approvals.length > 20 ||
          !nullableId(v.nextApprovalId)
        )
          invalid();
        const rows = v.approvals.map((x) => approval(x, scope));
        if (
          new Set(rows.map((x) => x.approvalId)).size !== rows.length ||
          new Set(rows.map((x) => x.buildId)).size !== rows.length ||
          (v.nextApprovalId !== null &&
            rows.at(-1)?.approvalId !== v.nextApprovalId)
        )
          invalid();
        return {
          approvals: rows,
          nextApprovalId: v.nextApprovalId,
        } as ApprovalPage;
      },
    );
  }
  approval(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    revoke = false,
  ): Observable<BuildApproval> {
    scope = { ...scope };
    ids(id);
    return this.request(
      context,
      scope,
      scope.mappingId,
      revoke ? "POST" : "GET",
      `/build-approvals/${id}` + (revoke ? "/revoke" : ""),
      undefined,
      (v) => {
        const value = approval(v, scope);
        if (value.approvalId !== id || (revoke && !value.revoked)) invalid();
        return value;
      },
    );
  }
  putApproval(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    body: string,
  ): Observable<ApprovalReceipt> {
    scope = { ...scope };
    ids(id);
    const selected = approvalBody(body);
    return this.request(
      context,
      scope,
      scope.mappingId,
      "PUT",
      `/build-approvals/${id}`,
      body,
      (v, status) => {
        if (!shape(v, ["outcome", "approval"])) invalid();
        receipt(status, v.outcome);
        const value = approval(v.approval, scope);
        if (value.approvalId !== id || !matchesApproval(value, selected))
          invalid();
        return { outcome: v.outcome, approval: value } as ApprovalReceipt;
      },
      "approval",
    );
  }
  runs(
    context: CatalogContext,
    scope: CatalogMappingScope,
    after?: string,
  ): Observable<BuildRunPage> {
    scope = { ...scope };
    if (after && !isCatalogId(after)) throw new BuildError("invalid");
    return this.request(
      context,
      scope,
      scope.mappingId,
      "GET",
      "/build-runs?limit=20" + (after ? "&afterBuildId=" + after : ""),
      undefined,
      (v) => {
        if (
          !shape(v, ["runs", "nextBuildId"]) ||
          !Array.isArray(v.runs) ||
          v.runs.length > 20 ||
          !nullableId(v.nextBuildId)
        )
          invalid();
        const rows = v.runs.map((x) => run(x, scope));
        if (
          new Set(rows.map((x) => x.buildId)).size !== rows.length ||
          (v.nextBuildId !== null && rows.at(-1)?.buildId !== v.nextBuildId)
        )
          invalid();
        return { runs: rows, nextBuildId: v.nextBuildId } as BuildRunPage;
      },
    );
  }
  run(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    cancel = false,
  ): Observable<BuildRun> {
    scope = { ...scope };
    ids(id);
    return this.request(
      context,
      scope,
      scope.mappingId,
      cancel ? "POST" : "GET",
      `/build-runs/${id}` + (cancel ? "/cancel" : ""),
      undefined,
      (v) => {
        const value = run(v, scope);
        if (value.buildId !== id) invalid();
        return value;
      },
    );
  }
  submit(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
    body: string,
  ): Observable<BuildRunReceipt> {
    scope = { ...scope };
    ids(id);
    if (new TextEncoder().encode(body).length > 2048)
      throw new BuildError("invalid");
    let selected: unknown;
    try {
      selected = JSON.parse(body);
    } catch {
      throw new BuildError("invalid");
    }
    if (
      !shape(selected, ["approvalId"]) ||
      !isCatalogId(selected.approvalId) ||
      JSON.stringify(selected) !== body
    )
      throw new BuildError("invalid");
    const approvalId = selected.approvalId;
    return this.request(
      context,
      scope,
      scope.mappingId,
      "PUT",
      `/build-runs/${id}`,
      body,
      (v, status) => {
        if (!shape(v, ["outcome", "run"])) invalid();
        receipt(status, v.outcome);
        const value = run(v.run, scope);
        if (value.buildId !== id || value.approvalId !== approvalId) invalid();
        return { outcome: v.outcome, run: value } as BuildRunReceipt;
      },
      "run",
    );
  }

  private request<T>(
    context: CatalogContext,
    scope: CatalogProjectScope,
    mappingId: string | null,
    method: "GET" | "PUT" | "POST",
    suffix: string,
    body: string | undefined,
    decode: (value: unknown, status: number) => T,
    media?: "environment" | "approval" | "run",
  ): Observable<T> {
    context = { ...context };
    const selected = {
      organizationId: scope.organizationId,
      projectId: scope.projectId,
      mappingId,
    };
    return defer(() => {
      if (
        !isCatalogId(selected.organizationId) ||
        !isCatalogId(selected.projectId) ||
        (selected.mappingId !== null && !isCatalogId(selected.mappingId)) ||
        context.allowedOrganizationId !== selected.organizationId ||
        !this.sessions.canDispatch(context)
      )
        throw new BuildError("rejected");
      if (
        method !== "GET" &&
        (!context.requestToken || /[\r\n\0]/.test(context.requestToken))
      )
        throw new BuildError("rejected");
      if (body !== undefined && new TextEncoder().encode(body).length > 2048)
        throw new BuildError("invalid");
      const generation = this.sessions.generation();
      const headers = new HttpHeaders(
        method === "GET"
          ? {}
          : {
              "X-XSRF-TOKEN": context.requestToken,
              ...(media
                ? {
                    "Content-Type": `application/vnd.weft.build-${media}.v1+json`,
                  }
                : {}),
            },
      );
      const url =
        `/api/v1/organizations/${selected.organizationId}/projects/${selected.projectId}` +
        (selected.mappingId === null ? "" : `/mappings/${selected.mappingId}`) +
        suffix;
      return this.http
        .request(method, url, {
          body,
          headers,
          observe: "response",
          responseType: "text",
        })
        .pipe(
          timeout(45000),
          takeUntil(this.sessions.invalidated),
          map((response) => {
            // A routine check does not refuse a sent request's reply; a boundary does.
            if (
              generation !== this.sessions.generation() ||
              !this.sessions.presents(context)
            )
              throw new BuildError("rejected");
            if (
              (method === "PUT"
                ? ![200, 201].includes(response.status)
                : response.status !== 200) ||
              new TextEncoder().encode(response.body || "").length >
                4 * 1024 * 1024
            )
              invalid();
            return decode(JSON.parse(response.body || "null"), response.status);
          }),
          catchError((error: unknown) => {
            if (
              error instanceof HttpErrorResponse &&
              [401, 403].includes(error.status)
            )
              this.sessions.rejectProtectedRequest(generation, context);
            return throwError(() => failure(error));
          }),
        );
    }).pipe(catchError((error: unknown) => throwError(() => failure(error))));
  }
}
function ids(id: string, number?: string): void {
  if (!isCatalogId(id) || (number !== undefined && !version(number)))
    throw new BuildError("invalid");
}
function receipt(status: number, outcome: unknown): void {
  if (
    status === 201
      ? outcome !== "created"
      : status !== 200 || outcome !== "replayed"
  )
    invalid();
}
function failure(error: unknown): BuildError {
  if (error instanceof BuildError) return error;
  if (error instanceof SyntaxError) return new BuildError("incompatible");
  if (!(error instanceof HttpErrorResponse))
    return new BuildError("unavailable");
  return new BuildError(
    error.status === 404
      ? "notFound"
      : [401, 403].includes(error.status)
        ? "rejected"
        : error.status === 409
          ? "conflict"
          : error.status === 412
            ? "precondition"
            : error.status === 429
              ? "busy"
              : [400, 413, 415].includes(error.status)
                ? "invalid"
                : "unavailable",
  );
}
