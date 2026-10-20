import { DestroyRef, Injectable, inject, signal } from "@angular/core";
import {
  catchError,
  defer,
  finalize,
  map,
  tap,
  throwError,
  type Observable,
  Subject,
} from "rxjs";
import { CatalogApi, catalogFailure } from "./catalog-api";
import {
  CatalogReadError,
  type CatalogContext,
  type CatalogContextState,
} from "./catalog-types";
import { BrowserSessionState } from "../auth/browser-session-state";

/** Admission is fetched for every read selection; neither identity nor tokens are persisted. */
@Injectable({ providedIn: "root" })
export class CatalogContextStore {
  private readonly api = inject(CatalogApi);
  private readonly sessions = inject(BrowserSessionState);
  private readonly current = signal<CatalogContext | null>(null);
  private readonly currentState = signal<CatalogContextState>({ kind: "idle" });
  private readonly identityRevision = signal(0);
  private lastIdentity: string | null = null;
  private generation = 0;
  private readonly changes = new Subject<void>();
  readonly invalidated = this.changes.asObservable();
  readonly visible = this.sessions.visible;
  readonly context = this.current.asReadonly();
  readonly state = this.currentState.asReadonly();
  readonly identityVersion = this.identityRevision.asReadonly();

  constructor() {
    const subscription = this.sessions.invalidated.subscribe(() => {
      this.clear();
      this.identityRevision.update((value) => value + 1);
      this.changes.next();
    });
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  canPresent(context: CatalogContext | null): boolean {
    return context !== null && this.sessions.canDispatch(context);
  }

  reject(status: number | null): void {
    const context = this.current();
    if ((status === 401 || status === 403) && context?.mode === "session")
      this.sessions.rejectProtectedRequest(this.sessions.generation(), context);
  }

  clear(): void {
    ++this.generation;
    this.current.set(null);
    this.currentState.set({ kind: "idle" });
  }

  refresh(): Observable<CatalogContext> {
    return defer(() => {
      const generation = ++this.generation;
      let settled = false;
      this.current.set(null);
      this.currentState.set({ kind: "loading" });
      return this.api.context().pipe(
        map((context) => {
          if (generation !== this.generation)
            throw new CatalogReadError("unavailable");
          return context;
        }),
        tap((context) => {
          settled = true;
          if (generation !== this.generation) return;
          const identityContext =
            context.mode === "session" ? context.identityContext : "";
          const identity = `${context.mode}/${context.actorPrincipalId}/${context.allowedOrganizationId}/${identityContext}`;
          if (this.lastIdentity !== null && identity !== this.lastIdentity)
            this.identityRevision.update((revision) => revision + 1);
          this.lastIdentity = identity;
          this.current.set(context);
          this.currentState.set({ kind: "ready", context });
        }),
        catchError((error: unknown) => {
          settled = true;
          const failure = catalogFailure(error);
          if (generation === this.generation) {
            this.current.set(null);
            this.currentState.set({ kind: failure.kind });
          }
          return throwError(() => failure);
        }),
        finalize(() => {
          if (!settled && generation === this.generation) {
            this.current.set(null);
            this.currentState.set({ kind: "idle" });
          }
        }),
      );
    });
  }
}
