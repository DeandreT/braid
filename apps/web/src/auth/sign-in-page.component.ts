import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  untracked,
} from "@angular/core";
import { takeUntilDestroyed, toSignal } from "@angular/core/rxjs-interop";
import { ActivatedRoute, Router } from "@angular/router";
import { map, type Observable } from "rxjs";
import { BrowserSessionState } from "./browser-session-state";
import { sessionActionMessage, sessionTone } from "./browser-auth-presentation";
import { defaultReturnPath, returnPathOrDefault } from "./return-path";

/** The Session profile's entry: one Microsoft sign-in that returns to the requested page. */
@Component({
  selector: "weft-sign-in-page",
  templateUrl: "./sign-in-page.component.html",
  styleUrl: "./sign-in-page.component.css",
})
export class SignInPageComponent {
  readonly session = inject(BrowserSessionState);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  /** Only a valid return path is honoured; anything else returns to the catalog. */
  readonly returnTo = toSignal(
    inject(ActivatedRoute).queryParamMap.pipe(
      map((params) =>
        params.keys.some((key) => key !== "returnTo") ||
        params.getAll("returnTo").length > 1
          ? defaultReturnPath
          : returnPathOrDefault(params.get("returnTo")),
      ),
    ),
    { requireSync: true },
  );
  readonly tone = computed(() =>
    sessionTone(this.session.state().kind, this.session.action().kind),
  );
  readonly message = computed(() => {
    switch (this.session.state().kind) {
      case "checking":
        return "Checking sign-in status…";
      case "anonymous":
        return "You are signed out. Sign in to open your workspace.";
      case "authenticationRequired":
        return "Your session has expired or can no longer be used. Sign in again to continue.";
      case "unavailable":
        return "Sign-in status is unavailable. This does not mean you are signed out; check again in a moment.";
      case "disabled":
        return "This host has no browser sign-in.";
      case "authenticated":
      case "development":
        return "Opening your workspace…";
    }
  });
  readonly actionMessage = computed(() =>
    sessionActionMessage(this.session.action()),
  );
  readonly canSignIn = computed(() => {
    const kind = this.session.state().kind;
    return kind === "anonymous" || kind === "authenticationRequired";
  });

  constructor() {
    // A confirmed identity has nothing to sign in to: continue to the requested page.
    effect(() => {
      const kind = this.session.state().kind;
      if (kind === "authenticated" || kind === "development")
        untracked(
          () =>
            void this.router.navigateByUrl(this.returnTo(), {
              replaceUrl: true,
            }),
        );
    });
  }

  signIn(): void {
    if (!this.session.canPresent("login")) return;
    this.run(this.session.signIn(this.returnTo()));
  }

  check(): void {
    if (this.session.busy() || this.session.state().kind === "checking") return;
    this.run(this.session.observe());
  }

  private run(request: Observable<void>): void {
    request
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ error: () => {} });
  }
}
