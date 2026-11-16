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
  of,
  switchMap,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import { BrowserSessionState } from "../../auth/browser-session-state";
import { CatalogContextStore } from "../catalog-context";
import type { CatalogContext } from "../catalog-types";
import { mfdFailureOutcome, readMfdReview } from "./mfd-interchange";
import { mfdProtectedResponse } from "./mfd-request-boundary";

@Injectable({ providedIn: "root" })
export class MfdInterchangeApi {
  private readonly http = inject(HttpClient);
  private readonly contexts = inject(CatalogContextStore);
  private readonly sessions = inject(BrowserSessionState);
  capabilities() {
    return this.request(() =>
      this.http.get<{ enabled: boolean }>("/api/v1/catalog/interchange"),
    ).pipe(
      timeout(10000),
      map((value) => value?.enabled === true),
    );
  }
  review(path: string, body: Blob | null) {
    return this.send(
      path,
      body,
      body ? "application/vnd.braid.mfd-package.v1" : null,
    ).pipe(map(readMfdReview));
  }
  restore(path: string) {
    return this.request(() => this.http.get<unknown>(path)).pipe(
      timeout(10000),
      map(readMfdReview),
    );
  }
  confirm(
    path: string,
    reviewId: string,
    reviewDigest: string,
    acknowledgeWarnings: boolean,
  ) {
    return this.send(
      path,
      JSON.stringify({ reviewDigest, acknowledgeWarnings }),
      "application/json",
      reviewId,
    ).pipe(
      catchError((error: unknown) => {
        // Native ingestion uses non-2xx statuses for honest terminal/retry outcomes.
        // Only typed ingestion bodies are candidates; catalog errors and uncertain
        // transport failures remain errors, and the component validates their scope.
        if (error instanceof HttpErrorResponse) {
          const known = mfdFailureOutcome(error.status, error.error);
          if (known) return of(known as unknown);
        }
        return throwError(() => error);
      }),
    );
  }
  download(path: string, reviewDigest: string, acknowledgeWarnings: boolean) {
    return this.request((current) =>
      this.http.post(
        path,
        JSON.stringify({ reviewDigest, acknowledgeWarnings }),
        {
          headers: new HttpHeaders({
            "Content-Type": "application/json",
            "X-XSRF-TOKEN": current.requestToken,
          }),
          responseType: "blob",
          observe: "response",
        },
      ),
    ).pipe(timeout(60000));
  }
  private send(
    path: string,
    body: Blob | string | null,
    media: string | null,
    key?: string,
  ) {
    return this.request((current) => {
      let headers = new HttpHeaders({ "X-XSRF-TOKEN": current.requestToken });
      if (media) headers = headers.set("Content-Type", media);
      if (key) headers = headers.set("Idempotency-Key", key);
      return this.http.post<unknown>(path, body, { headers });
    }).pipe(timeout(60000));
  }

  private request<T>(
    send: (context: CatalogContext) => Observable<T>,
  ): Observable<T> {
    return defer(() => {
      const context = this.contexts.context();
      if (!context) throw new Error("Current identity is unavailable.");
      return this.contexts
        .admitted(context)
        .pipe(
          switchMap((current) =>
            send(current).pipe(
              mfdProtectedResponse(
                this.sessions,
                current,
                this.sessions.generation(),
                (error) =>
                  error instanceof HttpErrorResponse ? error.status : null,
              ),
            ),
          ),
        );
    });
  }
}
