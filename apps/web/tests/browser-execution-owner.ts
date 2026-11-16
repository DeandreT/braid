import { expect } from "@playwright/test";
import {
  spawn,
  execFileSync,
  type ChildProcessWithoutNullStreams,
} from "node:child_process";
import { readFileSync } from "node:fs";
import { createHash, randomUUID } from "node:crypto";
import type { components } from "../src/api/generated";

interface Binding {
  fixtureId: string;
  config: string;
  manifest: string;
  readOrigin: string;
  node: string;
  dotnet: string;
  operator: string;
  launcher?: string;
  privateRoot: string;
}
export interface ExecutionManifest {
  schemaVersion: string;
  fixtureId: string;
  readOrigin: string;
  revision: components["schemas"]["CatalogRevisionScopeV1"];
}
type Ack = Record<string, unknown>;

/** One bounded operator pipe shared by actual execution scenarios. It carries no credentials, source or result bytes. */
export class ExecutionOwner<M extends ExecutionManifest = ExecutionManifest> {
  private child: ChildProcessWithoutNullStreams | null = null;
  private buffer = "";
  private queued: Ack[] = [];
  private pending: {
    resolve: (value: Ack) => void;
    reject: (error: Error) => void;
  } | null = null;
  private closed = false;
  private exitCode: number | null = null;
  private stderrBytes = 0;
  private failed = false;
  manifest!: M;
  binding!: Binding;
  constructor(
    private readonly kind: "regression" | "build",
    private readonly bindingPath: () => string | undefined,
  ) {}
  async start(): Promise<void> {
    const path = this.bindingPath();
    if (!path) throw new Error("Provide prepared execution-fixture bindings.");
    this.binding = JSON.parse(readFileSync(path, "utf8")) as Binding;
    const b = this.binding,
      prefix = this.kind.toUpperCase();
    this.child = spawn(
      "wsl.exe",
      [
        "--distribution",
        "Example-Build-Linux",
        "--user",
        "root",
        "--exec",
        "/usr/bin/env",
        "PATH=" +
          b.dotnet.slice(0, b.dotnet.lastIndexOf("/")) +
          ":" +
          b.node.slice(0, b.node.lastIndexOf("/")) +
          ":/usr/bin:/bin",
        "DOTNET_ROOT=" + b.dotnet.slice(0, b.dotnet.lastIndexOf("/")),
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false",
        "HOME=" + b.privateRoot,
        "TMPDIR=" + b.privateRoot,
        `BRAID_${prefix}_BROWSER_MANIFEST=` + b.manifest,
        `BRAID_${prefix}_BASE_URL=` + b.readOrigin,
        "/usr/bin/python3",
        "-I",
        "-B",
        b.launcher || b.operator.replace(/operator\.mjs$/, "run_linux.py"),
        b.config,
        b.node,
        b.operator,
      ],
      { stdio: "pipe", windowsHide: true },
    );
    this.child.stdout.on("data", (bytes: Buffer) => {
      this.buffer += bytes.toString("utf8");
      if (this.buffer.length > 8192) {
        this.fail();
        return;
      }
      while (this.buffer.includes("\n")) {
        const end = this.buffer.indexOf("\n"),
          line = this.buffer.slice(0, end);
        this.buffer = this.buffer.slice(end + 1);
        try {
          const value = JSON.parse(line) as Ack;
          if (this.pending) {
            const pending = this.pending;
            this.pending = null;
            pending.resolve(value);
          } else if (this.queued.length === 0) this.queued.push(value);
          else this.fail();
        } catch {
          this.fail();
        }
      }
    });
    this.child.stderr.on("data", (bytes: Buffer) => {
      this.stderrBytes += bytes.length;
      if (this.stderrBytes > 1048576) this.fail();
    });
    this.child.on("error", () => this.fail());
    this.child.on("close", (code) => {
      this.closed = true;
      this.exitCode = code;
      this.fail();
    });
    const ready = await this.next(300000);
    expect(ready.schemaVersion).toBe(`braid.${this.kind}-browser-ready.v1`);
    expect(ready.fixtureId).toBe(b.fixtureId);
    const bytes = execFileSync(
      "wsl.exe",
      [
        "--distribution",
        "Example-Build-Linux",
        "--user",
        "root",
        "--exec",
        "/bin/cat",
        b.manifest,
      ],
      { maxBuffer: 65536 },
    );
    expect(createHash("sha256").update(bytes).digest("hex")).toBe(
      ready.manifestSha256,
    );
    this.manifest = JSON.parse(bytes.toString("utf8")) as M;
    expect(this.manifest.schemaVersion).toBe(
      `braid.${this.kind}-browser-fixture.v1`,
    );
    expect(this.manifest.fixtureId).toBe(b.fixtureId);
    expect(this.manifest.readOrigin).toBe(b.readOrigin);
    for (const id of Object.values(this.manifest.revision))
      expect(id).toMatch(/^[0-9a-f-]{36}$/);
  }
  async control(
    action: string,
    extra: Record<string, string> = {},
  ): Promise<Ack> {
    if (!this.child || this.closed || this.pending || this.failed)
      throw new Error("Invalid execution owner dispatch state.");
    const requestNonce = randomUUID();
    this.child!.stdin.write(
      JSON.stringify({
        fixtureId: this.binding.fixtureId,
        requestNonce,
        action,
        ...extra,
      }) + "\n",
    );
    const maximum =
      this.kind === "build" &&
      ["shutdown", "restart", "restart-profile", "pause-worker"].includes(
        action,
      )
        ? 370000
        : action === "capture-build"
          ? 140000
          : ["shutdown", "restart"].includes(action)
            ? 130000
            : 80000;
    const ack = await this.next(maximum);
    expect(ack.requestNonce).toBe(requestNonce);
    expect(ack.action).toBe(action);
    expect(ack.schemaVersion).toBe(`braid.${this.kind}-browser-control.v1`);
    expect(ack.outcome).not.toBe("failed");
    expect(ack.outcome).not.toBe("refused");
    return ack;
  }
  async stop(): Promise<void> {
    if (!this.child) return;
    if (this.closed)
      throw new Error(
        "The execution owner exited before shutdown acknowledgement.",
      );
    try {
      expect((await this.control("shutdown")).outcome).toBe("closed");
    } finally {
      this.child.stdin.end();
    }
    const child = this.child;
    await new Promise<void>((resolve, reject) => {
      if (this.closed) {
        this.exitCode === 0
          ? resolve()
          : reject(new Error("Execution owner cleanup failed."));
        return;
      }
      const timer = setTimeout(
        () => reject(new Error("The execution owner did not settle.")),
        20000,
      );
      child.once("close", (code) => {
        clearTimeout(timer);
        code === 0
          ? resolve()
          : reject(new Error("Execution owner cleanup failed."));
      });
    });
  }
  private fail(): void {
    this.failed = true;
    this.pending?.reject(new Error("The real execution owner failed."));
    this.pending = null;
  }
  private next(milliseconds: number): Promise<Ack> {
    const value = this.queued.shift();
    if (value) return Promise.resolve(value);
    return new Promise((resolve, reject) => {
      if (this.closed || this.pending || this.failed) {
        reject(new Error("Invalid execution owner state."));
        return;
      }
      const timer = setTimeout(() => {
        this.pending = null;
        reject(new Error("The execution owner deadline elapsed."));
      }, milliseconds);
      this.pending = {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      };
    });
  }
}
