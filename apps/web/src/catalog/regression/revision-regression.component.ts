import { DOCUMENT, DatePipe } from "@angular/common";
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from "@angular/core";
import { FormsModule } from "@angular/forms";
import { StatusChipComponent } from "../../workbench/status-chip.component";
import { MetadataValueComponent } from "../../workbench/metadata-value.component";
import {
  comparisonStatus,
  operationStatus,
} from "../../workbench/presentation";
import { RouterLink } from "@angular/router";
import { Subscription, map, switchMap, type Observable } from "rxjs";
import { BrowserSessionState } from "../../auth/browser-session-state";
import { CatalogContextStore } from "../catalog-context";
import type {
  CatalogContext,
  CatalogMapping,
  CatalogRevision,
  CatalogRevisionScope,
} from "../catalog-types";
import { catalogRoutes, revisionPanelKey } from "../catalog-types";
import {
  RegressionApi,
  RegressionError,
  decodeBytes,
  encodeBytes,
  exactPolicy,
  type CaseCapabilities,
  type CaseDefinition,
  type CaseDetail,
  type CasePage,
  type RegressionDocument,
  type RegressionRun,
  type RunPage,
} from "./regression-api";
import {
  RegressionViewRecovery,
  type DocumentDraft,
  type ParameterDraft,
  type PendingCase,
  type PendingRun,
  type RegressionWork,
} from "./regression-view-recovery";
import { HelpLinkComponent } from "../../help/help-link.component";
import { UnsentWork } from "../../workbench/unsent-work";

const encoder = new TextEncoder();
const maxBytes = 8 * 1024 * 1024;

/**
 * Private samples live only in this revision view; no local/session storage or HTML rendering.
 * A session boundary or panel replacement suspends the editor and uncertain requests into the
 * page's recovery provider; only the same owner's next admission of this revision restores them.
 */
@Component({
  selector: "weft-revision-regression",
  imports: [
    FormsModule,
    DatePipe,
    RouterLink,
    StatusChipComponent,
    MetadataValueComponent,
    HelpLinkComponent,
  ],
  templateUrl: "./revision-regression.component.html",
  styleUrl: "./revision-regression.component.css",
})
export class RevisionRegressionComponent {
  readonly comparisonStatus = comparisonStatus;
  readonly operationStatus = operationStatus;
  readonly context = input.required<CatalogContext>();
  readonly mapping = input.required<CatalogMapping>();
  readonly revision = input.required<CatalogRevision>();
  readonly routes = catalogRoutes;
  readonly otherRevision = signal<CatalogRevisionScope | null>(null);
  readonly workbench = input(false);
  private readonly api = inject(RegressionApi);
  private readonly sessions = inject(BrowserSessionState);
  private readonly contexts = inject(CatalogContextStore);
  private readonly recovery = inject(RegressionViewRecovery);
  private readonly document = inject(DOCUMENT);
  private requests = new Subscription();
  private epoch = 0;
  private operation = 0;
  private sequence = 0;
  private fileSequence = new Map<number, number>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private editId: { id: string; version: string } | null = null;
  private admission: {
    context: CatalogContext;
    scope: CatalogRevisionScope;
  } | null = null;
  /** A same-owner re-read replaces the inputs without suspending this panel's work. */
  private readonly binding = computed(() =>
    revisionPanelKey(this.context(), this.mapping(), this.revision()),
  );
  readonly capabilities = signal<CaseCapabilities | null>(null);
  readonly kept = computed(() => {
    if (this.capabilities()) return null;
    const work = this.recovery.peek(this.context(), this.revision().scope);
    if (!work) return null;
    return [
      work.draft ? "An unsaved test case draft is kept in this tab." : null,
      work.pendingCase || work.pendingRun
        ? "Recovery details for an earlier request are kept in this tab."
        : null,
      "Load regression cases to continue with it.",
    ]
      .filter(Boolean)
      .join(" ");
  });
  readonly cases = signal<CasePage | null>(null);
  readonly caseFilter = signal("");
  readonly caseAscending = signal(true);
  readonly visibleCases = computed(() =>
    (this.cases()?.cases ?? [])
      .filter((item) =>
        item.name
          .toLocaleLowerCase()
          .includes(this.caseFilter().toLocaleLowerCase()),
      )
      .slice()
      .sort(
        (a, b) =>
          a.name.localeCompare(b.name) * (this.caseAscending() ? 1 : -1),
      ),
  );
  readonly history = signal<RunPage | null>(null);
  readonly selected = signal<CaseDetail | null>(null);
  readonly run = signal<RegressionRun | null>(null);
  readonly busy = signal(false);
  readonly importing = signal(0);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly pendingCase = signal<PendingCase | null>(null);
  readonly pendingRun = signal<PendingRun | null>(null);
  readonly editing = signal(false);
  readonly inputs = signal<DocumentDraft[]>([]);
  readonly outputs = signal<DocumentDraft[]>([]);
  readonly parameters = signal<ParameterDraft[]>([]);
  readonly shown = signal<{
    name: string;
    text: string;
    truncated: boolean;
  } | null>(null);
  name = "";
  clock = "";
  target = "all";
  targetName = "";

  constructor() {
    inject(UnsentWork).track(
      () => this.editing() || !!this.pendingCase() || !!this.pendingRun(),
    );
    effect(() => {
      this.binding();
      this.sessions.generation();
      untracked(() => this.suspend());
    });
    // A failed or negative check is not a new owner: suspend unsent work rather than discard it.
    const invalidation = this.sessions.invalidated.subscribe(() =>
      this.suspend(),
    );
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.suspend();
    });
  }

  load(): void {
    if (this.busy()) return;
    this.send(
      (context) => this.api.capabilities(context, this.mapping().scope),
      (value) => {
        this.capabilities.set(value);
        this.admission = {
          context: this.context(),
          scope: { ...this.revision().scope },
        };
        this.resume(value);
        if (!value.readSamples) this.hidePrivate();
        this.loadCases();
      },
    );
  }
  loadCases(next = false): void {
    if (!this.capabilities()) return;
    const after = next ? this.cases()?.nextCaseId : null;
    this.send(
      (context) =>
        this.api.cases(context, this.mapping().scope, after || undefined),
      (page) => this.cases.set(page),
    );
  }
  loadHistory(next = false): void {
    if (!this.capabilities()?.readSamples) return;
    const after = next ? this.history()?.nextRunId : null;
    this.send(
      (context) =>
        this.api.runs(context, this.mapping().scope, after || undefined),
      (page) => this.history.set(page),
    );
  }
  openCase(id: string, version: string, refreshCases = false): void {
    if (this.busy() || this.pendingCase() || this.pendingRun()) return;
    this.send(
      (context) => this.api.case(context, this.mapping().scope, id, version),
      (value) => {
        if (!this.isCurrentRevision(value.revision)) {
          this.rejectDifferentRevision(value.revision);
          return;
        }
        this.selectCase(value);
        this.editing.set(false);
        if (refreshCases) this.loadCases();
      },
    );
  }
  newCase(): void {
    if (this.busy() || this.pendingCase()) return;
    this.otherRevision.set(null);
    this.editId = null;
    this.name = "";
    this.clock = new Date().toISOString();
    this.target = "all";
    this.targetName = "";
    this.inputs.set([this.draft("")]);
    this.outputs.set([this.draft("")]);
    this.parameters.set([]);
    this.editing.set(true);
    this.error.set(null);
  }
  editNext(): void {
    const value = this.selected();
    if (
      !value ||
      !this.isCurrentRevision(value.revision) ||
      this.busy() ||
      this.pendingCase() ||
      BigInt(value.version) === 9223372036854775807n
    )
      return;
    this.editId = {
      id: value.caseId,
      version: String(BigInt(value.version) + 1n),
    };
    const definition = value.definition;
    this.name = definition.name;
    this.clock = definition.currentDateTime;
    this.target = definition.target.kind;
    this.targetName = definition.target.name || "";
    this.inputs.set([
      this.savedDraft(null, definition.primaryBase64Url),
      ...definition.namedInputs.map((item) =>
        this.savedDraft(item.name, item.bytesBase64Url),
      ),
    ]);
    this.outputs.set(
      definition.expected.map((item) =>
        this.savedDraft(item.name, item.bytesBase64Url),
      ),
    );
    this.parameters.set(
      definition.parameters.map((item) => ({ id: ++this.sequence, ...item })),
    );
    this.editing.set(true);
    this.notice.set(
      "The next version will use the revision currently shown on this page and the active runtime.",
    );
  }
  addDocument(output: boolean): void {
    const rows = output ? this.outputs : this.inputs;
    if (rows().length < (output ? 64 : 16))
      rows.update((values) => [...values, this.draft("new-name")]);
  }
  updateDocument(
    output: boolean,
    id: number,
    change: Partial<DocumentDraft>,
  ): void {
    (output ? this.outputs : this.inputs).update((values) =>
      values.map((v) => (v.id === id ? { ...v, ...change } : v)),
    );
  }
  removeDocument(output: boolean, id: number): void {
    (output ? this.outputs : this.inputs).update((values) =>
      values.filter((v, i) => v.id !== id || (!output && i === 0)),
    );
    this.fileSequence.set(id, (this.fileSequence.get(id) || 0) + 1);
  }
  async chooseFile(output: boolean, id: number, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = "";
    if (!file || this.pendingCase()) return;
    const epoch = this.epoch;
    const sequence = (this.fileSequence.get(id) || 0) + 1;
    this.fileSequence.set(id, sequence);
    if (file.size > maxBytes) {
      this.error.set("Each fixture document must be at most 8 MiB.");
      return;
    }
    this.importing.update((count) => count + 1);
    try {
      const bytes = new Uint8Array(await file.arrayBuffer());
      if (
        epoch !== this.epoch ||
        this.fileSequence.get(id) !== sequence ||
        this.pendingCase()
      )
        return;
      this.updateDocument(output, id, {
        file: {
          name: file.name,
          encoded: encodeBytes(bytes),
          length: bytes.length,
        },
      });
    } catch {
      if (epoch === this.epoch) this.error.set("The file could not be read.");
    } finally {
      if (epoch === this.epoch) this.importing.update((count) => count - 1);
    }
  }
  clearFile(output: boolean, id: number): void {
    this.fileSequence.set(id, (this.fileSequence.get(id) || 0) + 1);
    this.updateDocument(output, id, { file: null });
  }
  addParameter(): void {
    if (this.parameters().length < 32)
      this.parameters.update((values) => [
        ...values,
        { id: ++this.sequence, name: "", kind: "string", value: "" },
      ]);
  }
  updateParameter(id: number, change: Partial<ParameterDraft>): void {
    this.parameters.update((values) =>
      values.map((v) => (v.id === id ? { ...v, ...change } : v)),
    );
  }
  removeParameter(id: number): void {
    this.parameters.update((values) => values.filter((v) => v.id !== id));
  }

  publish(): void {
    if (
      this.busy() ||
      this.importing() > 0 ||
      this.pendingCase() ||
      !this.capabilities()?.publishCases
    )
      return;
    try {
      const body = this.definition();
      const identity = this.editId || { id: crypto.randomUUID(), version: "1" };
      // The request is recorded for recovery only once it can be sent.
      this.sendPublication(() => {
        const pending = { ...identity, body };
        this.pendingCase.set(pending);
        return pending;
      });
    } catch (error) {
      this.error.set(
        error instanceof Error ? error.message : "The fixture is invalid.",
      );
    }
  }
  retryPublication(): void {
    const pending = this.pendingCase();
    if (!pending || this.busy()) return;
    this.sendPublication(() => pending);
  }
  private sendPublication(record: () => PendingCase): void {
    this.send(
      (context) => {
        const pending = record();
        return this.api
          .publish(
            context,
            this.mapping().scope,
            pending.id,
            pending.version,
            pending.body,
          )
          .pipe(map(() => pending));
      },
      (pending) => {
        this.pendingCase.set(null);
        this.editing.set(false);
        this.notice.set("The exact fixture version is saved.");
        if (this.pendingRun()) this.loadCases();
        else this.openCase(pending.id, pending.version, true);
      },
    );
  }
  recoverPublication(): void {
    const pending = this.pendingCase();
    if (!pending || this.busy()) return;
    this.send(
      (context) =>
        this.api.case(
          context,
          this.mapping().scope,
          pending.id,
          pending.version,
        ),
      (value) => {
        // Only the byte-for-byte original definition settles an uncertain publication.
        if (
          canonicalDefinition(value.definition) !==
          canonicalDefinition(pending.body)
        ) {
          this.error.set(
            "The saved version differs from the original request. Do not retry with changed content.",
          );
          return;
        }
        if (!this.isCurrentRevision(value.revision)) {
          this.error.set(
            "The saved version belongs to another revision. The original request is retained for recovery.",
          );
          return;
        }
        if (!this.pendingRun()) this.selectCase(value);
        this.pendingCase.set(null);
        this.editing.set(false);
        this.notice.set("The original saved fixture was recovered.");
        this.loadCases();
      },
    );
  }
  private selectCase(value: CaseDetail): void {
    this.otherRevision.set(null);
    const run = this.run();
    if (
      run &&
      (run.run.caseId !== value.caseId || run.run.version !== value.version)
    ) {
      this.stopTimer();
      this.run.set(null);
    }
    this.selected.set(value);
    this.shown.set(null);
  }
  submit(): void {
    const value = this.selected();
    if (
      !value ||
      !this.isCurrentRevision(value.revision) ||
      this.busy() ||
      this.pendingRun() ||
      !this.capabilities()?.readSamples
    )
      return;
    // The run is recorded for recovery only once its request can be sent.
    this.sendRun(() => {
      const pending = {
        id: crypto.randomUUID(),
        caseId: value.caseId,
        version: value.version,
      };
      this.pendingRun.set(pending);
      return pending;
    });
  }
  retryRun(): void {
    const pending = this.pendingRun();
    if (!pending || this.busy()) return;
    this.sendRun(() => pending);
  }
  private sendRun(record: () => PendingRun): void {
    this.send(
      (context) => {
        const pending = record();
        return this.api
          .submit(
            context,
            this.mapping().scope,
            pending.id,
            pending.caseId,
            pending.version,
          )
          .pipe(map(() => pending));
      },
      (pending) => this.openRun(pending.id),
    );
  }
  recoverRun(): void {
    const pending = this.pendingRun();
    if (pending) this.openRun(pending.id);
  }
  openRun(id: string, cancel = false): void {
    if (this.busy()) return;
    this.stopTimer();
    this.send(
      (context) => this.api.run(context, this.mapping().scope, id, cancel),
      (value) => {
        const pending = this.pendingRun();
        if (
          pending &&
          (pending.id !== id ||
            pending.caseId !== value.run.caseId ||
            pending.version !== value.run.version)
        ) {
          this.error.set(
            "The returned run does not match the original request.",
          );
          return;
        }
        if (!this.isCurrentRevision(value.run.revision)) {
          if (pending) {
            this.error.set(
              "The returned run belongs to another revision. The original request is retained for recovery.",
            );
          } else this.rejectDifferentRevision(value.run.revision);
          return;
        }
        this.otherRevision.set(null);
        this.pendingRun.set(null);
        this.run.set(value);
        this.shown.set(null);
        if (active(value)) this.schedulePoll(id);
        const selected = this.selected();
        if (
          !selected ||
          selected.caseId !== value.run.caseId ||
          selected.version !== value.run.version
        ) {
          this.selected.set(null);
          this.openCase(value.run.caseId, value.run.version);
        }
      },
    );
  }
  cancelRun(): void {
    const value = this.run();
    if (value && active(value)) this.openRun(value.run.runId, true);
  }
  show(item: RegressionDocument): void {
    const bytes = decodeBytes(item.bytesBase64Url);
    this.shown.set({
      name: item.name || "Primary output",
      text: new TextDecoder().decode(bytes.subarray(0, 65536)),
      truncated: bytes.length > 65536,
    });
  }
  download(item: RegressionDocument): void {
    const bytes = decodeBytes(item.bytesBase64Url);
    const url = URL.createObjectURL(
      new Blob([bytes as BlobPart], { type: "application/octet-stream" }),
    );
    const link = this.document.createElement("a");
    link.href = url;
    link.download =
      "regression-" +
      ((item.name || "primary")
        .replace(/[^a-zA-Z0-9._-]+/g, "_")
        .slice(0, 80) || "output") +
      ".bin";
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
  byteCount(item: RegressionDocument): number {
    return decodeBytes(item.bytesBase64Url).length;
  }
  isActive(): boolean {
    return this.run() !== null && active(this.run()!);
  }
  label(value: string): string {
    return value
      .replace(/([A-Z])/g, " $1")
      .replace(/^./, (c) => c.toUpperCase());
  }
  private isCurrentRevision(scope: CatalogRevisionScope): boolean {
    const current = this.revision().scope;
    return (
      scope.organizationId === current.organizationId &&
      scope.projectId === current.projectId &&
      scope.mappingId === current.mappingId &&
      scope.revisionId === current.revisionId
    );
  }
  private rejectDifferentRevision(scope: CatalogRevisionScope): void {
    this.stopTimer();
    this.selected.set(null);
    this.run.set(null);
    this.shown.set(null);
    this.otherRevision.set({ ...scope });
  }
  abandon(): void {
    if (this.busy()) return;
    if (
      !this.document.defaultView?.confirm(
        "The earlier request may already have succeeded. Forget its recovery details? Check saved cases or run history before creating another request.",
      )
    )
      return;
    this.pendingCase.set(null);
    this.pendingRun.set(null);
  }
  /**
   * Requests wait for any pending access check and are built with the freshest admitted context.
   * The panel stays busy meanwhile. A failed wait sends nothing and is not a refusal: retained
   * work stays for the boundary handling that follows.
   */
  private send<T>(
    request: (context: CatalogContext) => Observable<T>,
    success: (value: T) => void,
  ): void {
    if (this.busy()) return;
    const epoch = this.epoch;
    const operation = ++this.operation;
    this.busy.set(true);
    this.error.set(null);
    const subscription = this.contexts
      .admitted(this.context())
      .pipe(switchMap(request))
      .subscribe({
        next: (value) => {
          if (epoch !== this.epoch || operation !== this.operation) return;
          this.busy.set(false);
          success(value);
        },
        error: (error: unknown) => {
          if (epoch !== this.epoch || operation !== this.operation) return;
          this.busy.set(false);
          if (
            error instanceof RegressionError &&
            ["notFound", "rejected"].includes(error.kind)
          )
            this.hidePrivate();
          this.error.set(errorText(error));
        },
        complete: () => {
          if (epoch === this.epoch && operation === this.operation)
            this.busy.set(false);
        },
      });
    this.requests.add(subscription);
  }
  private definition(): CaseDefinition {
    const cap = this.capabilities();
    if (!cap || !this.name.trim() || encoder.encode(this.name).length > 256)
      throw new Error("Enter a fixture name of at most 256 UTF-8 bytes.");
    if (
      !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?(?:Z|[+-]\d\d:\d\d)$/.test(
        this.clock,
      ) ||
      this.clock.length > 35 ||
      !Number.isFinite(Date.parse(this.clock))
    )
      throw new Error(
        "Use a fixed clock with Z or an explicit UTC offset, for example 2026-10-20T12:00:00Z.",
      );
    const inputs = this.documents(this.inputs(), false);
    const expected = this.documents(this.outputs(), true);
    if (this.target === "named" && !this.targetName)
      throw new Error("Enter the exact target name.");
    const names = new Set<string>();
    const parameters = this.parameters().map(({ name, kind, value }) => {
      if (
        !name ||
        encoder.encode(name).length > 256 ||
        encoder.encode(value).length > 4096 ||
        names.has(name)
      )
        throw new Error("Parameter names must be unique and values bounded.");
      names.add(name);
      if (!["string", "integer", "float", "boolean"].includes(kind))
        throw new Error("Choose a supported parameter type.");
      return { name, kind, value };
    });
    return {
      name: this.name,
      currentDateTime: this.clock,
      revisionId: this.revision().scope.revisionId,
      runtime: { ...cap.runtime },
      limits: { ...cap.limits },
      comparisonPolicy: exactPolicy,
      target: {
        kind: this.target,
        name: this.target === "named" ? this.targetName : null,
      },
      primaryBase64Url: inputs[0].bytesBase64Url,
      namedInputs: inputs.slice(1),
      expected,
      parameters,
    };
  }
  private documents(
    values: DocumentDraft[],
    output: boolean,
  ): RegressionDocument[] {
    const names = new Set<string | null>();
    let total = 0;
    return values.map((value, index) => {
      const name = !output && index === 0 ? null : value.name || null;
      if (
        (name === null && !output && index !== 0) ||
        (name !== null && encoder.encode(name).length > 256) ||
        names.has(name)
      )
        throw new Error(
          "Document names must be unique; only one primary output may have a blank name.",
        );
      names.add(name);
      const encoded =
        value.file?.encoded ?? encodeBytes(encoder.encode(value.text));
      total += value.file?.length ?? encoder.encode(value.text).length;
      if (total > maxBytes)
        throw new Error(
          "Combined inputs and combined expected outputs must each fit within 8 MiB.",
        );
      return { name, bytesBase64Url: encoded };
    });
  }
  private draft(name: string): DocumentDraft {
    return { id: ++this.sequence, name, text: "", file: null };
  }
  private savedDraft(name: string | null, encoded: string): DocumentDraft {
    return {
      ...this.draft(name || ""),
      file: {
        name: "Saved exact bytes",
        encoded,
        length: decodeBytes(encoded).length,
      },
    };
  }
  private stopTimer(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
  private schedulePoll(id: string): void {
    this.stopTimer();
    const epoch = this.epoch;
    this.timer = setTimeout(() => {
      if (
        epoch !== this.epoch ||
        this.run()?.run.runId !== id ||
        !this.isActive()
      )
        return;
      // Polling skips a turn while access is checked rather than holding the panel busy.
      if (this.busy() || !this.contexts.canSend(this.context()))
        this.schedulePoll(id);
      else this.openRun(id);
    }, 2000);
  }
  private suspend(): void {
    const admission = this.admission,
      work = this.work();
    if (admission && work)
      this.recovery.keep(admission.context, admission.scope, work);
    this.clear();
  }
  private work(): RegressionWork | null {
    const draft = this.editing()
      ? {
          editId: this.editId,
          name: this.name,
          clock: this.clock,
          target: this.target,
          targetName: this.targetName,
          inputs: this.inputs(),
          outputs: this.outputs(),
          parameters: this.parameters(),
        }
      : null;
    const pendingCase = this.pendingCase(),
      pendingRun = this.pendingRun();
    return draft || pendingCase || pendingRun
      ? { draft, pendingCase, pendingRun }
      : null;
  }
  /** Restores suspended work after this panel's own fresh capability read; nothing is sent again. */
  private resume(capabilities: CaseCapabilities): void {
    const work = this.recovery.take(this.context(), this.revision().scope);
    if (!work) return;
    this.pendingCase.set(work.pendingCase);
    this.pendingRun.set(work.pendingRun);
    // An editor needs current publication permission; an uncertain request keeps its frame regardless.
    const draft = capabilities.publishCases ? work.draft : null;
    if (draft) {
      this.editId = draft.editId;
      this.name = draft.name;
      this.clock = draft.clock;
      this.target = draft.target;
      this.targetName = draft.targetName;
      this.inputs.set(draft.inputs.map((item) => this.renumber(item)));
      this.outputs.set(draft.outputs.map((item) => this.renumber(item)));
      this.parameters.set(draft.parameters.map((item) => this.renumber(item)));
      this.editing.set(true);
    }
    if (draft || work.pendingCase || work.pendingRun)
      this.notice.set(
        "Work kept in this tab is restored. Nothing was sent again.",
      );
  }
  private renumber<T extends { id: number }>(item: T): T {
    return { ...item, id: ++this.sequence };
  }
  private clear(): void {
    this.otherRevision.set(null);
    this.admission = null;
    ++this.epoch;
    this.requests.unsubscribe();
    this.requests = new Subscription();
    this.stopTimer();
    this.fileSequence.clear();
    this.importing.set(0);
    this.capabilities.set(null);
    this.cases.set(null);
    this.caseFilter.set("");
    this.history.set(null);
    this.selected.set(null);
    this.run.set(null);
    this.busy.set(false);
    this.error.set(null);
    this.notice.set(null);
    this.pendingCase.set(null);
    this.pendingRun.set(null);
    this.editing.set(false);
    this.inputs.set([]);
    this.outputs.set([]);
    this.parameters.set([]);
    this.shown.set(null);
    this.name = "";
    this.clock = "";
    this.targetName = "";
    this.editId = null;
  }
  private hidePrivate(): void {
    this.otherRevision.set(null);
    this.stopTimer();
    this.selected.set(null);
    this.run.set(null);
    this.shown.set(null);
    this.inputs.set([]);
    this.outputs.set([]);
    this.parameters.set([]);
    this.pendingCase.set(null);
    this.editing.set(false);
    this.name = "";
    this.clock = "";
    this.targetName = "";
    this.editId = null;
    this.fileSequence.clear();
  }
}
function active(value: RegressionRun): boolean {
  return value.run.state === "queued" || value.run.state === "running";
}
function stable(value: unknown): string {
  if (Array.isArray(value)) return "[" + value.map(stable).join(",") + "]";
  if (value && typeof value === "object")
    return (
      "{" +
      Object.entries(value)
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([key, v]) => JSON.stringify(key) + ":" + stable(v))
        .join(",") +
      "}"
    );
  return JSON.stringify(value);
}
function canonicalDefinition(value: CaseDefinition): string {
  const order = <T extends { name: string | null }>(items: T[]) =>
    [...items].sort((a, b) => {
      if (a.name === b.name) return 0;
      if (a.name === null) return -1;
      if (b.name === null) return 1;
      return a.name < b.name ? -1 : 1;
    });
  return stable({
    ...value,
    namedInputs: order(value.namedInputs),
    expected: order(value.expected),
    parameters: order(value.parameters),
  });
}
function errorText(error: unknown): string {
  const kind = error instanceof RegressionError ? error.kind : "unavailable";
  return {
    unavailable:
      "The request could not be confirmed. Keep the original request and check or retry it explicitly.",
    notFound:
      "The requested fixture or run is unavailable, or regression testing is disabled.",
    rejected: "Your access changed. Refresh the catalog before continuing.",
    conflict:
      "This ID already belongs to different content. The original request has been kept.",
    invalid:
      "The server refused the fixture. Check its names, types, sizes and clock. The original request has been kept.",
    runtime:
      "This fixture requires a different runtime. Publish a new version for the active runtime.",
    busy: "The active-run limit has been reached. Complete or cancel existing runs before retrying.",
    incompatible:
      "The response could not be verified. No passing result is assumed.",
  }[kind];
}
