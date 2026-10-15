import { test as base, expect } from "@playwright/test";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { lstat, readFile } from "node:fs/promises";
import { dirname, isAbsolute } from "node:path";
import type { components } from "../src/api/generated";
// Pure DTO/path reuse only; no earlier automatic fixture is activated.
import {
  synthetic,
  projectsPath,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
} from "./catalog.fixture";
export type MappingScope = components["schemas"]["CatalogMappingScopeV1"];
export type Revision = components["schemas"]["CatalogRevisionV1"];
export type Metadata = components["schemas"]["CatalogMappingMetadataV1"];
export type Profile = "administrator" | "publisher" | "runner" | "viewer";
export type Mode = "edit" | "featureOff";
export interface MetadataCounts {
  readonly revisions: number;
  readonly reportAttempts: number;
  readonly reportResults: number;
  readonly workersCreated: number;
}
export interface MetadataState {
  readonly version: string;
  readonly headRevisionId: string;
  readonly headSequence: string;
}
export interface MappingMetadataManifest {
  readonly schemaVersion: "weft.mapping-metadata-browser-fixture.v1";
  readonly fixtureId: string;
  readonly source: { readonly commit: string; readonly tree: string };
  readonly readOrigin: string;
  readonly initialReadProfile: "publisher";
  readonly organizationId: string;
  readonly profiles: Readonly<
    Record<
      Profile,
      {
        readonly actorPrincipalId: string;
        readonly allowedOrganizationId: string;
        readonly primaryRole: Profile;
      }
    >
  >;
  readonly project: {
    readonly scope: components["schemas"]["CatalogProjectScopeV1"];
  };
  readonly primary: {
    readonly scope: MappingScope;
    readonly initialRevision: Revision;
  };
  readonly unrelated: { readonly scope: MappingScope };
  readonly reports: {
    readonly projectionDerivationId: string;
    readonly validationDerivationId: string;
  };
}
export type MetadataCommand =
  | { action: "api-start" | "restart-profile"; profile: Profile; mode: Mode }
  | {
      action:
        | "api-stop"
        | "checkpoint"
        | "append-primary-revision"
        | "revoke-publisher"
        | "restore-publisher"
        | "pg-normal-restart"
        | "shutdown";
    }
  | {
      action: "capture-edit";
      previousVersion: string;
      version: string;
      nameSha256: string;
      descriptionSha256: string;
    };
type AckBase = {
  schemaVersion: "weft.mapping-metadata-browser-control.v1";
  requestNonce: string;
  fixtureId: string;
};
export type MetadataAck = AckBase &
  (
    | {
        action: "api-start" | "restart-profile";
        outcome: "ready";
        profile: Profile;
        mode: Mode;
        contextStatus: 200;
      }
    | { action: "api-stop"; outcome: "stopped" | "alreadyStopped" }
    | {
        action: "checkpoint";
        outcome: "observed";
        state: MetadataState;
        counts: MetadataCounts;
      }
    | {
        action: "capture-edit";
        outcome: "captured";
        version: string;
        changed: boolean;
      }
    | {
        action: "append-primary-revision";
        outcome: "appended";
        appendedRevision: Revision;
      }
    | { action: "revoke-publisher"; outcome: "revoked" }
    | { action: "restore-publisher"; outcome: "restored" }
    | { action: "pg-normal-restart"; outcome: "restarted" }
    | { action: "shutdown"; outcome: "closed" }
  );
export function requireMetadata(
  condition: unknown,
  message: string,
): asserts condition {
  if (!condition)
    throw new Error(`Mapping metadata browser fixture: ${message}`);
}
export const canonicalId = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const positive = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[1-9][0-9]*$/.test(value) &&
  BigInt(value) <= 9223372036854775807n;
function object(
  value: unknown,
  keys: readonly string[],
): Record<string, unknown> {
  requireMetadata(
    value !== null && typeof value === "object" && !Array.isArray(value),
    "incompatible public envelope",
  );
  const row = value as Record<string, unknown>;
  requireMetadata(
    Object.keys(row).sort().join("|") === [...keys].sort().join("|"),
    "unexpected public envelope properties",
  );
  return row;
}
function sameScope(a: MappingScope, b: MappingScope): boolean {
  return (
    a.organizationId === b.organizationId &&
    a.projectId === b.projectId &&
    a.mappingId === b.mappingId
  );
}
function scope(
  value: unknown,
  organizationId: string,
  projectId?: string,
): MappingScope {
  const row = object(value, ["organizationId", "projectId", "mappingId"]);
  requireMetadata(
    row.organizationId === organizationId &&
      canonicalId(row.projectId) &&
      canonicalId(row.mappingId) &&
      (!projectId || row.projectId === projectId),
    "mapping scope escaped fixture",
  );
  return value as MappingScope;
}
function revision(value: unknown): asserts value is Revision {
  const row = object(value, [
    "admission",
    "backendId",
    "committedAtUtc",
    "creatorPrincipalId",
    "logicalDocument",
    "nativeByteCount",
    "nativeDigest",
    "originalFilename",
    "scope",
    "sequence",
  ]);
  const selected = object(row.scope, [
    "organizationId",
    "projectId",
    "mappingId",
    "revisionId",
  ]);
  const digest = object(row.nativeDigest, ["algorithm", "value"]);
  requireMetadata(
    Object.values(selected).every(canonicalId) &&
      positive(row.sequence) &&
      canonicalId(row.creatorPrincipalId) &&
      (row.admission === "valid" || row.admission === "decodedInvalid") &&
      Number.isSafeInteger(row.nativeByteCount) &&
      Number(row.nativeByteCount) > 0,
    "invalid revision identity",
  );
  requireMetadata(
    digest.algorithm === "sha256" &&
      typeof digest.value === "string" &&
      /^[0-9a-f]{64}$/.test(digest.value) &&
      row.backendId === "ferrule" &&
      row.logicalDocument === "entry.native.json" &&
      typeof row.originalFilename === "string" &&
      typeof row.committedAtUtc === "string" &&
      Number.isFinite(Date.parse(row.committedAtUtc)),
    "invalid native revision facts",
  );
}
function counts(value: unknown): void {
  const row = object(value, [
    "revisions",
    "reportAttempts",
    "reportResults",
    "workersCreated",
  ]);
  requireMetadata(
    Object.values(row).every((v) => Number.isSafeInteger(v) && Number(v) >= 0),
    "invalid operation counts",
  );
}
function state(value: unknown): void {
  const row = object(value, ["version", "headRevisionId", "headSequence"]);
  requireMetadata(
    positive(row.version) &&
      positive(row.headSequence) &&
      canonicalId(row.headRevisionId),
    "invalid mapping state",
  );
}
function manifest(value: unknown, origin: string): MappingMetadataManifest {
  const row = object(value, [
    "schemaVersion",
    "fixtureId",
    "source",
    "readOrigin",
    "initialReadProfile",
    "organizationId",
    "profiles",
    "project",
    "primary",
    "unrelated",
    "reports",
  ]);
  requireMetadata(
    row.schemaVersion === "weft.mapping-metadata-browser-fixture.v1" &&
      canonicalId(row.fixtureId) &&
      row.readOrigin === origin &&
      row.initialReadProfile === "publisher" &&
      canonicalId(row.organizationId),
    "manifest identity/origin differs",
  );
  requireMetadata(
    Object.values(object(row.source, ["commit", "tree"])).every(
      (v) => typeof v === "string" && /^[0-9a-f]{40}$/.test(v),
    ),
    "invalid source identity",
  );
  const profiles = object(row.profiles, [
    "administrator",
    "publisher",
    "runner",
    "viewer",
  ]);
  for (const name of [
    "administrator",
    "publisher",
    "runner",
    "viewer",
  ] as const) {
    const p = object(profiles[name], [
      "actorPrincipalId",
      "allowedOrganizationId",
      "primaryRole",
    ]);
    requireMetadata(
      canonicalId(p.actorPrincipalId) &&
        p.allowedOrganizationId === row.organizationId &&
        p.primaryRole === name,
      "invalid role profile",
    );
  }
  requireMetadata(
    new Set(
      Object.values(profiles).map(
        (p) => (p as Record<string, unknown>).actorPrincipalId,
      ),
    ).size === 4,
    "role profiles are not distinct",
  );
  const project = object(object(row.project, ["scope"]).scope, [
    "organizationId",
    "projectId",
  ]);
  requireMetadata(
    project.organizationId === row.organizationId &&
      canonicalId(project.projectId),
    "invalid project scope",
  );
  const primary = object(row.primary, ["scope", "initialRevision"]),
    unrelated = object(row.unrelated, ["scope"]);
  const target = scope(primary.scope, row.organizationId, project.projectId),
    other = scope(unrelated.scope, row.organizationId, project.projectId);
  revision(primary.initialRevision);
  requireMetadata(
    sameScope(primary.initialRevision.scope, target) &&
      target.mappingId !== other.mappingId,
    "invalid primary revision or unrelated mapping",
  );
  requireMetadata(
    Object.values(
      object(row.reports, ["projectionDerivationId", "validationDerivationId"]),
    ).every(canonicalId),
    "invalid real saved report identities",
  );
  return value as MappingMetadataManifest;
}
async function privateFile(path: string): Promise<void> {
  requireMetadata(isAbsolute(path), "private paths must be absolute");
  const [file, parent] = await Promise.all([lstat(path), lstat(dirname(path))]);
  requireMetadata(
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
export class MappingMetadataOwner {
  manifest!: MappingMetadataManifest;
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
    this.failure ??= new Error(`Mapping metadata browser fixture: ${message}`);
    if (this.pending) {
      clearTimeout(this.pending.timer);
      this.pending.reject(this.failure);
      this.pending = undefined;
    }
  }
  private line(timeout: number): Promise<unknown> {
    if (this.failure) return Promise.reject(this.failure);
    if (this.lines.length) return Promise.resolve(this.lines.shift());
    requireMetadata(!this.pending, "overlapping control reads");
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
    requireMetadata(
      ready.schemaVersion === "weft.mapping-metadata-browser-ready.v1" &&
        ready.profile === "publisher",
      "incompatible ready response",
    );
    await privateFile(path);
    requireMetadata(
      (await lstat(path)).size <= 65536,
      "manifest exceeds65536 bytes",
    );
    const bytes = await readFile(path);
    requireMetadata(
      bytes.length <= 65536 &&
        createHash("sha256").update(bytes).digest("hex") ===
          ready.manifestSha256,
      "manifest size/hash differs",
    );
    this.manifest = manifest(JSON.parse(bytes.toString("utf8")), origin);
    requireMetadata(
      this.manifest.fixtureId === ready.fixtureId,
      "manifest fixture identity differs",
    );
  }
  async control(command: MetadataCommand): Promise<MetadataAck> {
    requireMetadata(
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
      requireMetadata(
        Buffer.byteLength(wire) <= 1024,
        "control exceeds1024 bytes",
      );
      const deadlines: Record<MetadataCommand["action"], number> = {
        "api-stop": 15000,
        "api-start": 45000,
        "restart-profile": 60000,
        checkpoint: 15000,
        "capture-edit": 15000,
        "append-primary-revision": 90000,
        "revoke-publisher": 10000,
        "restore-publisher": 10000,
        "pg-normal-restart": 90000,
        shutdown: 120000,
      };
      const answer = this.line(deadlines[command.action] + 5000);
      this.child.stdin.write(wire);
      const value = await answer;
      requireMetadata(
        value !== null && typeof value === "object" && !Array.isArray(value),
        "invalid acknowledgement",
      );
      const row = value as Record<string, unknown>,
        keys = [
          "schemaVersion",
          "requestNonce",
          "fixtureId",
          "action",
          "outcome",
        ];
      requireMetadata(
        row.schemaVersion === "weft.mapping-metadata-browser-control.v1" &&
          row.fixtureId === envelope.fixtureId &&
          row.requestNonce === envelope.requestNonce &&
          row.action === command.action,
        "acknowledgement identity differs",
      );
      if (row.outcome === "failed" || row.outcome === "refused") {
        object(value, [...keys, "failure"]);
        requireMetadata(
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
          `Mapping metadata browser fixture: operator ${row.outcome} (${row.failure})`,
        );
      }
      const ack = value as MetadataAck;
      switch (ack.action) {
        case "api-start":
        case "restart-profile":
          object(value, [...keys, "profile", "mode", "contextStatus"]);
          requireMetadata(
            (command.action === "api-start" ||
              command.action === "restart-profile") &&
              ack.outcome === "ready" &&
              ack.profile === command.profile &&
              ack.mode === command.mode &&
              ack.contextStatus === 200,
            "API profile acknowledgement differs",
          );
          break;
        case "api-stop":
          object(value, keys);
          requireMetadata(
            ack.outcome === "stopped" || ack.outcome === "alreadyStopped",
            "API did not stop",
          );
          break;
        case "checkpoint":
          object(value, [...keys, "state", "counts"]);
          requireMetadata(
            ack.outcome === "observed",
            "checkpoint was not observed",
          );
          state(ack.state);
          counts(ack.counts);
          break;
        case "capture-edit":
          object(value, [...keys, "version", "changed"]);
          requireMetadata(
            command.action === "capture-edit" &&
              ack.outcome === "captured" &&
              ack.version === command.version &&
              typeof ack.changed === "boolean",
            "captured edit differs",
          );
          break;
        case "append-primary-revision":
          object(value, [...keys, "appendedRevision"]);
          requireMetadata(
            ack.outcome === "appended",
            "append did not complete",
          );
          revision(ack.appendedRevision);
          requireMetadata(
            sameScope(ack.appendedRevision.scope, this.manifest.primary.scope),
            "append escaped primary mapping",
          );
          break;
        case "revoke-publisher":
          object(value, keys);
          requireMetadata(
            ack.outcome === "revoked",
            "publisher was not revoked",
          );
          break;
        case "restore-publisher":
          object(value, keys);
          requireMetadata(
            ack.outcome === "restored",
            "publisher was not restored",
          );
          break;
        case "pg-normal-restart":
          object(value, keys);
          requireMetadata(
            ack.outcome === "restarted",
            "database did not restart",
          );
          break;
        case "shutdown":
          object(value, keys);
          requireMetadata(ack.outcome === "closed", "operator did not close");
          break;
      }
      return ack;
    } finally {
      this.busy = false;
    }
  }
  async checkpoint(): Promise<{
    readonly state: MetadataState;
    readonly counts: MetadataCounts;
  }> {
    const ack = await this.control({ action: "checkpoint" });
    requireMetadata(ack.action === "checkpoint", "expected checkpoint");
    return { state: ack.state, counts: ack.counts };
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
                  "Mapping metadata browser fixture: operator did not settle after EOF; retain root lifecycle evidence",
                ),
              ),
            120_000,
          );
        }),
      ]);
      requireMetadata(
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
  { mappingMetadataOwner: MappingMetadataOwner }
>({
  mappingMetadataOwner: [
    async ({}, use) => {
      const operator = process.env.WEFT_MAPPING_METADATA_BROWSER_OPERATOR,
        configuration =
          process.env.WEFT_MAPPING_METADATA_BROWSER_OPERATOR_CONFIG,
        path = process.env.WEFT_MAPPING_METADATA_BROWSER_MANIFEST,
        origin = process.env.WEFT_MAPPING_METADATA_BASE_URL;
      requireMetadata(
        operator && configuration && path && origin && isAbsolute(operator),
        "explicit packaged origin/operator/private configuration/manifest are required",
      );
      await privateFile(configuration);
      const owner = new MappingMetadataOwner(operator, configuration);
      try {
        await owner.initialize(path, origin);
        await use(owner);
      } finally {
        await owner.close();
      }
    },
    { scope: "worker", auto: true, timeout: 480000 },
  ],
});
export const editPath = (selected: MappingScope) =>
  `${mappingPath(selected)}/edit`;
export {
  expect,
  synthetic,
  projectsPath,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
};
