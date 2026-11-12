import { computed, signal } from "@angular/core";
import {
  catchError,
  switchMap,
  tap,
  throwError,
  type Observable,
  type Subscription,
} from "rxjs";
import type { CatalogRevisionScope } from "../catalog-types";
import { PreviewApi } from "./preview-api";
import {
  PreviewError,
  previewLimits,
  type PreviewErrorKind,
  type PreviewOutput,
  type PreviewParameterKind,
  type PreviewRun,
  type PreviewSubmission,
  type PreviewTargetKind,
} from "./preview-types";
import { csvPreviewProfile } from "./csv-contract";

export interface PreviewInputDraft {
  readonly id: number;
  name: string;
  text: string;
  /** Exact bytes of a chosen file; text is ignored while a file is attached. */
  file: { name: string; bytes: Uint8Array } | null;
}

export interface PreviewParameterDraft {
  readonly id: number;
  name: string;
  kind: PreviewParameterKind;
  value: string;
}

export interface PreviewBinding {
  readonly scope: CatalogRevisionScope;
  /**
   * Waits for any pending access check, then emits the freshest admitted request token. It fails
   * when the binding's context is no longer current, and nothing is sent.
   */
  readonly admit: () => Observable<string>;
  /** No access check is pending, so a request can be sent at once. */
  readonly ready: () => boolean;
  readonly csvAvailable?: () => boolean;
}

/** A submission whose key outlives a reload until its run is known and finished with. */
interface PreviewRecord {
  readonly key: string;
  readonly runId: string | null;
}

export type PreviewNotice =
  "recovered" | "unconfirmed" | "replayed" | "cancelRequested" | null;

const pollDelays = [1000, 1000, 2000, 2000, 3000, 5000];
const maximumPollFailures = 3;
const encoder = new TextEncoder();

/**
 * One revision's preview form and its latest run. Only the request key and run identity are kept
 * in session storage, so an unconfirmed submission can be recovered without duplicating a run.
 */
export class PreviewState {
  private binding: PreviewBinding | null = null;
  private request: Subscription | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private polls = 0;
  private pollFailures = 0;
  private nextId = 1;
  private readonly currentRun = signal<PreviewRun | null>(null);
  private readonly currentBusy = signal<
    "submitting" | "canceling" | "reading" | null
  >(null);
  private readonly currentError = signal<PreviewErrorKind | null>(null);
  private readonly currentFormError = signal<string | null>(null);
  private readonly currentNotice = signal<PreviewNotice>(null);
  private readonly currentRecord = signal<PreviewRecord | null>(null);

  readonly inputs = signal<PreviewInputDraft[]>([this.input("")]);
  readonly parameters = signal<PreviewParameterDraft[]>([]);
  readonly targetKind = signal<PreviewTargetKind>("all");
  readonly documentProfile = signal(false);
  readonly csvProfile = signal(false);
  readonly submitted = signal<{
    runId: string;
    body: PreviewSubmission;
  } | null>(null);
  readonly targetName = signal("");
  readonly run = this.currentRun.asReadonly();
  readonly busy = this.currentBusy.asReadonly();
  readonly error = this.currentError.asReadonly();
  readonly formError = this.currentFormError.asReadonly();
  readonly notice = this.currentNotice.asReadonly();
  readonly active = computed(() => {
    const state = this.currentRun()?.state;
    return state === "queued" || state === "running";
  });
  readonly pendingKey = computed(() => {
    const record = this.currentRecord();
    return record !== null && record.runId === null;
  });

  constructor(
    private readonly api: PreviewApi,
    private readonly storage: Storage | null,
  ) {}

  bind(binding: PreviewBinding): void {
    if (this.binding && sameScope(this.binding.scope, binding.scope)) {
      this.binding = binding;
      return;
    }
    this.unbind();
    this.binding = binding;
    const record = this.load();
    this.currentRecord.set(record);
    if (record?.runId) {
      this.currentNotice.set("recovered");
      this.read(record.runId);
    } else if (record) this.currentNotice.set("unconfirmed");
  }

  unbind(): void {
    this.stop();
    this.binding = null;
    this.currentRun.set(null);
    this.currentBusy.set(null);
    this.currentError.set(null);
    this.currentFormError.set(null);
    this.currentNotice.set(null);
    this.currentRecord.set(null);
    this.submitted.set(null);
  }

  destroy(): void {
    this.unbind();
  }

  addInput(): void {
    if (this.inputs().length < previewLimits.inputDocuments)
      this.inputs.update((inputs) => [...inputs, this.input("")]);
  }

  removeInput(id: number): void {
    this.inputs.update((inputs) =>
      inputs.filter((input, index) => index === 0 || input.id !== id),
    );
  }

  updateInput(
    id: number,
    change: Partial<Omit<PreviewInputDraft, "id">>,
  ): void {
    this.inputs.update((inputs) =>
      inputs.map((input) =>
        input.id === id ? { ...input, ...change } : input,
      ),
    );
  }

  addParameter(): void {
    if (this.parameters().length < previewLimits.parameters)
      this.parameters.update((parameters) => [
        ...parameters,
        { id: this.nextId++, name: "", kind: "string", value: "" },
      ]);
  }

  removeParameter(id: number): void {
    this.parameters.update((parameters) =>
      parameters.filter((item) => item.id !== id),
    );
  }

  updateParameter(
    id: number,
    change: Partial<Omit<PreviewParameterDraft, "id">>,
  ): void {
    this.parameters.update((parameters) =>
      parameters.map((item) =>
        item.id === id ? { ...item, ...change } : item,
      ),
    );
  }

  submit(): void {
    const binding = this.binding;
    if (!binding || this.currentBusy() !== null || this.active()) return;
    this.currentFormError.set(null);
    this.currentError.set(null);
    let body: PreviewSubmission;
    try {
      body = this.body();
    } catch (error: unknown) {
      this.currentFormError.set(
        error instanceof Error ? error.message : "The preview form is invalid.",
      );
      return;
    }
    const record = this.currentRecord();
    const key =
      record && record.runId === null ? record.key : crypto.randomUUID();
    this.currentBusy.set("submitting");
    // The key is kept for recovery only once the request can be sent.
    this.request = this.admitted(binding)
      .pipe(
        tap(() => this.save({ key, runId: null })),
        switchMap((token) => this.api.submit(binding.scope, token, key, body)),
      )
      .subscribe({
        next: (submitted) => {
          this.request = null;
          this.currentBusy.set(null);
          this.save({ key, runId: submitted.run.runId });
          this.submitted.set({ runId: submitted.run.runId, body });
          this.currentNotice.set(
            submitted.outcome === "replayed" ? "replayed" : null,
          );
          this.track(submitted.run);
        },
        error: (error: unknown) => {
          this.request = null;
          this.currentBusy.set(null);
          const kind = kindOf(error);
          // A refused request was never recorded, so its key can be reused for a corrected form.
          if (
            kind === "invalidRequest" ||
            kind === "bodyLimit" ||
            kind === "unsupportedMediaType"
          )
            this.save(record && record.runId === null ? record : null);
          this.currentError.set(kind);
        },
      });
  }

  /** Forgets an unconfirmed or finished submission so the next submit starts a new run. */
  discard(): void {
    if (this.currentBusy() !== null || this.active()) return;
    this.stop();
    this.save(null);
    this.currentRun.set(null);
    this.currentError.set(null);
    this.currentNotice.set(null);
  }

  cancel(): void {
    const binding = this.binding;
    const run = this.currentRun();
    if (!binding || !run || !this.active() || this.currentBusy() !== null)
      return;
    this.stop();
    this.currentBusy.set("canceling");
    this.request = this.admitted(binding)
      .pipe(
        switchMap((token) => this.api.cancel(binding.scope, run.runId, token)),
      )
      .subscribe({
        next: (canceled) => {
          this.request = null;
          this.currentBusy.set(null);
          this.currentNotice.set(
            canceled.outcome === "requested" ? "cancelRequested" : null,
          );
          this.track(canceled.run);
        },
        error: (error: unknown) => {
          this.request = null;
          this.currentBusy.set(null);
          this.currentError.set(kindOf(error));
          this.track(run);
        },
      });
  }

  /** Re-reads the current or recovered run after a failed status check. */
  refresh(): void {
    const runId = this.currentRun()?.runId ?? this.currentRecord()?.runId;
    if (runId && this.currentBusy() === null) {
      this.stop();
      this.read(runId);
    }
  }

  output(output: PreviewOutput) {
    const binding = this.binding;
    const run = this.currentRun();
    if (!binding || !run) throw new PreviewError("invalidLink");
    return this.admitted(binding).pipe(
      switchMap(() => this.api.output(binding.scope, run.runId, output)),
    );
  }

  private read(runId: string): void {
    const binding = this.binding;
    if (!binding) return;
    this.currentBusy.set("reading");
    // A status read waits for a pending access check instead of counting as a failed poll.
    this.request = this.admitted(binding)
      .pipe(switchMap(() => this.api.read(binding.scope, runId)))
      .subscribe({
        next: (run) => {
          this.request = null;
          this.currentBusy.set(null);
          this.currentError.set(null);
          this.pollFailures = 0;
          this.track(run);
        },
        error: (error: unknown) => {
          this.request = null;
          this.currentBusy.set(null);
          const kind = kindOf(error);
          if (kind === "notFound") {
            // The run expired, or belongs to someone else; nothing remains to recover.
            this.save(null);
            this.currentRun.set(null);
            this.currentNotice.set(null);
          } else if (
            this.active() &&
            ++this.pollFailures < maximumPollFailures
          ) {
            this.schedule(runId);
            return;
          }
          this.currentError.set(kind);
        },
      });
  }

  /** A failed wait sent nothing and is not a refusal. */
  private admitted(binding: PreviewBinding): Observable<string> {
    return binding
      .admit()
      .pipe(
        catchError(() => throwError(() => new PreviewError("unavailable"))),
      );
  }

  private track(run: PreviewRun): void {
    this.currentRun.set(run);
    if (run.state === "queued" || run.state === "running")
      this.schedule(run.runId);
    else this.stop();
  }

  private schedule(runId: string): void {
    if (this.timer !== null) clearTimeout(this.timer);
    const delay = pollDelays[Math.min(this.polls++, pollDelays.length - 1)];
    this.timer = setTimeout(() => {
      this.timer = null;
      if (!this.binding || this.currentBusy() !== null) return;
      // Polling skips a turn while access is checked rather than holding the form busy.
      if (this.binding.ready()) this.read(runId);
      else this.schedule(runId);
    }, delay);
  }

  private stop(): void {
    this.request?.unsubscribe();
    this.request = null;
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    this.polls = 0;
    this.pollFailures = 0;
  }

  private body(): PreviewSubmission {
    if (this.csvProfile() && !this.binding?.csvAvailable?.())
      throw new Error(
        "The server has not offered the CSV/TSV preview runtime.",
      );
    if (
      this.csvProfile() &&
      (this.inputs().length !== 1 || this.targetKind() === "named")
    )
      throw new Error(
        "CSV/TSV preview requires one primary input and a primary or all-output target.",
      );
    const inputs = this.inputs();
    const names = new Set<string>();
    let total = 0;
    const encoded = inputs.map((input, index) => {
      const bytes = input.file ? input.file.bytes : encoder.encode(input.text);
      if (bytes.length === 0 && !this.csvProfile())
        throw new Error(
          index === 0
            ? this.documentProfile()
              ? "Enter or choose the primary document."
              : "Enter or choose the primary JSON document."
            : "Every named input needs content.",
        );
      total += bytes.length;
      if (index === 0) return { content: base64(bytes), name: null };
      const name = input.name.trim();
      if (!name || encoder.encode(name).length > previewLimits.nameBytes)
        throw new Error(
          "Every named input needs a source name of at most 256 bytes.",
        );
      if (names.has(name))
        throw new Error(`The input name "${name}" is used more than once.`);
      names.add(name);
      return { content: base64(bytes), name };
    });
    if (total > previewLimits.inputBytes)
      throw new Error("The inputs exceed the 8 MiB preview limit.");
    const parameterNames = new Set<string>();
    const parameters = this.parameters().map((parameter) => {
      const name = parameter.name.trim();
      if (!name || encoder.encode(name).length > previewLimits.nameBytes)
        throw new Error("Every parameter needs a name of at most 256 bytes.");
      if (parameterNames.has(name))
        throw new Error(`The parameter "${name}" is set more than once.`);
      parameterNames.add(name);
      if (encoder.encode(parameter.value).length > previewLimits.parameterBytes)
        throw new Error(`The value of "${name}" exceeds 4 KiB.`);
      return { name, kind: parameter.kind, value: parameter.value };
    });
    const kind = this.targetKind();
    const targetName = this.targetName().trim();
    if (kind === "named" && !targetName)
      throw new Error("Enter the named output target to preview.");
    return {
      target:
        kind === "named" ? { kind, name: targetName } : { kind, name: null },
      parameters,
      inputs: encoded,
      ...(this.csvProfile()
        ? { profile: csvPreviewProfile }
        : this.documentProfile()
          ? { profile: "weft.ferrule-document-preview.v1" as const }
          : {}),
    };
  }

  private input(name: string): PreviewInputDraft {
    return { id: this.nextId++, name, text: "", file: null };
  }

  private storageKey(): string | null {
    const scope = this.binding?.scope;
    return scope
      ? `weft.preview.v1/${scope.organizationId}/${scope.projectId}/${scope.mappingId}/${scope.revisionId}`
      : null;
  }

  private load(): PreviewRecord | null {
    const key = this.storageKey();
    try {
      const value: unknown =
        key && this.storage
          ? JSON.parse(this.storage.getItem(key) ?? "null")
          : null;
      if (
        value !== null &&
        typeof value === "object" &&
        typeof (value as PreviewRecord).key === "string" &&
        uuid.test((value as PreviewRecord).key) &&
        ((value as PreviewRecord).runId === null ||
          uuid.test(String((value as PreviewRecord).runId)))
      )
        return {
          key: (value as PreviewRecord).key,
          runId: (value as PreviewRecord).runId,
        };
    } catch {
      // An unreadable record is discarded below.
    }
    if (key) this.storage?.removeItem(key);
    return null;
  }

  private save(record: PreviewRecord | null): void {
    this.currentRecord.set(record);
    const key = this.storageKey();
    if (!key || !this.storage) return;
    try {
      if (record) this.storage.setItem(key, JSON.stringify(record));
      else this.storage.removeItem(key);
    } catch {
      // Recovery is best effort; the server still deduplicates by key within this page.
    }
  }
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function kindOf(error: unknown): PreviewErrorKind {
  return error instanceof PreviewError ? error.kind : "unavailable";
}

function sameScope(a: CatalogRevisionScope, b: CatalogRevisionScope): boolean {
  return (
    a.organizationId === b.organizationId &&
    a.projectId === b.projectId &&
    a.mappingId === b.mappingId &&
    a.revisionId === b.revisionId
  );
}

function base64(bytes: Uint8Array): string {
  let binary = "";
  for (let index = 0; index < bytes.length; index += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
  return btoa(binary);
}
