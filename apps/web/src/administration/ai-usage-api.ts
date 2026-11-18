import {
  HttpClient,
  HttpErrorResponse,
  HttpParams,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  map,
  switchMap,
  takeUntil,
  throwError,
  timeout,
} from "rxjs";
import type { components } from "../api/generated";
import { BrowserSessionState } from "../auth/browser-session-state";
import { CatalogContextStore } from "../catalog/catalog-context";
import { isCatalogId, type CatalogContext } from "../catalog/catalog-types";

export type AiUsagePage = components["schemas"]["AiUsagePageV1"];
export type AiUsageGroup = components["schemas"]["AiUsageGroupV1"];
export const isUsageCursor = (value: unknown): value is string =>
  typeof value === "string" &&
  value.length > 0 &&
  value.length <= 1024 &&
  /^[A-Za-z0-9_-]+$/.test(value);
export const isUsageCount = (value: unknown): value is string =>
  typeof value === "string" &&
  /^(0|[1-9][0-9]{0,18})$/.test(value) &&
  (value.length < 19 || value <= "9223372036854775807");

const failureCategories = new Set([
  "InvalidRequest",
  "ProfileUnavailable",
  "MetadataDisallowed",
  "CapabilityUnavailable",
  "CredentialUnavailable",
  "Busy",
  "BudgetExceeded",
  "Cancelled",
  "DeadlineExceeded",
  "TransportFailure",
  "ProviderAuthentication",
  "ProviderRateLimited",
  "ProviderRejected",
  "ProviderUnavailable",
  "UnsupportedResponse",
  "MalformedResponse",
  "TruncatedResponse",
  "TurnFailed",
  "TurnCancelled",
  "TurnInterrupted",
]);
const isUtcDate = (value: unknown): value is string =>
  typeof value === "string" &&
  value.length <= 40 &&
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
    value,
  ) &&
  Number.isFinite(Date.parse(value));

@Injectable({ providedIn: "root" })
export class AiUsageApi {
  private readonly http = inject(HttpClient);
  private readonly contexts = inject(CatalogContextStore);
  private readonly sessions = inject(BrowserSessionState);

  read(context: CatalogContext, query: Readonly<Record<string, string>>) {
    return this.contexts.admitted(context).pipe(
      switchMap((current) => {
        const generation = this.sessions.generation();
        return this.http
          .get<AiUsagePage>(
            `/api/v1/organizations/${current.allowedOrganizationId}/usage`,
            {
              params: new HttpParams({ fromObject: query }),
            },
          )
          .pipe(
            timeout(20_000),
            takeUntil(this.sessions.invalidated),
            map((page) => {
              const valid = (group: AiUsageGroup) => {
                const usage = group.usage;
                const tokens = (
                  complete: string | null,
                  known: string,
                  missing: string,
                ) =>
                  BigInt(missing) <= BigInt(usage.rounds) &&
                  (missing === "0" ? complete === known : complete === null);
                return (
                  group.organizationId === current.allowedOrganizationId &&
                  (group.projectId === null || isCatalogId(group.projectId)) &&
                  (group.principalId === null ||
                    isCatalogId(group.principalId)) &&
                  (group.profileId === null ||
                    /^[a-z0-9][a-z0-9._-]{0,63}$/.test(group.profileId)) &&
                  (group.profileVersion === null ||
                    (group.profileId !== null &&
                      Number.isSafeInteger(group.profileVersion) &&
                      group.profileVersion > 0)) &&
                  (!query["projectId"] ||
                    group.projectId === query["projectId"]) &&
                  (!query["principalId"] ||
                    group.principalId === query["principalId"]) &&
                  (!query["profileId"] ||
                    group.profileId === query["profileId"]) &&
                  [
                    usage.turns,
                    usage.rounds,
                    usage.toolCalls,
                    usage.failedTurns,
                    usage.knownInputTokens,
                    usage.knownOutputTokens,
                    usage.knownTotalTokens,
                    usage.unknownInputRounds,
                    usage.unknownOutputRounds,
                    usage.unknownTotalRounds,
                  ].every(isUsageCount) &&
                  [
                    usage.inputTokens,
                    usage.outputTokens,
                    usage.totalTokens,
                  ].every((value) => value === null || isUsageCount(value)) &&
                  BigInt(usage.turns) > 0n &&
                  BigInt(usage.failedTurns) <= BigInt(usage.turns) &&
                  tokens(
                    usage.inputTokens,
                    usage.knownInputTokens,
                    usage.unknownInputRounds,
                  ) &&
                  tokens(
                    usage.outputTokens,
                    usage.knownOutputTokens,
                    usage.unknownOutputRounds,
                  ) &&
                  tokens(
                    usage.totalTokens,
                    usage.knownTotalTokens,
                    usage.unknownTotalRounds,
                  ) &&
                  Array.isArray(usage.failures) &&
                  usage.failures.length <= 20 &&
                  new Set(usage.failures.map((failure) => failure.category))
                    .size === usage.failures.length &&
                  usage.failures.every(
                    (failure) =>
                      failureCategories.has(failure.category) &&
                      isUsageCount(failure.count) &&
                      BigInt(failure.count) > 0n &&
                      BigInt(failure.count) <=
                        BigInt(
                          failure.category.startsWith("Turn")
                            ? usage.failedTurns
                            : usage.rounds,
                        ),
                  )
                );
              };
              if (
                generation !== this.sessions.generation() ||
                !this.contexts.isCurrent(current) ||
                !this.contexts.canPresent(current) ||
                typeof page.organizationWide !== "boolean" ||
                typeof page.includesPartialHistory !== "boolean" ||
                ![
                  page.since,
                  page.until,
                  page.observedAtUtc,
                  page.completeHistorySince,
                ].every(isUtcDate) ||
                !Array.isArray(page.groups) ||
                page.groups.length > 100 ||
                JSON.stringify(page).length > 512 * 1024 ||
                !page.groups.every(valid) ||
                (page.nextCursor !== null && !isUsageCursor(page.nextCursor))
              )
                throw new Error("Usage data is unavailable.");
              return page;
            }),
            catchError((error: unknown) => {
              if (
                error instanceof HttpErrorResponse &&
                [401, 403].includes(error.status)
              )
                this.sessions.rejectProtectedRequest(generation, current);
              return throwError(() => error);
            }),
          );
      }),
    );
  }
}
