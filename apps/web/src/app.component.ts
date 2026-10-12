import { Component, inject, signal } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Subject, startWith, switchMap, tap } from "rxjs";
import { type HealthState, WorkspaceConnection } from "./health";

@Component({ selector: "weft-root", templateUrl: "./app.component.html" })
export class AppComponent {
  readonly health = signal<HealthState>({ kind: "checking" });
  private readonly connection = inject(WorkspaceConnection);
  private readonly attempts = new Subject<void>();
  readonly features = [
    {
      number: "01",
      title: "Follow every field",
      description:
        "See where a value comes from, the rules it passes through, and where it lands.",
    },
    {
      number: "02",
      title: "Ask your mapping",
      description:
        "Explore rules in a conversation, with answers linked to the fields that matter.",
    },
    {
      number: "03",
      title: "Test before publishing",
      description:
        "Try sample data, compare revisions, and keep examples of the results you expect.",
    },
  ];

  constructor() {
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

  status(): string {
    return {
      checking: "Connecting to your workspace…",
      connected: "Workspace connected",
      unavailable: "Workspace unavailable",
    }[this.health().kind];
  }
}
