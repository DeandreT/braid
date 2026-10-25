#!/usr/bin/env node
// Actual-API preview browser qualification. This operator owns one labeled PostgreSQL
// container migrated to the current schema, a private storage root, the published
// Weft.Server and Weft.Worker processes and the pinned Ferrule worker image, then runs the
// live Playwright spec against them. No API reply or worker result is scripted.
// Node core only. Passwords, cookies and tokens never leave this process and its children.
import { execFileSync, spawn } from "node:child_process";
import { randomBytes, randomUUID } from "node:crypto";
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import http from "node:http";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { setTimeout as pause } from "node:timers/promises";
import { fileURLToPath } from "node:url";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const usage =
  "Usage: run.mjs --publications DIR --worker-image sha256:<id> [--playwright-args ...]";
const option = (name) => {
  const index = process.argv.indexOf(name);
  return index > 1 ? process.argv[index + 1] : undefined;
};
const publications = option("--publications");
const workerImage = option("--worker-image");
const postgresImage =
  "docker.io/library/postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc";
if (
  !publications ||
  !/^sha256:[0-9a-f]{64}$/.test(workerImage ?? "") ||
  !process.env.WEFT_CHROMIUM_EXECUTABLE
) {
  console.error(usage + " (WEFT_CHROMIUM_EXECUTABLE is required)");
  process.exit(2);
}
const extraPlaywright = process.argv.includes("--playwright-args")
  ? process.argv.slice(process.argv.indexOf("--playwright-args") + 1)
  : [];
const products = Object.fromEntries(
  [
    ["server", "Weft.Server"],
    ["worker", "Weft.Worker"],
    ["database", "Weft.DatabaseTool"],
    ["storage", "Weft.StorageTool"],
  ].map(([key, assembly]) => {
    const path = join(resolve(publications), key, assembly + ".dll");
    if (!existsSync(path)) {
      console.error("Missing publication: " + path);
      process.exit(2);
    }
    return [key, path];
  }),
);
if (!existsSync(join(dirname(products.server), "wwwroot", "index.html"))) {
  console.error("The published server has no Angular application in wwwroot.");
  process.exit(2);
}

const dotnet = process.env.DOTNET_HOST_PATH ?? "dotnet";
const readOrigin = "http://127.0.0.1:4120";
const mutationOrigin = "http://127.0.0.1:4121";
const nonce = randomBytes(6).toString("hex");
const label = "weft.preview-browser.owner=" + nonce;
const scratch = mkdtempSync(join(tmpdir(), "weft-preview-browser-"));
chmodSync(scratch, 0o700);
const storageRoot = join(scratch, "storage", "root");
const controlToken = randomBytes(24).toString("hex");
const roles = [
  ["weft_migrator", "MIGRATOR"],
  ["weft_runtime", "RUNTIME"],
  ["weft_ingest", "INGEST"],
  ["weft_catalog_reader", "CATALOG"],
  ["weft_catalog_editor", "METADATA"],
  ["weft_project_creator", "CREATOR"],
  ["weft_report_reader", "REPORT"],
  ["weft_deriver", "DERIVATION"],
  ["weft_maintenance", "MAINTENANCE"],
  ["weft_identity_reader", "IDENTITY"],
  ["weft_payload_permission_reader", "PAYLOAD_PERMISSION"],
  ["weft_session_reader", "SESSION_READER"],
  ["weft_session_writer", "SESSION_WRITER"],
  ["weft_chat_reader", "CHAT_READER"],
  ["weft_chat_writer", "CHAT_WRITER"],
  ["weft_chat_lifecycle", "CHAT_LIFECYCLE"],
  ["weft_preview_reader", "PREVIEW_READER"],
  ["weft_preview_writer", "PREVIEW_WRITER"],
  ["weft_case_reader", "CASE_READER"],
  ["weft_case_writer", "CASE_WRITER"],
  ["weft_run_reader", "RUN_READER"],
  ["weft_run_writer", "RUN_WRITER"],
  ["weft_build_reader", "BUILD_READER"],
  ["weft_build_writer", "BUILD_WRITER"],
  ["weft_build_run_reader", "BUILD_RUN_READER"],
  ["weft_build_run_writer", "BUILD_RUN_WRITER"],
  ["weft_build_dispatcher", "BUILD_DISPATCHER"],
  ["weft_build_metadata_reader", "BUILD_METADATA"],
  ["weft_qualification_reader", "QUALIFICATION_READER"],
  ["weft_qualification_writer", "QUALIFICATION_WRITER"],
  ["weft_release_reader", "RELEASE_READER"],
  ["weft_release_writer", "RELEASE_WRITER"],
  ["weft_invocation_grant_writer", "INVOCATION_GRANT_WRITER"],
  ["weft_invocation_writer", "INVOCATION_WRITER"],
  ["weft_api_audit_writer", "API_AUDIT_WRITER"],
  ["weft_administration", "ADMINISTRATION"],
];
const secrets = new Map(
  ["POSTGRES_ADMIN", ...roles.map(([, key]) => key)].map((key) => [
    key,
    randomBytes(24).toString("hex"),
  ]),
);
const baseEnvironment = Object.fromEntries(
  ["PATH", "DOTNET_ROOT", "HOME", "LANG", "TZ"]
    .filter((key) => process.env[key] !== undefined)
    .map((key) => [key, process.env[key]]),
);
const ids = {
  organization: randomUUID(),
  administrator: randomUUID(),
  publisher: randomUUID(),
  viewer: randomUUID(),
};
const children = new Map();
let container = null;
let connections = {};

function docker(args, input) {
  return execFileSync("/usr/bin/docker", args, {
    encoding: "utf8",
    input,
    stdio: ["pipe", "pipe", "pipe"],
    timeout: 120000,
    env: { ...baseEnvironment, ...dockerSecrets() },
  }).trim();
}
function dockerSecrets() {
  const values = {
    POSTGRES_DB: "weft",
    POSTGRES_USER: "postgres",
    POSTGRES_PASSWORD: secrets.get("POSTGRES_ADMIN"),
    POSTGRES_INITDB_ARGS: "--auth-host=scram-sha-256 --auth-local=trust",
  };
  for (const [key, value] of secrets)
    if (key !== "POSTGRES_ADMIN")
      values["WEFT_LOCAL_" + key + "_PASSWORD"] = value;
  return values;
}
/** Read-only owner query; returns psql's unaligned text. */
function query(text) {
  return execFileSync(
    "/usr/bin/docker",
    [
      "exec",
      "-i",
      "-e",
      "PGPASSWORD",
      container,
      "psql",
      "-X",
      "-qAt",
      "--host=127.0.0.1",
      "--username=weft_migrator",
      "--dbname=weft",
      "--set=ON_ERROR_STOP=1",
    ],
    {
      encoding: "utf8",
      input: "BEGIN READ ONLY;\n" + text + ";\nCOMMIT;\n",
      stdio: ["pipe", "pipe", "pipe"],
      timeout: 15000,
      env: { ...baseEnvironment, PGPASSWORD: secrets.get("MIGRATOR") },
    },
  ).trim();
}
function tool(args, extra = {}) {
  execFileSync(dotnet, [products.database, ...args], {
    stdio: ["ignore", "ignore", "pipe"],
    timeout: 120000,
    env: {
      ...baseEnvironment,
      DOTNET_CLI_TELEMETRY_OPTOUT: "1",
      DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
      WEFT_MIGRATION_CONNECTION_STRING: connections.weft_migrator,
      ...extra,
    },
  });
}

async function startPostgres() {
  const names = Object.keys(dockerSecrets());
  container = docker([
    "create",
    "--label",
    label,
    "--name",
    "weft-preview-browser-" + nonce,
    "--memory",
    "512m",
    "--publish",
    "127.0.0.1::5432",
    ...names.flatMap((name) => ["--env", name]),
    postgresImage,
  ]);
  docker([
    "cp",
    join(repository, "deploy/local/postgres-init.sh"),
    container + ":/docker-entrypoint-initdb.d/01-weft-roles.sh",
  ]);
  docker(["start", container]);
  const port = docker(["port", container, "5432/tcp"])
    .split("\n")[0]
    .split(":")
    .pop();
  for (const [role, key] of roles)
    connections[role] =
      `Host=127.0.0.1;Port=${port};Database=weft;Username=${role};Password=${secrets.get(key)};Timeout=15;Command Timeout=30`;
  const end = Date.now() + 90000;
  while (true) {
    try {
      // The init script runs before the final server start; the migrator login proves both.
      if (query("SELECT 1") === "1") break;
    } catch {
      // Not ready yet.
    }
    if (Date.now() > end) throw new Error("PostgreSQL did not become ready.");
    await pause(500);
  }
}

function hostEnvironment(origin, principal, extra) {
  return {
    ...baseEnvironment,
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
    DOTNET_ENVIRONMENT: "Development",
    ASPNETCORE_ENVIRONMENT: "Development",
    TMPDIR: scratch,
    WEFT_CATALOG_ENABLED: "true",
    WEFT_DEV_IDENTITY_ENABLED: "true",
    WEFT_DEV_PRINCIPAL_ID: principal,
    WEFT_DEV_ORGANIZATION_ID: ids.organization,
    WEFT_DEV_BROWSER_ORIGIN: origin,
    WEFT_DEV_LISTEN_ADDR: new URL(origin).host,
    WEFT_CATALOG_CONNECTION_STRING: connections.weft_catalog_reader,
    ...extra,
  };
}

async function startChild(name, assembly, environment, origin = null) {
  if (children.has(name)) throw new Error(name + " is already running.");
  const child = spawn(dotnet, [assembly], {
    cwd: dirname(assembly),
    env: environment,
    stdio: ["ignore", "pipe", "pipe"],
  });
  const record = { child, output: [], exited: null };
  record.closed = new Promise((fulfil) =>
    child.once("close", (code, signal) => {
      record.exited = { code, signal };
      fulfil(record.exited);
    }),
  );
  for (const stream of [child.stdout, child.stderr])
    stream.on("data", (bytes) => {
      record.output.push(bytes);
      if (record.output.length > 400) record.output.shift();
    });
  children.set(name, record);
  if (origin) {
    const end = Date.now() + 90000;
    while (true) {
      if (record.exited)
        throw new Error(name + " exited during startup:\n" + tail(record));
      try {
        const reply = await request(origin, "/api/v1/health");
        if (reply.status === 200) break;
      } catch {
        // Listener not ready.
      }
      if (Date.now() > end) throw new Error(name + " did not become healthy.");
      await pause(200);
    }
  } else {
    // The worker has no listener; Wolverine reports readiness only through its log.
    const end = Date.now() + 90000;
    while (
      !Buffer.concat(record.output)
        .toString("utf8")
        .includes("Application started")
    ) {
      if (record.exited)
        throw new Error(name + " exited during startup:\n" + tail(record));
      if (Date.now() > end) throw new Error(name + " did not start.");
      await pause(200);
    }
  }
}
const tail = (record) =>
  Buffer.concat(record.output).toString("utf8").slice(-4000);

async function stopChild(name) {
  const record = children.get(name);
  if (!record) return;
  if (!record.exited) {
    record.child.kill("SIGTERM");
    const settled = await Promise.race([
      record.closed,
      pause(20000).then(() => null),
    ]);
    if (!settled) {
      record.child.kill("SIGKILL");
      await record.closed;
    }
  }
  children.delete(name);
}

/** One HTTP exchange with cookie/token state kept on the session object. */
async function request(
  origin,
  path,
  {
    method = "GET",
    body = null,
    media = null,
    key = null,
    session = null,
  } = {},
) {
  const headers = {};
  if (session?.cookies.size)
    headers.Cookie = [...session.cookies]
      .map(([k, v]) => k + "=" + v)
      .join("; ");
  if (method !== "GET" && session) {
    headers.Origin = origin;
    headers["X-XSRF-TOKEN"] = session.token;
  }
  if (media) headers["Content-Type"] = media;
  if (key) headers["Idempotency-Key"] = key;
  if (body) headers["Content-Length"] = String(body.length);
  return await new Promise((fulfil, reject) => {
    const req = http.request(
      new URL(path, origin),
      { method, headers, agent: false, timeout: 60000 },
      (response) => {
        const parts = [];
        response.on("data", (chunk) => parts.push(chunk));
        response.once("error", reject);
        response.once("end", () => {
          for (const cookie of response.headers["set-cookie"] ?? []) {
            const [pair] = cookie.split(";", 1);
            const index = pair.indexOf("=");
            session?.cookies.set(pair.slice(0, index), pair.slice(index + 1));
          }
          const bytes = Buffer.concat(parts);
          let json = null;
          try {
            json = bytes.length ? JSON.parse(bytes.toString("utf8")) : null;
          } catch {
            json = null;
          }
          fulfil({
            status: response.statusCode,
            headers: response.headers,
            body: json,
          });
        });
      },
    );
    req.once("timeout", () => req.destroy(new Error("HTTP timeout")));
    req.once("error", reject);
    req.end(body ?? undefined);
  });
}

async function session(origin) {
  const state = { cookies: new Map(), token: null };
  const context = await request(origin, "/api/v1/catalog/context", {
    session: state,
  });
  if (context.status !== 200)
    throw new Error("Catalog context refused: " + context.status);
  state.token = context.body.requestToken;
  return state;
}

async function upload(origin, state, project, fixture, ordinal) {
  const bytes = readFileSync(
    join(repository, "engines/ferrule-host/tests/fixtures", fixture),
  );
  const path =
    `/api/v1/organizations/${project.organizationId}/projects/${project.projectId}/mappings?name=` +
    encodeURIComponent("Preview " + fixture.replace(/\.json$/, "")) +
    "&backendId=ferrule&description=Actual%20preview%20fixture&originalFilename=preview-" +
    ordinal +
    ".native.json";
  let reply = await request(origin, path, {
    method: "POST",
    body: bytes,
    media: "application/octet-stream",
    key: randomUUID(),
    session: state,
  });
  const end = Date.now() + 120000;
  while (reply.status === 202) {
    if (Date.now() > end) throw new Error("Upload did not commit.");
    await pause(250);
    reply = await request(origin, reply.headers.location, { session: state });
  }
  if (reply.status !== 200 && reply.status !== 201)
    throw new Error(
      "Upload failed: " + reply.status + " " + JSON.stringify(reply.body),
    );
  if (
    !["committedValid", "committedDecodedInvalid"].includes(reply.body.outcome)
  )
    throw new Error("Upload outcome: " + reply.body.outcome);
  return reply.body.receipt.futureRevision;
}

async function prepare() {
  await startPostgres();
  tool(["migrate"]);
  const name = { WEFT_DEV_ORGANIZATION_NAME: "Preview browser fixture" };
  tool(["provision-development-identity"], {
    ...name,
    WEFT_DEV_ORGANIZATION_ID: ids.organization,
    WEFT_DEV_PRINCIPAL_ID: ids.administrator,
  });
  tool([
    "set-project-creation-capability",
    ids.organization,
    ids.administrator,
    "true",
  ]);
  mkdirSync(dirname(storageRoot), { mode: 0o700 });
  execFileSync(
    dotnet,
    [products.storage, "initialize-v2", storageRoot, dirname(products.server)],
    {
      stdio: ["ignore", "ignore", "pipe"],
      timeout: 60000,
      env: baseEnvironment,
    },
  );

  // A Development administrator host with the in-process worker runner creates the actual project
  // and admits both native mappings through ordinary HTTP uploads.
  await startChild(
    "mutation",
    products.server,
    hostEnvironment(mutationOrigin, ids.administrator, {
      WEFT_PROJECT_CREATION_ENABLED: "true",
      WEFT_POSTGRES_PROJECT_CREATION_CONNECTION_STRING:
        connections.weft_project_creator,
      WEFT_REVISION_INGESTION_ENABLED: "true",
      WEFT_POSTGRES_INGESTION_CONNECTION_STRING: connections.weft_ingest,
      WEFT_PRIVATE_STORAGE_ROOT: storageRoot,
      WEFT_FERRULE_WORKER_IMAGE: workerImage,
    }),
    mutationOrigin,
  );
  const state = await session(mutationOrigin);
  const created = await request(
    mutationOrigin,
    `/api/v1/organizations/${ids.organization}/projects`,
    {
      method: "POST",
      body: Buffer.from(
        JSON.stringify({
          name: "Preview browser project",
          description: "Actual preview qualification",
        }),
      ),
      media: "application/vnd.weft.project-create.v1+json",
      key: randomUUID(),
      session: state,
    },
  );
  if (created.status !== 201)
    throw new Error("Project creation failed: " + created.status);
  const project = created.body.receipt.project;
  const orders = await upload(
    mutationOrigin,
    state,
    project,
    "preview-orders.json",
    0,
  );
  const unsupported = await upload(
    mutationOrigin,
    state,
    project,
    "preview-csv-source.json",
    1,
  );
  await stopChild("mutation");
  for (const [principal, role] of [
    [ids.publisher, "publisher"],
    [ids.viewer, "viewer"],
  ])
    tool(["provision-development-identity"], {
      ...name,
      WEFT_DEV_ORGANIZATION_ID: ids.organization,
      WEFT_DEV_PRINCIPAL_ID: principal,
      WEFT_DEV_PROJECT_ID: project.projectId,
      WEFT_DEV_PROJECT_ROLE: role,
    });
  return {
    schemaVersion: "weft.preview-browser-manifest.v1",
    readOrigin,
    organizationId: ids.organization,
    profiles: { publisher: ids.publisher, viewer: ids.viewer },
    project,
    orders,
    unsupported,
  };
}

const startServer = (profile) =>
  startChild(
    "server",
    products.server,
    hostEnvironment(readOrigin, ids[profile], {
      WEFT_PREVIEWS_ENABLED: "true",
      WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING:
        connections.weft_preview_writer,
      WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING:
        connections.weft_preview_reader,
    }),
    readOrigin,
  );
const startWorker = () =>
  startChild("worker", products.worker, {
    ...baseEnvironment,
    DOTNET_CLI_TELEMETRY_OPTOUT: "1",
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
    TMPDIR: scratch,
    WEFT_PREVIEW_WRITER_CONNECTION_STRING: connections.weft_preview_writer,
    WEFT_PRIVATE_STORAGE_ROOT: storageRoot,
    WEFT_FERRULE_WORKER_IMAGE: workerImage,
    WEFT_DOCKER_EXECUTABLE: "/usr/bin/docker",
  });

/** Closed control surface for the spec: process lifecycle and read-only run facts only. */
async function control(item) {
  switch (item.action) {
    case "stop-worker":
      await stopChild("worker");
      return { worker: "stopped" };
    case "start-worker":
      await startWorker();
      return { worker: "started" };
    case "restart-server":
      if (!["publisher", "viewer"].includes(item.profile))
        throw new Error("Unknown profile.");
      await stopChild("server");
      await startServer(item.profile);
      return { server: item.profile };
    case "run": {
      if (!/^[0-9a-f-]{36}$/.test(item.runId ?? ""))
        throw new Error("Invalid run.");
      const row = query(
        "SELECT json_build_object('state',state,'attempt',attempt,'outcome',outcome,'submitter',submitter_id," +
          "'outputs',(SELECT count(*) FROM weft.preview_documents d WHERE d.organization_id=r.organization_id" +
          " AND d.run_id=r.run_id AND d.direction=2)) " +
          `FROM weft.preview_runs r WHERE run_id='${item.runId}'`,
      );
      return row ? JSON.parse(row) : null;
    }
    case "runs":
      return { count: Number(query("SELECT count(*) FROM weft.preview_runs")) };
    default:
      throw new Error("Unknown control.");
  }
}

function controlServer() {
  const server = http.createServer((req, res) => {
    const parts = [];
    req.on("data", (chunk) => parts.push(chunk));
    req.on("end", async () => {
      try {
        if (
          req.method !== "POST" ||
          req.headers.authorization !== "Bearer " + controlToken
        )
          throw new Error("Refused.");
        const result = await control(
          JSON.parse(Buffer.concat(parts).toString("utf8")),
        );
        res
          .writeHead(200, { "Content-Type": "application/json" })
          .end(JSON.stringify(result));
      } catch (error) {
        res
          .writeHead(500, { "Content-Type": "application/json" })
          .end(JSON.stringify({ error: String(error.message) }));
      }
    });
  });
  return new Promise((fulfil) =>
    server.listen(0, "127.0.0.1", () => fulfil(server)),
  );
}

async function cleanup() {
  const failures = [];
  for (const name of [...children.keys()])
    try {
      await stopChild(name);
    } catch (error) {
      failures.push(name + ": " + error.message);
    }
  if (container)
    try {
      docker(["rm", "--force", "--volumes", container]);
    } catch (error) {
      failures.push("postgres: " + error.message);
    }
  try {
    const left = docker([
      "ps",
      "--all",
      "--quiet",
      "--filter",
      "label=" + label,
    ]);
    if (left) failures.push("labeled containers remain");
  } catch (error) {
    failures.push("inventory: " + error.message);
  }
  try {
    rmSync(scratch, { recursive: true, force: true });
  } catch (error) {
    failures.push("scratch: " + error.message);
  }
  return failures;
}

let exitCode = 1;
let control$ = null;
const interrupt = () => {
  // Cleanup runs in finally; a second signal is ignored so it is not cut short.
};
process.on("SIGINT", interrupt);
process.on("SIGTERM", interrupt);
try {
  const manifest = await prepare();
  await startServer("publisher");
  await startWorker();
  const manifestPath = join(scratch, "manifest.json");
  writeFileSync(manifestPath, JSON.stringify(manifest) + "\n", { mode: 0o600 });
  control$ = await controlServer();
  const playwright = spawn(
    process.execPath,
    [
      join(repository, "node_modules/@playwright/test/cli.js"),
      "test",
      "--config=" +
        join(repository, "apps/web/playwright.preview-live.config.ts"),
      ...extraPlaywright,
    ],
    {
      cwd: join(repository, "apps/web"),
      stdio: "inherit",
      env: {
        ...process.env,
        WEFT_PREVIEW_BROWSER_MANIFEST: manifestPath,
        WEFT_PREVIEW_BROWSER_BASE_URL: readOrigin,
        WEFT_PREVIEW_BROWSER_CONTROL:
          "http://127.0.0.1:" + control$.address().port,
        WEFT_PREVIEW_BROWSER_CONTROL_TOKEN: controlToken,
      },
    },
  );
  exitCode = await new Promise((fulfil) =>
    playwright.once("close", (code) => fulfil(code ?? 1)),
  );
} catch (error) {
  console.error("PreviewBrowserFixtureFailed: " + error.message);
  for (const [name, record] of children)
    console.error("--- " + name + " output ---\n" + tail(record));
  exitCode = 1;
} finally {
  control$?.close();
  const failures = await cleanup();
  if (failures.length) {
    console.error("PreviewBrowserCleanupFailed: " + failures.join("; "));
    exitCode = 1;
  }
}
process.exit(exitCode);
