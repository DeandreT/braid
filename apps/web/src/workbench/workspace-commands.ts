import { Injectable, signal } from "@angular/core";
import { Subject } from "rxjs";
import type { WorkbenchCommand } from "./workbench-frame.component";

/** Current route contributes commands; it removes them when its authorized content unmounts. */
@Injectable({ providedIn: "root" })
export class WorkspaceCommands {
  readonly entries = signal<readonly WorkbenchCommand[]>([]);
  readonly opening = new Subject<void>();
  readonly chosen = new Subject<WorkbenchCommand>();
  readonly requested = new Subject<void>();
}
