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
export type RevisionScope = components["schemas"]["CatalogRevisionScopeV1"];
export type Revision = components["schemas"]["CatalogRevisionV1"];
export type Receipt =
  components["schemas"]["CatalogRevisionIngestionReceiptV1"];
export type UploadResult =
  components["schemas"]["CatalogRevisionIngestionResultV1"];
export type Profile = "administrator" | "publisher" | "runner" | "viewer";
export type UploadMode = "upload" | "featureOff";
export type Sample =
  | "legacy"
  | "versioned"
  | "decodedInvalid"
  | "decodeRejected"
  | "unsupportedRejected";
export interface UploadCounts {
  readonly mappings: number;
  readonly revisions: number;
  readonly ingestions: number;
  readonly ingestionAttempts: number;
  readonly nativePublications: number;
  readonly workersCreated: number;
}
export interface NativeMappingUploadManifest {
  readonly schemaVersion: "weft.native-mapping-upload-browser-fixture.v1";
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
  readonly protected: {
    readonly project: ProjectScope;
    readonly revision: RevisionScope;
  };
}
export type UploadCommand =
  | {
      action: "api-start" | "restart-profile";
      profile: Profile;
      mode: UploadMode;
    }
  | { action: "capture-upload"; sample: Sample; operationId: string }
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
  schemaVersion: "weft.native-mapping-upload-browser-control.v1";
  requestNonce: string;
  fixtureId: string;
};
export type UploadAck = AckBase &
  (
    | {
        action: "api-start" | "restart-profile";
        outcome: "ready";
        profile: Profile;
        mode: UploadMode;
        contextStatus: 200;
      }
    | { action: "api-stop"; outcome: "stopped" | "alreadyStopped" }
    | { action: "checkpoint"; outcome: "observed"; counts: UploadCounts }
    | {
        action: "capture-upload";
        outcome: "captured";
        sample: Sample;
        operationId: string;
        phase: "committed" | "rejected";
        exactNativeBytes: true;
        counts: UploadCounts;
      }
    | { action: "revoke-publisher"; outcome: "revoked" }
    | { action: "restore-publisher"; outcome: "restored" }
    | { action: "pg-normal-restart"; outcome: "restarted" }
    | { action: "shutdown"; outcome: "closed" }
  );

class UploadFixtureError extends Error {
  constructor(message: string) {
    super(`Native mapping upload browser fixture: ${message}`);
  }
}
export function requireUpload(
  condition: unknown,
  message: string,
): asserts condition {
  if (!condition) throw new UploadFixtureError(message);
}
export const canonicalId = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
function object(
  value: unknown,
  keys: readonly string[],
): Record<string, unknown> {
  requireUpload(
    value !== null && typeof value === "object" && !Array.isArray(value),
    "incompatible public envelope",
  );
  const row = value as Record<string, unknown>;
  requireUpload(
    Object.keys(row).sort().join("|") === [...keys].sort().join("|"),
    "unexpected public envelope properties",
  );
  return row;
}
function projectScope(value: unknown, organizationId: string): ProjectScope {
  const row = object(value, ["organizationId", "projectId"]);
  requireUpload(
    row.organizationId === organizationId && canonicalId(row.projectId),
    "project scope escaped fixture organization",
  );
  return value as ProjectScope;
}
function counts(value: unknown): UploadCounts {
  const row = object(value, [
    "mappings",
    "revisions",
    "ingestions",
    "ingestionAttempts",
    "nativePublications",
    "workersCreated",
  ]);
  requireUpload(
    Object.values(row).every((v) => Number.isSafeInteger(v) && Number(v) >= 0),
    "invalid upload counts",
  );
  return value as UploadCounts;
}
function manifest(value: unknown, origin: string): NativeMappingUploadManifest {
  const row = object(value, [
    "schemaVersion",
    "fixtureId",
    "source",
    "readOrigin",
    "initialReadProfile",
    "organizationId",
    "profiles",
    "project",
    "protected",
  ]);
  requireUpload(
    row.schemaVersion === "weft.native-mapping-upload-browser-fixture.v1" &&
      canonicalId(row.fixtureId) &&
      row.readOrigin === origin &&
      row.initialReadProfile === "publisher" &&
      canonicalId(row.organizationId),
    "manifest identity/origin differs",
  );
  const endpoint = new URL(origin);
  requireUpload(
    endpoint.protocol === "http:" &&
      endpoint.hostname === "127.0.0.1" &&
      endpoint.origin === origin &&
      endpoint.username === "" &&
      endpoint.password === "",
    "fixture origin must be explicit loopback HTTP",
  );
  requireUpload(
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
    requireUpload(
      canonicalId(profile.actorPrincipalId) &&
        profile.allowedOrganizationId === row.organizationId &&
        profile.primaryRole === name,
      "invalid fixed upload profile",
    );
    actors.push(profile.actorPrincipalId);
  }
  requireUpload(new Set(actors).size === 4, "upload profiles must be distinct");
  const target = projectScope(
    object(row.project, ["scope"]).scope,
    row.organizationId,
  );
  const protectedRow = object(row.protected, ["project", "revision"]),
    sibling = projectScope(protectedRow.project, row.organizationId);
  const revision = object(protectedRow.revision, [
    "organizationId",
    "projectId",
    "mappingId",
    "revisionId",
  ]);
  requireUpload(
    sibling.projectId !== target.projectId &&
      revision.organizationId === sibling.organizationId &&
      revision.projectId === sibling.projectId &&
      canonicalId(revision.mappingId) &&
      canonicalId(revision.revisionId),
    "protected sibling scope differs",
  );
  return value as NativeMappingUploadManifest;
}
async function privateFile(path: string): Promise<void> {
  requireUpload(isAbsolute(path), "private paths must be absolute");
  const [file, parent] = await Promise.all([lstat(path), lstat(dirname(path))]);
  requireUpload(
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
  "legacy",
  "versioned",
  "decodedInvalid",
  "decodeRejected",
  "unsupportedRejected",
];
function validateCommand(command: UploadCommand): void {
  switch (command.action) {
    case "api-start":
    case "restart-profile":
      object(command, ["action", "profile", "mode"]);
      requireUpload(
        profiles.includes(command.profile) &&
          (command.mode === "upload" || command.mode === "featureOff"),
        "invalid fixed API profile/mode",
      );
      break;
    case "capture-upload":
      object(command, ["action", "sample", "operationId"]);
      requireUpload(
        samples.includes(command.sample) && canonicalId(command.operationId),
        "invalid fixed capture identity",
      );
      break;
    default:
      object(command, ["action"]);
      requireUpload(
        [
          "api-stop",
          "checkpoint",
          "revoke-publisher",
          "restore-publisher",
          "pg-normal-restart",
          "shutdown",
        ].includes(command.action),
        "unsupported control action",
      );
  }
}

/** The only automatic owner for both E files; controls carry no source body/key/query/token. */
export class NativeMappingUploadOwner {
  manifest!: NativeMappingUploadManifest;
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
    this.failure ??= new UploadFixtureError(message);
    if (this.pending) {
      clearTimeout(this.pending.timer);
      this.pending.reject(this.failure);
      this.pending = undefined;
    }
  }
  private line(timeout: number): Promise<unknown> {
    if (this.failure) return Promise.reject(this.failure);
    if (this.queued.length) return Promise.resolve(this.queued.shift());
    requireUpload(!this.pending, "overlapping control reads");
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
    requireUpload(
      ready.schemaVersion === "weft.native-mapping-upload-browser-ready.v1" &&
        ready.profile === "publisher" &&
        typeof ready.manifestSha256 === "string" &&
        /^[0-9a-f]{64}$/.test(ready.manifestSha256),
      "incompatible ready response",
    );
    await privateFile(path);
    requireUpload(
      (await lstat(path)).size <= 65536,
      "manifest exceeds65536 bytes",
    );
    const bytes = await readFile(path);
    requireUpload(
      bytes.length <= 65536 &&
        createHash("sha256").update(bytes).digest("hex") ===
          ready.manifestSha256,
      "manifest size/hash differs",
    );
    let value: unknown;
    try {
      value = JSON.parse(bytes.toString("utf8"));
    } catch {
      throw new UploadFixtureError("manifest is not JSON");
    }
    this.manifest = manifest(value, origin);
    requireUpload(
      this.manifest.fixtureId === ready.fixtureId,
      "manifest fixture identity differs",
    );
  }
  async control(command: UploadCommand): Promise<UploadAck> {
    validateCommand(command);
    requireUpload(
      !this.busy && this.nonces.size < 64,
      "controls must be serialized and bounded to64",
    );
    this.busy = true;
    try {
      const nonce = randomUUID();
      requireUpload(!this.nonces.has(nonce), "duplicate control nonce");
      this.nonces.add(nonce);
      const envelope = {
          ...command,
          fixtureId: this.manifest.fixtureId,
          requestNonce: nonce,
        },
        wire = JSON.stringify(envelope) + "\n";
      requireUpload(
        Buffer.byteLength(wire) <= 1024,
        "control exceeds1024 bytes",
      );
      const deadlines: Record<UploadCommand["action"], number> = {
        "api-stop": 15000,
        "api-start": 45000,
        "restart-profile": 60000,
        checkpoint: 15000,
        "capture-upload": 15000,
        "revoke-publisher": 10000,
        "restore-publisher": 10000,
        "pg-normal-restart": 90000,
        shutdown: 120000,
      };
      const answer = this.line(deadlines[command.action] + 5000);
      this.child.stdin.write(wire);
      const value = await answer;
      requireUpload(
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
      requireUpload(
        row.schemaVersion === "weft.native-mapping-upload-browser-control.v1" &&
          row.fixtureId === envelope.fixtureId &&
          row.requestNonce === nonce &&
          row.action === command.action,
        "acknowledgement identity differs",
      );
      if (row.outcome === "failed" || row.outcome === "refused") {
        object(value, [...keys, "failure"]);
        requireUpload(
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
        throw new UploadFixtureError(
          `operator ${row.outcome} (${row.failure})`,
        );
      }
      const ack = value as UploadAck;
      switch (ack.action) {
        case "api-start":
        case "restart-profile":
          object(value, [...keys, "profile", "mode", "contextStatus"]);
          requireUpload(
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
          requireUpload(
            ack.outcome === "stopped" || ack.outcome === "alreadyStopped",
            "API did not stop",
          );
          break;
        case "checkpoint":
          object(value, [...keys, "counts"]);
          requireUpload(
            ack.outcome === "observed",
            "checkpoint was not observed",
          );
          counts(ack.counts);
          break;
        case "capture-upload":
          object(value, [
            ...keys,
            "sample",
            "operationId",
            "phase",
            "exactNativeBytes",
            "counts",
          ]);
          requireUpload(
            command.action === "capture-upload" &&
              ack.outcome === "captured" &&
              ack.sample === command.sample &&
              ack.operationId === command.operationId &&
              ack.phase ===
                (command.sample === "decodeRejected" ||
                command.sample === "unsupportedRejected"
                  ? "rejected"
                  : "committed") &&
              ack.exactNativeBytes === true,
            "captured upload differs",
          );
          counts(ack.counts);
          break;
        case "revoke-publisher":
          object(value, keys);
          requireUpload(ack.outcome === "revoked", "publisher was not revoked");
          break;
        case "restore-publisher":
          object(value, keys);
          requireUpload(
            ack.outcome === "restored",
            "publisher was not restored",
          );
          break;
        case "pg-normal-restart":
          object(value, keys);
          requireUpload(
            ack.outcome === "restarted",
            "database did not restart",
          );
          break;
        case "shutdown":
          object(value, keys);
          requireUpload(ack.outcome === "closed", "operator did not close");
          break;
      }
      return ack;
    } finally {
      this.busy = false;
    }
  }
  async checkpoint(): Promise<UploadCounts> {
    const ack = await this.control({ action: "checkpoint" });
    requireUpload(ack.action === "checkpoint", "expected checkpoint");
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
                new UploadFixtureError(
                  "operator did not settle after EOF; retain root lifecycle evidence",
                ),
              ),
            120000,
          );
        }),
      ]);
      requireUpload(
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
        new UploadFixtureError(
          "operator left unsolicited or unterminated stdout at final EOF",
        ),
      );
    if (failures.length === 1) throw failures[0];
    if (failures.length > 1)
      throw new AggregateError(
        failures,
        "Native mapping upload browser fixture: primary and cleanup failures retained",
      );
  }
}

export const test = base.extend<
  {},
  { nativeMappingUploadOwner: NativeMappingUploadOwner }
>({
  nativeMappingUploadOwner: [
    async ({}, use) => {
      const operator = process.env.WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_OPERATOR,
        configuration =
          process.env.WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_OPERATOR_CONFIG,
        path = process.env.WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_MANIFEST,
        origin = process.env.WEFT_NATIVE_MAPPING_UPLOAD_BASE_URL;
      requireUpload(
        operator && configuration && path && origin && isAbsolute(operator),
        "explicit packaged origin/operator/private configuration/manifest are required",
      );
      requireUpload(
        process.env.PLAYWRIGHT_NO_COPY_PROMPT === "1",
        "E global automatic page-snapshot guard is required before any page",
      );
      await privateFile(configuration);
      const owner = new NativeMappingUploadOwner(operator, configuration);
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
export const uploadPath = (selected: ProjectScope) =>
  `${projectPath(selected)}/mappings/new`;
export {
  expect,
  synthetic,
  projectsPath,
  projectPath,
  mappingPath,
  revisionPath,
  catalogApi,
};
