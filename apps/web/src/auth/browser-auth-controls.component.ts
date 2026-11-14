import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
} from "@angular/core";
import { Router } from "@angular/router";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import type { Observable } from "rxjs";
import { BrowserSessionState } from "./browser-session-state";
import { sessionActionMessage, sessionTone } from "./browser-auth-presentation";
import { UnsentWork } from "../workbench/unsent-work";
import { ThemePreference, type ThemeChoice } from "../theme-preference";

/** The header account menu: the session's state, organization and explicit session actions. */
@Component({
  selector: "weft-browser-auth-controls",
  templateUrl: "./browser-auth-controls.component.html",
  styleUrl: "./browser-auth-controls.component.css",
})
export class BrowserAuthControlsComponent {
  readonly session = inject(BrowserSessionState);
  /** The confirmed workspace's organization, when the catalog has read one. */
  readonly organization = input<string | null>(null);
  readonly principal = input<string | null>(null);
  readonly organizationId = input<string | null>(null);
  /** Sign-in navigation or an ended session can discard work held only by this tab. */
  readonly unsentWork = inject(UnsentWork).present;
  readonly open = signal(false);
  readonly theme = inject(ThemePreference);
  readonly themes: readonly { value: ThemeChoice; label: string }[] = [
    { value: "light", label: "Light" },
    { value: "dark", label: "Dark" },
    { value: "system", label: "System" },
  ];
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly tone = computed(() =>
    sessionTone(this.session.state().kind, this.session.action().kind),
  );
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
        return "Signed in to braid";
      case "unavailable":
        return "Sign-in status unavailable";
    }
  });
  readonly detail = computed(() => {
    switch (this.session.state().kind) {
      case "checking":
        return "Protected content stays hidden while the session is checked.";
      case "disabled":
        return "This host has no browser sign-in.";
      case "development":
        return "The explicit Development workspace is active; it is not an Entra session.";
      case "anonymous":
        return "Sign in to use your permitted catalog workspace.";
      case "authenticationRequired":
        return "The current local session cannot grant access. Sign in again, or review explicit local recovery.";
      case "authenticated":
        return "Catalog permissions are checked separately for each operation. Sign out ends only this local braid session.";
      case "unavailable":
        return "Protected content is hidden. An unavailable check does not mean you are signed out.";
    }
  });
  readonly actionMessage = computed(() =>
    sessionActionMessage(this.session.action()),
  );
  readonly label = computed(() =>
    this.session.state().kind === "authenticated"
      ? (this.session.displayName() ?? "Account")
      : this.summary(),
  );

  constructor() {
    // The menu closes on Escape or a pointer press outside it.
    const press = (event: Event) => {
      if (
        this.open() &&
        event.target instanceof Node &&
        !this.host.nativeElement.contains(event.target)
      )
        this.open.set(false);
    };
    this.document.addEventListener("pointerdown", press, true);
    this.destroyRef.onDestroy(() =>
      this.document.removeEventListener("pointerdown", press, true),
    );
    effect(() => {
      if (
        this.session.state().kind !== "authenticationRequired" ||
        this.forgetGeneration() !== this.session.generation()
      )
        this.forgetGeneration.set(null);
    });
  }

  check(): void {
    if (
      this.session.busy() ||
      this.session.checking() ||
      this.session.state().kind === "checking"
    )
      return;
    this.forgetGeneration.set(null);
    this.run(this.session.observe());
  }

  toggle(): void {
    this.open.update((value) => !value);
  }

  close(): void {
    if (!this.open()) return;
    this.open.set(false);
    this.document.getElementById("account-trigger")?.focus();
  }
  @HostListener("document:keydown", ["$event"])
  key(event: KeyboardEvent): void {
    if (event.key === "Escape" && this.open()) {
      event.preventDefault();
      this.close();
    }
  }

  signIn(): void {
    if (!this.session.canPresent("login")) return;
    this.forgetGeneration.set(null);
    // Signing in again returns to the page on screen.
    this.run(this.session.signIn(this.router.url));
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
