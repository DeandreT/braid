import { test as base, expect, type Page, type Route } from "@playwright/test";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { lstat, readFile } from "node:fs/promises";
import { dirname, isAbsolute } from "node:path";
import type { components } from "../src/api/generated";

export type ProjectScope = components["schemas"]["CatalogProjectScopeV1"];
export type MappingScope = components["schemas"]["CatalogMappingScopeV1"];
export type Revision = components["schemas"]["CatalogRevisionV1"];
type Role = components["schemas"]["CatalogRoleV1"];
export type ReadProfile =
  | "administrator"
  | "viewer"
  | "runner"
  | "publisher"
  | "noProjects"
  | "readerUnavailable";
export interface CatalogBrowserManifest {
  readonly schemaVersion: "weft.catalog-browser-fixture.v1";
  readonly fixtureId: string;
  readonly source: { readonly commit: string; readonly tree: string };
  readonly readOrigin: string;
  readonly initialReadProfile: "viewer";
  readonly organizationId: string;
  readonly profiles: Readonly<
    Record<
      ReadProfile,
      {
        readonly actorPrincipalId: string;
        readonly allowedOrganizationId: string;
        readonly expectedContextStatus: 200 | 500;
        readonly primaryRole: Role | null;
      }
    >
  >;
  readonly projects: {
    readonly primary: ProjectScope;
    readonly empty: ProjectScope;
    readonly deniedToViewer: ProjectScope;
    readonly foreign: ProjectScope;
    readonly viewerVisibleUuidOrder: readonly ProjectScope[];
  };
  readonly mappings: {
    readonly primary: MappingScope;
    readonly uuidOrder: readonly MappingScope[];
  };
  readonly history: {
    readonly original: Revision;
    readonly initialHead: Revision;
    readonly sequenceDescending: readonly Revision[];
  };
}
type Action =
  | "api-start"
  | "restart-profile"
  | "api-stop"
  | "revoke-project"
  | "revoke-org"
  | "append-primary-revision"
  | "pg-normal-restart"
  | "shutdown";
type Ack = {
  schemaVersion: "weft.catalog-browser-control.v1";
  requestNonce: string;
  fixtureId: string;
  action: Action;
} & (
  | { action: "api-stop"; outcome: "stopped" | "alreadyStopped" }
  | {
      action: "api-start" | "restart-profile";
      outcome: "ready";
      profile: ReadProfile;
      contextStatus: 200 | 404 | 500;
    }
  | { action: "revoke-project" | "revoke-org"; outcome: "revoked" }
  | {
      action: "append-primary-revision";
      outcome: "appended";
      appendedRevision: Revision;
    }
  | { action: "pg-normal-restart"; outcome: "restarted" }
  | { action: "shutdown"; outcome: "closed" }
  | {
      outcome: "refused" | "failed";
      failure:
        | "invalidControl"
        | "ownershipChanged"
        | "deadline"
        | "productFailure"
        | "stateMismatch"
        | "cleanupFailure";
    }
);

function requireFixture(
  condition: unknown,
  message: string,
): asserts condition {
  if (!condition) throw new Error(`Catalog owner fixture: ${message}`);
}
const uuid = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
    value,
  ) &&
  value !== "00000000-0000-0000-0000-000000000000";
function object(
  value: unknown,
  keys: readonly string[],
): Record<string, unknown> {
  requireFixture(
    value !== null && typeof value === "object" && !Array.isArray(value),
    "incompatible public envelope",
  );
  const record = value as Record<string, unknown>;
  requireFixture(
    Object.keys(record).sort().join("|") === [...keys].sort().join("|"),
    "unexpected public envelope properties",
  );
  return record;
}
function scope(value: unknown, kind: "project" | "mapping" | "revision"): void {
  const keys = [
    "organizationId",
    "projectId",
    ...(kind !== "project" ? ["mappingId"] : []),
    ...(kind === "revision" ? ["revisionId"] : []),
  ];
  const row = object(value, keys);
  requireFixture(
    keys.every((key) => uuid(row[key])),
    "noncanonical public scope",
  );
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
  scope(row.scope, "revision");
  const digest = object(row.nativeDigest, ["algorithm", "value"]);
  requireFixture(
    digest.algorithm === "sha256" &&
      typeof digest.value === "string" &&
      /^[a-f0-9]{64}$/.test(digest.value),
    "invalid public digest",
  );
  requireFixture(
    row.admission === "valid" || row.admission === "decodedInvalid",
    "invalid public admission",
  );
  requireFixture(
    typeof row.sequence === "string" &&
      /^[1-9][0-9]*$/.test(row.sequence) &&
      BigInt(row.sequence) <= 9223372036854775807n,
    "invalid public sequence",
  );
  requireFixture(
    uuid(row.creatorPrincipalId) &&
      Number.isSafeInteger(row.nativeByteCount) &&
      Number(row.nativeByteCount) > 0,
    "invalid revision metadata",
  );
  for (const key of [
    "backendId",
    "committedAtUtc",
    "logicalDocument",
    "originalFilename",
  ])
    requireFixture(typeof row[key] === "string", "invalid revision text");
}
function manifest(
  value: unknown,
  expectedOrigin: string,
): CatalogBrowserManifest {
  const row = object(value, [
    "schemaVersion",
    "fixtureId",
    "source",
    "readOrigin",
    "initialReadProfile",
    "organizationId",
    "profiles",
    "projects",
    "mappings",
    "history",
  ]);
  requireFixture(
    row.schemaVersion === "weft.catalog-browser-fixture.v1" &&
      uuid(row.fixtureId) &&
      uuid(row.organizationId) &&
      row.initialReadProfile === "viewer" &&
      row.readOrigin === expectedOrigin,
    "manifest identity/origin differs",
  );
  const source = object(row.source, ["commit", "tree"]);
  requireFixture(
    [source.commit, source.tree].every(
      (v) => typeof v === "string" && /^[a-f0-9]{40}$/.test(v),
    ),
    "invalid source identity",
  );
  const profiles = object(row.profiles, [
    "administrator",
    "viewer",
    "runner",
    "publisher",
    "noProjects",
    "readerUnavailable",
  ]);
  for (const value of Object.values(profiles)) {
    const profile = object(value, [
      "actorPrincipalId",
      "allowedOrganizationId",
      "expectedContextStatus",
      "primaryRole",
    ]);
    requireFixture(
      uuid(profile.actorPrincipalId) &&
        profile.allowedOrganizationId === row.organizationId &&
        [200, 500].includes(Number(profile.expectedContextStatus)) &&
        [null, "viewer", "runner", "publisher", "administrator"].includes(
          profile.primaryRole as Role | null,
        ),
      "invalid public profile",
    );
  }
  const projects = object(row.projects, [
    "primary",
    "empty",
    "deniedToViewer",
    "foreign",
    "viewerVisibleUuidOrder",
  ]);
  for (const key of ["primary", "empty", "deniedToViewer", "foreign"])
    scope(projects[key], "project");
  requireFixture(
    Array.isArray(projects.viewerVisibleUuidOrder) &&
      projects.viewerVisibleUuidOrder.length === 11,
    "expected eleven real visible projects",
  );
  projects.viewerVisibleUuidOrder.forEach((p) => scope(p, "project"));
  const mappings = object(row.mappings, ["primary", "uuidOrder"]);
  scope(mappings.primary, "mapping");
  requireFixture(
    Array.isArray(mappings.uuidOrder) && mappings.uuidOrder.length === 11,
    "expected eleven real mappings",
  );
  mappings.uuidOrder.forEach((m) => scope(m, "mapping"));
  const history = object(row.history, [
    "original",
    "initialHead",
    "sequenceDescending",
  ]);
  revision(history.original);
  revision(history.initialHead);
  requireFixture(
    Array.isArray(history.sequenceDescending) &&
      history.sequenceDescending.length === 11,
    "expected eleven real revisions",
  );
  history.sequenceDescending.forEach(revision);
  return value as CatalogBrowserManifest;
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

export class CatalogOwner {
  manifest!: CatalogBrowserManifest;
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
  private commands = 0;
  private busy = false;

  constructor(operator: string, config: string) {
    this.child = spawn(process.execPath, [operator, "--config", config], {
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.exited = new Promise((resolve) =>
      this.child.once("close", (code, signal) => resolve({ code, signal })),
    );
    this.child.once("error", () =>
      this.fail("operator process could not start"),
    );
    this.child.once("close", () =>
      this.fail("operator closed before its acknowledgement"),
    );
    // Drain private operator diagnostics without persisting provider text or credentials.
    this.child.stderr.on("data", () => {});
    this.child.stdout.on("data", (chunk: Buffer) => {
      this.buffer = Buffer.concat([this.buffer, chunk]);
      while (this.buffer.includes(10)) {
        const end = this.buffer.indexOf(10);
        const line = this.buffer.subarray(0, end);
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
    this.failure ??= new Error(`Catalog owner fixture: ${message}`);
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
      ready.schemaVersion === "weft.catalog-browser-ready.v1" &&
        ready.profile === "viewer",
      "incompatible ready response",
    );
    await privateFile(path);
    const metadata = await lstat(path);
    requireFixture(metadata.size <= 65536, "manifest exceeds65536 bytes");
    const bytes = await readFile(path);
    requireFixture(bytes.length <= 65536, "manifest exceeds65536 bytes");
    requireFixture(
      createHash("sha256").update(bytes).digest("hex") === ready.manifestSha256,
      "manifest hash differs from ready response",
    );
    this.manifest = manifest(JSON.parse(bytes.toString("utf8")), origin);
    requireFixture(
      this.manifest.fixtureId === ready.fixtureId,
      "manifest fixture identity differs",
    );
  }
  async control(action: Action, profile?: ReadProfile): Promise<Ack> {
    requireFixture(
      !this.busy && ++this.commands <= 64,
      "controls must be serialized and bounded to64",
    );
    requireFixture(
      (action === "api-start" || action === "restart-profile") ===
        (profile !== undefined),
      "profile is required only for API start/restart",
    );
    this.busy = true;
    try {
      const command = {
        requestNonce: randomUUID(),
        fixtureId: this.manifest.fixtureId,
        action,
        ...(profile ? { profile } : {}),
      };
      const wire = JSON.stringify(command) + "\n";
      requireFixture(
        Buffer.byteLength(wire) <= 1024,
        "control exceeds1024 bytes",
      );
      const timeouts: Record<Action, number> = {
        "api-stop": 20_000,
        "api-start": 50_000,
        "restart-profile": 65_000,
        "revoke-project": 15_000,
        "revoke-org": 15_000,
        "append-primary-revision": 95_000,
        "pg-normal-restart": 95_000,
        shutdown: 120_000,
      };
      const answer = this.line(timeouts[action]);
      this.child.stdin.write(wire);
      const value = await answer;
      requireFixture(
        value !== null && typeof value === "object",
        "invalid acknowledgement",
      );
      const ack = value as Ack;
      requireFixture(
        ack.schemaVersion === "weft.catalog-browser-control.v1" &&
          ack.fixtureId === command.fixtureId &&
          ack.requestNonce === command.requestNonce &&
          ack.action === action,
        "acknowledgement identity differs",
      );
      requireFixture(
        ack.outcome !== "refused" && ack.outcome !== "failed",
        `operator ${action} did not complete`,
      );
      object(value, [
        "schemaVersion",
        "requestNonce",
        "fixtureId",
        "action",
        "outcome",
        ...(action === "append-primary-revision" ? ["appendedRevision"] : []),
        ...(action === "api-start" || action === "restart-profile"
          ? ["profile", "contextStatus"]
          : []),
      ]);
      const expected = {
        "api-stop": ["stopped", "alreadyStopped"],
        "api-start": ["ready"],
        "restart-profile": ["ready"],
        "revoke-project": ["revoked"],
        "revoke-org": ["revoked"],
        "append-primary-revision": ["appended"],
        "pg-normal-restart": ["restarted"],
        shutdown: ["closed"],
      };
      requireFixture(
        expected[action].includes(ack.outcome),
        "unexpected acknowledgement outcome",
      );
      if (
        ack.action === "append-primary-revision" &&
        ack.outcome === "appended"
      )
        revision(ack.appendedRevision);
      if (
        (ack.action === "api-start" || ack.action === "restart-profile") &&
        ack.outcome === "ready"
      )
        requireFixture(
          ack.profile === profile &&
            [200, 404, 500].includes(ack.contextStatus),
          "API profile acknowledgement differs",
        );
      return ack;
    } finally {
      this.busy = false;
    }
  }
  async close(): Promise<void> {
    let failure: unknown = this.failure;
    try {
      if (!this.failure && this.manifest) await this.control("shutdown");
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
                  "Catalog owner fixture: operator did not settle after EOF; retain root lifecycle evidence",
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

export const test = base.extend<{}, { catalogOwner: CatalogOwner }>({
  catalogOwner: [
    async ({}, use) => {
      const operator = process.env.WEFT_CATALOG_BROWSER_OPERATOR;
      const config = process.env.WEFT_CATALOG_BROWSER_OPERATOR_CONFIG;
      const path = process.env.WEFT_CATALOG_BROWSER_MANIFEST;
      const origin = process.env.WEFT_CATALOG_BASE_URL;
      requireFixture(
        operator && config && path && origin && isAbsolute(operator),
        "explicit packaged origin/operator/private config/manifest are required",
      );
      await privateFile(config);
      const owner = new CatalogOwner(operator, config);
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

export const projectPath = (s: ProjectScope) =>
  `/catalog/organizations/${s.organizationId}/projects/${s.projectId}`;
export const projectsPath = (organizationId: string) =>
  `/catalog/organizations/${organizationId}/projects`;
export const mappingPath = (s: MappingScope) =>
  `${projectPath(s)}/mappings/${s.mappingId}`;
export const revisionPath = (s: Revision["scope"]) =>
  `${mappingPath(s)}/revisions/${s.revisionId}`;
export const catalogApi = (path: string) =>
  `/api/v1${path.slice("/catalog".length)}`;
export const metadataValue = (page: Page, label: string) =>
  page
    .locator("dt", { hasText: new RegExp(`^${label}$`) })
    .locator("xpath=following-sibling::dd[1]");
export async function expectRevision(
  page: Page,
  value: Revision,
): Promise<void> {
  await expect(page).toHaveURL(new RegExp(revisionPath(value.scope) + "$"));
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    `Revision ${value.sequence}`,
  );
  for (const [label, text] of [
    ["Revision ID", value.scope.revisionId],
    ["Sequence", value.sequence],
    ["Native digest", value.nativeDigest.value],
    [
      "Admission",
      value.admission === "valid" ? "Passed validation" : "Validation issues",
    ],
    ["Original filename", value.originalFilename],
    ["Native bytes", String(value.nativeByteCount)],
    ["Backend", value.backendId],
    ["Logical document", value.logicalDocument],
    ["Creator", value.creatorPrincipalId],
  ])
    await expect(metadataValue(page, label)).toContainText(text);
  await expect(
    metadataValue(page, "Committed").locator("time"),
  ).toHaveAttribute("datetime", value.committedAtUtc);
}

// Deliberately synthetic DTOs. These qualify UI boundaries, never persistence or authority.
export const synthetic = (() => {
  const org = "10000000-0000-4000-8000-000000000001";
  const actor = "10000000-0000-4000-8000-000000000002";
  const scope: MappingScope = {
    organizationId: org,
    projectId: "20000000-0000-4000-8000-000000000001",
    mappingId: "30000000-0000-4000-8000-000000000001",
  };
  const actions: components["schemas"]["CatalogActionsV1"] = {
    view: true,
    chat: false,
    createMappings: false,
    createProjects: false,
    deriveReports: false,
    editMetadata: false,
    manageMembership: false,
    preview: false,
    readReports: false,
    uploadRevisions: false,
  };
  const { view: _view, ...featureActions } = actions;
  const context: components["schemas"]["CatalogContextV1"] = {
    actorPrincipalId: actor,
    allowedOrganizationId: org,
    features: { ...featureActions, readCatalog: true },
    mode: "development",
    organizationActions: { createProjects: false },
    organizationName: "Synthetic UI boundary organization",
    requestToken: "synthetic-ui-only-not-a-credential",
  };
  const project: components["schemas"]["CatalogProjectV1"] = {
    scope: { organizationId: org, projectId: scope.projectId },
    access: { role: "viewer", actions },
    name: "Synthetic project",
    description: "Synthetic read-only rendering",
    createdAtUtc: "2026-01-01T00:00:00Z",
    updatedAtUtc: "2026-01-01T00:00:00Z",
    creatorPrincipalId: actor,
    version: "1",
  };
  const revision: Revision = {
    scope: { ...scope, revisionId: "40000000-0000-4000-8000-000000000001" },
    backendId: "ferrule",
    sequence: "1",
    admission: "valid",
    committedAtUtc: "2026-01-01T00:00:00Z",
    creatorPrincipalId: actor,
    logicalDocument: "entry.native.json",
    nativeByteCount: 2191,
    nativeDigest: { algorithm: "sha256", value: "a".repeat(64) },
    originalFilename: "mapping.json",
  };
  const mapping: components["schemas"]["CatalogMappingV1"] = {
    scope,
    access: { role: "viewer", actions },
    name: "Synthetic mapping",
    description: "Synthetic current metadata",
    backendId: "ferrule",
    head: {
      scope: revision.scope,
      sequence: revision.sequence,
      admission: revision.admission,
    },
    version: "1",
    createdAtUtc: project.createdAtUtc,
    updatedAtUtc: project.updatedAtUtc,
    creatorPrincipalId: actor,
    ownerPrincipalId: actor,
  };
  return { org, actor, context, project, mapping, revision };
})();
export async function syntheticCatalog(
  page: Page,
  overrides: Readonly<Record<string, unknown>> = {},
): Promise<void> {
  const replies: Record<string, unknown> = {
    "/api/v1/catalog/context": synthetic.context,
    [catalogApi(projectsPath(synthetic.org))]: {
      projects: [synthetic.project],
      nextCursor: null,
    },
    [catalogApi(projectPath(synthetic.project.scope))]: synthetic.project,
    [catalogApi(projectPath(synthetic.project.scope)) + "/mappings"]: {
      mappings: [synthetic.mapping],
      nextCursor: null,
    },
    [catalogApi(mappingPath(synthetic.mapping.scope))]: synthetic.mapping,
    [catalogApi(mappingPath(synthetic.mapping.scope)) + "/revisions"]: {
      revisions: [synthetic.revision],
      nextCursor: null,
    },
    [catalogApi(revisionPath(synthetic.revision.scope))]: synthetic.revision,
    ...overrides,
  };
  await page.route("**/api/v1/**", async (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() !== "GET" || !(path in replies)) {
      await route.fulfill({ status: 404, json: { code: "notFound" } });
      return;
    }
    await route.fulfill({ json: replies[path] });
  });
}
