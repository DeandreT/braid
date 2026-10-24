import { test as base, expect, type Page } from "@playwright/test";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { lstat, readFile } from "node:fs/promises";
import { dirname, isAbsolute } from "node:path";
import type { components } from "../src/api/generated";

export type Revision = components["schemas"]["CatalogRevisionV1"];
export type ProjectionReport =
  components["schemas"]["CatalogProjectionReportV1"];
export type ValidationReport =
  components["schemas"]["CatalogValidationReportV1"];
export type Report = ProjectionReport | ValidationReport;
export type Operation = Report["operation"];
export type Role = "viewer" | "runner" | "publisher" | "administrator";
export type Sample =
  | "basic"
  | "exact"
  | "partial"
  | "ambiguous"
  | "invalid"
  | "uncached"
  | "pending";
export type HostMode = "full" | "readOnlyNoWorker" | "profileUnavailable";

export interface ReportsBrowserManifest {
  readonly schemaVersion: "weft.reports-browser-fixture.v1";
  readonly fixtureId: string;
  readonly source: { readonly commit: string; readonly tree: string };
  readonly readOrigin: string;
  readonly initialReadProfile: "viewer";
  readonly organizationId: string;
  readonly profiles: Readonly<
    Record<
      Role,
      {
        readonly actorPrincipalId: string;
        readonly allowedOrganizationId: string;
        readonly primaryRole: Role;
      }
    >
  >;
  readonly project: {
    readonly scope: components["schemas"]["CatalogProjectScopeV1"];
  };
  readonly samples: Readonly<Record<Sample, { readonly revision: Revision }>>;
}

export interface ReadEffects {
  readonly reportAttempts: number;
  readonly reportResults: number;
  readonly workerCreates: number;
}

type Command =
  | { action: "restart-profile"; profile: Role; mode: HostMode }
  | { action: "checkpoint" }
  | { action: "append-primary-revision" }
  | { action: "pg-normal-restart" }
  | {
      action: "fault-report";
      sample: Sample;
      operation: Operation;
      derivationId: string;
      fault: "missing" | "integrity";
    }
  | {
      action: "restore-report";
      sample: Sample;
      operation: Operation;
      derivationId: string;
    }
  | {
      action: "derive-by-http";
      sample: Sample;
      operation: Operation;
      repairDerivationId?: string;
    }
  | { action: "pending-start" | "pending-release" }
  | { action: "revoke-project" | "revoke-org" | "shutdown" };

type AckBase = {
  schemaVersion: "weft.reports-browser-control.v1";
  requestNonce: string;
  fixtureId: string;
};
export type Ack = AckBase &
  (
    | {
        action: "restart-profile";
        outcome: "ready";
        profile: Role;
        mode: HostMode;
        contextStatus: 200 | 404;
      }
    | { action: "checkpoint"; outcome: "observed"; effects: ReadEffects }
    | {
        action: "append-primary-revision";
        outcome: "appended";
        appendedRevision: Revision;
      }
    | { action: "pg-normal-restart"; outcome: "restarted" }
    | {
        action: "fault-report";
        outcome: "faulted";
        sample: Sample;
        operation: Operation;
        derivationId: string;
        fault: "missing" | "integrity";
      }
    | {
        action: "restore-report";
        outcome: "restored";
        sample: Sample;
        operation: Operation;
        derivationId: string;
      }
    | {
        action: "derive-by-http";
        outcome: "derived";
        sample: Sample;
        operation: Operation;
        result: components["schemas"]["CatalogRevisionReportResultV1"];
      }
    | {
        action: "pending-start";
        outcome: "pending";
        sample: "pending";
        operation: "projection";
      }
    | {
        action: "pending-release";
        outcome: "derived";
        sample: "pending";
        operation: "projection";
        result: components["schemas"]["CatalogRevisionReportResultV1"];
      }
    | { action: "revoke-project" | "revoke-org"; outcome: "revoked" }
    | { action: "shutdown"; outcome: "closed" }
  );

function requireFixture(
  condition: unknown,
  message: string,
): asserts condition {
  if (!condition) throw new Error(`Reports owner fixture: ${message}`);
}
const uuid = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
function object(
  value: unknown,
  keys: readonly string[],
): Record<string, unknown> {
  requireFixture(
    value !== null && typeof value === "object" && !Array.isArray(value),
    "incompatible public envelope",
  );
  const row = value as Record<string, unknown>;
  requireFixture(
    Object.keys(row).sort().join("|") === [...keys].sort().join("|"),
    "unexpected public envelope properties",
  );
  return row;
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
  const scope = object(row.scope, [
    "organizationId",
    "projectId",
    "mappingId",
    "revisionId",
  ]);
  requireFixture(
    Object.values(scope).every(uuid),
    "noncanonical revision scope",
  );
  const digest = object(row.nativeDigest, ["algorithm", "value"]);
  requireFixture(
    digest.algorithm === "sha256" &&
      typeof digest.value === "string" &&
      /^[0-9a-f]{64}$/.test(digest.value),
    "invalid native digest",
  );
  requireFixture(
    (row.admission === "valid" || row.admission === "decodedInvalid") &&
      uuid(row.creatorPrincipalId),
    "invalid revision admission/creator",
  );
  requireFixture(
    typeof row.sequence === "string" &&
      /^[1-9][0-9]*$/.test(row.sequence) &&
      BigInt(row.sequence) <= 9223372036854775807n,
    "invalid revision sequence",
  );
  requireFixture(
    Number.isSafeInteger(row.nativeByteCount) &&
      Number(row.nativeByteCount) > 0,
    "invalid native byte count",
  );
  for (const key of [
    "backendId",
    "committedAtUtc",
    "logicalDocument",
    "originalFilename",
  ])
    requireFixture(typeof row[key] === "string", "invalid revision metadata");
}
function manifest(value: unknown, origin: string): ReportsBrowserManifest {
  const row = object(value, [
    "schemaVersion",
    "fixtureId",
    "source",
    "readOrigin",
    "initialReadProfile",
    "organizationId",
    "profiles",
    "project",
    "samples",
  ]);
  requireFixture(
    row.schemaVersion === "weft.reports-browser-fixture.v1" &&
      uuid(row.fixtureId) &&
      row.readOrigin === origin &&
      row.initialReadProfile === "viewer" &&
      uuid(row.organizationId),
    "manifest identity/origin differs",
  );
  const source = object(row.source, ["commit", "tree"]);
  requireFixture(
    Object.values(source).every(
      (value) => typeof value === "string" && /^[0-9a-f]{40}$/.test(value),
    ),
    "invalid source identity",
  );
  const roles = ["viewer", "runner", "publisher", "administrator"] as const;
  const profiles = object(row.profiles, roles);
  for (const role of roles) {
    const profile = object(profiles[role], [
      "actorPrincipalId",
      "allowedOrganizationId",
      "primaryRole",
    ]);
    requireFixture(
      uuid(profile.actorPrincipalId) &&
        profile.allowedOrganizationId === row.organizationId &&
        profile.primaryRole === role,
      "invalid role profile",
    );
  }
  const project = object(row.project, ["scope"]);
  const projectScope = object(project.scope, ["organizationId", "projectId"]);
  requireFixture(
    projectScope.organizationId === row.organizationId &&
      uuid(projectScope.projectId),
    "invalid sample project scope",
  );
  const samples = object(row.samples, [
    "basic",
    "exact",
    "partial",
    "ambiguous",
    "invalid",
    "uncached",
    "pending",
  ]);
  const revisions: Revision[] = [];
  for (const sample of Object.values(samples)) {
    const nested = object(sample, ["revision"]);
    revision(nested.revision);
    const admitted = nested.revision;
    requireFixture(
      admitted.scope.organizationId === row.organizationId &&
        admitted.scope.projectId === projectScope.projectId &&
        admitted.backendId === "ferrule",
      "sample escaped actual fixture project/backend",
    );
    revisions.push(admitted);
  }
  requireFixture(
    new Set(revisions.map((value) => value.scope.mappingId)).size === 7,
    "samples must be separately HTTP-created mappings",
  );
  return value as ReportsBrowserManifest;
}
async function privateFile(path: string): Promise<void> {
  requireFixture(isAbsolute(path), "private paths must be absolute");
  const [file, parent] = await Promise.all([lstat(path), lstat(dirname(path))]);
  requireFixture(
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

export class ReportsOwner {
  manifest!: ReportsBrowserManifest;
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
    // Private process diagnostics are drained, never attached to browser reports.
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
    this.failure ??= new Error(`Reports owner fixture: ${message}`);
    if (this.pending) {
      clearTimeout(this.pending.timer);
      this.pending.reject(this.failure);
      this.pending = undefined;
    }
  }
  private line(timeout: number): Promise<unknown> {
    if (this.failure) return Promise.reject(this.failure);
    if (this.lines.length) return Promise.resolve(this.lines.shift());
    requireFixture(!this.pending, "overlapping control reads");
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
    requireFixture(
      ready.schemaVersion === "weft.reports-browser-ready.v1" &&
        ready.profile === "viewer",
      "incompatible ready response",
    );
    await privateFile(path);
    requireFixture(
      (await lstat(path)).size <= 65536,
      "manifest exceeds65536 bytes",
    );
    const bytes = await readFile(path);
    requireFixture(
      bytes.length <= 65536 &&
        createHash("sha256").update(bytes).digest("hex") ===
          ready.manifestSha256,
      "manifest size/hash differs",
    );
    this.manifest = manifest(JSON.parse(bytes.toString("utf8")), origin);
    requireFixture(
      this.manifest.fixtureId === ready.fixtureId,
      "manifest fixture identity differs",
    );
  }
  async control(command: Command): Promise<Ack> {
    requireFixture(
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
      requireFixture(
        Buffer.byteLength(wire) <= 1024,
        "control exceeds1024 bytes",
      );
      const answer = this.line(
        command.action === "shutdown" ? 120_000 : 100_000,
      );
      this.child.stdin.write(wire);
      const value = await answer;
      requireFixture(
        value !== null && typeof value === "object",
        "invalid acknowledgement",
      );
      const ack = value as Ack;
      requireFixture(
        ack.schemaVersion === "weft.reports-browser-control.v1" &&
          ack.fixtureId === envelope.fixtureId &&
          ack.requestNonce === envelope.requestNonce &&
          ack.action === command.action,
        "acknowledgement identity differs",
      );
      const baseKeys = [
        "schemaVersion",
        "requestNonce",
        "fixtureId",
        "action",
        "outcome",
      ];
      switch (ack.action) {
        case "restart-profile":
          object(value, [...baseKeys, "profile", "mode", "contextStatus"]);
          requireFixture(
            command.action === "restart-profile" &&
              ack.outcome === "ready" &&
              ack.profile === command.profile &&
              ack.mode === command.mode &&
              [200, 404].includes(ack.contextStatus),
            "API profile acknowledgement differs",
          );
          break;
        case "checkpoint":
          object(value, [...baseKeys, "effects"]);
          requireFixture(
            ack.outcome === "observed",
            "checkpoint was not observed",
          );
          const effects = object(ack.effects, [
            "reportAttempts",
            "reportResults",
            "workerCreates",
          ]);
          requireFixture(
            Object.values(effects).every(
              (number) => Number.isSafeInteger(number) && Number(number) >= 0,
            ),
            "invalid read-effect counts",
          );
          break;
        case "append-primary-revision":
          object(value, [...baseKeys, "appendedRevision"]);
          requireFixture(ack.outcome === "appended", "append did not complete");
          revision(ack.appendedRevision);
          break;
        case "fault-report":
          object(value, [
            ...baseKeys,
            "sample",
            "operation",
            "derivationId",
            "fault",
          ]);
          requireFixture(
            command.action === "fault-report" &&
              ack.outcome === "faulted" &&
              ack.sample === command.sample &&
              ack.operation === command.operation &&
              ack.derivationId === command.derivationId &&
              ack.fault === command.fault,
            "fault did not match captured result",
          );
          break;
        case "restore-report":
          object(value, [...baseKeys, "sample", "operation", "derivationId"]);
          requireFixture(
            command.action === "restore-report" &&
              ack.outcome === "restored" &&
              ack.sample === command.sample &&
              ack.operation === command.operation &&
              ack.derivationId === command.derivationId,
            "restore did not match captured result",
          );
          break;
        case "derive-by-http":
        case "pending-release":
          object(value, [...baseKeys, "sample", "operation", "result"]);
          requireFixture(
            ack.outcome === "derived" &&
              ((command.action === "derive-by-http" &&
                ack.action === "derive-by-http" &&
                ack.sample === command.sample &&
                ack.operation === command.operation) ||
                (command.action === "pending-release" &&
                  ack.action === "pending-release" &&
                  ack.sample === "pending" &&
                  ack.operation === "projection")),
            "HTTP derivation acknowledgement differs",
          );
          const result = object(ack.result, [
            "derivationId",
            "generation",
            "startedAtUtc",
            "finishedAtUtc",
          ]);
          requireFixture(
            uuid(result.derivationId) &&
              typeof result.generation === "string" &&
              /^[1-9][0-9]*$/.test(result.generation) &&
              BigInt(result.generation) <= 9223372036854775807n &&
              typeof result.startedAtUtc === "string" &&
              typeof result.finishedAtUtc === "string",
            "competing HTTP request did not produce a successful result identity",
          );
          break;
        case "pending-start":
          object(value, [...baseKeys, "sample", "operation"]);
          requireFixture(
            ack.outcome === "pending" &&
              ack.sample === "pending" &&
              ack.operation === "projection",
            "actual durable pending operation was not admitted",
          );
          break;
        case "pg-normal-restart":
          object(value, baseKeys);
          requireFixture(
            ack.outcome === "restarted",
            "database did not restart",
          );
          break;
        case "revoke-project":
        case "revoke-org":
          object(value, baseKeys);
          requireFixture(
            ack.outcome === "revoked",
            "membership was not revoked",
          );
          break;
        case "shutdown":
          object(value, baseKeys);
          requireFixture(ack.outcome === "closed", "operator did not close");
          break;
      }
      return ack;
    } finally {
      this.busy = false;
    }
  }
  async effects(): Promise<ReadEffects> {
    const ack = await this.control({ action: "checkpoint" });
    requireFixture(
      ack.action === "checkpoint",
      "expected checkpoint acknowledgement",
    );
    return ack.effects;
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
                  "Reports owner fixture: operator did not settle after EOF; retain root lifecycle evidence",
                ),
              ),
            120_000,
          );
        }),
      ]);
      requireFixture(
        exit.code === 0 && exit.signal === null,
        "operator cleanup exit was not successful",
      );
    } finally {
      if (timer) clearTimeout(timer);
    }
    if (failure) throw failure;
  }
}

export const test = base.extend<{}, { reportsOwner: ReportsOwner }>({
  reportsOwner: [
    async ({}, use) => {
      const operator = process.env.WEFT_REPORTS_BROWSER_OPERATOR;
      const configuration = process.env.WEFT_REPORTS_BROWSER_OPERATOR_CONFIG;
      const path = process.env.WEFT_REPORTS_BROWSER_MANIFEST;
      const origin = process.env.WEFT_REPORTS_BASE_URL;
      requireFixture(
        operator && configuration && path && origin && isAbsolute(operator),
        "explicit packaged origin/operator/private configuration/manifest are required",
      );
      await privateFile(configuration);
      const owner = new ReportsOwner(operator, configuration);
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
export { expect };

export const revisionPath = (revision: Pick<Revision, "scope">) =>
  `/catalog/organizations/${revision.scope.organizationId}/projects/${revision.scope.projectId}/mappings/${revision.scope.mappingId}/revisions/${revision.scope.revisionId}`;
export const revisionApi = (revision: Revision) =>
  `/api/v1${revisionPath(revision).slice("/catalog".length)}`;
export const reportApi = (
  revision: Revision,
  operation: Operation,
  retained?: string,
) =>
  `${revisionApi(revision)}/reports/${operation}${retained ? `/${retained}` : ""}`;
export const pinnedPath = (
  revision: Revision,
  projectionId: string,
  validationId?: string,
) =>
  `${revisionPath(revision)}?projectionDerivationId=${projectionId}${validationId ? `&validationDerivationId=${validationId}` : ""}`;
export const panel = (page: Page, operation: Operation) =>
  page.getByRole("region", {
    name: operation === "projection" ? "Saved explanation" : "Saved validation",
    exact: true,
  });
export const graph = (page: Page) =>
  page.getByRole("region", { name: "Source → rule → target", exact: true });

export function expectReport(
  value: Report,
  revision: Revision,
  operation: Operation,
  retained?: string,
): void {
  object(value, [
    "schemaVersion",
    "revision",
    "operation",
    "state",
    "readOutcome",
    "status",
    "result",
    "currentAttempt",
    "nextAttemptAtUtc",
    "failure",
    "report",
    ...(operation === "validation" ? ["projectionDerivationId"] : []),
  ]);
  object(value.revision, [
    "organizationId",
    "projectId",
    "mappingId",
    "revisionId",
  ]);
  expect(value.schemaVersion).toBe("weft.catalog-revision-report.v1");
  expect(value.operation).toBe(operation);
  expect(value.revision).toEqual(revision.scope);
  expect(["notDerived", "pending", "available", "failed"]).toContain(
    value.state,
  );
  expect([
    "observed",
    "objectUnavailable",
    "storageIntegrity",
    "unsupportedFormat",
    "resourceLimit",
  ]).toContain(value.readOutcome);
  expect([
    null,
    "succeeded",
    "invalidMapping",
    "unsupportedArtifact",
    "resourceLimit",
    "protocolFailure",
    "backendUnavailable",
    "backendFailure",
    "timedOut",
    "canceled",
    "isolationFailure",
  ]).toContain(value.status);
  expect([
    null,
    "objectUnavailable",
    "storageIntegrity",
    "storageIo",
    "backendUnavailable",
    "backendFailure",
    "timedOut",
    "canceled",
    "isolationFailure",
    "protocolFailure",
    "profileUnavailable",
    "resourceLimit",
    "unsupportedArtifact",
  ]).toContain(value.failure);
  if (value.result) {
    object(value.result, [
      "derivationId",
      "generation",
      "startedAtUtc",
      "finishedAtUtc",
    ]);
    expect(uuid(value.result.derivationId)).toBe(true);
    expect(value.result.generation).toMatch(/^[1-9][0-9]*$/);
    expect(BigInt(value.result.generation)).toBeLessThanOrEqual(
      9223372036854775807n,
    );
    if (retained) expect(value.result.derivationId).toBe(retained);
  }
  if (value.currentAttempt) {
    object(value.currentAttempt, [
      "derivationId",
      "generation",
      "startedAtUtc",
      "finishedAtUtc",
      "status",
      "failure",
    ]);
    expect(uuid(value.currentAttempt.derivationId)).toBe(true);
    expect(value.currentAttempt.generation).toMatch(/^[1-9][0-9]*$/);
    expect(BigInt(value.currentAttempt.generation)).toBeLessThanOrEqual(
      9223372036854775807n,
    );
  }
  if (value.state === "notDerived") {
    expect(value.result).toBeNull();
    expect(value.report).toBeNull();
    expect(value.currentAttempt).toBeNull();
    expect(value.status).toBeNull();
    expect(value.failure).toBeNull();
  }
  if (value.state === "available") expect(value.result).not.toBeNull();
  if (value.report) {
    expect(value.state).toBe("available");
    expect(value.readOutcome).toBe("observed");
    expect(value.result).not.toBeNull();
    expect(value.report.status).toBe(value.status);
    const provenance = value.report.provenance;
    expect(provenance).not.toBeNull();
    expect(provenance?.revision).toEqual(revision.scope);
    expect(provenance?.nativeArtifactDigest).toEqual(revision.nativeDigest);
    expect(provenance?.confirmed).toBe(true);
    expect(provenance?.backend.id).toBe("ferrule");
    expect(provenance?.backend.revision).toBe(
      "0169809123b8caebd141b26a625be4c07e750536",
    );
    expect(provenance?.backend.buildId).toBe(
      "sha256:439291fb4d665e3ba9c086e88214a73fd44552af80932a9b40c97f5816c7047e",
    );
    expect(provenance?.adapterVersion).toBe("weft.ferrule-adapter.v3");
    if (value.operation === "projection") {
      expect(value.report.document).not.toBeNull();
      expect(value.report.document?.provenance.revision).toEqual(
        revision.scope,
      );
      expect(value.report.document?.provenance.nativeArtifactDigest).toEqual(
        revision.nativeDigest,
      );
      expect(value.report.document?.provenance.backend).toEqual(
        provenance?.backend,
      );
      expect(value.report.document?.provenance.adapterVersion).toBe(
        "weft.ferrule-adapter.v3",
      );
      expect(value.report.document?.provenance.projectionVersion).toBe(
        "weft.ferrule-projection.v1",
      );
    }
  } else if (value.readOutcome !== "observed")
    expect(value.result).not.toBeNull();
}
