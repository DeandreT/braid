import { test as base, expect } from "@playwright/test";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { lstat, readFile } from "node:fs/promises";
import { dirname, isAbsolute } from "node:path";
import type { components } from "../src/api/generated";
// Only pure data/path exports are reused. Neither A's nor B's automatic owner is activated.
import {
  synthetic,
  projectsPath,
  projectPath,
  catalogApi,
} from "./catalog.fixture";

export type ProjectScope = components["schemas"]["CatalogProjectScopeV1"];
export type CreationResult =
  components["schemas"]["CatalogProjectCreationResultV1"];
export type CreationReceipt =
  components["schemas"]["CatalogProjectCreationReceiptV1"];
export type Profile = "creator" | "projectAdministrator";
export interface CreationCounts {
  readonly projects: number;
  readonly projectMemberships: number;
  readonly creationFacts: number;
  readonly creatorProjects: number;
  readonly workersCreated: 0;
}
export interface ProjectCreationManifest {
  readonly schemaVersion: "weft.project-creation-browser-fixture.v1";
  readonly fixtureId: string;
  readonly source: { readonly commit: string; readonly tree: string };
  readonly readOrigin: string;
  readonly initialReadProfile: "creator";
  readonly organizationId: string;
  readonly profiles: Readonly<
    Record<
      Profile,
      {
        readonly actorPrincipalId: string;
        readonly allowedOrganizationId: string;
      }
    >
  >;
  readonly seedProject: { readonly scope: ProjectScope };
}
export type CreationCommand =
  | { action: "restart-profile" | "api-start"; profile: Profile }
  | {
      action:
        | "api-stop"
        | "checkpoint"
        | "revoke-capability"
        | "restore-capability"
        | "pg-normal-restart"
        | "shutdown";
    }
  | {
      action: "capture-created" | "membership-viewer" | "membership-delete";
      projectId: string;
    };
type AckBase = {
  schemaVersion: "weft.project-creation-browser-control.v1";
  requestNonce: string;
  fixtureId: string;
};
export type CreationAck = AckBase &
  (
    | {
        action: "restart-profile" | "api-start";
        outcome: "ready";
        profile: Profile;
        contextStatus: 200;
      }
    | { action: "api-stop"; outcome: "stopped" | "alreadyStopped" }
    | { action: "checkpoint"; outcome: "observed"; counts: CreationCounts }
    | {
        action: "capture-created";
        outcome: "captured";
        project: ProjectScope;
        counts: CreationCounts;
        initialFactsMatch: true;
      }
    | {
        action: "membership-viewer" | "membership-delete";
        outcome: "changed";
        project: ProjectScope;
        membership: "viewer" | "deleted";
      }
    | { action: "revoke-capability"; outcome: "revoked" }
    | { action: "restore-capability"; outcome: "restored" }
    | { action: "pg-normal-restart"; outcome: "restarted" }
    | { action: "shutdown"; outcome: "closed" }
  );

export function requireCreation(
  condition: unknown,
  message: string,
): asserts condition {
  if (!condition)
    throw new Error(`Project creation browser fixture: ${message}`);
}
export const canonicalId = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
function object(
  value: unknown,
  keys: readonly string[],
): Record<string, unknown> {
  requireCreation(
    value !== null && typeof value === "object" && !Array.isArray(value),
    "incompatible public envelope",
  );
  const row = value as Record<string, unknown>;
  requireCreation(
    Object.keys(row).sort().join("|") === [...keys].sort().join("|"),
    "unexpected public envelope properties",
  );
  return row;
}
function scope(value: unknown, organizationId: string): ProjectScope {
  const row = object(value, ["organizationId", "projectId"]);
  requireCreation(
    row.organizationId === organizationId && canonicalId(row.projectId),
    "project scope escaped fixture organization",
  );
  return value as ProjectScope;
}
function counts(value: unknown): CreationCounts {
  const row = object(value, [
    "projects",
    "projectMemberships",
    "creationFacts",
    "creatorProjects",
    "workersCreated",
  ]);
  requireCreation(
    Object.values(row).every(
      (v) => Number.isSafeInteger(v) && Number(v) >= 0,
    ) && row.workersCreated === 0,
    "invalid creation counts or native worker observed",
  );
  return value as CreationCounts;
}
function manifest(value: unknown, origin: string): ProjectCreationManifest {
  const row = object(value, [
    "schemaVersion",
    "fixtureId",
    "source",
    "readOrigin",
    "initialReadProfile",
    "organizationId",
    "profiles",
    "seedProject",
  ]);
  requireCreation(
    row.schemaVersion === "weft.project-creation-browser-fixture.v1" &&
      canonicalId(row.fixtureId) &&
      row.readOrigin === origin &&
      row.initialReadProfile === "creator" &&
      canonicalId(row.organizationId),
    "manifest identity/origin differs",
  );
  const source = object(row.source, ["commit", "tree"]);
  requireCreation(
    Object.values(source).every(
      (v) => typeof v === "string" && /^[0-9a-f]{40}$/.test(v),
    ),
    "invalid committed source identity",
  );
  const profiles = object(row.profiles, ["creator", "projectAdministrator"]);
  for (const name of ["creator", "projectAdministrator"] as const) {
    const profile = object(profiles[name], [
      "actorPrincipalId",
      "allowedOrganizationId",
    ]);
    requireCreation(
      canonicalId(profile.actorPrincipalId) &&
        profile.allowedOrganizationId === row.organizationId,
      "invalid creation profile",
    );
  }
  requireCreation(
    (profiles.creator as Record<string, unknown>).actorPrincipalId !==
      (profiles.projectAdministrator as Record<string, unknown>)
        .actorPrincipalId,
    "creation profiles must be distinct",
  );
  scope(object(row.seedProject, ["scope"]).scope, row.organizationId);
  return value as ProjectCreationManifest;
}
async function privateFile(path: string): Promise<void> {
  requireCreation(isAbsolute(path), "private paths must be absolute");
  const [file, parent] = await Promise.all([lstat(path), lstat(dirname(path))]);
  requireCreation(
    file.isFile() &&
      !file.isSymbolicLink() &&
      (file.mode & 0o777) === 0o600 &&
      parent.isDirectory() &&
      !parent.isSymbolicLink() &&
      (parent.mode & 0o777) === 0o700 &&
      file.uid === process.getuid?.() &&
      parent.uid === file.uid,
    "private file must be owned0600 in owned0700 directory",
  );
}

export class ProjectCreationOwner {
  manifest!: ProjectCreationManifest;
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly exited: Promise<{
    code: number | null;
    signal: NodeJS.Signals | null;
  }>;
  private pending?: {
    resolve: (value: unknown) => void;
    reject: (error: Error) => void;
    timer: ReturnType<typeof setTimeout>;
  };
  private lines: unknown[] = [];
  private buffer = Buffer.alloc(0);
  private failure?: Error;
  private busy = false;
  private commands = 0;
  constructor(operator: string, configuration: string) {
    this.child = spawn(
      process.execPath,
      [operator, "--config", configuration],
      { stdio: ["pipe", "pipe", "pipe"] },
    );
    this.exited = new Promise((resolve) =>
      this.child.once("close", (code, signal) => resolve({ code, signal })),
    );
    this.child.once("error", () =>
      this.fail("operator process could not start"),
    );
    this.child.once("close", () =>
      this.fail("operator closed before its acknowledgement"),
    );
    // Drain private diagnostics without copying them into browser reports.
    this.child.stderr.on("data", () => {});
    this.child.stdout.on("data", (chunk: Buffer) => {
      this.buffer = Buffer.concat([this.buffer, chunk]);
      while (this.buffer.includes(10)) {
        const end = this.buffer.indexOf(10),
          line = this.buffer.subarray(0, end);
        this.buffer = this.buffer.subarray(end + 1);
        if (line.length > 4096) {
          this.fail("operator line exceeds4096 bytes");
          return;
        }
        try {
          const value: unknown = JSON.parse(line.toString("utf8"));
          if (this.pending) {
            clearTimeout(this.pending.timer);
            this.pending.resolve(value);
            this.pending = undefined;
          } else if (this.lines.length === 0) this.lines.push(value);
          else this.fail("unsolicited operator response");
        } catch {
          this.fail("operator returned invalid JSON");
        }
      }
      if (this.buffer.length > 4096)
        this.fail("unterminated operator line exceeds4096 bytes");
    });
    this.child.stdin.on("error", () =>
      this.fail("operator control pipe closed"),
    );
  }
  private fail(message: string): void {
    this.failure ??= new Error(`Project creation browser fixture: ${message}`);
    if (this.pending) {
      clearTimeout(this.pending.timer);
      this.pending.reject(this.failure);
      this.pending = undefined;
    }
  }
  private line(timeout: number): Promise<unknown> {
    if (this.failure) return Promise.reject(this.failure);
    if (this.lines.length) return Promise.resolve(this.lines.shift());
    requireCreation(!this.pending, "overlapping control reads");
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending = undefined;
        this.fail("operator acknowledgement deadline exceeded");
        reject(this.failure);
      }, timeout);
      this.pending = { resolve, reject, timer };
    });
  }
  async initialize(path: string, origin: string): Promise<void> {
    const ready = object(await this.line(300_000), [
      "schemaVersion",
      "fixtureId",
      "manifestSha256",
      "profile",
    ]);
    requireCreation(
      ready.schemaVersion === "weft.project-creation-browser-ready.v1" &&
        ready.profile === "creator",
      "incompatible ready response",
    );
    await privateFile(path);
    requireCreation(
      (await lstat(path)).size <= 65536,
      "manifest exceeds65536 bytes",
    );
    const bytes = await readFile(path);
    requireCreation(
      bytes.length <= 65536 &&
        createHash("sha256").update(bytes).digest("hex") ===
          ready.manifestSha256,
      "manifest size/hash differs",
    );
    this.manifest = manifest(JSON.parse(bytes.toString("utf8")), origin);
    requireCreation(
      this.manifest.fixtureId === ready.fixtureId,
      "manifest fixture identity differs",
    );
  }
  async control(command: CreationCommand): Promise<CreationAck> {
    requireCreation(
      !this.busy && ++this.commands <= 64,
      "controls must be serialized and bounded to64",
    );
    this.busy = true;
    try {
      const envelope = {
        ...command,
        requestNonce: randomUUID(),
        fixtureId: this.manifest.fixtureId,
      };
      const wire = JSON.stringify(envelope) + "\n";
      requireCreation(
        Buffer.byteLength(wire) <= 1024,
        "control exceeds1024 bytes",
      );
      const deadlines: Record<CreationCommand["action"], number> = {
        "api-stop": 15_000,
        "api-start": 45_000,
        "restart-profile": 60_000,
        checkpoint: 15_000,
        "capture-created": 15_000,
        "revoke-capability": 10_000,
        "restore-capability": 10_000,
        "membership-viewer": 10_000,
        "membership-delete": 10_000,
        "pg-normal-restart": 90_000,
        shutdown: 120_000,
      };
      const answer = this.line(deadlines[command.action] + 5_000);
      this.child.stdin.write(wire);
      const value = await answer;
      requireCreation(
        value !== null && typeof value === "object" && !Array.isArray(value),
        "invalid acknowledgement",
      );
      const keys = [
        "schemaVersion",
        "requestNonce",
        "fixtureId",
        "action",
        "outcome",
      ];
      const row = value as Record<string, unknown>;
      requireCreation(
        row.schemaVersion === "weft.project-creation-browser-control.v1" &&
          row.fixtureId === envelope.fixtureId &&
          row.requestNonce === envelope.requestNonce &&
          row.action === command.action,
        "acknowledgement identity differs",
      );
      if (row.outcome === "failed" || row.outcome === "refused") {
        object(value, [...keys, "failure"]);
        requireCreation(
          typeof row.failure === "string" &&
            [
              "invalidControl",
              "ownershipChanged",
              "deadline",
              "productFailure",
              "stateMismatch",
              "cleanupFailure",
            ].includes(row.failure),
          "incompatible safe failure category",
        );
        throw new Error(
          `Project creation browser fixture: operator ${row.outcome} (${row.failure})`,
        );
      }
      const ack = value as CreationAck;
      switch (ack.action) {
        case "api-start":
        case "restart-profile":
          object(value, [...keys, "profile", "contextStatus"]);
          requireCreation(
            (command.action === "api-start" ||
              command.action === "restart-profile") &&
              ack.outcome === "ready" &&
              ack.profile === command.profile &&
              ack.contextStatus === 200,
            "API profile acknowledgement differs",
          );
          break;
        case "api-stop":
          object(value, keys);
          requireCreation(
            ack.outcome === "stopped" || ack.outcome === "alreadyStopped",
            "API did not stop",
          );
          break;
        case "checkpoint":
          object(value, [...keys, "counts"]);
          requireCreation(
            ack.outcome === "observed",
            "checkpoint was not observed",
          );
          counts(ack.counts);
          break;
        case "capture-created":
          object(value, [...keys, "project", "counts", "initialFactsMatch"]);
          requireCreation(
            command.action === "capture-created" &&
              ack.outcome === "captured" &&
              scope(ack.project, this.manifest.organizationId).projectId ===
                command.projectId &&
              ack.initialFactsMatch === true,
            "creation capture differs",
          );
          counts(ack.counts);
          break;
        case "membership-viewer":
        case "membership-delete":
          object(value, [...keys, "project", "membership"]);
          requireCreation(
            (command.action === "membership-viewer" ||
              command.action === "membership-delete") &&
              ack.outcome === "changed" &&
              scope(ack.project, this.manifest.organizationId).projectId ===
                command.projectId &&
              ack.membership ===
                (command.action === "membership-viewer" ? "viewer" : "deleted"),
            "membership change differs",
          );
          break;
        case "revoke-capability":
          object(value, keys);
          requireCreation(
            ack.outcome === "revoked",
            "capability was not revoked",
          );
          break;
        case "restore-capability":
          object(value, keys);
          requireCreation(
            ack.outcome === "restored",
            "capability was not restored",
          );
          break;
        case "pg-normal-restart":
          object(value, keys);
          requireCreation(
            ack.outcome === "restarted",
            "database did not restart",
          );
          break;
        case "shutdown":
          object(value, keys);
          requireCreation(ack.outcome === "closed", "operator did not close");
          break;
      }
      return ack;
    } finally {
      this.busy = false;
    }
  }
  async counts(): Promise<CreationCounts> {
    const ack = await this.control({ action: "checkpoint" });
    requireCreation(
      ack.action === "checkpoint",
      "expected checkpoint acknowledgement",
    );
    return ack.counts;
  }
  async close(): Promise<void> {
    let failure: unknown = this.failure;
    try {
      if (!this.failure && this.manifest)
        await this.control({ action: "shutdown" });
    } catch (error) {
      failure = error;
    }
    this.child.stdin.end();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const exit = await Promise.race([
        this.exited,
        new Promise<never>((_, reject) => {
          timer = setTimeout(
            () =>
              reject(
                new Error(
                  "Project creation browser fixture: operator did not settle after EOF; retain root lifecycle evidence",
                ),
              ),
            120_000,
          );
        }),
      ]);
      requireCreation(
        exit.code === 0 && exit.signal === null,
        "operator cleanup exit was not successful",
      );
    } finally {
      if (timer) clearTimeout(timer);
    }
    if (failure) throw failure;
  }
}

export const test = base.extend<
  {},
  { projectCreationOwner: ProjectCreationOwner }
>({
  projectCreationOwner: [
    async ({}, use) => {
      const operator = process.env.WEFT_PROJECT_CREATION_BROWSER_OPERATOR;
      const configuration =
        process.env.WEFT_PROJECT_CREATION_BROWSER_OPERATOR_CONFIG;
      const path = process.env.WEFT_PROJECT_CREATION_BROWSER_MANIFEST;
      const origin = process.env.WEFT_PROJECT_CREATION_BASE_URL;
      requireCreation(
        operator && configuration && path && origin && isAbsolute(operator),
        "explicit packaged origin/operator/private configuration/manifest are required",
      );
      await privateFile(configuration);
      const owner = new ProjectCreationOwner(operator, configuration);
      try {
        await owner.initialize(path, origin);
        await use(owner);
      } finally {
        await owner.close();
      }
    },
    { scope: "worker", auto: true, timeout: 480_000 },
  ],
});
export const createPath = (organizationId: string) =>
  `${projectsPath(organizationId)}/new`;
export { expect, synthetic, projectsPath, projectPath, catalogApi };
