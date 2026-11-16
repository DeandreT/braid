import { DOCUMENT } from "@angular/common";
import { Component, DestroyRef, inject, signal } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Subject, startWith, switchMap, tap } from "rxjs";
import { type HealthState, WorkspaceConnection } from "../health";

/** Browser connectivity and the workspace service's health; silent unless one is unavailable. */
@Component({
  selector: "braid-connection-notice",
  template: `@if (offline()) {
      <aside
        class="wb-state"
        data-tone="attention"
        role="status"
        aria-label="Browser offline"
      >
        <strong>Browser offline.</strong> Reconnect, then check the previous
        request before trying again. A request already sent may still finish.
      </aside>
    } @else if (health().kind === "unavailable") {
      <aside
        class="wb-state"
        data-tone="attention"
        role="status"
        aria-label="Workspace connection"
      >
        <strong>Workspace unavailable.</strong> Check that your workspace
        service is running, then try again.
        <button type="button" class="secondary-button" (click)="retry()">
          Check connection
        </button>
      </aside>
    }`,
  styles: [
    `
      :host {
        display: contents;
      }
      aside {
        margin: 4px 6px;
        padding-block: 4px;
        font-size: 0.8125rem;
        line-height: 1.5;
        background: var(--wb-attention-tint);
        color: var(--wb-attention-text);
      }
      button {
        margin-inline-start: 8px;
      }
    `,
  ],
})
export class ConnectionNoticeComponent {
  private readonly window = inject(DOCUMENT).defaultView;
  private readonly connection = inject(WorkspaceConnection);
  private readonly attempts = new Subject<void>();
  readonly offline = signal(this.window?.navigator.onLine === false);
  readonly health = signal<HealthState>({ kind: "checking" });

  constructor() {
    const update = () =>
      this.offline.set(this.window?.navigator.onLine === false);
    this.window?.addEventListener("online", update);
    this.window?.addEventListener("offline", update);
    inject(DestroyRef).onDestroy(() => {
      this.window?.removeEventListener("online", update);
      this.window?.removeEventListener("offline", update);
    });
    this.attempts
      .pipe(
        startWith(undefined),
        tap(() => this.health.set({ kind: "checking" })),
        switchMap(() => this.connection.check()),
        takeUntilDestroyed(),
      )
      .subscribe((state) => this.health.set(state));
  }

  retry(): void {
    this.attempts.next();
  }
}
