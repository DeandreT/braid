import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import { firstValueFrom, timeout } from "rxjs";
import type { components } from "../api/generated";
import { BrowserSessionState } from "../auth/browser-session-state";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog/catalog-types";
import { CatalogContextStore } from "../catalog/catalog-context";
import type { EditScript } from "./edit-core";

export type Draft = components["schemas"]["MappingDraftV1"];
export type DraftResult = components["schemas"]["MappingDraftResultV1"];
export type DraftComparison = components["schemas"]["RevisionComparisonV1"];
export type DraftPreview = components["schemas"]["CatalogPreviewRunV1"];
export type DraftValidation = components["schemas"]["ValidationReportV1"];
export class DraftError extends Error {
  constructor(
    readonly status: number,
    readonly result: DraftResult | null = null,
  ) {
    super(
      status === 412
        ? "The draft changed in another tab. Reload it before editing."
        : "The draft request could not be completed.",
    );
  }
}

@Injectable({ providedIn: "root" })
export class DraftApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);
  private readonly contexts = inject(CatalogContextStore);

  async list(
    context: CatalogContext,
    scope: CatalogMappingScope,
  ): Promise<Draft[]> {
    const value = await this.request<{ drafts: Draft[] }>(
      context,
      scope,
      "GET",
      "",
    );
    if (
      !Array.isArray(value.drafts) ||
      !value.drafts.every((d) => validDraft(d, context, scope))
    )
      throw new DraftError(0);
    return value.drafts;
  }
  async read(
    context: CatalogContext,
    scope: CatalogMappingScope,
    id: string,
  ): Promise<Draft> {
    const value = await this.request<Draft>(context, scope, "GET", suffix(id));
    if (!validDraft(value, context, scope) || value.id !== id)
      throw new DraftError(0);
    return value;
  }
  async create(
    context: CatalogContext,
    scope: CatalogMappingScope,
    revisionId: string,
    key: string,
  ): Promise<Draft> {
    return this.result(
      context,
      scope,
      "POST",
      "",
      { baseRevisionId: revisionId },
      key,
    );
  }
  async operations(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
  ): Promise<EditScript> {
    const value = await this.request<EditScript>(
      context,
      scope,
      "GET",
      suffix(draft.id) + "/operations",
    );
    if (
      value.format !== "ferrule.edit.v1" ||
      !Array.isArray(value.operations) ||
      value.operations.length > 2000
    )
      throw new DraftError(0);
    return value;
  }
  async base(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
  ): Promise<ArrayBuffer> {
    context = await firstValueFrom(this.contexts.admitted(context));
    const generation = this.admit(context, scope);
    const response = await firstValueFrom(
      this.http
        .get(path(scope) + suffix(draft.id) + "/base", {
          responseType: "arraybuffer",
          observe: "response",
        })
        .pipe(timeout(30_000)),
    );
    this.current(context, generation);
    const bytes = response.body;
    if (
      !bytes ||
      bytes.byteLength !== draft.base.nativeByteCount ||
      bytes.byteLength > 4 * 1024 * 1024
    )
      throw new DraftError(0);
    const digest = await crypto.subtle.digest("SHA-256", bytes);
    this.current(context, generation);
    const hex = Array.from(new Uint8Array(digest), (b) =>
      b.toString(16).padStart(2, "0"),
    ).join("");
    if (hex !== draft.base.nativeDigest.value) throw new DraftError(0);
    return bytes;
  }
  put(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
    operations: EditScript,
  ): Promise<Draft> {
    return this.result(
      context,
      scope,
      "PUT",
      suffix(draft.id) + "/operations",
      operations,
      undefined,
      tag(draft),
    );
  }
  checkpoint(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
  ): Promise<Draft> {
    return this.result(
      context,
      scope,
      "POST",
      suffix(draft.id) + "/checkpoints",
      { version: draft.version },
    );
  }
  rebase(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
    targetRevisionId: string,
  ): Promise<Draft> {
    return this.result(
      context,
      scope,
      "POST",
      suffix(draft.id) + "/rebase",
      { targetRevisionId },
      undefined,
      tag(draft),
    );
  }
  discard(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
  ): Promise<Draft> {
    return this.result(
      context,
      scope,
      "DELETE",
      suffix(draft.id),
      undefined,
      undefined,
      tag(draft),
    );
  }
  save(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
    key: string,
    dispatched?: () => void,
  ): Promise<components["schemas"]["CatalogRevisionIngestionResultV1"]> {
    return this.request(
      context,
      scope,
      "POST",
      suffix(draft.id) + "/save",
      { version: draft.version },
      key,
      undefined,
      dispatched,
    );
  }
  compare(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
  ): Promise<DraftComparison> {
    return this.request(
      context,
      scope,
      "GET",
      suffix(draft.id) + "/comparison",
    );
  }
  validation(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
  ): Promise<DraftValidation> {
    return this.request(
      context,
      scope,
      "GET",
      suffix(draft.id) + "/checkpoints/current/validation",
    );
  }
  preview(
    context: CatalogContext,
    scope: CatalogMappingScope,
    draft: Draft,
    key: string,
    body: components["schemas"]["CatalogPreviewSubmissionV1"],
  ): Promise<components["schemas"]["CatalogPreviewSubmitResultV1"]> {
    return this.request(
      context,
      scope,
      "POST",
      suffix(draft.id) + "/previews",
      body,
      key,
    );
  }
  async readPreview(
    context: CatalogContext,
    scope: CatalogMappingScope,
    runId: string,
  ): Promise<DraftPreview> {
    context = await firstValueFrom(this.contexts.admitted(context));
    const generation = this.admit(context, scope);
    if (!isCatalogId(runId)) throw new DraftError(0);
    const run = await firstValueFrom(
      this.http
        .get<DraftPreview>(
          `/api/v1/organizations/${scope.organizationId}/previews/${runId}`,
        )
        .pipe(timeout(15_000)),
    );
    this.current(context, generation);
    if (
      run.runId !== runId ||
      run.draft?.mapping.organizationId !== scope.organizationId ||
      run.draft.mapping.projectId !== scope.projectId ||
      run.draft.mapping.mappingId !== scope.mappingId
    )
      throw new DraftError(0);
    return run;
  }
  async previewOutput(
    context: CatalogContext,
    scope: CatalogMappingScope,
    run: DraftPreview,
    ordinal: number,
  ): Promise<string> {
    context = await firstValueFrom(this.contexts.admitted(context));
    const generation = this.admit(context, scope);
    const output = run.outputs.find((output) => output.ordinal === ordinal);
    if (
      !output ||
      !isCatalogId(run.runId) ||
      output.byteCount > 8 * 1024 * 1024
    )
      throw new DraftError(0);
    const bytes = await firstValueFrom(
      this.http
        .get(
          `/api/v1/organizations/${scope.organizationId}/previews/${run.runId}/outputs/${ordinal}`,
          { responseType: "arraybuffer" },
        )
        .pipe(timeout(30_000)),
    );
    const digest = await crypto.subtle.digest("SHA-256", bytes);
    this.current(context, generation);
    if (
      bytes.byteLength !== output.byteCount ||
      Array.from(new Uint8Array(digest), (b) =>
        b.toString(16).padStart(2, "0"),
      ).join("") !== output.sha256
    )
      throw new DraftError(0);
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  }
  private async result(
    context: CatalogContext,
    scope: CatalogMappingScope,
    method: string,
    suffix: string,
    body?: unknown,
    key?: string,
    match?: string,
  ): Promise<Draft> {
    const result = await this.request<DraftResult>(
      context,
      scope,
      method,
      suffix,
      body,
      key,
      match,
    );
    if (!validDraft(result.draft, context, scope)) throw new DraftError(0);
    return result.draft;
  }
  private async request<T>(
    context: CatalogContext,
    scope: CatalogMappingScope,
    method: string,
    suffix: string,
    body?: unknown,
    key?: string,
    match?: string,
    dispatched?: () => void,
  ): Promise<T> {
    context = await firstValueFrom(this.contexts.admitted(context));
    const generation = this.admit(context, scope);
    let headers = new HttpHeaders();
    if (method !== "GET")
      headers = headers.set("X-XSRF-TOKEN", context.requestToken);
    if (key) {
      if (!isCatalogId(key)) throw new DraftError(0);
      headers = headers.set("Idempotency-Key", key);
    }
    if (match) headers = headers.set("If-Match", match);
    try {
      dispatched?.();
      const value = await firstValueFrom(
        this.http
          .request<T>(method, path(scope) + suffix, { body, headers })
          .pipe(timeout(60_000)),
      );
      this.current(context, generation);
      return value;
    } catch (error) {
      if (error instanceof HttpErrorResponse) {
        if (error.status === 401 || error.status === 403)
          this.sessions.rejectProtectedRequest(generation, context);
        this.current(context, generation);
        if (
          suffix.endsWith("/save") &&
          (error.status === 409 || error.status === 422)
        ) {
          const ingestion =
            error.error as components["schemas"]["CatalogRevisionIngestionResultV1"];
          if (
            ingestion?.receipt?.futureRevision?.organizationId ===
              scope.organizationId &&
            ingestion.receipt.futureRevision.projectId === scope.projectId &&
            ingestion.receipt.futureRevision.mappingId === scope.mappingId &&
            isCatalogId(ingestion.receipt.operationId)
          )
            return ingestion as T;
        }
        const result = error.error as DraftResult | null;
        throw new DraftError(
          error.status,
          result &&
            typeof result.outcome === "string" &&
            (!result.draft || validDraft(result.draft, context, scope))
            ? result
            : null,
        );
      }
      throw error;
    }
  }
  private admit(context: CatalogContext, scope: CatalogMappingScope): number {
    if (
      !this.sessions.presents(context) ||
      context.allowedOrganizationId !== scope.organizationId ||
      !Object.values(scope).every(isCatalogId)
    )
      throw new DraftError(0);
    return this.sessions.generation();
  }
  private current(context: CatalogContext, generation: number): void {
    if (
      generation !== this.sessions.generation() ||
      !this.sessions.presents(context)
    )
      throw new DraftError(0);
  }
}
function path(scope: CatalogMappingScope): string {
  return `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/drafts`;
}
function suffix(id: string): string {
  if (!isCatalogId(id)) throw new DraftError(0);
  return `/${id}`;
}
function tag(draft: Draft): string {
  return `"draft-${draft.id}-${draft.version}"`;
}
function validDraft(
  value: unknown,
  context: CatalogContext,
  scope: CatalogMappingScope,
): value is Draft {
  if (!value || typeof value !== "object") return false;
  const draft = value as Draft;
  return (
    isCatalogId(draft.id) &&
    draft.authorPrincipalId === context.actorPrincipalId &&
    draft.mapping?.organizationId === scope.organizationId &&
    draft.mapping.projectId === scope.projectId &&
    draft.mapping.mappingId === scope.mappingId &&
    /^(0|[1-9][0-9]*)$/.test(draft.version) &&
    Number.isSafeInteger(draft.operationCount) &&
    draft.operationCount >= 0 &&
    draft.operationCount <= 2000
  );
}
