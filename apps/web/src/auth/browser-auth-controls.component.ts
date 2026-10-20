import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import type { Observable } from "rxjs";
import { BrowserSessionState } from "./browser-session-state";

@Component({
  selector: "weft-browser-auth-controls",
  templateUrl: "./browser-auth-controls.component.html",
  styleUrl: "./browser-auth-controls.component.css",
})
export class BrowserAuthControlsComponent {
  readonly session = inject(BrowserSessionState);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly forgetGeneration = signal<number | null>(null);
  readonly showForget = computed(
    () =>
      this.forgetGeneration() !== null &&
      this.forgetGeneration() === this.session.generation() &&
      this.session.state().kind === "authenticationRequired" &&
      this.session.canPresent("forgetLocal"),
  );
  readonly summary = computed(() => {
    const action = this.session.action().kind;
    if (
      this.session.state().kind === "unavailable" &&
      (action === "logoutAcknowledged" ||
        action === "revocationUnconfirmed" ||
        action === "unconfirmed" ||
        action === "redirecting")
    )
      return "Session check needed";
    switch (this.session.state().kind) {
      case "checking":
        return "Checking sign-in status…";
      case "disabled":
        return "Sign-in is disabled";
      case "development":
        return "Development identity";
      case "anonymous":
        return "Not signed in";
      case "authenticationRequired":
        return "Sign-in required";
      case "authenticated":
        return "Signed in to Weft";
      case "unavailable":
        return "Sign-in status unavailable";
    }
  });
  readonly detail = computed(() => {
    switch (this.session.state().kind) {
      case "checking":
        return "Protected content stays hidden while the session is checked.";
      case "disabled":
        return "This host has no browser sign-in. Examples remain available.";
      case "development":
        return "The explicit Development workspace is active; it is not an Entra session.";
      case "anonymous":
        return "Sign in to use your permitted catalog workspace.";
      case "authenticationRequired":
        return "The current local session cannot grant access. Sign in again, or review explicit local recovery.";
      case "authenticated":
        return "Catalog permissions are checked separately for each operation. Sign out ends only this local Weft session.";
      case "unavailable":
        return "Protected content is hidden. An unavailable check does not mean you are signed out.";
    }
  });
  readonly actionMessage = computed(() => {
    const action = this.session.action();
    switch (action.kind) {
      case "idle":
        return "";
      case "admitting":
        return "Checking the current session before your chosen action…";
      case "pending":
        return action.action === "logout"
          ? "Waiting for local sign-out acknowledgement…"
          : action.action === "forgetLocal"
            ? "Waiting for explicit local forgetting…"
            : "Requesting sign-in…";
      case "redirecting":
        return "Continuing to the server-approved sign-in page. Sign-in is not yet confirmed.";
      case "notAdmitted":
        return "The action was not sent because the current session could not be admitted unchanged. Check session, then choose again.";
      case "logoutAcknowledged":
        return "Local sign-out was acknowledged. This does not sign you out of Microsoft Entra. Check session to continue.";
      case "revocationUnconfirmed":
        return "Local forgetting was acknowledged. Revocation is unconfirmed: a server session may still exist. Check session to continue.";
      case "unconfirmed":
        return action.action === "logout"
          ? "Sign-out was not confirmed. The session may still be active. Nothing was retried; check session before choosing another action."
          : action.action === "forgetLocal"
            ? "Local forgetting was not confirmed or was not available. Revocation is unconfirmed. Nothing was retried; check session to continue."
            : "Sign-in could not be confirmed. Nothing was retried; check session before choosing again.";
    }
  });

  constructor() {
    effect(() => {
      if (
        this.session.state().kind !== "authenticationRequired" ||
        this.forgetGeneration() !== this.session.generation()
      )
        this.forgetGeneration.set(null);
    });
  }

  check(): void {
    if (this.session.busy() || this.session.state().kind === "checking") return;
    this.forgetGeneration.set(null);
    this.run(this.session.observe());
  }

  signIn(): void {
    if (!this.session.canPresent("login")) return;
    this.forgetGeneration.set(null);
    this.run(this.session.signIn());
  }

  logout(): void {
    if (!this.session.canPresent("logout")) return;
    this.forgetGeneration.set(null);
    this.run(this.session.logout());
  }

  reviewForget(): void {
    if (!this.session.canPresent("forgetLocal")) return;
    this.forgetGeneration.set(this.session.generation());
    afterNextRender(
      () => {
        if (this.showForget())
          this.document.getElementById("forget-local-review")?.focus();
      },
      { injector: this.injector },
    );
  }

  cancelForget(): void {
    this.forgetGeneration.set(null);
    this.document.getElementById("review-forget-local")?.focus();
  }

  confirmForget(): void {
    if (!this.showForget()) return;
    this.forgetGeneration.set(null);
    this.run(this.session.forgetLocal());
  }

  private run(request: Observable<void>): void {
    // Exactly one caller-owned subscription; classifier ownership is independent.
    request
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ error: () => {} });
  }
}
