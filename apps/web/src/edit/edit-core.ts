import type { components } from "../api/generated";
import type { ProjectionDocument } from "../workbench/projection-view";
import { editAssets } from "./edit-assets.generated";

export type EditScript = components["schemas"]["EditScriptV1"];
export type EditOperation = EditScript["operations"][number];
export type EditBinding = components["schemas"]["EditBindingV1"];
export interface EditView {
  operations: EditScript;
  projection: Omit<ProjectionDocument, "schemaVersion" | "provenance">;
  validation: {
    isValid: boolean;
    totalIssueCount: number;
    issues: { code: string; message: string }[];
  };
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string | null;
  redoLabel: string | null;
  targets: { fieldId: string; binding: EditBinding; node: string | null }[];
  nextNodeId: string | null;
}
export class EditCoreError extends Error {
  constructor(
    readonly code: string,
    message = "The editor could not complete this step.",
    readonly operationIndex: string | null = null,
  ) {
    super(message);
  }
}

/** A single-owner, bounded dedicated worker. Native data is only ever transferred as opaque bytes. */
export class EditCore {
  private worker: Worker | null = null;
  private id = 0;
  private generation = 0;
  private loading: AbortController | null = null;
  private pending: {
    id: number;
    resolve: (view: EditView) => void;
    reject: (error: Error) => void;
    timer: ReturnType<typeof setTimeout>;
  } | null = null;

  async open(native: ArrayBuffer, operations: EditScript): Promise<EditView> {
    this.close();
    const generation = this.generation;
    if (!editAssets)
      throw new EditCoreError(
        "unavailable",
        "This package does not contain the verified editor module.",
      );
    if (native.byteLength < 1 || native.byteLength > 4 * 1024 * 1024)
      throw new EditCoreError("nativeLimitExceeded");
    this.loading = new AbortController();
    const response = await fetch(editAssets.wasm, {
      integrity: editAssets.integrity,
      credentials: "same-origin",
      cache: "no-cache",
      signal: this.loading.signal,
    });
    if (!response.ok) throw new EditCoreError("integrity");
    const module = await response.arrayBuffer();
    if (generation !== this.generation) throw new EditCoreError("suspended");
    this.loading = null;
    if (module.byteLength > 16 * 1024 * 1024)
      throw new EditCoreError("integrity");
    this.worker = new Worker(editAssets.worker, {
      type: "module",
      name: "weft-mapping-editor",
    });
    this.worker.onmessage = (event: MessageEvent<unknown>) =>
      this.receive(event.data);
    this.worker.onerror = () => this.close(new EditCoreError("workerStopped"));
    this.worker.onmessageerror = () =>
      this.close(new EditCoreError("invalidView"));
    return this.call(
      { kind: "open", native, operations: JSON.stringify(operations), module },
      [native, module],
    );
  }
  apply(operation: EditOperation): Promise<EditView> {
    return this.call({ kind: "apply", operation: JSON.stringify(operation) });
  }
  applyText(operation: string): Promise<EditView> {
    return this.call({ kind: "apply", operation });
  }
  undo(): Promise<EditView> {
    return this.call({ kind: "undo" });
  }
  redo(): Promise<EditView> {
    return this.call({ kind: "redo" });
  }
  close(error = new EditCoreError("suspended")): void {
    ++this.generation;
    this.loading?.abort();
    this.loading = null;
    this.worker?.terminate();
    this.worker = null;
    if (this.pending) {
      clearTimeout(this.pending.timer);
      this.pending.reject(error);
      this.pending = null;
    }
  }
  private call(
    data: Record<string, unknown>,
    transfers: Transferable[] = [],
  ): Promise<EditView> {
    if (!this.worker || this.pending)
      return Promise.reject(new EditCoreError("busy"));
    return new Promise((resolve, reject) => {
      const id = ++this.id;
      const timer = setTimeout(
        () =>
          this.close(
            new EditCoreError(
              "timedOut",
              "The editor stopped after 10 seconds. Restart from your autosaved draft.",
            ),
          ),
        10_000,
      );
      this.pending = { id, resolve, reject, timer };
      this.worker!.postMessage({ ...data, id }, transfers);
    });
  }
  private receive(data: unknown): void {
    if (
      !this.pending ||
      !data ||
      typeof data !== "object" ||
      !("id" in data) ||
      data.id !== this.pending.id
    )
      return;
    const pending = this.pending;
    this.pending = null;
    clearTimeout(pending.timer);
    if ("failure" in data && data.failure && typeof data.failure === "object") {
      const failure = data.failure as {
        code?: string;
        message?: string;
        operationIndex?: string;
      };
      pending.reject(
        new EditCoreError(
          failure.code ?? "editFailure",
          failure.message,
          failure.operationIndex ?? null,
        ),
      );
    } else if ("view" in data && validView(data.view))
      pending.resolve(data.view);
    else {
      pending.reject(new EditCoreError("invalidView"));
      this.close();
    }
  }
}

function validView(value: unknown): value is EditView {
  if (!value || typeof value !== "object") return false;
  const view = value as EditView;
  return (
    view.operations?.format === "ferrule.edit.v1" &&
    Array.isArray(view.operations.operations) &&
    view.operations.operations.length <= 2000 &&
    JSON.stringify(view).length <= 16 * 1024 * 1024 &&
    Array.isArray(view.projection?.fields) &&
    Array.isArray(view.targets) &&
    Array.isArray(view.validation?.issues) &&
    typeof view.canUndo === "boolean" &&
    typeof view.canRedo === "boolean"
  );
}
