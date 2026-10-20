import {
  DestroyRef,
  Injectable,
  computed,
  inject,
  signal,
} from "@angular/core";
import {
  Subject,
  Subscriber,
  catchError,
  defer,
  finalize,
  map,
  shareReplay,
  switchMap,
  takeUntil,
  tap,
  throwError,
  type Observable,
} from "rxjs";
import type { CatalogContext } from "../catalog/catalog-types";
import { BrowserSessionApi } from "./browser-session-api";
import { BrowserAuthNavigation } from "./browser-auth-navigation";
import {
  BrowserSessionError,
  type AuthStatus,
  type BrowserAuthAction,
  type BrowserAuthActionView,
  type BrowserSessionView,
  type SessionAuthStatus,
} from "./browser-session-types";

type ProtectedObservation = {
  readonly generation: number;
  readonly subscriber: Subscriber<void>;
  settled: boolean;
};

/** One tab-memory observation boundary. It neither caches authority nor publishes credentials. */
@Injectable({ providedIn: "root" })
export class BrowserSessionState {
  private readonly api = inject(BrowserSessionApi);
  private readonly navigation = inject(BrowserAuthNavigation);
  private readonly status = signal<AuthStatus | null>(null);
  private readonly view = signal<BrowserSessionView>({ kind: "checking" });
  private readonly revision = signal(0);
  private readonly changes = new Subject<void>();
  private pending: Observable<void> | null = null;
  private observation = 0;
  private protectedObservation: ProtectedObservation | null = null;
  private destroyed = false;
  private readonly actionView = signal<BrowserAuthActionView>({ kind: "idle" });
  private readonly actionPending = signal(false);
  private readonly actionLifetime = new Subject<void>();
  private actionOperation = 0;
  private mutating = false;
  readonly state = this.view.asReadonly();
  readonly generation = this.revision.asReadonly();
  readonly invalidated = this.changes.asObservable();
  readonly action = this.actionView.asReadonly();
  readonly busy = this.actionPending.asReadonly();
  readonly visible = computed(() => {
    const kind = this.view().kind;
    return kind === "development" || kind === "authenticated";
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      ++this.observation;
      this.pending = null;
      this.cancelProtectedObservation();
      // Auth callers own their cold subscription; service teardown also ends it.
      // Classifier retirement must never cancel a current auth action instead.
      this.actionLifetime.next();
      this.actionLifetime.complete();
    });
  }

  /** Fresh unless overlapping the same active observation; no completed result is reused. */
  observe(): Observable<void> {
    return defer(() => {
      if (this.destroyed) throw new BrowserSessionError();
      // No safe check may publish pre-mutation identity while a POST is unresolved.
      if (this.mutating) throw new BrowserSessionError();
      if (!this.actionPending()) this.actionView.set({ kind: "idle" });
      if (this.pending) return this.pending;
      this.cancelProtectedObservation();
      return this.startObservation();
    });
  }

  /** Clear a current Session request's authority, then classify identity once without retrying it. */
  rejectProtectedRequest(generation: number, context?: CatalogContext): void {
    if (this.destroyed || generation !== this.revision()) return;
    if (this.protectedObservation && !this.protectedObservation.settled) return;
    const status = this.status();
    // Checking hides dispatch, not the retained ownership of an already-sent request.
    if (status?.mode !== "session" || status.state !== "authenticated") return;
    if (context && !this.accepts(context)) return;
    this.cancelProtectedObservation();
    const observation: ProtectedObservation = {
      generation: generation + 1,
      subscriber: new Subscriber<void>({
        next: () => {},
        error: () => {},
        complete: () => {},
      }),
      settled: false,
    };
    // Own the intent and subscriber before synchronous consumers cancel their requests.
    this.protectedObservation = observation;
    observation.subscriber.add(() => {
      if (this.protectedObservation === observation)
        this.protectedObservation = null;
    });
    const boundary = ++this.observation;
    this.pending = null;
    this.clearBoundary(observation);
    if (
      this.protectedObservation !== observation ||
      boundary !== this.observation ||
      observation.generation !== this.revision()
    ) {
      if (this.protectedObservation === observation)
        this.cancelProtectedObservation();
      return;
    }
    // This subscriber is not owned by the rejected read/editor. The API bounds it to eight seconds.
    this.startObservation(observation).subscribe(observation.subscriber);
  }

  invalidate(): void {
    ++this.observation;
    this.pending = null;
    this.clearBoundary();
    this.view.set({ kind: "unavailable" });
  }

  /** Retained provenance for already-sent callbacks, also while a safe check hides the view. */
  accepts(context: CatalogContext): boolean {
    const status = this.status();
    return status?.mode === "development"
      ? context.mode === "development" &&
          (!("identityContext" in context) || context.identityContext == null)
      : status?.mode === "session" &&
          status.state === "authenticated" &&
          context.mode === "session" &&
          context.identityContext === status.identityContext;
  }

  canDispatch(context: CatalogContext): boolean {
    return this.visible() && this.accepts(context);
  }

  canPresent(action: BrowserAuthAction): boolean {
    const status = this.status();
    return (
      !this.destroyed &&
      !this.busy() &&
      allows(action, status) &&
      this.view().kind === status.state
    );
  }

  signIn(): Observable<void> {
    return this.perform("login");
  }

  logout(): Observable<void> {
    return this.perform("logout");
  }

  /** Only an explicit confirmed choice in the authenticationRequired UI calls this. */
  forgetLocal(): Observable<void> {
    return this.perform("forgetLocal");
  }

  private perform(action: BrowserAuthAction): Observable<void> {
    return defer(() => {
      const selected = this.status();
      if (!this.canPresent(action) || !allows(action, selected))
        throw new BrowserSessionError();

      const operation = ++this.actionOperation;
      const owner = identity(selected);
      const generation = this.generation();
      let dispatched = false;
      let settled = false;
      this.actionPending.set(true);
      this.actionView.set({ kind: "admitting", action });
      return this.observe().pipe(
        switchMap(() => {
          const status = this.status();
          // State and identityContext are part of owner; a new epoch cannot inherit intent.
          if (
            this.destroyed ||
            operation !== this.actionOperation ||
            generation !== this.generation() ||
            !allows(action, status) ||
            identity(status) !== owner ||
            this.view().kind !== status.state
          )
            throw new BrowserSessionError();

          this.mutating = true;
          const fence = this.generation() + 1;
          const observation = this.observation + 1;
          this.invalidate();
          // Synchronous invalidation listeners cannot replace this selected owner's fence.
          if (
            this.destroyed ||
            fence !== this.generation() ||
            observation !== this.observation
          )
            throw new BrowserSessionError();
          dispatched = true;
          this.actionView.set({ kind: "pending", action });
          return this.dispatch(action, status).pipe(
            map((result) => {
              if (
                this.destroyed ||
                operation !== this.actionOperation ||
                fence !== this.generation() ||
                observation !== this.observation
              )
                throw new BrowserSessionError();
              if (result.kind === "login") {
                this.navigation.go(result.authorizationUrl);
                if (
                  this.destroyed ||
                  operation !== this.actionOperation ||
                  fence !== this.generation() ||
                  observation !== this.observation
                )
                  throw new BrowserSessionError();
                this.actionView.set({ kind: "redirecting" });
              } else {
                this.actionView.set({
                  kind:
                    result.kind === "logout"
                      ? "logoutAcknowledged"
                      : "revocationUnconfirmed",
                });
              }
              settled = true;
            }),
          );
        }),
        catchError(() => {
          settled = true;
          if (operation === this.actionOperation)
            this.actionView.set(
              dispatched
                ? { kind: "unconfirmed", action }
                : { kind: "notAdmitted" },
            );
          return throwError(() => new BrowserSessionError());
        }),
        takeUntil(this.actionLifetime),
        finalize(() => {
          if (operation !== this.actionOperation) return;
          if (!settled)
            this.actionView.set(
              dispatched
                ? { kind: "unconfirmed", action }
                : { kind: "notAdmitted" },
            );
          this.mutating = false;
          this.actionPending.set(false);
        }),
      );
    });
  }

  private dispatch(
    action: BrowserAuthAction,
    status: SessionAuthStatus,
  ): Observable<AuthActionResult> {
    switch (action) {
      case "login":
        return this.api.login(status).pipe(
          map((result) => ({
            kind: "login" as const,
            authorizationUrl: result.authorizationUrl,
          })),
        );
      case "logout":
        return this.api
          .logout(status)
          .pipe(map(() => ({ kind: "logout" as const })));
      case "forgetLocal":
        return this.api
          .forgetLocal(status)
          .pipe(map(() => ({ kind: "forgetLocal" as const })));
    }
  }

  private startObservation(
    protectedObservation: ProtectedObservation | null = null,
  ): Observable<void> {
    const observation = ++this.observation;
    this.view.set({ kind: "checking" });
    let settled = false;
    const request = this.api.observe().pipe(
      map((status) => {
        if (observation !== this.observation) throw new BrowserSessionError();
        settled = true;
        if (protectedObservation) protectedObservation.settled = true;
        const previous = this.status();
        if (previous && identity(previous) !== identity(status))
          this.clearBoundary();
        if (observation !== this.observation) throw new BrowserSessionError();
        this.status.set(status);
        this.view.set({
          kind: status.mode === "session" ? status.state : status.mode,
        });
      }),
      catchError(() => {
        if (this.pending === request) this.pending = null;
        if (observation === this.observation) {
          settled = true;
          if (protectedObservation) protectedObservation.settled = true;
          // Rejection already cleared this boundary; a failed classification cannot clear it twice.
          if (!protectedObservation) this.clearBoundary();
          if (observation === this.observation)
            this.view.set({ kind: "unavailable" });
        }
        return throwError(() => new BrowserSessionError());
      }),
      tap({
        complete: () => {
          if (this.pending === request) this.pending = null;
        },
      }),
      finalize(() => {
        if (this.pending === request) this.pending = null;
        if (!settled && observation === this.observation) {
          // Canceling a safe check is not an observed identity change. Keep existing
          // sent-work bookkeeping private, while denying new dispatch/display.
          this.view.set({ kind: "unavailable" });
        }
      }),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
    this.pending = request;
    return request;
  }

  private cancelProtectedObservation(): void {
    const observation = this.protectedObservation;
    this.protectedObservation = null;
    // Release only this service's subscription; overlapping observers keep their own lifetime.
    observation?.subscriber.unsubscribe();
  }

  private clearBoundary(owner: ProtectedObservation | null = null): void {
    if (this.protectedObservation !== owner) this.cancelProtectedObservation();
    this.revision.update((value) => value + 1);
    this.status.set(null);
    this.view.set({ kind: "checking" });
    // Synchronous subscribers remove protected DTOs/requests before replacement publication.
    this.changes.next();
  }
}

type AuthActionResult =
  | { readonly kind: "login"; readonly authorizationUrl: string }
  | { readonly kind: "logout" | "forgetLocal" };

function allows(
  action: BrowserAuthAction,
  status: AuthStatus | null,
): status is SessionAuthStatus {
  if (status?.mode !== "session") return false;
  if (action === "forgetLocal")
    return status.state === "authenticationRequired";
  if (action === "logout") return status.state === "authenticated";
  return true;
}

function identity(status: AuthStatus): string {
  return status.mode === "session"
    ? `${status.mode}/${status.state}/${status.identityContext ?? ""}`
    : status.mode;
}
