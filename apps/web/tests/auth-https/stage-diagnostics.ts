import {
  constants,
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  realpathSync,
  writeFileSync,
} from "node:fs";
import { isAbsolute, join } from "node:path";

// This receipt is observational only. It contains no error, request, config or
// browser operands and is never read by admission, assertions or cleanup owners.
// A stage names the operation attempted; "none" means no observed failure, NOT
// qualification success. A missing receipt likewise provides no success evidence.
export const bodyStages = Object.freeze([
  "launch",
  "version",
  "rootFetch",
  "baseline",
  "context",
  "pageBinding",
  "navigation",
  "anonymous",
  "scenario",
] as const);
export const cleanupStages = Object.freeze([
  "seal",
  "context",
  "browser",
  "boundary",
  "finalStop",
] as const);
type BodyStage = (typeof bodyStages)[number];
export type CleanupStage = (typeof cleanupStages)[number];

export function stageReceipt(body: unknown, cleanup: unknown): string {
  if (
    !(body === "none" || bodyStages.some((stage) => stage === body)) ||
    !(cleanup === "none" || cleanupStages.some((stage) => stage === cleanup))
  )
    throw new Error("Invalid qualification stage category.");
  return (
    JSON.stringify({ version: 1, bodyFailure: body, cleanupFailure: cleanup }) +
    "\n"
  );
}

export class StageDiagnostics {
  private bodyStage: BodyStage = "launch";
  private cleanupStage: CleanupStage = "seal";
  private bodyFailure: BodyStage | "none" = "none";
  private cleanupFailure: CleanupStage | "none" = "none";
  private readonly root: string;
  constructor(root: string) {
    this.root = root;
  }
  body(stage: BodyStage): void {
    if (bodyStages.some((allowed) => allowed === stage)) this.bodyStage = stage;
  }
  cleanup(stage: CleanupStage): void {
    if (cleanupStages.some((allowed) => allowed === stage))
      this.cleanupStage = stage;
  }
  bodyFailed(): void {
    if (this.bodyFailure === "none") this.bodyFailure = this.bodyStage;
  }
  cleanupFailed(stage: CleanupStage = this.cleanupStage): void {
    if (
      this.cleanupFailure === "none" &&
      cleanupStages.some((allowed) => allowed === stage)
    )
      this.cleanupFailure = stage;
  }
  receipt(): string {
    return stageReceipt(this.bodyFailure, this.cleanupFailure);
  }
  write(): void {
    let fd: number | undefined;
    try {
      // Revalidate the admitted private root; never follow a replacement link or
      // overwrite an existing receipt. Unsupported platforms simply emit none.
      if (
        !isAbsolute(this.root) ||
        realpathSync(this.root) !== this.root ||
        !Number.isInteger(constants.O_NOFOLLOW)
      )
        return;
      const directory = lstatSync(this.root);
      if (
        !directory.isDirectory() ||
        directory.uid !== process.geteuid?.() ||
        (directory.mode & 0o777) !== 0o700
      )
        return;
      const receipt = this.receipt();
      fd = openSync(
        join(this.root, "stage-diagnostics.json"),
        constants.O_WRONLY |
          constants.O_CREAT |
          constants.O_EXCL |
          constants.O_NOFOLLOW,
        0o600,
      );
      const file = fstatSync(fd);
      if (
        !file.isFile() ||
        file.nlink !== 1 ||
        file.uid !== process.geteuid?.() ||
        (file.mode & 0o777) !== 0o600
      )
        return;
      writeFileSync(fd, receipt, { encoding: "utf8" });
    } catch {
      // Diagnostics cannot replace a failure, authorize a request, or skip cleanup.
    } finally {
      if (fd !== undefined) {
        try {
          closeSync(fd);
        } catch {
          /* Observational only. */
        }
      }
    }
  }
}
