import { Component, inject, signal } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { Subject, of, startWith, switchMap, tap } from "rxjs";
import { MappingProjectionComponent } from "./projection.component";
import { NativeEvidenceComponent } from "./native-evidence.component";
import {
  type ExampleState,
  type ManifestState,
  MappingExamples,
  operationMessage,
  validationMessage,
} from "./examples";

@Component({
  selector: "weft-mapping-workspace",
  imports: [MappingProjectionComponent, NativeEvidenceComponent],
  templateUrl: "./workspace.component.html",
})
export class MappingWorkspaceComponent {
  readonly manifest = signal<ManifestState>({ kind: "loading" });
  readonly state = signal<ExampleState>({ kind: "idle" });
  readonly selectedId = signal("");
  readonly operation = operationMessage;
  readonly validation = validationMessage;
  private readonly examples = inject(MappingExamples);
  private readonly listAttempts = new Subject<void>();
  private readonly selections = new Subject<string | null>();

  constructor() {
    this.selections
      .pipe(
        tap((exampleId) => {
          this.selectedId.set(exampleId ?? "");
          this.state.set({ kind: exampleId == null ? "idle" : "loading" });
        }),
        switchMap((exampleId) =>
          exampleId == null
            ? of<ExampleState>({ kind: "idle" })
            : this.examples.read(exampleId),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((state) => this.state.set(state));
    this.listAttempts
      .pipe(
        startWith(undefined),
        tap(() => {
          this.manifest.set({ kind: "loading" });
          this.selections.next(null);
        }),
        switchMap(() => this.examples.list()),
        takeUntilDestroyed(),
      )
      .subscribe((manifest) => {
        this.manifest.set(manifest);
        if (manifest.kind === "loaded" && manifest.examples.length)
          this.select(manifest.examples[0].id);
      });
  }

  select(exampleId: string): void {
    this.selections.next(exampleId);
  }
  retry(): void {
    if (this.selectedId()) this.select(this.selectedId());
  }
  retryList(): void {
    this.listAttempts.next();
  }
}
