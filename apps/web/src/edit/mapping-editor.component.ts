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
  output,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { firstValueFrom } from "rxjs";
import { RouterLink } from "@angular/router";
import type {
  CatalogContext,
  CatalogMapping,
  CatalogRevision,
} from "../catalog/catalog-types";
import { BrowserSessionState } from "../auth/browser-session-state";
import {
  ProjectionView,
  type NodeReference,
  pathText,
} from "../workbench/projection-view";
import {
  WorkbenchFrameComponent,
  type WorkbenchCommand,
} from "../workbench/workbench-frame.component";
import { WorkbenchMappingTableComponent } from "../workbench/mapping-table.component";
import { WorkbenchState } from "../workbench/workbench-state";
import { StatusChipComponent } from "../workbench/status-chip.component";
import type { PresentedStatus } from "../workbench/presentation";
import {
  DraftApi,
  DraftError,
  type Draft,
  type DraftComparison,
  type DraftPreview,
  type DraftValidation,
} from "./draft-api";
import { CatalogApi } from "../catalog/catalog-api";
import {
  EditCore,
  EditCoreError,
  type EditOperation,
  type EditScript,
  type EditView,
} from "./edit-core";
import { DraftMemory, type RetainedPreviewRequest } from "./draft-memory";
import {
  operationKinds,
  operationNames,
  operationTemplate,
} from "./operation-templates";

@Component({
  selector: "weft-mapping-editor",
  imports: [
    WorkbenchFrameComponent,
    WorkbenchMappingTableComponent,
    StatusChipComponent,
    RouterLink,
  ],
  templateUrl: "./mapping-editor.component.html",
  styleUrl: "./mapping-editor.component.css",
})
export class MappingEditorComponent {
  readonly context = input.required<CatalogContext>();
  readonly mapping = input.required<CatalogMapping>();
  readonly revision = input.required<CatalogRevision>();
  readonly closed = output<void>();
  readonly saved = output<string>();
  private readonly api = inject(DraftApi);
  private readonly catalog = inject(CatalogApi);
  private readonly memory = inject(DraftMemory);
  private readonly sessions = inject(BrowserSessionState);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  readonly workbench = inject(WorkbenchState);
  private readonly frame = viewChild(WorkbenchFrameComponent);
  readonly admitted = computed(
    () =>
      this.sessions.presents(this.context()) &&
      this.context().features.edit &&
      this.mapping().access.actions.edit,
  );
  private readonly core = new EditCore();
  private lifetime = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private pendingAutosave: Promise<void> | null = null;
  private editsSinceSave = 0;
  private saveKey: string | null = null;
  private createKey: string | null = null;
  private redoOperations: EditOperation[] = [];
  readonly draft = signal<Draft | null>(null);
  readonly view = signal<EditView | null>(null);
  readonly busy = signal(false);
  readonly savePending = signal(false);
  readonly dirty = signal(false);
  readonly status = computed<PresentedStatus>(() =>
    this.savePending()
      ? { label: "Save awaiting confirmation", tone: "uncertain", icon: "?" }
      : this.conflict()
        ? { label: "Draft conflict", tone: "attention", icon: "!" }
        : this.busy()
          ? { label: "Working", tone: "running", icon: "…" }
          : this.dirty()
            ? { label: "Local edits", tone: "attention", icon: "•" }
            : this.view()
              ? { label: "Draft autosaved", tone: "success", icon: "✓" }
              : { label: "Editor unavailable", tone: "unavailable", icon: "—" },
  );
  readonly conflict = signal(false);
  readonly message = signal("Opening your private draft…");
  readonly error = signal("");
  readonly comparison = signal<DraftComparison | null>(null);
  readonly validation = signal<DraftValidation | null>(null);
  readonly preview = signal<DraftPreview | null>(null);
  readonly previewRequest = signal<RetainedPreviewRequest | null>(null);
  readonly sampleText = signal("{}");
  readonly sampleNamed = signal<{ name: string; content: string }[]>([]);
  readonly sampleParameters = signal<
    { name: string; kind: string; value: string }[]
  >([]);
  readonly sampleTarget = signal("all");
  readonly sampleTargetName = signal("");
  readonly sampleOutput = signal("");
  readonly latestHead = signal<CatalogRevision | null>(null);
  readonly headComparisonPath = computed(() => {
    const scope = this.mapping().scope;
    return `/catalog/organizations/${scope.organizationId}/projects/${scope.projectId}/mappings/${scope.mappingId}/comparisons`;
  });
  readonly selected = signal<NodeReference | null>(null);
  readonly selectedKind = signal<(typeof operationKinds)[number]>("bind");
  readonly operationText = signal("");
  readonly expressionKind = signal("constant");
  readonly scalarType = signal("string");
  readonly valueText = signal("");
  readonly sourceId = signal("");
  readonly functionName = signal("concat");
  readonly argumentsText = signal("");
  readonly pathText = pathText;
  readonly operationKinds = operationKinds;
  readonly operationNames = operationNames;
  readonly projection = computed(() => {
    const view = this.view(),
      draft = this.draft();
    if (!view || !draft) return null;
    return new ProjectionView({
      ...view.projection,
      schemaVersion: "weft.mapping-projection.v1",
      provenance: {
        revision: draft.base.revision,
        nativeArtifactDigest: draft.base.nativeDigest,
        backend: {
          id: "ferrule",
          revision: "browser-draft",
          buildId: "local-uncheckpointed",
        },
        adapterVersion: "browser-editor",
        projectionVersion: "weft.ferrule-projection.v1",
      },
    });
  });
  readonly target = computed(() => {
    const selected = this.selected();
    return selected?.kind === "field"
      ? (this.view()?.targets.find((t) => t.fieldId === selected.fieldId) ??
          null)
      : null;
  });
  readonly sources = computed(() => {
    const view = this.view();
    if (!view) return [];
    const input = new Set(
      view.projection.boundaries
        .filter((b) => b.role === "primaryInput" || b.role === "namedInput")
        .map((b) => b.id),
    );
    return view.projection.fields.filter(
      (f) => input.has(f.boundaryId) && f.dataType.kind === "scalar",
    );
  });
  readonly commands = computed<WorkbenchCommand[]>(() => [
    { id: "map", label: "Map field…" },
    { id: "remove", label: "Remove mapping" },
    { id: "undo", label: `Undo ${this.view()?.undoLabel ?? "edit"}` },
    { id: "redo", label: `Redo ${this.view()?.redoLabel ?? "edit"}` },
  ]);

  constructor() {
    effect(() => {
      const context = this.context();
      this.mapping();
      this.revision();
      untracked(() => {
        this.workbench.dispatch({ kind: "view", view: "mapping" });
        this.latestHead.set(
          this.mapping().head.scope.revisionId ===
            this.revision().scope.revisionId
            ? this.revision()
            : null,
        );
        this.memory.admit(context);
        void this.open();
      });
    });
    this.sessions.invalidated
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.suspend());
    inject(DestroyRef).onDestroy(() => this.suspend());
  }
  private suspend(): void {
    ++this.lifetime;
    this.retain();
    this.core.close();
    this.view.set(null);
    this.validation.set(null);
    this.comparison.set(null);
    this.preview.set(null);
    this.sampleOutput.set("");
    this.sampleText.set("{}");
    this.sampleNamed.set([]);
    this.sampleParameters.set([]);
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
  private retain(): void {
    const draft = this.draft(),
      view = this.view();
    if (draft && view)
      this.memory.keep(
        {
          draft,
          operations: view.operations,
          redo: this.redoOperations.slice(),
          dirty: this.dirty(),
          saveKey: this.saveKey,
          saving: this.savePending(),
          previewRequest: this.previewRequest(),
        },
        this.context(),
      );
  }
  async open(reload = false): Promise<void> {
    const unconfirmed = this.previewRequest(),
      unconfirmedDraft = this.draft()?.id;
    if (!reload) this.retain();
    else if (this.draft())
      this.memory.forget(this.draft()!.id, this.mapping().scope);
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.core.close();
    this.view.set(null);
    this.validation.set(null);
    this.comparison.set(null);
    this.preview.set(null);
    this.sampleOutput.set("");
    const life = ++this.lifetime,
      context = this.context(),
      mapping = this.mapping();
    this.busy.set(true);
    this.error.set("");
    this.conflict.set(false);
    try {
      const pending = this.memory.pending(mapping.scope);
      const open = pending
        ? [await this.api.read(context, mapping.scope, pending.draft.id)]
        : await this.api.list(context, mapping.scope);
      let draft = open[0];
      if (draft?.state === "saved" && draft.savedRevisionId) {
        this.memory.forget(draft.id, mapping.scope);
        this.saved.emit(draft.savedRevisionId);
        return;
      }
      if (!draft) {
        this.createKey ??= crypto.randomUUID();
        try {
          draft = await this.api.create(
            context,
            mapping.scope,
            this.revision().scope.revisionId,
            this.createKey,
          );
        } catch (error) {
          if (
            error instanceof DraftError &&
            error.result?.outcome === "draftExists" &&
            error.result.draft
          )
            draft = error.result.draft;
          else throw error;
        }
      }
      const retained = !reload
        ? this.memory.read(draft.id, mapping.scope)
        : undefined;
      if (life !== this.lifetime) return;
      this.draft.set(draft);
      if (
        retained &&
        retained.draft.version !== draft.version &&
        retained.dirty
      ) {
        this.draft.set(draft);
        this.conflict.set(true);
        throw new Error(
          "This draft changed elsewhere. Reload to use the saved operations; your earlier local work remains in this tab until you choose reload.",
        );
      }
      const operations = retained?.dirty
        ? retained.operations
        : await this.api.operations(context, mapping.scope, draft);
      const base = await this.api.base(context, mapping.scope, draft);
      if (life !== this.lifetime) return;
      this.redoOperations =
        retained?.draft.version === draft.version ? retained.redo.slice() : [];
      const complete: EditScript = {
        format: "ferrule.edit.v1",
        operations: [
          ...operations.operations,
          ...this.redoOperations.slice().reverse(),
        ],
      };
      let view = await this.core.open(base, complete);
      for (const _operation of this.redoOperations)
        view = await this.core.undo();
      if (life !== this.lifetime) return;
      this.draft.set(draft);
      this.view.set(view);
      this.dirty.set(retained?.dirty ?? false);
      this.saveKey = retained?.saveKey ?? draft.savingRequestId ?? null;
      this.previewRequest.set(
        retained?.previewRequest ??
          (unconfirmedDraft === draft.id ? unconfirmed : null),
      );
      this.savePending.set(
        draft.state === "saving" || (retained?.saving ?? false),
      );
      this.retain();
      this.template(this.selectedKind());
      this.message.set(
        this.dirty()
          ? "Restored local edits. Not yet autosaved."
          : "Private draft loaded. Changes autosave after two seconds.",
      );
      if (this.dirty()) this.schedule();
    } catch (error) {
      if (life === this.lifetime) this.fail(error);
    } finally {
      if (life === this.lifetime) this.busy.set(false);
    }
  }
  inspect(reference: NodeReference | null): void {
    this.selected.set(reference);
    this.template(this.selectedKind());
    // The workbench starts with its inspector collapsed; the edit controls live there.
    if (reference) this.frame()?.openInspector();
    this.focusAfterRender("#edit-expression");
  }
  template(kind: (typeof operationKinds)[number]): void {
    this.selectedKind.set(kind);
    const target = this.target(),
      view = this.view();
    if (!view) return;
    this.operationText.set(
      JSON.stringify(
        operationTemplate(
          kind,
          target?.binding ?? {
            scope: { target: { kind: "primary" }, children: [] },
            field: "",
          },
          view.nextNodeId ?? "0",
          target?.node ?? "0",
        ),
        null,
        2,
      ),
    );
  }
  async applyBinding(): Promise<void> {
    const target = this.target(),
      view = this.view();
    if (!target || !view?.nextNodeId) return;
    let expression: Extract<EditOperation, { op: "bind" }>["expression"];
    if (this.expressionKind() === "source") {
      const field = this.sources().find((f) => f.id === this.sourceId());
      const boundary = view.projection.boundaries.find(
        (b) => b.id === field?.boundaryId,
      );
      if (!field || !boundary) {
        this.error.set("Choose an incoming field.");
        return;
      }
      expression = {
        kind: "owned_source",
        id: view.nextNodeId,
        owner:
          boundary.role === "primaryInput"
            ? { kind: "primary" }
            : { kind: "named", name: boundary.name },
        path: field.path.flatMap((p) =>
          p.kind === "field" || p.kind === "attribute" ? [p.name] : [],
        ),
        frame: null,
      };
    } else if (this.expressionKind() === "call") {
      expression = {
        kind: "call",
        id: view.nextNodeId,
        function: this.functionName(),
        args: this.argumentsText()
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean),
      };
    } else {
      const type = this.scalarType();
      const value =
        type === "null" || type === "json_null" || type === "xml_nil"
          ? { type }
          : { type, text: this.valueText() };
      expression = {
        kind: "constant",
        id: view.nextNodeId,
        value,
      } as typeof expression;
    }
    await this.apply({ op: "bind", binding: target.binding, expression });
  }
  async applyJson(): Promise<void> {
    try {
      if (new TextEncoder().encode(this.operationText()).length > 1024 * 1024)
        throw new Error("An operation cannot exceed 1 MiB.");
      // Preserve the original operation text so Rust can reject duplicate members too.
      await this.change(
        () => this.core.applyText(this.operationText()),
        "Applied typed operation",
        "apply",
      );
    } catch (error) {
      this.fail(error);
    }
  }
  async apply(operation: EditOperation): Promise<void> {
    await this.change(
      () => this.core.apply(operation),
      operationNames[operation.op],
      "apply",
    );
  }
  undo(): Promise<void> {
    return this.change(() => this.core.undo(), "Undid edit", "undo");
  }
  redo(): Promise<void> {
    return this.change(() => this.core.redo(), "Redid edit", "redo");
  }
  private async change(
    run: () => Promise<EditView>,
    label: string,
    history: "apply" | "undo" | "redo",
  ): Promise<void> {
    if (this.busy() || this.savePending() || this.conflict() || !this.view())
      return;
    const life = this.lifetime;
    this.busy.set(true);
    this.error.set("");
    try {
      const before = this.view()!.operations.operations;
      const view = await run();
      if (life !== this.lifetime) return;
      if (history === "apply") this.redoOperations = [];
      else if (
        history === "undo" &&
        view.operations.operations.length < before.length
      )
        this.redoOperations.push(before[before.length - 1]!);
      else if (
        history === "redo" &&
        view.operations.operations.length > before.length
      )
        this.redoOperations.pop();
      this.view.set(view);
      this.dirty.set(true);
      this.comparison.set(null);
      this.validation.set(null);
      this.saveKey = null;
      this.message.set(
        `${label}. ${view.validation.totalIssueCount} local validation issues. Not yet autosaved.`,
      );
      ++this.editsSinceSave;
      this.retain();
      this.schedule();
      this.focusAfterRender('button[data-row-key][aria-pressed="true"]');
    } catch (error) {
      if (life === this.lifetime) this.fail(error);
    } finally {
      if (life === this.lifetime) this.busy.set(false);
    }
  }
  private schedule(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(
      () => {
        this.timer = null;
        void this.flush().catch((error) => this.fail(error));
      },
      this.editsSinceSave >= 20 ? 0 : 2000,
    );
  }
  async flush(): Promise<void> {
    if (this.pendingAutosave) {
      await this.pendingAutosave;
      if (this.dirty()) return this.flush();
      return;
    }
    const draft = this.draft(),
      operations = this.view()?.operations,
      life = this.lifetime;
    if (!draft || !operations || !this.dirty() || this.conflict()) return;
    const serialized = JSON.stringify(operations);
    this.pendingAutosave = (async () => {
      const updated = await this.api.put(
        this.context(),
        this.mapping().scope,
        draft,
        operations,
      );
      if (life !== this.lifetime) return;
      this.draft.set(updated);
      this.dirty.set(JSON.stringify(this.view()?.operations) !== serialized);
      this.editsSinceSave = 0;
      this.message.set(
        this.dirty()
          ? "Newer edits are waiting to autosave."
          : "Draft autosaved.",
      );
      this.retain();
      if (this.dirty()) this.schedule();
    })();
    try {
      await this.pendingAutosave;
    } finally {
      this.pendingAutosave = null;
    }
  }
  async checkpoint(diff = false): Promise<void> {
    await this.action(async () => {
      await this.flush();
      const draft = this.draft();
      if (!draft || this.conflict()) return;
      const updated = await this.api.checkpoint(
        this.context(),
        this.mapping().scope,
        draft,
      );
      this.draft.set(updated);
      this.validation.set(
        await this.api.validation(
          this.context(),
          this.mapping().scope,
          updated,
        ),
      );
      this.message.set(
        `Worker checkpoint: ${updated.checkpoint?.totalIssueCount ?? 0} validation issues.`,
      );
      if (diff)
        this.comparison.set(
          await this.api.compare(this.context(), this.mapping().scope, updated),
        );
    });
  }
  async save(): Promise<void> {
    await this.action(async () => {
      await this.flush();
      let draft = this.draft();
      if (!draft || this.conflict()) return;
      if (!draft.checkpoint) {
        draft = await this.api.checkpoint(
          this.context(),
          this.mapping().scope,
          draft,
        );
        this.draft.set(draft);
      }
      this.saveKey ??= crypto.randomUUID();
      this.retain();
      let result: Awaited<ReturnType<DraftApi["save"]>>;
      try {
        result = await this.api.save(
          this.context(),
          this.mapping().scope,
          draft,
          this.saveKey,
          () => {
            this.savePending.set(true);
            this.retain();
          },
        );
      } catch (error) {
        if (
          error instanceof DraftError &&
          [400, 404, 409, 412, 413, 415, 422].includes(error.status)
        ) {
          this.savePending.set(false);
          this.retain();
        }
        throw error;
      }
      if (result.receipt?.phase === "committed") {
        this.savePending.set(false);
        this.forgetView(draft.id);
        this.saved.emit(result.receipt.futureRevision.revisionId);
      } else if (result.receipt?.phase === "headConflict") {
        this.savePending.set(false);
        this.conflict.set(true);
        await this.refreshHead();
        this.message.set(
          "A newer version was saved. Review the current head, then explicitly rebase or discard.",
        );
      } else if (
        result.receipt?.phase === "rejected" ||
        result.receipt?.phase === "attemptExhausted"
      ) {
        this.savePending.set(false);
        this.message.set(
          `Save ended: ${result.receipt.phase}. The draft remains available.`,
        );
      } else
        this.message.set(
          `Save is ${result.receipt?.phase ?? result.outcome}. Retry Save to observe the same operation.`,
        );
      this.retain();
    });
  }
  async rebase(): Promise<void> {
    await this.action(async () => {
      const draft = this.draft();
      if (!draft || this.dirty() || this.savePending())
        throw new Error(
          "Resolve the pending save and autosave or reload before rebasing.",
        );
      // Rebase only onto the head the user reviewed; a newer head needs another review.
      const reviewed = this.latestHead()?.scope.revisionId;
      await this.refreshHead();
      if (!reviewed || this.latestHead()!.scope.revisionId !== reviewed) {
        this.message.set(
          "Another version was saved after the one you reviewed. Review the latest saved version, then rebase again.",
        );
        return;
      }
      const updated = await this.api.rebase(
        this.context(),
        this.mapping().scope,
        draft,
        reviewed,
      );
      this.draft.set(updated);
      this.memory.forget(draft.id, this.mapping().scope);
      await this.open(true);
    });
  }
  async discard(): Promise<void> {
    await this.action(async () => {
      const draft = this.draft();
      if (draft) {
        await this.api.discard(this.context(), this.mapping().scope, draft);
        this.forgetView(draft.id);
      }
      this.closed.emit();
    });
  }
  private async refreshHead(): Promise<void> {
    const life = this.lifetime;
    const mapping = await firstValueFrom(
      this.catalog.mapping(this.mapping().scope),
    );
    const head = await firstValueFrom(
      this.catalog.revision(mapping.head.scope),
    );
    if (life !== this.lifetime) throw new Error("The editor was suspended.");
    this.latestHead.set(head);
  }
  async trySample(): Promise<void> {
    await this.action(async () => {
      // An unconfirmed submission is replayed exactly; the server returns its original run.
      const retained = this.previewRequest();
      const request = retained ?? {
        key: crypto.randomUUID(),
        body: this.sampleSubmission(),
      };
      await this.flush();
      let draft = this.draft();
      if (!draft || this.conflict()) return;
      if (!draft.checkpoint) {
        draft = await this.api.checkpoint(
          this.context(),
          this.mapping().scope,
          draft,
        );
        this.draft.set(draft);
      }
      this.previewRequest.set(request);
      this.retain();
      let result: Awaited<ReturnType<DraftApi["preview"]>>;
      try {
        result = await this.api.preview(
          this.context(),
          this.mapping().scope,
          draft,
          request.key,
          request.body,
        );
      } catch (error) {
        if (
          error instanceof DraftError &&
          [400, 404, 409, 412, 413, 415, 422].includes(error.status)
        ) {
          this.previewRequest.set(null);
          this.retain();
        }
        throw error;
      }
      this.previewRequest.set(null);
      this.retain();
      this.preview.set(result.run);
      this.message.set(
        retained
          ? "Recovered the earlier sample preview request."
          : "Sample preview queued. Its checkpoint is retained independently of further edits.",
      );
    });
  }
  private sampleSubmission(): RetainedPreviewRequest["body"] {
    let total = 0;
    const names = new Set<string>();
    const encode = (text: string) => {
      const bytes = new TextEncoder().encode(text);
      total += bytes.length;
      if (!bytes.length || total > 8 * 1024 * 1024)
        throw new Error("Samples must be nonempty and total at most 8 MiB.");
      let binary = "";
      for (const byte of bytes) binary += String.fromCharCode(byte);
      return btoa(binary);
    };
    const inputs: { name: string | null; content: string }[] = [
      { name: null, content: encode(this.sampleText()) },
    ];
    for (const input of this.sampleNamed()) {
      if (!input.name || names.has(input.name))
        throw new Error("Named inputs need distinct source names.");
      names.add(input.name);
      inputs.push({ name: input.name, content: encode(input.content) });
    }
    return {
      target: {
        kind: this.sampleTarget(),
        ...(this.sampleTarget() === "named"
          ? { name: this.sampleTargetName() }
          : {}),
      },
      parameters: this.sampleParameters(),
      inputs,
    };
  }
  async refreshPreview(): Promise<void> {
    await this.action(async () => {
      const preview = this.preview();
      if (preview)
        this.preview.set(
          await this.api.readPreview(
            this.context(),
            this.mapping().scope,
            preview.runId,
          ),
        );
    });
  }
  addSampleInput(): void {
    if (this.sampleNamed().length < 15)
      this.sampleNamed.update((values) => [
        ...values,
        { name: "", content: "{}" },
      ]);
  }
  changeSampleInput(
    index: number,
    field: "name" | "content",
    value: string,
  ): void {
    this.sampleNamed.update((values) =>
      values.map((item, at) =>
        at === index ? { ...item, [field]: value } : item,
      ),
    );
  }
  removeSampleInput(index: number): void {
    this.sampleNamed.update((values) => values.filter((_, at) => at !== index));
  }
  addSampleParameter(): void {
    if (this.sampleParameters().length < 32)
      this.sampleParameters.update((values) => [
        ...values,
        { name: "", kind: "string", value: "" },
      ]);
  }
  changeSampleParameter(
    index: number,
    field: "name" | "kind" | "value",
    value: string,
  ): void {
    this.sampleParameters.update((values) =>
      values.map((item, at) =>
        at === index ? { ...item, [field]: value } : item,
      ),
    );
  }
  removeSampleParameter(index: number): void {
    this.sampleParameters.update((values) =>
      values.filter((_, at) => at !== index),
    );
  }
  async readOutput(ordinal: number): Promise<void> {
    await this.action(async () => {
      const preview = this.preview();
      if (preview)
        this.sampleOutput.set(
          await this.api.previewOutput(
            this.context(),
            this.mapping().scope,
            preview,
            ordinal,
          ),
        );
    });
  }
  async close(): Promise<void> {
    await this.action(async () => {
      await this.flush();
      if (!this.conflict()) this.closed.emit();
    });
  }
  private forgetView(id: string): void {
    ++this.lifetime;
    this.memory.forget(id, this.mapping().scope);
    this.core.close();
    this.view.set(null);
    this.draft.set(null);
    this.redoOperations = [];
    this.saveKey = null;
    this.previewRequest.set(null);
    this.savePending.set(false);
    this.dirty.set(false);
    this.busy.set(false);
    this.validation.set(null);
    this.comparison.set(null);
    this.preview.set(null);
    this.sampleOutput.set("");
  }
  command(command: WorkbenchCommand): void {
    if (command.id === "undo") void this.undo();
    else if (command.id === "redo") void this.redo();
    else if (command.id === "remove" && this.target())
      void this.apply({
        op: "remove_binding",
        binding: this.target()!.binding,
      });
    else
      this.element.nativeElement
        .querySelector<HTMLElement>("#edit-expression")
        ?.focus();
  }
  @HostListener("keydown", ["$event"])
  keyboard(event: KeyboardEvent): void {
    if (
      (event.ctrlKey || event.metaKey) &&
      event.key.toLowerCase() === "z" &&
      !(
        event.target instanceof HTMLInputElement ||
        event.target instanceof HTMLTextAreaElement
      )
    ) {
      event.preventDefault();
      if (event.shiftKey) void this.redo();
      else void this.undo();
    }
  }
  private async action(run: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set("");
    const life = this.lifetime;
    try {
      await run();
    } catch (error) {
      if (life === this.lifetime) this.fail(error);
    } finally {
      if (life === this.lifetime) this.busy.set(false);
    }
  }
  private fail(error: unknown): void {
    if (error instanceof DraftError && error.status === 412)
      this.conflict.set(true);
    const failure = error instanceof DraftError ? error.result?.failure : null;
    this.error.set(
      failure
        ? `${failure.operationIndex === null ? "" : `Operation ${failure.operationIndex}: `}${failure.message}`
        : error instanceof Error
          ? error.message
          : "The editor step failed.",
    );
    if (
      error instanceof EditCoreError &&
      (error.code === "timedOut" || error.code === "workerStopped")
    )
      this.view.set(null);
  }
  private focusAfterRender(selector: string): void {
    const life = this.lifetime;
    afterNextRender(
      () => {
        if (life === this.lifetime)
          this.element.nativeElement
            .querySelector<HTMLElement>(selector)
            ?.focus();
      },
      { injector: this.injector },
    );
  }
}
