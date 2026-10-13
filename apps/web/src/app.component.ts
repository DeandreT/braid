import { Component, inject, signal } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Subject, startWith, switchMap, tap } from "rxjs";
import { type HealthState, WorkspaceConnection } from "./health";
import { MappingWorkspaceComponent } from "./mappings/workspace.component";

@Component({
  selector: "weft-root",
  imports: [MappingWorkspaceComponent],
  templateUrl: "./app.component.html",
})
export class AppComponent {
  readonly health = signal<HealthState>({ kind: "checking" });
  private readonly connection = inject(WorkspaceConnection);
  private readonly attempts = new Subject<void>();

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
