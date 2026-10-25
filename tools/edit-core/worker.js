// Loaded only as /edit/worker.js. Its response CSP blocks networking and nested workers.
import { initSync, EditorSession } from "./core.js";

let session;
let initialized = false;
const maximumNative = 4 * 1024 * 1024;
const maximumOperations = 1024 * 1024;
const bytes = new TextEncoder();
self.onmessage = ({ data }) => {
  const id = data?.id;
  if (!Number.isSafeInteger(id) || id < 1) return;
  try {
    if (data.kind === "open") {
      if (!(data.native instanceof ArrayBuffer) || data.native.byteLength < 1 || data.native.byteLength > maximumNative
          || typeof data.operations !== "string" || data.operations.length > maximumOperations || bytes.encode(data.operations).length > maximumOperations) throw new Error("Invalid bounded edit input.");
      if (!initialized) {
        if (!(data.module instanceof ArrayBuffer) || data.module.byteLength > 16 * 1024 * 1024) throw new Error("A verified bounded module is required.");
        initSync({ module: data.module });
        initialized = true;
      }
      session?.free();
      session = new EditorSession(new Uint8Array(data.native), data.operations);
    } else {
      if (!session) throw new Error("Open the draft first.");
      switch (data.kind) {
        case "apply":
          if (typeof data.operation !== "string" || data.operation.length > maximumOperations || bytes.encode(data.operation).length > maximumOperations) throw new Error("Invalid bounded edit input.");
          session.apply(data.operation);
          break;
        case "undo": session.undo(); break;
        case "redo": session.redo(); break;
        default: throw new Error("Unknown edit request.");
      }
    }
    self.postMessage({ id, view: JSON.parse(session.view()) });
  } catch (error) {
    // Rust exposes a bounded typed failure; never reflect native bytes or JS exception details.
    let failure = { code: "editFailure", message: "The edit could not be applied.", operationIndex: null };
    try {
      const parsed = JSON.parse(String(error));
      if (typeof parsed.code === "string" && typeof parsed.message === "string") failure = parsed;
    } catch { /* An infrastructure error has no mapping details. */ }
    self.postMessage({ id, failure });
  }
};
