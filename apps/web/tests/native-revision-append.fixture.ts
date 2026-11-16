import { test as base, expect } from "@playwright/test";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { lstat, readFile } from "node:fs/promises";
import { dirname, isAbsolute } from "node:path";
import type { components } from "../src/api/generated";
// Pure data/path exports only. No earlier automatic owner is requested.
import {
  synthetic,
  projectsPath,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
} from "./catalog.fixture";

export type ProjectScope = components["schemas"]["CatalogProjectScopeV1"];
export type MappingScope = components["schemas"]["CatalogMappingScopeV1"];
export type RevisionScope = components["schemas"]["CatalogRevisionScopeV1"];
export type Revision = components["schemas"]["CatalogRevisionV1"];
export type Receipt =
  components["schemas"]["CatalogRevisionIngestionReceiptV1"];
export type AppendResult =
  components["schemas"]["CatalogRevisionIngestionResultV1"];
export type Profile = "administrator" | "publisher" | "runner" | "viewer";
export type AppendMode = "append" | "featureOff";
export type Sample =
  | "valid"
  | "decodedInvalid"
  | "winner"
  | "loser"
  | "renewed"
  | "lost"
  | "restored";
export interface AppendCounts {
  readonly mappings: number;
  readonly revisions: number;
  readonly ingestions: number;
  readonly ingestionAttempts: number;
  readonly nativePublications: number;
  readonly workersCreated: number;
  readonly reportAttempts: number;
  readonly reportResults: number;
}
export interface NativeRevisionAppendManifest {
  readonly schemaVersion: "braid.native-revision-append-browser-fixture.v1";
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
  readonly project: { readonly scope: ProjectScope };
  readonly mapping: {
    readonly scope: MappingScope;
    readonly initialRevision: {
      readonly scope: RevisionScope;
      readonly sequence: "1";
      readonly admission: "valid";
    };
  };
  readonly protected: { readonly revision: RevisionScope };
  readonly reports: {
    readonly projectionDerivationId: string;
    readonly validationDerivationId: string;
  };
}
export type AppendCommand =
  | {
      action: "api-start" | "restart-profile";
      profile: Profile;
      mode: AppendMode;
    }
  | { action: "capture-append"; sample: Sample; operationId: string }
  | { action: "metadata-edit" | "advance-head" }
  | {
      action:
        | "api-stop"
        | "checkpoint"
        | "revoke-publisher"
        | "restore-publisher"
        | "pg-normal-restart"
        | "shutdown";
    };
type AckBase = {
  schemaVersion: "braid.native-revision-append-browser-control.v1";
  requestNonce: string;
  fixtureId: string;
};
export type AppendAck = AckBase &
  (
    | {
        action: "api-start" | "restart-profile";
        outcome: "ready";
        profile: Profile;
        mode: AppendMode;
        contextStatus: 200;
      }
    | { action: "api-stop"; outcome: "stopped" | "alreadyStopped" }
    | { action: "checkpoint"; outcome: "observed"; counts: AppendCounts }
    | {
        action: "capture-append";
        outcome: "captured";
        sample: Sample;
        operationId: string;
        phase: "committed" | "headConflict";
        exactNativeBytes: true;
        counts: AppendCounts;
      }
    | { action: "metadata-edit"; outcome: "edited"; counts: AppendCounts }
    | {
        action: "advance-head";
        outcome: "appended";
        counts: AppendCounts;
        head: {
          readonly scope: RevisionScope;
          readonly sequence: string;
          readonly admission: "valid" | "decodedInvalid";
        };
      }
    | { action: "revoke-publisher"; outcome: "revoked" }
    | { action: "restore-publisher"; outcome: "restored" }
    | { action: "pg-normal-restart"; outcome: "restarted" }
    | { action: "shutdown"; outcome: "closed" }
  );

class AppendFixtureError extends Error {
  constructor(message: string) {
    super(`Native revision append browser fixture: ${message}`);
  }
}
export function requireAppend(
  condition: unknown,
  message: string,
): asserts condition {
  if (!condition) throw new AppendFixtureError(message);
}
export const canonicalId = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
function object(
  value: unknown,
  keys: readonly string[],
): Record<string, unknown> {
  requireAppend(
    value !== null && typeof value === "object" && !Array.isArray(value),
    "incompatible public envelope",
  );
  const row = value as Record<string, unknown>;
  requireAppend(
    Object.keys(row).sort().join("|") === [...keys].sort().join("|"),
    "unexpected public envelope properties",
  );
  return row;
}
function projectScope(value: unknown, organizationId: string): ProjectScope {
  const row = object(value, ["organizationId", "projectId"]);
  requireAppend(
    row.organizationId === organizationId && canonicalId(row.projectId),
    "project scope escaped fixture organization",
  );
  return value as ProjectScope;
}
function counts(value: unknown): AppendCounts {
  const row = object(value, [
    "mappings",
    "revisions",
    "ingestions",
    "ingestionAttempts",
    "nativePublications",
    "workersCreated",
    "reportAttempts",
    "reportResults",
  ]);
  requireAppend(
    Object.values(row).every((v) => Number.isSafeInteger(v) && Number(v) >= 0),
    "invalid append counts",
  );
  return value as AppendCounts;
}
function manifest(
  value: unknown,
  origin: string,
): NativeRevisionAppendManifest {
  const row = object(value, [
    "schemaVersion",
    "fixtureId",
    "source",
    "readOrigin",
    "initialReadProfile",
    "organizationId",
    "profiles",
    "project",
    "mapping",
    "protected",
    "reports",
  ]);
  requireAppend(
    row.schemaVersion === "braid.native-revision-append-browser-fixture.v1" &&
      canonicalId(row.fixtureId) &&
      row.readOrigin === origin &&
      row.initialReadProfile === "publisher" &&
      canonicalId(row.organizationId),
    "manifest identity/origin differs",
  );
  const endpoint = new URL(origin);
  requireAppend(
    endpoint.protocol === "http:" &&
      endpoint.hostname === "127.0.0.1" &&
      endpoint.origin === origin &&
      endpoint.username === "" &&
      endpoint.password === "",
    "fixture origin must be explicit loopback HTTP",
  );
  requireAppend(
    Object.values(object(row.source, ["commit", "tree"])).every(
      (v) => typeof v === "string" && /^[0-9a-f]{40}$/.test(v),
    ),
    "invalid committed source identity",
  );
  const profiles = object(row.profiles, [
      "administrator",
      "publisher",
      "runner",
      "viewer",
    ]),
    actors: string[] = [];
  for (const name of [
    "administrator",
    "publisher",
    "runner",
    "viewer",
  ] as const) {
    const profile = object(profiles[name], [
      "actorPrincipalId",
      "allowedOrganizationId",
      "primaryRole",
    ]);
    requireAppend(
      canonicalId(profile.actorPrincipalId) &&
        profile.allowedOrganizationId === row.organizationId &&
        profile.primaryRole === name,
      "invalid fixed append profile",
    );
    actors.push(profile.actorPrincipalId);
  }
  requireAppend(new Set(actors).size === 4, "append profiles must be distinct");
  const target = projectScope(
    object(row.project, ["scope"]).scope,
    row.organizationId,
  );
  const selected = object(row.mapping, ["scope", "initialRevision"]),
    mapping = object(selected.scope, [
      "organizationId",
      "projectId",
      "mappingId",
    ]);
  requireAppend(
    mapping.organizationId === target.organizationId &&
      mapping.projectId === target.projectId &&
      canonicalId(mapping.mappingId),
    "mapping escaped target project",
  );
  const initial = object(selected.initialRevision, [
    "scope",
    "sequence",
    "admission",
  ]) as unknown as NativeRevisionAppendManifest["mapping"]["initialRevision"];
  object(initial.scope, [
    "organizationId",
    "projectId",
    "mappingId",
    "revisionId",
  ]);
  requireAppend(
    initial &&
      initial.scope.organizationId === mapping.organizationId &&
      initial.scope.projectId === mapping.projectId &&
      initial.scope.mappingId === mapping.mappingId &&
      canonicalId(initial.scope.revisionId) &&
      initial.sequence === "1" &&
      initial.admission === "valid",
    "initial actual revision differs",
  );
  const revision = object(object(row.protected, ["revision"]).revision, [
    "organizationId",
    "projectId",
    "mappingId",
    "revisionId",
  ]);
  requireAppend(
    revision.organizationId === target.organizationId &&
      revision.projectId === target.projectId &&
      canonicalId(revision.mappingId) &&
      revision.mappingId !== mapping.mappingId &&
      canonicalId(revision.revisionId),
    "protected sibling scope differs",
  );
  requireAppend(
    Object.values(
      object(row.reports, ["projectionDerivationId", "validationDerivationId"]),
    ).every(canonicalId),
    "invalid genuine saved report identities",
  );
  return value as NativeRevisionAppendManifest;
}
async function privateFile(path: string): Promise<void> {
  requireAppend(isAbsolute(path), "private paths must be absolute");
  const [file, parent] = await Promise.all([lstat(path), lstat(dirname(path))]);
  requireAppend(
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
const profiles: readonly string[] = [
  "administrator",
  "publisher",
  "runner",
  "viewer",
];
const samples: readonly string[] = [
  "valid",
  "decodedInvalid",
  "winner",
  "loser",
  "renewed",
  "lost",
  "restored",
];
function validateCommand(command: AppendCommand): void {
  switch (command.action) {
    case "api-start":
    case "restart-profile":
      object(command, ["action", "profile", "mode"]);
      requireAppend(
        profiles.includes(command.profile) &&
          (command.mode === "append" || command.mode === "featureOff"),
        "invalid fixed API profile/mode",
      );
      break;
    case "capture-append":
      object(command, ["action", "sample", "operationId"]);
      requireAppend(
        samples.includes(command.sample) && canonicalId(command.operationId),
        "invalid fixed capture identity",
      );
      break;
    default:
      object(command, ["action"]);
      requireAppend(
        [
          "api-stop",
          "checkpoint",
          "revoke-publisher",
          "restore-publisher",
          "pg-normal-restart",
          "metadata-edit",
          "advance-head",
          "shutdown",
        ].includes(command.action),
        "unsupported control action",
      );
  }
}

/** The only automatic owner for the F actual persistence spec; controls carry no source body/key/query/token. */
export class NativeRevisionAppendOwner {
  manifest!: NativeRevisionAppendManifest;
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
  private readonly queued: unknown[] = [];
  private buffer = Buffer.alloc(0);
  private failure?: Error;
  private busy = false;
  private closing = false;
  private readonly nonces = new Set<string>();
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
    this.child.once("close", () => {
      if (!this.closing) this.fail("operator closed before settlement");
    });
    // Operator diagnostics stay in its private evidence; never attach native/request material.
    this.child.stderr.on("data", () => {});
    this.child.stdout.on("data", (chunk: Buffer) => {
      if (this.failure) return;
      this.buffer = Buffer.concat([this.buffer, chunk]);
      while (this.buffer.includes(10)) {
        const end = this.buffer.indexOf(10),
          line = this.buffer.subarray(0, end);
        this.buffer = this.buffer.subarray(end + 1);
        if (line.length > 4096) {
          this.fail("operator line exceeds4096 bytes");
          return;
        }
        let value: unknown;
        try {
          value = JSON.parse(line.toString("utf8"));
        } catch {
          this.fail("operator returned invalid JSON");
          return;
        }
        if (this.pending) {
          clearTimeout(this.pending.timer);
          this.pending.resolve(value);
          this.pending = undefined;
        } else if (this.queued.length === 0) this.queued.push(value);
        else {
          this.fail("unsolicited operator response");
          return;
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
    this.failure ??= new AppendFixtureError(message);
    if (this.pending) {
      clearTimeout(this.pending.timer);
      this.pending.reject(this.failure);
      this.pending = undefined;
    }
  }
  private line(timeout: number): Promise<unknown> {
    if (this.failure) return Promise.reject(this.failure);
    if (this.queued.length) return Promise.resolve(this.queued.shift());
    requireAppend(!this.pending, "overlapping control reads");
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
    const ready = object(await this.line(300000), [
      "schemaVersion",
      "fixtureId",
      "manifestSha256",
      "profile",
    ]);
    requireAppend(
      ready.schemaVersion === "braid.native-revision-append-browser-ready.v1" &&
        ready.profile === "publisher" &&
        typeof ready.manifestSha256 === "string" &&
        /^[0-9a-f]{64}$/.test(ready.manifestSha256),
      "incompatible ready response",
    );
    await privateFile(path);
    requireAppend(
      (await lstat(path)).size <= 65536,
      "manifest exceeds65536 bytes",
    );
    const bytes = await readFile(path);
    requireAppend(
      bytes.length <= 65536 &&
        createHash("sha256").update(bytes).digest("hex") ===
          ready.manifestSha256,
      "manifest size/hash differs",
    );
    let value: unknown;
    try {
      value = JSON.parse(bytes.toString("utf8"));
    } catch {
      throw new AppendFixtureError("manifest is not JSON");
    }
    this.manifest = manifest(value, origin);
    requireAppend(
      this.manifest.fixtureId === ready.fixtureId,
      "manifest fixture identity differs",
    );
  }
  async control(command: AppendCommand): Promise<AppendAck> {
    validateCommand(command);
    requireAppend(
      !this.busy && this.nonces.size < 64,
      "controls must be serialized and bounded to64",
    );
    this.busy = true;
    try {
      const nonce = randomUUID();
      requireAppend(!this.nonces.has(nonce), "duplicate control nonce");
      this.nonces.add(nonce);
      const envelope = {
          ...command,
          fixtureId: this.manifest.fixtureId,
          requestNonce: nonce,
        },
        wire = JSON.stringify(envelope) + "\n";
      requireAppend(
        Buffer.byteLength(wire) <= 1024,
        "control exceeds1024 bytes",
      );
      const deadlines: Record<AppendCommand["action"], number> = {
        "api-stop": 15000,
        "api-start": 45000,
        "restart-profile": 60000,
        checkpoint: 15000,
        "capture-append": 15000,
        "metadata-edit": 60000,
        "advance-head": 90000,
        "revoke-publisher": 10000,
        "restore-publisher": 10000,
        "pg-normal-restart": 90000,
        shutdown: 120000,
      };
      const answer = this.line(deadlines[command.action] + 5000);
      this.child.stdin.write(wire);
      const value = await answer;
      requireAppend(
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
      requireAppend(
        row.schemaVersion ===
          "braid.native-revision-append-browser-control.v1" &&
          row.fixtureId === envelope.fixtureId &&
          row.requestNonce === nonce &&
          row.action === command.action,
        "acknowledgement identity differs",
      );
      if (row.outcome === "failed" || row.outcome === "refused") {
        object(value, [...keys, "failure"]);
        requireAppend(
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
        throw new AppendFixtureError(
          `operator ${row.outcome} (${row.failure})`,
        );
      }
      const ack = value as AppendAck;
      switch (ack.action) {
        case "api-start":
        case "restart-profile":
          object(value, [...keys, "profile", "mode", "contextStatus"]);
          requireAppend(
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
          requireAppend(
            ack.outcome === "stopped" || ack.outcome === "alreadyStopped",
            "API did not stop",
          );
          break;
        case "checkpoint":
          object(value, [...keys, "counts"]);
          requireAppend(
            ack.outcome === "observed",
            "checkpoint was not observed",
          );
          counts(ack.counts);
          break;
        case "capture-append":
          object(value, [
            ...keys,
            "sample",
            "operationId",
            "phase",
            "exactNativeBytes",
            "counts",
          ]);
          requireAppend(
            command.action === "capture-append" &&
              ack.outcome === "captured" &&
              ack.sample === command.sample &&
              ack.operationId === command.operationId &&
              ack.phase ===
                (command.sample === "loser" ? "headConflict" : "committed") &&
              ack.exactNativeBytes === true,
            "captured append differs",
          );
          counts(ack.counts);
          break;
        case "metadata-edit":
          object(value, [...keys, "counts"]);
          requireAppend(
            ack.outcome === "edited",
            "metadata edit did not settle",
          );
          counts(ack.counts);
          break;
        case "advance-head":
          object(value, [...keys, "counts", "head"]);
          object(ack.head, ["scope", "sequence", "admission"]);
          object(ack.head.scope, [
            "organizationId",
            "projectId",
            "mappingId",
            "revisionId",
          ]);
          requireAppend(
            ack.outcome === "appended" &&
              ack.head?.scope.organizationId === this.manifest.organizationId &&
              ack.head.scope.projectId ===
                this.manifest.project.scope.projectId &&
              ack.head.scope.mappingId ===
                this.manifest.mapping.scope.mappingId &&
              canonicalId(ack.head.scope.revisionId) &&
              typeof ack.head.sequence === "string" &&
              /^[1-9][0-9]{0,18}$/.test(ack.head.sequence) &&
              BigInt(ack.head.sequence) <= 9223372036854775807n &&
              (ack.head.admission === "valid" ||
                ack.head.admission === "decodedInvalid"),
            "separate head append differs",
          );
          counts(ack.counts);
          break;
        case "revoke-publisher":
          object(value, keys);
          requireAppend(ack.outcome === "revoked", "publisher was not revoked");
          break;
        case "restore-publisher":
          object(value, keys);
          requireAppend(
            ack.outcome === "restored",
            "publisher was not restored",
          );
          break;
        case "pg-normal-restart":
          object(value, keys);
          requireAppend(
            ack.outcome === "restarted",
            "database did not restart",
          );
          break;
        case "shutdown":
          object(value, keys);
          requireAppend(ack.outcome === "closed", "operator did not close");
          break;
      }
      return ack;
    } finally {
      this.busy = false;
    }
  }
  async checkpoint(): Promise<AppendCounts> {
    const ack = await this.control({ action: "checkpoint" });
    requireAppend(ack.action === "checkpoint", "expected checkpoint");
    return ack.counts;
  }
  async close(): Promise<void> {
    const failures: unknown[] = this.failure ? [this.failure] : [];
    try {
      if (!this.failure && this.manifest)
        await this.control({ action: "shutdown" });
    } catch (error) {
      failures.push(error);
    }
    this.closing = true;
    this.child.stdin.end();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const exit = await Promise.race([
        this.exited,
        new Promise<never>((_, reject) => {
          timer = setTimeout(
            () =>
              reject(
                new AppendFixtureError(
                  "operator did not settle after EOF; retain root lifecycle evidence",
                ),
              ),
            120000,
          );
        }),
      ]);
      requireAppend(
        exit.code === 0 && exit.signal === null,
        "operator cleanup exit was not successful",
      );
    } catch (error) {
      failures.push(error);
    } finally {
      if (timer) clearTimeout(timer);
    }
    if (this.failure && !failures.includes(this.failure))
      failures.push(this.failure);
    if (this.queued.length !== 0 || this.buffer.length !== 0)
      failures.push(
        new AppendFixtureError(
          "operator left unsolicited or unterminated stdout at final EOF",
        ),
      );
    if (failures.length === 1) throw failures[0];
    if (failures.length > 1)
      throw new AggregateError(
        failures,
        "Native revision append browser fixture: primary and cleanup failures retained",
      );
  }
}

export const test = base.extend<
  {},
  { nativeRevisionAppendOwner: NativeRevisionAppendOwner }
>({
  nativeRevisionAppendOwner: [
    async ({}, use) => {
      const operator =
          process.env.BRAID_NATIVE_REVISION_APPEND_BROWSER_OPERATOR,
        configuration =
          process.env.BRAID_NATIVE_REVISION_APPEND_BROWSER_OPERATOR_CONFIG,
        path = process.env.BRAID_NATIVE_REVISION_APPEND_BROWSER_MANIFEST,
        origin = process.env.BRAID_NATIVE_REVISION_APPEND_BASE_URL;
      requireAppend(
        operator && configuration && path && origin && isAbsolute(operator),
        "explicit packaged origin/operator/private configuration/manifest are required",
      );
      requireAppend(
        process.env.PLAYWRIGHT_NO_COPY_PROMPT === "1",
        "F global automatic page-snapshot guard is required before any page",
      );
      await privateFile(configuration);
      const owner = new NativeRevisionAppendOwner(operator, configuration);
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
export const appendPath = (selected: MappingScope) =>
  `${mappingPath(selected)}/revisions/new`;
export {
  expect,
  synthetic,
  projectsPath,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
};
