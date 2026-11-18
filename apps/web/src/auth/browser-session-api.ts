import { HttpBackend, HttpClient, HttpHeaders } from "@angular/common/http";
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
  isAuthForgetLocal,
  isAuthLogin,
  isAuthStatus,
  type AuthForgetLocal,
  type AuthLogin,
  type AuthStatus,
  type SessionAuthStatus,
} from "./browser-session-types";
import { isReturnPath } from "./return-path";

/** Same-origin observation and bodyless auth actions; no provider protocol or token cache. */
/** A private invitation secret and the organization its link targets. */
export type InvitationLogin = {
  readonly secret: string;
  readonly organizationId: string | null;
};

@Injectable({ providedIn: "root" })
export class BrowserSessionApi {
  private readonly http = inject(HttpClient);
  // Mutations cannot inherit cookie-to-header XSRF fallback or interceptor retries.
  private readonly actions = new HttpClient(inject(HttpBackend));

  observe(): Observable<AuthStatus> {
    return defer(() =>
      this.http.get<unknown>("/api/auth/session", {
        observe: "response",
        headers: new HttpHeaders({ "X-Braid-Session": "1" }),
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

  /**
   * Sign-in returns to `returnTo`, which must satisfy the shared return-path rule; without one
   * the server returns to the application root, which opens the catalog.
   */
  login(
    status: SessionAuthStatus,
    returnTo: string | null,
    invitation?: InvitationLogin,
  ): Observable<AuthLogin> {
    return defer(() => {
      if (returnTo !== null && !isReturnPath(returnTo))
        throw new BrowserSessionError();
      let headers = actionHeaders(status, "X-Braid-Login", true);
      if (invitation !== undefined) {
        headers = headers.set("X-Braid-Invitation", invitation.secret);
        // The target is public routing metadata; legacy secret-only links target the root.
        if (invitation.organizationId !== null)
          headers = headers.set(
            "X-Braid-Invitation-Organization",
            invitation.organizationId,
          );
      }
      return this.actions.post<unknown>("/api/auth/login", null, {
        observe: "response",
        headers:
          returnTo === null
            ? headers
            : headers.set("X-Braid-Return-To", returnTo),
      });
    }).pipe(
      timeout(8000),
      map((response) => {
        if (response.status !== 200 || !isAuthLogin(response.body))
          throw new BrowserSessionError();
        return Object.freeze({ ...response.body });
      }),
      catchError(() => throwError(() => new BrowserSessionError())),
    );
  }

  logout(status: SessionAuthStatus): Observable<void> {
    return defer(() =>
      this.actions.post<unknown>("/api/auth/logout", null, {
        observe: "response",
        headers: actionHeaders(status, "X-Braid-Logout", true),
      }),
    ).pipe(
      timeout(8000),
      map((response) => {
        if (response.status !== 204 || response.body !== null)
          throw new BrowserSessionError();
      }),
      catchError(() => throwError(() => new BrowserSessionError())),
    );
  }

  forgetLocal(status: SessionAuthStatus): Observable<AuthForgetLocal> {
    return defer(() => {
      if (status.state !== "authenticationRequired")
        throw new BrowserSessionError();
      return this.actions.post<unknown>("/api/auth/forget-local", null, {
        observe: "response",
        headers: actionHeaders(status, "X-Braid-Forget-Local", false),
      });
    }).pipe(
      timeout(8000),
      map((response) => {
        if (response.status !== 200 || !isAuthForgetLocal(response.body))
          throw new BrowserSessionError();
        return Object.freeze({ ...response.body });
      }),
      catchError(() => throwError(() => new BrowserSessionError())),
    );
  }
}

function actionHeaders(
  status: SessionAuthStatus,
  action: "X-Braid-Login" | "X-Braid-Logout" | "X-Braid-Forget-Local",
  sessionToken: boolean,
): HttpHeaders {
  // Only fresh auth status supplies these distinct-purpose tokens, never catalog context.
  if (!isAuthStatus(status) || status.mode !== "session")
    throw new BrowserSessionError();
  let headers = new HttpHeaders({
    [action]: "1",
    "X-Braid-Auth-Token": status.authActionRequestToken,
  });
  if (sessionToken && status.state === "authenticated")
    headers = headers.set("X-XSRF-TOKEN", status.requestToken);
  // The browser supplies Origin on this same-origin POST. It is never caller-selected.
  return headers;
}
