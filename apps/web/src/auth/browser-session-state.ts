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
  tap,
  throwError,
  type Observable,
} from "rxjs";
import type { CatalogContext } from "../catalog/catalog-types";
import { BrowserSessionApi } from "./browser-session-api";
import {
  BrowserSessionError,
  type AuthStatus,
  type BrowserSessionView,
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
  private readonly status = signal<AuthStatus | null>(null);
  private readonly view = signal<BrowserSessionView>({ kind: "checking" });
  private readonly revision = signal(0);
  private readonly changes = new Subject<void>();
  private pending: Observable<void> | null = null;
  private observation = 0;
  private protectedObservation: ProtectedObservation | null = null;
  private destroyed = false;
  readonly state = this.view.asReadonly();
  readonly generation = this.revision.asReadonly();
  readonly invalidated = this.changes.asObservable();
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
    });
  }

  /** Fresh unless overlapping the same active observation; no completed result is reused. */
  observe(): Observable<void> {
    return defer(() => {
      if (this.destroyed) throw new BrowserSessionError();
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

function identity(status: AuthStatus): string {
  return status.mode === "session"
    ? `${status.mode}/${status.state}/${status.identityContext ?? ""}`
    : status.mode;
}
