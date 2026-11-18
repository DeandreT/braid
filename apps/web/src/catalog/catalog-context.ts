import { DestroyRef, Injectable, inject, signal } from "@angular/core";
import {
  asapScheduler,
  catchError,
  defer,
  finalize,
  map,
  merge,
  observeOn,
  of,
  switchMap,
  take,
  tap,
  throwError,
  type Observable,
  Subject,
} from "rxjs";
import { CatalogApi, catalogFailure } from "./catalog-api";
import { OrganizationSelection } from "./organization-selection";
import {
  CatalogReadError,
  catalogOwner,
  type CatalogContext,
  type CatalogContextState,
} from "./catalog-types";
import { BrowserSessionState } from "../auth/browser-session-state";

/**
 * Admission is fetched for every read selection; neither identity nor tokens are persisted.
 *
 * The confirmed context stays in place while a fresh one is read. A fresh context for the same
 * owner and admission incarnation joins its lineage, keeping earlier admitted content current.
 * A different owner or incarnation, a failed read or a session boundary ends the lineage.
 */
@Injectable({ providedIn: "root" })
export class CatalogContextStore {
  private readonly api = inject(CatalogApi);
  private readonly sessions = inject(BrowserSessionState);
  private readonly selection = inject(OrganizationSelection);
  private readonly current = signal<CatalogContext | null>(null);
  private currentOwner: string | null = null;
  private readonly currentState = signal<CatalogContextState>({ kind: "idle" });
  private readonly identityRevision = signal(0);
  private lineage = new WeakSet<CatalogContext>();
  private lastIdentity: string | null = null;
  private generation = 0;
  private selections = 0;
  private readonly changes = new Subject<void>();
  private readonly settles = new Subject<void>();
  readonly invalidated = this.changes.asObservable();
  readonly visible = this.sessions.visible;
  readonly checking = this.sessions.checking;
  readonly settled = this.sessions.settled;
  readonly context = this.current.asReadonly();
  readonly state = this.currentState.asReadonly();
  readonly identityVersion = this.identityRevision.asReadonly();
  /** The tab's route-selected organization; `null` is a root-only route. */
  readonly organizationId = this.selection.organizationId;

  constructor() {
    const subscription = this.sessions.invalidated.subscribe(() => {
      this.selection.observeRoot(null);
      this.clear();
      this.identityRevision.update((value) => value + 1);
      this.changes.next();
    });
    subscription.add(
      this.sessions.routineSettled.subscribe(() => this.settles.next()),
    );
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  /** Content admitted under this context may stay on screen, also during a routine check. */
  canPresent(context: CatalogContext | null): boolean {
    return context !== null && this.sessions.presents(context);
  }

  /** A new request may start under this context now. */
  canDispatch(context: CatalogContext | null): boolean {
    return context !== null && this.sessions.canDispatch(context);
  }

  /** No session check or catalog context read is pending and the context is current. */
  canSend(context: CatalogContext | null): boolean {
    return (
      this.currentState().kind === "ready" &&
      this.isCurrent(context) &&
      this.canDispatch(context)
    );
  }

  /**
   * Mounted content that has no fresh admission of its own sends through this. It waits until no
   * session check or catalog context read is pending, then emits the freshest confirmed context
   * of the given context's same-owner lineage, whose request token is current. Once the context
   * is no longer current or presented it fails as `unavailable`, and nothing is sent; that is not
   * a refusal, so callers keep their retained work for the boundary handling that follows.
   *
   * A waiter decides again only after the settling task has finished: a refresh delivers its
   * result to its own reader first, a canceled read's replacement has started, and consumers of a
   * boundary have suspended or cleared their work.
   */
  admitted(context: CatalogContext): Observable<CatalogContext> {
    return defer(() => this.decide(context, this.selections));
  }

  /**
   * A page starts reading a different selection. Requests still waiting for admission belong to
   * the selection being replaced, so they fail rather than wait for its teardown.
   */
  beginSelection(): void {
    ++this.selections;
    this.settles.next();
  }

  /** The context is the confirmed one, or an earlier context of its same-owner lineage. */
  isCurrent(context: CatalogContext | null): boolean {
    const current = this.current();
    return (
      current !== null &&
      context !== null &&
      (context === current || this.lineage.has(context))
    );
  }

  /**
   * The route selected another organization, after its single leave decision. Everything
   * admitted for the previous one ends synchronously, before the destination is read or shown.
   */
  select(organizationId: string | null): void {
    if (!this.selection.select(organizationId)) return;
    this.clear();
    this.changes.next();
  }

  /** A refusal of work admitted under an earlier context classifies nothing current. */
  reject(status: number | null, admitted?: CatalogContext): void {
    if (admitted !== undefined && !this.isCurrent(admitted)) return;
    const context = this.current();
    if ((status === 401 || status === 403) && context?.mode === "session")
      this.sessions.rejectProtectedRequest(this.sessions.generation(), context);
  }

  clear(): void {
    ++this.generation;
    this.end();
    this.currentState.set({ kind: "idle" });
    this.settles.next();
  }

  refresh(): Observable<CatalogContext> {
    return defer(() => {
      const generation = ++this.generation;
      let settled = false;
      this.currentState.set({ kind: "loading" });
      return this.read(this.selection.organizationId()).pipe(
        map((read) => {
          if (generation !== this.generation)
            throw new CatalogReadError("unavailable");
          return read;
        }),
        tap(({ context, admission }) => {
          settled = true;
          if (generation !== this.generation) return;
          // A changed admission incarnation ends the lineage like another owner.
          const identity = JSON.stringify([catalogOwner(context), admission]);
          // Another owner is a boundary: nothing admitted earlier stays current.
          if (this.current() && this.currentOwner !== identity) this.end();
          if (this.lastIdentity !== null && identity !== this.lastIdentity)
            this.identityRevision.update((revision) => revision + 1);
          this.lastIdentity = identity;
          this.lineage.add(context);
          this.current.set(context);
          this.currentOwner = identity;
          this.currentState.set({ kind: "ready", context });
          this.settles.next();
        }),
        map(({ context }) => context),
        catchError((error: unknown) => {
          settled = true;
          const failure = catalogFailure(error);
          if (generation === this.generation) {
            this.end();
            this.currentState.set({ kind: failure.kind });
          }
          this.settles.next();
          return throwError(() => failure);
        }),
        finalize(() => {
          // A canceled read observed nothing; the confirmed context stays as it was.
          if (!settled && generation === this.generation) {
            const context = this.current();
            this.currentState.set(
              context ? { kind: "ready", context } : { kind: "idle" },
            );
            this.settles.next();
          }
        }),
      );
    });
  }

  /**
   * The legacy context discovers the root only. Every organization, including either root
   * alias, gets its context and admission incarnation from the qualified endpoint.
   */
  private read(
    organizationId: string | null,
  ): Observable<{ context: CatalogContext; admission: string }> {
    const root = this.api
      .context()
      .pipe(
        tap((context) =>
          this.selection.observeRoot(context.allowedOrganizationId),
        ),
      );
    const qualified = (id: string) =>
      this.api.organizationContext(id).pipe(
        map((read) => ({
          context: read.context,
          admission: read.admissionVersion,
        })),
      );
    const known = this.selection.rootOrganizationId();
    if (organizationId === null)
      return known
        ? qualified(known)
        : root.pipe(
            switchMap((context) => qualified(context.allowedOrganizationId)),
          );
    if (known !== null) return qualified(organizationId);
    return root.pipe(
      catchError((error: unknown) => {
        if (catalogFailure(error).kind !== "notFound")
          return throwError(() => error);
        // No root admission: this principal's organizations are all read qualified.
        this.selection.observeRoot("");
        return of(null);
      }),
      switchMap(() => qualified(organizationId)),
    );
  }

  private decide(
    context: CatalogContext,
    selection: number,
  ): Observable<CatalogContext> {
    return defer(() => {
      // A request waiting for a replaced selection never sends, even once access is ready.
      if (selection !== this.selections)
        return throwError(() => new CatalogReadError("unavailable"));
      const current = this.current();
      if (current && this.canSend(context)) return of(current);
      if (!this.isCurrent(context) || !this.sessions.presents(context))
        return throwError(() => new CatalogReadError("unavailable"));
      return merge(this.settles, this.changes).pipe(
        take(1),
        observeOn(asapScheduler),
        switchMap(() => this.decide(context, selection)),
      );
    });
  }

  private end(): void {
    this.lineage = new WeakSet();
    this.current.set(null);
    this.currentOwner = null;
  }
}
