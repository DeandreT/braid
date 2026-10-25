import { DOCUMENT } from "@angular/common";
import { Component, DestroyRef, inject, signal } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { BrowserSessionState } from "../auth/browser-session-state";
import { UnsentWork } from "../workbench/unsent-work";

@Component({
  selector: "weft-invitation-page",
  template: `
    <section aria-labelledby="invitation-heading">
      <h1 id="invitation-heading">Join an organization</h1>
      @if (valid) {
        <p>
          Sign in with the organization's Microsoft Entra account to redeem this
          invitation. The link can be used only once and may have expired or
          been revoked.
        </p>
        <button
          type="button"
          [disabled]="sessions.busy() || !sessions.canPresent('login')"
          (click)="redeem()"
        >
          Sign in and redeem invitation
        </button>
      } @else {
        <p role="alert">
          This invitation link is incomplete. Ask an administrator for a new
          invitation.
        </p>
      }
      @if (failed()) {
        <p role="alert">
          Sign-in was not confirmed. The invitation may already have been
          redeemed. Ask an administrator before trying again.
        </p>
      }
    </section>
  `,
  styles: [
    `
      section {
        max-width: 42rem;
        margin: 3rem auto;
        padding: 1.5rem;
      }
      button {
        margin-top: 1rem;
      }
    `,
  ],
})
export class InvitationPageComponent {
  readonly sessions = inject(BrowserSessionState);
  private readonly document = inject(DOCUMENT);
  private readonly lifetime = inject(DestroyRef);
  private readonly secret =
    this.document.defaultView?.location.hash.slice(1) ?? "";
  readonly valid = /^[0-9a-f]{64}$/.test(this.secret);
  readonly failed = signal(false);

  constructor() {
    inject(UnsentWork).track(() => this.valid, this.lifetime);
    // Fragments never reach the server or referrer. Remove it from visible browser
    // history immediately; only this component retains the in-memory credential.
    this.document.defaultView?.history.replaceState(null, "", "/invitations");
    this.sessions
      .observe()
      .pipe(takeUntilDestroyed(this.lifetime))
      .subscribe({ error: () => this.failed.set(true) });
  }
  redeem(): void {
    if (!this.valid) return;
    this.failed.set(false);
    this.sessions
      .signIn(null, this.secret)
      .pipe(takeUntilDestroyed(this.lifetime))
      .subscribe({ error: () => this.failed.set(true) });
  }
}
