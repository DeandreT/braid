import { safeFailure } from "./wire";

export type CleanupResult = Readonly<{
  primaryFailed: boolean;
  closeFailed: boolean;
  drainFailed: boolean;
}>;
export class CleanupFailure extends Error {
  constructor(readonly outcomes: CleanupResult) {
    super("Owned qualification operation/close/drain failed.");
  }
  override toString(): string {
    return "Owned qualification operation/close/drain failed.";
  }
}
// Start and observe BOTH original first calls before awaiting either. A rejection
// cannot skip the other owner; these observations fulfill with categories only.
export class CleanupPair {
  private original: Promise<CleanupResult> | null = null;
  run(
    primaryFailed: boolean,
    close: () => Promise<void>,
    drain: () => Promise<void>,
  ): Promise<CleanupResult> {
    return (this.original ??= this.runCore(primaryFailed, close, drain));
  }
  private async runCore(
    primaryFailed: boolean,
    close: () => Promise<void>,
    drain: () => Promise<void>,
  ): Promise<CleanupResult> {
    const start = (operation: () => Promise<void>): Promise<void> => {
      try {
        return operation();
      } catch {
        return Promise.reject(safeFailure());
      }
    };
    const closeOriginal = start(close),
      closeResult = closeOriginal.then(
        () => false,
        () => true,
      );
    const drainOriginal = start(drain),
      drainResult = drainOriginal.then(
        () => false,
        () => true,
      );
    const closeFailed = await closeResult,
      drainFailed = await drainResult;
    return { primaryFailed, closeFailed, drainFailed };
  }
}
