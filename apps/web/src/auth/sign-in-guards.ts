import { inject } from "@angular/core";
import { Router, type CanActivateFn, type UrlTree } from "@angular/router";
import { catchError, map, of, type Observable } from "rxjs";
import { BrowserSessionState } from "./browser-session-state";
import { defaultReturnPath, isReturnPath } from "./return-path";

/** Session states in which opening an application section goes to the sign-in page. */
const signedOut = new Set([
  "anonymous",
  "authenticationRequired",
  "unavailable",
]);

/**
 * Opening an application section without a confirmed Session goes to `/sign-in`, carrying the
 * requested path when it is a valid return path. It applies when a section is entered, not to
 * a session that ends while the section is open: that keeps the page's own boundary handling.
 */
export const requireSignIn: CanActivateFn = (_route, state) =>
  afterObservation((session, router) =>
    signedOut.has(session.state().kind)
      ? router.createUrlTree(["/sign-in"], {
          queryParams: isReturnPath(state.url) ? { returnTo: state.url } : {},
        })
      : true,
  );

/** A confirmed Session, or the Development profile, has nothing to sign in to. */
export const signInEntry: CanActivateFn = () =>
  afterObservation((session, router) => {
    const kind = session.state().kind;
    return kind === "authenticated" || kind === "development"
      ? router.parseUrl(defaultReturnPath)
      : true;
  });

function afterObservation(
  decide: (session: BrowserSessionState, router: Router) => boolean | UrlTree,
): boolean | UrlTree | Observable<boolean | UrlTree> {
  const session = inject(BrowserSessionState);
  const router = inject(Router);
  if (session.state().kind !== "checking") return decide(session, router);
  return session.observe().pipe(
    map(() => decide(session, router)),
    catchError(() => of(decide(session, router))),
  );
}
