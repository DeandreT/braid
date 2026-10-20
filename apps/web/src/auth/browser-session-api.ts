import { HttpClient, HttpHeaders } from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  defer,
  map,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import {
  BrowserSessionError,
  isAuthStatus,
  type AuthStatus,
} from "./browser-session-types";

/** Only trusted host discovery/observation; no auth action or provider protocol runs here. */
@Injectable({ providedIn: "root" })
export class BrowserSessionApi {
  private readonly http = inject(HttpClient);

  observe(): Observable<AuthStatus> {
    return defer(() =>
      this.http.get<unknown>("/api/auth/session", {
        observe: "response",
        headers: new HttpHeaders({ "X-Weft-Session": "1" }),
      }),
    ).pipe(
      timeout(8000),
      map((response) => {
        if (response.status !== 200 || !isAuthStatus(response.body))
          throw new BrowserSessionError();
        // Own the closed scalar representation, including the two separate private AF values.
        return Object.freeze({ ...response.body });
      }),
      catchError(() => throwError(() => new BrowserSessionError())),
    );
  }
}
