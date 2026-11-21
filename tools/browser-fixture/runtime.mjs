// Root-operated browser fixture. Standard library only; no product or historical operator imports.
// Configuration, journals and child output stay outside repositories and publications.
import { spawn, execFileSync } from "node:child_process";
import { createHash, randomBytes, randomUUID } from "node:crypto";
import {
  readFileSync,
  writeFileSync,
  openSync,
  closeSync,
  fsyncSync,
  linkSync,
  unlinkSync,
  lstatSync,
  existsSync,
  readdirSync,
  mkdirSync,
  renameSync,
} from "node:fs";
import { resolve, dirname, join, relative, isAbsolute } from "node:path";
import http from "node:http";
import net from "node:net";
import { setTimeout as pause } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { failureSite } from "./failure-site.mjs";
import { hostFailureSummary } from "./safe-diagnostics.mjs";
import { createOnFreeSubnet, isPoolOverlap } from "./test-subnets.mjs";
import {
  browserFixtureRolePairs as rolePairs,
  matchesBrowserFixtureRoles,
} from "./roles.mjs";
import {
  browserFixtureMigrations,
  browserFixtureProtectedTables,
  browserFixtureRowsQuery,
  matchesBrowserFixtureMigrationJournal,
  regressionReadProfile,
} from "./schema.mjs";

export async function runBrowserFixture(definition) {
  try {
    const creationScenario = definition.kind === "project-creation";
    const metadataScenario = definition.kind === "mapping-metadata";
    const uploadScenario = definition.kind === "native-mapping-upload";
    const appendScenario = definition.kind === "native-revision-append";
    const buildScenario = definition.kind === "build";
    const regressionScenario =
      definition.kind === "regression" || buildScenario;
    class Failure extends Error {
      constructor(category) {
        super(category);
        this.category = category;
      }
    }
    const check = (value, category = "stateMismatch") => {
      if (!value) throw new Failure(category);
    };
    const sha = (bytes) => createHash("sha256").update(bytes).digest("hex");
    const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
    const canonical = (value) =>
      value === null || typeof value !== "object"
        ? value
        : Array.isArray(value)
          ? value.map(canonical)
          : Object.fromEntries(
              Object.keys(value)
                .sort()
                .map((key) => [key, canonical(value[key])]),
            );
    const equivalent = (a, b) => same(canonical(a), canonical(b));
    const guid = (value) => {
      check(
        typeof value === "string" &&
          /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value) &&
          value !== "00000000-0000-0000-0000-000000000000",
        "invalidControl",
      );
      return value;
    };
    const hash = (value) => {
      check(typeof value === "string" && /^[a-f0-9]{64}$/.test(value));
      return value;
    };
    const absolute = (value) => {
      check(
        typeof value === "string" &&
          isAbsolute(value) &&
          resolve(value) === value,
      );
      return value;
    };
    const inside = (base, child) => {
      const path = relative(base, child);
      return (
        path === "" ||
        (!path.startsWith("../") && path !== ".." && !isAbsolute(path))
      );
    };
    const ordinary = (path) => {
      const stat = lstatSync(path);
      check(stat.isFile() && !stat.isSymbolicLink());
      return stat;
    };
    const privateFile = (path) => {
      check((ordinary(path).mode & 0o777) === 0o600);
      return readFileSync(path);
    };
    const privateDirectory = (path) => {
      const stat = lstatSync(path);
      check(
        stat.isDirectory() &&
          !stat.isSymbolicLink() &&
          (stat.mode & 0o777) === 0o700,
      );
    };
    const origin = (value) => {
      const url = new URL(value);
      check(
        url.protocol === "http:" &&
          url.hostname === "127.0.0.1" &&
          /^[1-9][0-9]*$/.test(url.port) &&
          Number(url.port) <= 65535 &&
          url.origin === value &&
          url.pathname === "/" &&
          !url.username &&
          !url.password,
      );
      return value;
    };
    const bound = (item) => {
      check(item && typeof item === "object");
      const bytes = readFileSync(absolute(item.path));
      ordinary(item.path);
      check(sha(bytes) === hash(item.sha256));
      return bytes;
    };

    check(
      process.argv.length === 4 && process.argv[2] === "--config",
      "invalidControl",
    );
    const configPath = absolute(process.argv[3]);
    privateDirectory(dirname(configPath));
    const parse = (bytes) => {
      try {
        return JSON.parse(bytes);
      } catch {
        throw new Failure("invalidControl");
      }
    };
    const configBytes = privateFile(configPath),
      cfg = parse(configBytes);
    check(
      cfg.schemaVersion ===
        `braid.${definition.kind}-browser-operator-config.v1` &&
        (regressionScenario || cfg.runtimeReleased === true),
    );
    const fixtureId = guid(cfg.fixtureId),
      privateRoot = absolute(cfg.privateDirectory);
    privateDirectory(privateRoot);
    check(
      dirname(configPath) === privateRoot &&
        dirname(absolute(cfg.manifestPath)) === privateRoot &&
        (regressionScenario ||
          dirname(absolute(cfg.releasePath)) === privateRoot),
    );
    check(
      process.env[
        buildScenario
          ? "BRAID_BUILD_BROWSER_MANIFEST"
          : regressionScenario
            ? "BRAID_REGRESSION_BROWSER_MANIFEST"
            : creationScenario
              ? "BRAID_PROJECT_CREATION_BROWSER_MANIFEST"
              : appendScenario
                ? "BRAID_NATIVE_REVISION_APPEND_BROWSER_MANIFEST"
                : uploadScenario
                  ? "BRAID_NATIVE_MAPPING_UPLOAD_BROWSER_MANIFEST"
                  : metadataScenario
                    ? "BRAID_MAPPING_METADATA_BROWSER_MANIFEST"
                    : definition.kind === "catalog"
                      ? "BRAID_CATALOG_BROWSER_MANIFEST"
                      : "BRAID_REPORTS_BROWSER_MANIFEST"
      ] === cfg.manifestPath &&
        process.env[
          buildScenario
            ? "BRAID_BUILD_BASE_URL"
            : regressionScenario
              ? "BRAID_REGRESSION_BASE_URL"
              : creationScenario
                ? "BRAID_PROJECT_CREATION_BASE_URL"
                : appendScenario
                  ? "BRAID_NATIVE_REVISION_APPEND_BASE_URL"
                  : uploadScenario
                    ? "BRAID_NATIVE_MAPPING_UPLOAD_BASE_URL"
                    : metadataScenario
                      ? "BRAID_MAPPING_METADATA_BASE_URL"
                      : definition.kind === "catalog"
                        ? "BRAID_CATALOG_BASE_URL"
                        : "BRAID_REPORTS_BASE_URL"
        ] === origin(cfg.readOrigin),
    );
    origin(cfg.mutationOrigin);
    check(cfg.readOrigin !== cfg.mutationOrigin);
    if (!regressionScenario) {
      const release = parse(privateFile(cfg.releasePath));
      check(
        release.schemaVersion ===
          `braid.${definition.kind}-browser-release.v1` &&
          release.released === true &&
          release.configurationSha256 === sha(configBytes) &&
          release.operatorSha256 ===
            sha(readFileSync(fileURLToPath(definition.entrypoint))) &&
          release.sourceAndPublicationsAccepted === true &&
          release.outerObserversAccepted === true,
      );
      bound(cfg.outerObserverAdmission);
    }
    bound(cfg.source.admission);
    bound(cfg.publications.admission);
    check(
      /^[a-f0-9]{40}$/.test(cfg.source.commit) &&
        /^[a-f0-9]{40}$/.test(cfg.source.tree),
    );
    const workspace = absolute(cfg.source.workspace),
      sourceManifest = parse(bound(cfg.source.manifest));
    check(Array.isArray(sourceManifest) && sourceManifest.length > 0);
    const sourceHashes = {};
    for (const item of sourceManifest) {
      check(
        typeof item.path === "string" &&
          !isAbsolute(item.path) &&
          inside(workspace, resolve(workspace, item.path)) &&
          !sourceHashes[item.path],
      );
      const file = join(workspace, item.path),
        stat = ordinary(file);
      check(
        sha(readFileSync(file)) === hash(item.sha256) &&
          ["100644", "100755"].includes(String(item.mode)) &&
          (stat.mode & 0o777) ===
            (String(item.mode) === "100755" ? 0o755 : 0o644),
      );
      sourceHashes[item.path] = item.sha256;
    }
    const walk = (base, prefix = "") =>
      readdirSync(join(base, prefix), { withFileTypes: true })
        .sort((a, b) => a.name.localeCompare(b.name))
        .flatMap((entry) => {
          check(!entry.isSymbolicLink());
          const path = join(prefix, entry.name);
          if (entry.isDirectory()) return walk(base, path);
          ordinary(join(base, path));
          return [
            {
              path,
              sha256: sha(readFileSync(join(base, path))),
              bytes: lstatSync(join(base, path)).size,
            },
          ];
        });
    const publicationKinds = [
      ["server", "Braid.Server"],
      ["databaseTool", "Braid.DatabaseTool"],
      ...(regressionScenario ? [["worker", "Braid.Worker"]] : []),
      ...(creationScenario ? [] : [["storageTool", "Braid.StorageTool"]]),
    ];
    for (const [kind, assembly] of publicationKinds) {
      const item = cfg.publications[kind];
      absolute(item.directory);
      check(
        !inside(item.directory, privateRoot) && !inside(workspace, privateRoot),
      );
      const actual = walk(item.directory);
      check(actual.length === Object.keys(item.hashes).length);
      for (const file of actual) check(item.hashes[file.path] === file.sha256);
      for (const suffix of [".dll", ".deps.json", ".runtimeconfig.json"])
        hash(item.hashes[assembly + suffix]);
    }
    check(
      cfg.docker === "/usr/bin/docker" &&
        cfg.daemonSocket === "/var/run/docker.sock",
    );
    absolute(cfg.dotnet);
    const workerImage = regressionScenario
      ? cfg.workerImage
      : "sha256:a63f57a009717d2171e48c02c068e9dcb19974561b91cf8484345d02d5c05439";
    check(!regressionScenario || /^sha256:[a-f0-9]{64}$/.test(workerImage));
    const postgresImage =
      "postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc";
    check(
      (creationScenario
        ? cfg.workerImage === null && cfg.nativeWorkers === "forbidden"
        : cfg.workerImage === workerImage) &&
        cfg.postgresImage === postgresImage,
    );
    const fixedFixtures = definition.fixtures;
    if (creationScenario)
      check(Array.isArray(fixedFixtures) && fixedFixtures.length === 0);
    const fixtures = fixedFixtures.map(([path, digest, length]) => {
      const bytes = readFileSync(join(workspace, path));
      check(
        sourceHashes[path] === digest &&
          sha(bytes) === digest &&
          bytes.length === length,
      );
      return { path, bytes, sha256: digest };
    });
    const migrations = browserFixtureMigrations;
    check(
      equivalent(
        Object.keys(sourceHashes)
          .filter((path) => /^migrations\/postgres\/\d{4}_.*\.sql$/.test(path))
          .sort(),
        migrations.map((name) => "migrations/postgres/" + name),
      ),
    );
    check(sourceHashes["deploy/local/postgres-init.sh"]);

    // No resource, HTTP or product operation precedes all of the held-input checks above.
    check(
      !existsSync(cfg.manifestPath) &&
        !existsSync(join(privateRoot, "ledger.json")),
    );
    const scratch = join(privateRoot, "scratch");
    check(!existsSync(scratch));
    mkdirSync(scratch, { mode: 0o700 });
    const storageRoot = regressionScenario
        ? absolute(cfg.privateStorageRoot)
        : join(scratch, "private-objects"),
      nonce = randomBytes(8).toString("hex");
    if (regressionScenario)
      check(storageRoot === "/run/braid-regression-" + fixtureId + "/objects");
    const resourceName =
      (creationScenario
        ? "braid-project-creation-browser-"
        : "braid-catalog-browser-") + nonce;
    const labels = {
      "braid.qualification.owner": fixtureId,
      "braid.qualification.nonce": nonce,
      "braid.qualification.case": resourceName,
    };
    const labelArgs = Object.entries(labels).flatMap(([key, value]) => [
      "--label",
      key + "=" + value,
    ]);
    const state = {
      schemaVersion: `braid.${definition.kind}-browser-private-ledger.v1`,
      fixtureId,
      source: cfg.source.commit,
      labels,
      attempts: [],
      children: [],
      controls: [],
      observations: [],
      primaryFailure: null,
      cleanupFailures: [],
      terminal: null,
    };
    function atomic(path, bytes, replace = false) {
      const stage = path + ".stage-" + randomBytes(8).toString("hex");
      let fd;
      try {
        fd = openSync(stage, "wx", 0o600);
        writeFileSync(fd, bytes);
        fsyncSync(fd);
        closeSync(fd);
        fd = undefined;
        if (replace) renameSync(stage, path);
        else {
          linkSync(stage, path);
          unlinkSync(stage);
        }
        const directory = openSync(dirname(path), "r");
        try {
          fsyncSync(directory);
        } finally {
          closeSync(directory);
        }
      } finally {
        if (fd !== undefined) closeSync(fd);
        if (existsSync(stage)) unlinkSync(stage);
      }
    }
    const save = () =>
      atomic(
        join(privateRoot, "ledger.json"),
        Buffer.from(JSON.stringify(state, null, 2) + "\n"),
        true,
      );
    let deadline = Infinity;
    function budget(maximum) {
      check(!interrupted || closing, "productFailure");
      const remaining = deadline - Date.now();
      check(remaining > 0, "deadline");
      return Math.max(1, Math.min(maximum, remaining));
    }
    function run(
      command,
      args,
      {
        env = process.env,
        input,
        timeout = 30000,
        allowFailure = false,
        keepError = false,
      } = {},
    ) {
      let code = 0,
        stdout = "",
        stderr = "";
      try {
        stdout = execFileSync(command, args, {
          env,
          input,
          timeout: budget(timeout),
          maxBuffer: 1048576,
          encoding: "utf8",
          stdio: ["pipe", "pipe", "pipe"],
        });
      } catch (error) {
        code = Number.isInteger(error.status) ? error.status : -1;
        stdout = error.stdout?.toString() ?? "";
        stderr = error.stderr?.toString() ?? "";
      }
      state.observations.push({
        kind: "command",
        site: failureSite(new Error()),
        ordinal: state.observations.length,
        exitCode: code,
        stdoutBytes: Buffer.byteLength(stdout),
        stderrBytes: Buffer.byteLength(stderr),
        stderrSha256: sha(Buffer.from(stderr)),
      });
      save();
      check(
        allowFailure || code === 0,
        code === -1 ? "deadline" : "productFailure",
      );
      // Error text stays with the caller that classifies it; it is never recorded.
      return { code, stdout: stdout.trim(), ...(keepError ? { stderr } : {}) };
    }
    const docker = (args, options) =>
      run(cfg.docker, ["--host=unix://" + cfg.daemonSocket, ...args], options)
        .stdout;
    const jsonDocker = (args) => JSON.parse(docker(args));
    const inventories = () => ({
      containers: docker(["ps", "--all", "--quiet", "--no-trunc"])
        .split("\n")
        .filter(Boolean)
        .sort(),
      networks: docker(["network", "ls", "--quiet", "--no-trunc"])
        .split("\n")
        .filter(Boolean)
        .sort(),
      volumes: docker(["volume", "ls", "--quiet"])
        .split("\n")
        .filter(Boolean)
        .sort(),
    });
    let before = null,
      pg = null,
      network = null,
      volume = null,
      readHost = null,
      mutationHost = null;
    let events = null,
      eventError = null,
      eventBytes = 0,
      eventPartial = "",
      eventClosed = false;
    const workerEvents = new Map(),
      secrets = new Map();
    const roleEnvironment = { ...process.env };
    for (const key of Object.keys(roleEnvironment))
      if (
        key.startsWith("BRAID_") ||
        key.startsWith("POSTGRES_") ||
        key === "PGPASSWORD"
      )
        delete roleEnvironment[key];
    for (const role of ["POSTGRES_ADMIN", ...rolePairs.map((pair) => pair[1])])
      secrets.set(role, randomBytes(24).toString("hex"));
    for (const [key, value] of secrets)
      roleEnvironment["BRAID_LOCAL_" + key + "_PASSWORD"] = value;
    Object.assign(roleEnvironment, {
      POSTGRES_DB: "braid",
      POSTGRES_USER: "postgres",
      POSTGRES_PASSWORD: secrets.get("POSTGRES_ADMIN"),
      POSTGRES_INITDB_ARGS: "--auth-host=scram-sha-256 --auth-local=trust",
    });
    const connections = {};
    const ids = {
      organization: randomUUID(),
      foreignOrganization: randomUUID(),
      administrator: randomUUID(),
      viewer: randomUUID(),
      runner: randomUUID(),
      publisher: randomUUID(),
      noProjects: randomUUID(),
      foreignAdministrator: randomUUID(),
    };
    let membership = null,
      organizationMembership = null,
      authorityProject = null,
      projectRevoked = false,
      orgRevoked = false,
      manifest = null;
    let scenario;

    async function daemon(path, maximum = 1048576) {
      return await new Promise((fulfil, reject) => {
        const request = http.request(
          { socketPath: cfg.daemonSocket, path, method: "GET", agent: false },
          (response) => {
            const parts = [];
            let count = 0;
            response.on("data", (chunk) => {
              count += chunk.length;
              if (count > maximum)
                request.destroy(new Failure("stateMismatch"));
              else parts.push(chunk);
            });
            response.once("error", reject);
            response.once("end", () =>
              fulfil({
                status: response.statusCode,
                body: Buffer.concat(parts).toString("utf8"),
              }),
            );
          },
        );
        const timer = setTimeout(
          () => request.destroy(new Failure("deadline")),
          budget(5000),
        );
        request.once("close", () => clearTimeout(timer));
        request.once("error", reject);
        request.end();
      });
    }
    async function openEvents() {
      await new Promise((fulfil, reject) => {
        const request = http.request({
          socketPath: cfg.daemonSocket,
          path:
            "/events?filters=" +
            encodeURIComponent(JSON.stringify({ type: ["container"] })),
          method: "GET",
        });
        const timer = setTimeout(
          () => request.destroy(new Failure("deadline")),
          budget(5000),
        );
        request.once("error", (error) => {
          clearTimeout(timer);
          eventError = error.category ?? "productFailure";
          reject(error);
        });
        request.once("response", (response) => {
          clearTimeout(timer);
          if (response.statusCode !== 200) {
            response.resume();
            reject(new Failure("productFailure"));
            return;
          }
          events = { request, response };
          state.observations.push({
            kind: "events-admitted",
            daemonStatus: 200,
          });
          save();
          response.on("data", (chunk) => {
            eventBytes += chunk.length;
            if (eventBytes > 16777216) {
              eventError = "stateMismatch";
              response.destroy();
              return;
            }
            eventPartial += chunk.toString("utf8");
            if (
              Buffer.byteLength(eventPartial) > 65536 &&
              !eventPartial.includes("\n")
            ) {
              eventError = "stateMismatch";
              response.destroy();
              return;
            }
            let index;
            while ((index = eventPartial.indexOf("\n")) >= 0) {
              const line = eventPartial.slice(0, index);
              eventPartial = eventPartial.slice(index + 1);
              try {
                check(Buffer.byteLength(line) <= 65536);
                const item = JSON.parse(line),
                  name = item.Actor?.Attributes?.name;
                if (
                  creationScenario &&
                  item.Type === "container" &&
                  (/^braid-inspect-[0-9a-f]{32}$/.test(name ?? "") ||
                    item.Actor?.Attributes?.image === workerImage)
                )
                  throw new Failure("productFailure");
                if (
                  item.Type === "container" &&
                  /^braid-inspect-[0-9a-f]{32}$/.test(name ?? "") &&
                  ["create", "destroy"].includes(item.Action)
                ) {
                  const id = item.Actor.ID ?? item.id;
                  check(/^[a-f0-9]{64}$/.test(id));
                  const record = workerEvents.get(id) ?? {
                    id,
                    name,
                    create: false,
                    destroy: false,
                    image: item.Actor.Attributes.image ?? null,
                  };
                  if (item.Action === "create") {
                    check(item.Actor.Attributes.image === workerImage);
                    record.create = true;
                  }
                  if (item.Action === "destroy") record.destroy = true;
                  workerEvents.set(id, record);
                }
              } catch {
                eventError = "stateMismatch";
                response.destroy();
              }
            }
          });
          response.once("error", () => {
            if (!eventClosed) eventError = "productFailure";
          });
          response.once("close", () => {
            if (!eventClosed) eventError = "productFailure";
          });
          fulfil();
        });
        request.end();
      });
    }
    async function settleWorkers(requireSome = false) {
      const end = Date.now() + budget(5000);
      while (true) {
        check(events && !eventError && !eventClosed, "productFailure");
        const workers = [...workerEvents.values()].filter(
          (item) => item.create,
        );
        if (creationScenario)
          check(!requireSome && workers.length === 0, "productFailure");
        const observations = [];
        for (const item of workers)
          observations.push({
            ...item,
            daemonStatus: (await daemon("/containers/" + item.id + "/json"))
              .status,
          });
        if (
          (!requireSome || workers.length > 0) &&
          observations.every(
            (item) => item.destroy && item.daemonStatus === 404,
          )
        ) {
          state.observations.push({
            kind: "workers-settled",
            workers: observations,
            operatorWorkerRemoval: false,
          });
          save();
          return;
        }
        check(Date.now() < end, "deadline");
        await pause(100);
      }
    }
    function attempt(kind, name) {
      const item = { kind, name, id: null };
      state.attempts.push(item);
      save();
      return item;
    }
    function labelsMatch(item) {
      return Object.entries(labels).every(
        ([key, value]) => item.Labels?.[key] === value,
      );
    }
    function inspectOwned() {
      const container = jsonDocker(["inspect", pg.Id])[0],
        net = jsonDocker(["network", "inspect", network.Id])[0],
        vol = jsonDocker(["volume", "inspect", volume.Name])[0];
      check(
        container.Id === pg.Id &&
          container.Name === pg.Name &&
          container.Image === pg.Image &&
          labelsMatch({ Labels: container.Config.Labels }) &&
          net.Id === network.Id &&
          net.Name === network.Name &&
          labelsMatch(net) &&
          vol.Name === volume.Name &&
          vol.Mountpoint === volume.Mountpoint &&
          labelsMatch(vol),
        "ownershipChanged",
      );
      check(
        container.HostConfig.Memory === 536870912 &&
          container.HostConfig.MemorySwap === 536870912 &&
          container.HostConfig.NanoCpus === 1000000000 &&
          container.HostConfig.PidsLimit === 128 &&
          !container.State.OOMKilled &&
          container.Mounts.length === 1 &&
          container.Mounts[0].Type === "volume" &&
          container.Mounts[0].Name === volume.Name &&
          container.Mounts[0].Destination === "/var/lib/postgresql/data" &&
          Object.keys(container.NetworkSettings.Networks).length === 1 &&
          container.NetworkSettings.Networks[network.Name]?.NetworkID ===
            network.Id,
        "ownershipChanged",
      );
      const ports = container.NetworkSettings.Ports["5432/tcp"],
        bindings = container.HostConfig.PortBindings["5432/tcp"];
      check(
        Object.keys(container.HostConfig.PortBindings).length === 1 &&
          bindings.length === 1 &&
          bindings[0].HostIp === "127.0.0.1" &&
          ports?.length === 1 &&
          ports[0].HostIp === "127.0.0.1" &&
          /^[1-9][0-9]*$/.test(ports[0].HostPort) &&
          Number(ports[0].HostPort) <= 65535 &&
          (pg.hostPort === undefined || ports[0].HostPort === pg.hostPort),
        "ownershipChanged",
      );
      return container;
    }
    function sql(query, admin = false) {
      check(pg && /^(?:SELECT|WITH)\s/i.test(query));
      const role = admin ? "postgres" : "braid_migrator";
      const password = admin
        ? secrets.get("POSTGRES_ADMIN")
        : secrets.get("MIGRATOR");
      return run(
        cfg.docker,
        [
          "--host=unix://" + cfg.daemonSocket,
          "exec",
          "--interactive",
          "--env",
          "PGPASSWORD",
          pg.Id,
          "psql",
          "-X",
          "-qAt",
          "--host=127.0.0.1",
          "--username",
          role,
          "--dbname",
          "braid",
          "--set=ON_ERROR_STOP=1",
        ],
        {
          env: { ...roleEnvironment, PGPASSWORD: password },
          input: "BEGIN READ ONLY;\n" + query + ";\nCOMMIT;\n",
          timeout: 10000,
        },
      ).stdout;
    }
    const jsonSql = (query, admin = false) => JSON.parse(sql(query, admin));
    const tables = [
      "organizations",
      "principals",
      "organization_memberships",
      "projects",
      "project_memberships",
      "mappings",
      "mapping_revisions",
      "revision_ingestions",
      "revision_ingestion_attempts",
      "organization_project_creation_capabilities",
      "project_creations",
      "revision_derivations",
      "revision_derivation_attempts",
      "private_object_publications",
      "private_object_retirements",
      "private_object_cleanup_attempts",
      "external_identity_bindings",
      "project_payload_read_grants",
      "server_sessions",
      ...browserFixtureProtectedTables,
      ...(regressionScenario
        ? ["regression_cases", "regression_case_versions", "regression_runs"]
        : []),
      ...(buildScenario
        ? [
            "build_environments",
            "build_approvals",
            "build_runs",
            "build_publications",
          ]
        : []),
    ];
    const rows = () => jsonSql(browserFixtureRowsQuery(tables));
    const journal = () =>
      jsonSql(
        "SELECT coalesce(json_agg(json_build_object('version',version,'checksum',checksum,'appliedAt',applied_at) ORDER BY version),'[]'::json) FROM braid.schema_migrations",
      );
    const credentialState = () =>
      jsonSql(
        "SELECT json_build_object('roles',(SELECT jsonb_agg(to_jsonb(r) ORDER BY rolname) FROM pg_authid r WHERE rolname LIKE 'braid_%'),'memberships',(SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY to_jsonb(m)::text),'[]'::jsonb) FROM pg_auth_members m))",
        true,
      );
    // C/D-only, fixed read-only grant capture. Hash complete ordered catalog arrays
    // inside PostgreSQL so even the built-in routine ACL set stays below pipe limits.
    // Relation capture includes column ACLs, not only table-level relacl.
    function creationGrantState() {
      check(
        creationScenario ||
          metadataScenario ||
          uploadScenario ||
          appendScenario ||
          regressionScenario,
      );
      return jsonSql(
        `SELECT json_build_object(
        'database',(SELECT jsonb_build_array(oid,datname,datdba,datacl::text) FROM pg_catalog.pg_database WHERE datname=current_database()),
        'schemasSha256',(SELECT encode(sha256(convert_to(coalesce(jsonb_agg(jsonb_build_array(oid,nspname,nspowner,nspacl::text) ORDER BY oid),'[]'::jsonb)::text,'UTF8')),'hex') FROM pg_catalog.pg_namespace),
        'relationsAndColumnsSha256',(SELECT encode(sha256(convert_to(coalesce(jsonb_agg(jsonb_build_array(c.oid,c.relnamespace,c.relname,c.relkind,c.relowner,c.relacl::text,
          (SELECT coalesce(jsonb_agg(jsonb_build_array(a.attnum,a.attname,a.attacl::text) ORDER BY a.attnum),'[]'::jsonb)
           FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped AND a.attacl IS NOT NULL)) ORDER BY c.oid),'[]'::jsonb)::text,'UTF8')),'hex') FROM pg_catalog.pg_class c),
        'functionsSha256',(SELECT encode(sha256(convert_to(coalesce(jsonb_agg(jsonb_build_array(oid,pronamespace,proname,prokind,proargtypes::text,proowner,proacl::text) ORDER BY oid),'[]'::jsonb)::text,'UTF8')),'hex') FROM pg_catalog.pg_proc),
        'defaultPrivilegesSha256',(SELECT encode(sha256(convert_to(coalesce(jsonb_agg(jsonb_build_array(oid,defaclrole,defaclnamespace,defaclobjtype,defaclacl::text) ORDER BY oid),'[]'::jsonb)::text,'UTF8')),'hex') FROM pg_catalog.pg_default_acl))`,
        true,
      );
    }
    function fileIdentity(path) {
      const stat = lstatSync(path, { bigint: true });
      check(stat.isFile() && !stat.isSymbolicLink());
      const fd = openSync(path, "r");
      try {
        const mount = /^mnt_id:\s+([0-9]+)$/m.exec(
          readFileSync("/proc/self/fdinfo/" + fd, "utf8"),
        );
        check(mount);
        return {
          inode: String(stat.ino),
          device: String(stat.dev),
          mountId: mount[1],
          mode: Number(stat.mode & 0o777n),
          userId: String(stat.uid),
          links: String(stat.nlink),
        };
      } finally {
        closeSync(fd);
      }
    }
    const physical = () => {
      if (creationScenario) {
        check(!existsSync(storageRoot));
        return [];
      }
      return walk(storageRoot).map((item) => ({
        ...item,
        ...fileIdentity(join(storageRoot, item.path)),
      }));
    };
    const snapshot = () => ({
      rows: rows(),
      journal: journal(),
      credentials: credentialState(),
      physical: physical(),
      ...(creationScenario ||
      metadataScenario ||
      uploadScenario ||
      appendScenario
        ? { grants: creationGrantState() }
        : {}),
    });
    function tool(command, extra = {}) {
      return run(
        cfg.dotnet,
        [
          join(
            cfg.publications.databaseTool.directory,
            "Braid.DatabaseTool.dll",
          ),
          ...command,
        ],
        {
          env: {
            ...roleEnvironment,
            BRAID_MIGRATION_CONNECTION_STRING: connections.braid_migrator,
            ...extra,
          },
          timeout: 30000,
        },
      );
    }
    function provision(actor, organization, project = null, role = null) {
      tool(["provision-development-identity"], {
        BRAID_DEV_ORGANIZATION_ID: organization,
        BRAID_DEV_ORGANIZATION_NAME: "Catalog browser owned fixture",
        BRAID_DEV_PRINCIPAL_ID: actor,
        ...(project
          ? { BRAID_DEV_PROJECT_ID: project, BRAID_DEV_PROJECT_ROLE: role }
          : {}),
      });
    }
    async function pgReady() {
      // First boot includes initdb and role provisioning on the real data volume.
      // This is fixture admission, not an application or worker execution budget.
      const end = Date.now() + budget(120000);
      while (true) {
        const result = run(
          cfg.docker,
          [
            "--host=unix://" + cfg.daemonSocket,
            "exec",
            "--env",
            "PGPASSWORD",
            pg.Id,
            "psql",
            "-X",
            "-qAt",
            "--host=127.0.0.1",
            "--username",
            "postgres",
            "--dbname",
            "braid",
            "--command",
            "SELECT 1",
          ],
          {
            env: {
              ...roleEnvironment,
              PGPASSWORD: secrets.get("POSTGRES_ADMIN"),
            },
            timeout: 2000,
            allowFailure: true,
          },
        );
        if (result.code === 0 && result.stdout === "1") return;
        check(Date.now() < end, "deadline");
        await pause(200);
      }
    }

    function processIdentity(pid) {
      check(
        Number.isSafeInteger(pid) && pid > 1 && existsSync("/proc/" + pid),
        "ownershipChanged",
      );
      const stat = readFileSync("/proc/" + pid + "/stat", "utf8"),
        fields = stat.slice(stat.lastIndexOf(") ") + 2).split(" ");
      check(/^[0-9]+$/.test(fields[19]));
      return {
        pid,
        startTicks: fields[19],
        command: readFileSync("/proc/" + pid + "/cmdline")
          .toString("utf8")
          .split("\0")
          .filter(Boolean),
      };
    }
    function ownedChild(host) {
      check(host && !host.closeResult, "ownershipChanged");
      const actual = processIdentity(host.child.pid);
      check(
        actual.startTicks === host.identity.startTicks &&
          equivalent(actual.command, host.identity.command) &&
          equivalent(actual.command, [
            cfg.dotnet,
            host.regressionWorker
              ? join(cfg.publications.worker.directory, "Braid.Worker.dll")
              : join(cfg.publications.server.directory, "Braid.Server.dll"),
          ]),
        "ownershipChanged",
      );
      return actual;
    }
    async function listenerAbsent(value) {
      await new Promise((fulfil, reject) => {
        const socket = net.connect(Number(new URL(value).port), "127.0.0.1");
        const timer = setTimeout(
          () => socket.destroy(new Failure("deadline")),
          budget(1000),
        );
        socket.once("connect", () => {
          socket.destroy();
          reject(new Failure("stateMismatch"));
        });
        socket.once("error", (error) =>
          error.code === "ECONNREFUSED" ? fulfil() : reject(error),
        );
        socket.once("close", () => clearTimeout(timer));
      });
    }
    async function request(
      host,
      path,
      {
        method = "GET",
        body = null,
        media = null,
        key = null,
        ifMatch = null,
        unsafe = false,
        timeout = 40000,
      } = {},
    ) {
      ownedChild(host);
      check(
        typeof path === "string" &&
          path.startsWith("/") &&
          !path.startsWith("//") &&
          new URL(path, host.origin).origin === host.origin,
      );
      const headers = { Cookie: host.cookies.join("; ") };
      if (unsafe) {
        check(host.token);
        headers.Origin = host.origin;
        headers["X-XSRF-TOKEN"] = host.token;
      }
      if (media) headers["Content-Type"] = media;
      if (key) headers["Idempotency-Key"] = guid(key);
      if (ifMatch !== null) {
        check(
          appendScenario &&
            host.kind === "mutation" &&
            host.metadataEdits &&
            method === "PATCH" &&
            typeof ifMatch === "string",
        );
        const target =
          /^\/api\/v1\/organizations\/([0-9a-f-]{36})\/projects\/([0-9a-f-]{36})\/mappings\/([0-9a-f-]{36})\/metadata$/.exec(
            path,
          );
        check(target);
        for (const id of target.slice(1)) guid(id);
        const prefix =
          '"metadata-v1:' + target[1] + ":" + target[2] + ":" + target[3] + ":";
        check(ifMatch.startsWith(prefix) && ifMatch.endsWith('"'));
        const version = ifMatch.slice(prefix.length, -1);
        check(
          /^[1-9][0-9]{0,18}$/.test(version) &&
            BigInt(version) <= 9223372036854775807n,
        );
        headers["If-Match"] = ifMatch;
      }
      if (body !== null) headers["Content-Length"] = String(body.length);
      return await new Promise((fulfil, reject) => {
        const req = http.request(
          new URL(path, host.origin),
          { method, headers, agent: false },
          (response) => {
            const parts = [];
            let count = 0;
            response.on("data", (chunk) => {
              count += chunk.length;
              if (count > 1048576) req.destroy(new Failure("stateMismatch"));
              else parts.push(chunk);
            });
            response.once("error", reject);
            response.once("end", () => {
              try {
                const bytes = Buffer.concat(parts),
                  parsed = bytes.length
                    ? JSON.parse(
                        new TextDecoder("utf-8", { fatal: true }).decode(bytes),
                      )
                    : null;
                for (const cookie of response.headers["set-cookie"] ?? []) {
                  const value = cookie.split(";", 1)[0];
                  host.cookies = [
                    ...host.cookies.filter(
                      (item) =>
                        item.split("=", 1)[0] !== value.split("=", 1)[0],
                    ),
                    value,
                  ];
                }
                state.observations.push({
                  kind: "http",
                  origin: host.origin,
                  method,
                  path:
                    uploadScenario || appendScenario
                      ? new URL(path, host.origin).pathname
                      : path,
                  status: response.statusCode,
                  bytes: bytes.length,
                  bodySha256:
                    path === "/api/v1/catalog/context" ? null : sha(bytes),
                });
                save();
                fulfil({
                  status: response.statusCode,
                  headers: response.headers,
                  body: parsed,
                });
              } catch {
                reject(new Failure("productFailure"));
              }
            });
          },
        );
        const timer = setTimeout(
          () => req.destroy(new Failure("deadline")),
          budget(timeout),
        );
        req.once("close", () => clearTimeout(timer));
        req.once("error", reject);
        req.end(body ?? undefined);
      });
    }
    async function captureContext(host, expected) {
      const reply = await request(host, "/api/v1/catalog/context", {
        timeout: 5000,
      });
      check(reply.status === expected, "productFailure");
      if (expected === 200) {
        check(
          reply.body.actorPrincipalId === host.actor &&
            reply.body.allowedOrganizationId === host.organization &&
            typeof reply.body.requestToken === "string" &&
            reply.body.requestToken.length > 0 &&
            host.cookies.length > 0,
          "productFailure",
        );
        host.token = reply.body.requestToken;
        const expectedFeatures = {
          readCatalog: true,
          createProjects: host.writer || host.creationOnly,
          createMappings: host.writer || host.uploadOnly,
          editMetadata: host.metadataEdits,
          uploadRevisions: host.writer || host.uploadOnly,
          readReports: host.reportMode !== null,
          chat: false,
          preview: false,
          manageMembership: false,
          deriveReports:
            host.reportMode !== null && host.reportMode !== "readOnlyNoWorker",
        };
        check(
          equivalent(reply.body.features, expectedFeatures),
          "productFailure",
        );
      }
      return reply.status;
    }
    async function startHost(
      kind,
      {
        profile = "viewer",
        actor = null,
        organization = ids.organization,
        writer = false,
        creationOnly = false,
        metadataEdits = false,
        uploadOnly = false,
        reportMode = null,
        regression = false,
        regressionWorker = false,
        builds = false,
        buildWorker = false,
      } = {},
    ) {
      const regressionReader = regressionReadProfile({
        regressionScenario,
        kind,
        writer,
        uploadOnly,
        metadataEdits,
        creationOnly,
        reportMode,
      });
      check(kind === "read" ? readHost === null : mutationHost === null);
      check(!builds || (buildScenario && kind === "read" && regression));
      check(!buildWorker || (buildScenario && regressionWorker));
      check(!regression || regressionReader);
      check(
        !regressionWorker ||
          (regressionScenario &&
            kind === "mutation" &&
            readHost?.regression &&
            !writer &&
            !uploadOnly &&
            !metadataEdits &&
            !creationOnly &&
            reportMode === null),
      );
      if (kind === "read")
        check(
          scenario.profiles.includes(profile) &&
            !writer &&
            mutationHost === null,
        );
      check(
        reportMode === null ||
          ((definition.kind === "reports" ||
            (metadataScenario && kind === "mutation")) &&
            ["full", "readOnlyNoWorker", "profileUnavailable"].includes(
              reportMode,
            )) ||
          ((appendScenario || buildScenario) &&
            kind === "mutation" &&
            reportMode === "full") ||
          (regression && regressionReader && reportMode === "full"),
      );
      check(
        !creationOnly || (creationScenario && !writer && reportMode === null),
      );
      if (creationScenario)
        check(creationOnly && !writer && reportMode === null);
      check(
        !metadataEdits ||
          (((metadataScenario && kind === "read") ||
            (appendScenario && kind === "mutation")) &&
            !writer &&
            !creationOnly &&
            reportMode === null),
      );
      if (metadataScenario && kind === "read")
        check(!writer && !creationOnly && reportMode === null);
      check(
        !uploadOnly ||
          ((uploadScenario || appendScenario) &&
            kind === "read" &&
            !writer &&
            !creationOnly &&
            !metadataEdits &&
            reportMode === null),
      );
      if ((uploadScenario || appendScenario) && kind === "read")
        check(
          !writer && !creationOnly && !metadataEdits && reportMode === null,
        );
      const address = kind === "read" ? cfg.readOrigin : cfg.mutationOrigin;
      await listenerAbsent(address);
      const actualActor =
        actor ?? ids[profile === "readerUnavailable" ? "viewer" : profile];
      guid(actualActor);
      const ownScratch = join(scratch, "host-" + state.children.length);
      mkdirSync(ownScratch, { mode: 0o700 });
      const keys = [
        "PATH",
        "DOTNET_ROOT",
        "LANG",
        "LC_ALL",
        "TZ",
        "DOCKER_CONFIG",
        "HOME",
      ];
      const environment = Object.fromEntries(
        keys
          .filter((key) => process.env[key] !== undefined)
          .map((key) => [key, process.env[key]]),
      );
      Object.assign(environment, {
        DOTNET_PROCESSOR_COUNT: "2",
        DOTNET_GCHeapHardLimit: "40000000",
        DOTNET_CLI_TELEMETRY_OPTOUT: "1",
        DOTNET_NOLOGO: "1",
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: "false",
        TMPDIR: ownScratch,
        DOTNET_CLI_HOME: ownScratch,
        XDG_DATA_HOME: ownScratch,
        DOCKER_HOST: "unix://" + cfg.daemonSocket,
        DOTNET_ENVIRONMENT: "Development",
        ASPNETCORE_ENVIRONMENT: "Development",
        BRAID_CATALOG_ENABLED: "true",
        BRAID_DEV_IDENTITY_ENABLED: "true",
        BRAID_DEV_PRINCIPAL_ID: actualActor,
        BRAID_DEV_ORGANIZATION_ID: organization,
        BRAID_DEV_BROWSER_ORIGIN: address,
        BRAID_DEV_LISTEN_ADDR: "127.0.0.1:" + new URL(address).port,
        BRAID_CATALOG_CONNECTION_STRING:
          profile === "readerUnavailable"
            ? connections.unavailable
            : connections.braid_catalog_reader,
        BRAID_PROJECT_CREATION_ENABLED: String(writer || creationOnly),
        BRAID_REVISION_INGESTION_ENABLED: String(writer || uploadOnly),
        BRAID_METADATA_EDITS_ENABLED: String(metadataEdits),
        BRAID_REVISION_REPORTS_ENABLED: "false",
        BRAID_REVISION_REPORT_DERIVATION_ENABLED: "false",
      });
      if (creationOnly) {
        environment.BRAID_POSTGRES_PROJECT_CREATION_CONNECTION_STRING =
          connections.braid_project_creator;
        delete environment.DOCKER_HOST;
        delete environment.DOCKER_CONFIG;
      }
      if (metadataScenario && kind === "read") {
        delete environment.DOCKER_HOST;
        delete environment.DOCKER_CONFIG;
        if (metadataEdits)
          environment.BRAID_POSTGRES_METADATA_CONNECTION_STRING =
            connections.braid_catalog_editor;
      }
      if (appendScenario && kind === "mutation" && metadataEdits) {
        environment.BRAID_POSTGRES_METADATA_CONNECTION_STRING =
          connections.braid_catalog_editor;
        delete environment.DOCKER_HOST;
        delete environment.DOCKER_CONFIG;
      }
      if (writer)
        Object.assign(environment, {
          BRAID_POSTGRES_PROJECT_CREATION_CONNECTION_STRING:
            connections.braid_project_creator,
          BRAID_POSTGRES_INGESTION_CONNECTION_STRING: connections.braid_ingest,
          BRAID_PRIVATE_STORAGE_ROOT: storageRoot,
          BRAID_FERRULE_WORKER_IMAGE: workerImage,
        });
      if (uploadOnly)
        Object.assign(environment, {
          BRAID_POSTGRES_INGESTION_CONNECTION_STRING: connections.braid_ingest,
          BRAID_PRIVATE_STORAGE_ROOT: storageRoot,
          BRAID_FERRULE_WORKER_IMAGE: workerImage,
        });
      if (regression)
        Object.assign(environment, {
          BRAID_REGRESSIONS_ENABLED: "true",
          BRAID_FERRULE_WORKER_IMAGE: workerImage,
          BRAID_REGRESSION_CASE_READER_CONNECTION_STRING:
            connections.braid_case_reader,
          BRAID_REGRESSION_CASE_WRITER_CONNECTION_STRING:
            connections.braid_case_writer,
          BRAID_REGRESSION_RUN_READER_CONNECTION_STRING:
            connections.braid_run_reader,
          BRAID_REGRESSION_RUN_WRITER_CONNECTION_STRING:
            connections.braid_run_writer,
        });
      if (builds)
        Object.assign(environment, {
          BRAID_BUILDS_ENABLED: "true",
          BRAID_PRIVATE_STORAGE_ROOT: storageRoot,
          BRAID_FERRULE_WORKER_IMAGE: workerImage,
          BRAID_BUILD_METADATA_CONNECTION_STRING:
            connections.braid_build_metadata_reader,
          BRAID_BUILD_CONTROL_CONNECTION_STRING: connections.braid_build_writer,
          BRAID_BUILD_ACCESS_CONNECTION_STRING: connections.braid_runtime,
          BRAID_BUILD_REPORT_CONNECTION_STRING: connections.braid_report_reader,
          BRAID_BUILD_CASE_READER_CONNECTION_STRING:
            connections.braid_case_reader,
          BRAID_BUILD_EVIDENCE_READER_CONNECTION_STRING:
            connections.braid_run_reader,
          BRAID_BUILD_RUN_READER_CONNECTION_STRING:
            connections.braid_build_run_reader,
          BRAID_BUILD_RUN_WRITER_CONNECTION_STRING:
            connections.braid_build_run_writer,
        });
      if (
        (uploadScenario || appendScenario) &&
        kind === "read" &&
        !uploadOnly
      ) {
        delete environment.DOCKER_HOST;
        delete environment.DOCKER_CONFIG;
      }
      if (reportMode !== null) {
        Object.assign(environment, {
          BRAID_REVISION_REPORTS_ENABLED: "true",
          BRAID_REVISION_REPORT_DERIVATION_ENABLED: String(
            reportMode !== "readOnlyNoWorker",
          ),
          BRAID_POSTGRES_REPORT_CONNECTION_STRING:
            connections.braid_report_reader,
          BRAID_PRIVATE_STORAGE_ROOT: storageRoot,
        });
        if (reportMode === "full")
          Object.assign(environment, {
            BRAID_POSTGRES_DERIVATION_CONNECTION_STRING:
              connections.braid_deriver,
            BRAID_FERRULE_WORKER_IMAGE: workerImage,
          });
      }
      if (regressionWorker) {
        for (const key of Object.keys(environment))
          if (key.startsWith("BRAID_")) delete environment[key];
        Object.assign(environment, {
          BRAID_PREVIEW_WRITER_CONNECTION_STRING:
            connections.braid_preview_writer,
          BRAID_PRIVATE_STORAGE_ROOT: storageRoot,
          BRAID_FERRULE_WORKER_IMAGE: workerImage,
          BRAID_DOCKER_EXECUTABLE: cfg.docker,
          BRAID_REGRESSION_WORKER_ENABLED: "true",
          BRAID_REGRESSION_RUN_WRITER_CONNECTION_STRING:
            connections.braid_run_writer,
          BRAID_REGRESSION_CASE_READER_CONNECTION_STRING:
            connections.braid_case_reader,
          BRAID_REGRESSION_ACCESS_CONNECTION_STRING: connections.braid_runtime,
          BRAID_REGRESSION_REPORT_READER_CONNECTION_STRING:
            connections.braid_report_reader,
        });
        if (buildWorker)
          Object.assign(environment, {
            BRAID_BUILD_WORKER_ENABLED: "true",
            BRAID_BUILD_DISPATCHER_CONNECTION_STRING:
              connections.braid_build_dispatcher,
            BRAID_BUILD_WRITER_CONNECTION_STRING:
              connections.braid_build_writer,
            BRAID_BUILD_ACCESS_CONNECTION_STRING: connections.braid_runtime,
            BRAID_BUILD_REPORT_READER_CONNECTION_STRING:
              connections.braid_report_reader,
            BRAID_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING:
              connections.braid_run_reader,
            BRAID_BUILD_CASE_READER_CONNECTION_STRING:
              connections.braid_case_reader,
          });
      }
      check(
        !Object.keys(environment).some((key) =>
          /MIGRATION|LOCAL_|POSTGRES_ADMIN/.test(key),
        ),
      );
      if (creationOnly)
        check(
          !Object.keys(environment).some((key) =>
            /INGESTION_CONNECTION|DERIVATION_CONNECTION|REPORT_CONNECTION|PRIVATE_STORAGE|WORKER_IMAGE|MAINTENANCE_CONNECTION/.test(
              key,
            ),
          ) &&
            environment.BRAID_PROJECT_CREATION_ENABLED === "true" &&
            environment.BRAID_REVISION_INGESTION_ENABLED === "false" &&
            environment.BRAID_POSTGRES_PROJECT_CREATION_CONNECTION_STRING ===
              connections.braid_project_creator,
        );
      if (metadataScenario && kind === "read")
        check(
          !Object.keys(environment).some((key) =>
            /PROJECT_CREATION_CONNECTION|INGESTION_CONNECTION|REPORT_CONNECTION|DERIVATION_CONNECTION|MAINTENANCE_CONNECTION|PRIVATE_STORAGE|WORKER_IMAGE|DOCKER_/.test(
              key,
            ),
          ) &&
            environment.BRAID_PROJECT_CREATION_ENABLED === "false" &&
            environment.BRAID_REVISION_INGESTION_ENABLED === "false" &&
            (metadataEdits
              ? environment.BRAID_POSTGRES_METADATA_CONNECTION_STRING ===
                connections.braid_catalog_editor
              : !Object.hasOwn(
                  environment,
                  "BRAID_POSTGRES_METADATA_CONNECTION_STRING",
                )),
        );
      if ((uploadScenario || appendScenario) && kind === "read")
        check(
          environment.BRAID_PROJECT_CREATION_ENABLED === "false" &&
            environment.BRAID_METADATA_EDITS_ENABLED === "false" &&
            environment.BRAID_REVISION_REPORTS_ENABLED === "false" &&
            environment.BRAID_REVISION_REPORT_DERIVATION_ENABLED === "false" &&
            !Object.keys(environment).some((key) =>
              /PROJECT_CREATION_CONNECTION|METADATA_CONNECTION|REPORT_CONNECTION|DERIVATION_CONNECTION|MAINTENANCE_CONNECTION/.test(
                key,
              ),
            ) &&
            (uploadOnly
              ? environment.BRAID_REVISION_INGESTION_ENABLED === "true" &&
                environment.BRAID_POSTGRES_INGESTION_CONNECTION_STRING ===
                  connections.braid_ingest &&
                environment.BRAID_PRIVATE_STORAGE_ROOT === storageRoot &&
                environment.BRAID_FERRULE_WORKER_IMAGE === workerImage
              : environment.BRAID_REVISION_INGESTION_ENABLED === "false" &&
                !Object.keys(environment).some((key) =>
                  /POSTGRES_|PRIVATE_STORAGE|WORKER_IMAGE|DOCKER_/.test(key),
                )),
        );
      if (
        !writer &&
        !creationOnly &&
        !metadataEdits &&
        !uploadOnly &&
        !regression &&
        !regressionWorker &&
        reportMode === null
      )
        check(
          !Object.keys(environment).some((key) =>
            /POSTGRES_|PRIVATE_STORAGE|WORKER_IMAGE/.test(key),
          ),
        );
      const end = Date.now() + budget(30000),
        product = cfg.publications.server.directory,
        assembly = regressionWorker
          ? join(cfg.publications.worker.directory, "Braid.Worker.dll")
          : join(product, "Braid.Server.dll"),
        child = spawn(cfg.dotnet, [assembly], {
          cwd: product,
          env: environment,
          stdio: ["ignore", "pipe", "pipe"],
        });
      const host = {
        kind,
        profile,
        actor: actualActor,
        organization,
        origin: address,
        writer,
        creationOnly,
        metadataEdits,
        uploadOnly,
        reportMode,
        regression,
        regressionWorker,
        builds,
        buildWorker,
        child,
        cookies: [],
        token: null,
        closeResult: null,
        outputBytes: 0,
        overflow: false,
        spawnFailure: false,
        diagnostics: {
          stdout: hostFailureSummary(),
          stderr: hostFailureSummary(),
        },
      };
      const admission = {
        stage: "spawn",
        spawnConfirmed: false,
        procReadSucceeded: false,
        startTicksCaptured: false,
        argvArityTwo: false,
        expectedCommandMatch: false,
      };
      let resolveSpawn, rejectSpawn;
      const spawned = new Promise((fulfil, reject) => {
        resolveSpawn = fulfil;
        rejectSpawn = reject;
      });
      void spawned.catch(() => undefined);
      child.once("spawn", () => {
        admission.spawnConfirmed = true;
        resolveSpawn();
      });
      host.closed = new Promise((fulfil) =>
        child.once("close", (code, signal) => {
          host.closeResult = { code, signal };
          rejectSpawn(new Failure("productFailure"));
          fulfil(host.closeResult);
        }),
      );
      child.once("error", () => {
        host.spawnFailure = true;
        rejectSpawn(new Failure("productFailure"));
      });
      for (const [name, stream] of [
        ["stdout", child.stdout],
        ["stderr", child.stderr],
      ])
        stream.on("data", (bytes) => {
          host.outputBytes += bytes.length;
          if (host.outputBytes > 2097152) host.overflow = true;
          host.diagnostics[name].write(bytes);
        });
      // Do not retain framework output, cookies or tokens, even in the private ledger.
      if (kind === "read") readHost = host;
      else mutationHost = host;
      try {
        let spawnTimer;
        try {
          await Promise.race([
            spawned,
            new Promise((_, reject) => {
              spawnTimer = setTimeout(
                () => reject(new Failure("deadline")),
                budget(Math.max(1, end - Date.now())),
              );
            }),
          ]);
        } finally {
          clearTimeout(spawnTimer);
        }
        check(Date.now() < end, "deadline");
        check(
          admission.spawnConfirmed && !host.closeResult && !host.spawnFailure,
          "productFailure",
        );
        admission.stage = "initialIdentity";
        check(child.pid, "productFailure");
        host.identity = processIdentity(child.pid);
        admission.procReadSucceeded = true;
        admission.startTicksCaptured = true;
        admission.stage = "initialCommand";
        admission.argvArityTwo = host.identity.command.length === 2;
        admission.expectedCommandMatch = equivalent(host.identity.command, [
          cfg.dotnet,
          assembly,
        ]);
        check(admission.expectedCommandMatch, "ownershipChanged");
        host.ledger = {
          ...host.identity,
          kind,
          profile,
          origin: address,
          source: cfg.source.commit,
          processorCount: 2,
          gcHeapLimitBytes: 1073741824,
          metadataOnly:
            !writer &&
            !creationOnly &&
            !metadataEdits &&
            !uploadOnly &&
            !regression &&
            !regressionWorker &&
            reportMode === null,
          ...(metadataScenario || appendScenario ? { metadataEdits } : {}),
          ...(uploadScenario || appendScenario ? { uploadOnly } : {}),
          ...(creationScenario ? { creationOnly } : {}),
          reportMode,
          ...(regressionScenario ? { regression, regressionWorker } : {}),
          ...(buildScenario ? { builds, buildWorker } : {}),
          terminal: null,
        };
        state.children.push(host.ledger);
        save();
        if (!regressionWorker) {
          admission.stage = "health";
          while (true) {
            check(
              !host.closeResult && !host.spawnFailure && !host.overflow,
              "productFailure",
            );
            try {
              const health = await request(host, "/api/v1/health", {
                timeout: 1000,
              });
              check(
                health.status === 200 && health.body.status === "ok",
                "productFailure",
              );
              break;
            } catch (error) {
              check(
                error.code === "ECONNREFUSED" && Date.now() < end,
                error.category ?? "productFailure",
              );
              await pause(100);
            }
          }
          admission.stage = "context";
          const expected =
            profile === "readerUnavailable"
              ? 500
              : profile === "viewer" && orgRevoked
                ? 404
                : 200;
          host.contextStatus = await captureContext(host, expected);
        }
        admission.stage = "mappedIdentity";
        let mapped = readFileSync("/proc/" + child.pid + "/maps", "utf8");
        while (
          regressionWorker &&
          !mapped.split("\n").some((line) => line.endsWith(" " + assembly))
        ) {
          check(
            Date.now() < end &&
              !host.closeResult &&
              !host.spawnFailure &&
              !host.overflow,
            "productFailure",
          );
          await pause(100);
          mapped = readFileSync("/proc/" + child.pid + "/maps", "utf8");
        }
        ownedChild(host);
        check(
          mapped.split("\n").some((line) => line.endsWith(" " + assembly)),
          "ownershipChanged",
        );
        check(
          sha(readFileSync(assembly)) ===
            (regressionWorker
              ? cfg.publications.worker.hashes["Braid.Worker.dll"]
              : cfg.publications.server.hashes["Braid.Server.dll"]),
        );
        if (regressionWorker)
          host.ledger.mappedWorkerSha256 = sha(readFileSync(assembly));
        else
          host.ledger.mappedServerSha256 =
            cfg.publications.server.hashes["Braid.Server.dll"];
        save();
        return host;
      } catch (original) {
        try {
          state.observations.push({
            kind: "host-start-admission-failure",
            hostKind: kind,
            metadataEdits,
            ...admission,
            pidPresent: Number.isSafeInteger(child.pid) && child.pid > 1,
            childClosed: host.closeResult !== null,
          });
          save();
        } catch {
          // Diagnostic persistence must not replace the original startup failure.
        }
        try {
          await stopHost(kind);
        } catch (cleanup) {
          state.cleanupFailures.push({
            phase: "failed-host-start",
            category: cleanup.category ?? "cleanupFailure",
          });
          save();
        }
        throw original;
      }
    }
    async function waitClosed(host, milliseconds) {
      let timer;
      try {
        return await Promise.race([
          host.closed,
          new Promise((fulfil) => {
            timer = setTimeout(() => fulfil(null), budget(milliseconds));
          }),
        ]);
      } finally {
        clearTimeout(timer);
      }
    }
    async function stopHost(kind) {
      const host = kind === "read" ? readHost : mutationHost;
      if (!host) return false;
      let forced = false,
        result = host.closeResult;
      const unexpectedExit = result !== null;
      if (!result) {
        ownedChild(host);
        check(host.child.kill("SIGTERM"), "ownershipChanged");
        result = await waitClosed(host, host.buildWorker ? 240000 : 10000);
        if (!result) {
          forced = true;
          ownedChild(host);
          check(host.child.kill("SIGKILL"), "ownershipChanged");
          result = await waitClosed(host, 5000);
        }
      }
      check(result, "cleanupFailure");
      if (kind === "read") readHost = null;
      else mutationHost = null;
      host.cookies = [];
      host.token = null;
      if (host.ledger) {
        host.ledger.terminal = {
          ...result,
          forced,
          outputBytes: host.outputBytes,
          overflow: host.overflow,
          diagnostics: {
            stdout: host.diagnostics.stdout.snapshot(),
            stderr: host.diagnostics.stderr.snapshot(),
          },
        };
        save();
      }
      check(!existsSync("/proc/" + host.child.pid), "ownershipChanged");
      await listenerAbsent(host.origin);
      check(
        result.code === 0 &&
          !forced &&
          !unexpectedExit &&
          !host.overflow &&
          !host.spawnFailure,
        "productFailure",
      );
      return true;
    }
    function projectPath(scope) {
      return (
        "/api/v1/organizations/" +
        guid(scope.organizationId) +
        "/projects/" +
        guid(scope.projectId)
      );
    }
    function mappingPath(scope) {
      return projectPath(scope) + "/mappings/" + guid(scope.mappingId);
    }
    async function createProject(host, name) {
      const key = randomUUID(),
        body = Buffer.from(
          JSON.stringify({
            name,
            description: "Actual browser fixture HTTP creation",
          }),
        );
      const path = "/api/v1/organizations/" + host.organization + "/projects";
      const reply = await request(host, path, {
        method: "POST",
        body,
        media: "application/vnd.braid.project-create.v1+json",
        key,
        unsafe: true,
      });
      check(
        reply.status === 201 &&
          reply.body.outcome === "created" &&
          reply.body.receipt.creatorPrincipalId === host.actor &&
          reply.body.receipt.initialMembershipRole === "administrator",
        "productFailure",
      );
      const scope = reply.body.receipt.project;
      check(
        scope.organizationId === host.organization &&
          reply.body.projectUrl === projectPath(scope),
      );
      const replay = await request(host, path, {
        method: "POST",
        body,
        media: "application/vnd.braid.project-create.v1+json",
        key,
        unsafe: true,
      });
      check(
        replay.status === 200 &&
          replay.body.outcome === "replayed" &&
          equivalent(replay.body.receipt, reply.body.receipt),
        "productFailure",
      );
      return scope;
    }
    function validateRevision(metadata, fixture, scope) {
      check(
        equivalent(metadata.scope, scope) &&
          metadata.nativeDigest?.algorithm === "sha256" &&
          metadata.nativeDigest.value === fixture.sha256 &&
          metadata.nativeByteCount === fixture.bytes.length &&
          metadata.backendId === "ferrule" &&
          metadata.logicalDocument === "entry.native.json" &&
          /^[1-9][0-9]*$/.test(metadata.sequence) &&
          ["valid", "decodedInvalid"].includes(metadata.admission),
        "productFailure",
      );
      for (const value of Object.values(metadata.scope)) guid(value);
    }
    async function upload(
      host,
      project,
      fixture,
      appendTo = null,
      ordinal = 0,
    ) {
      const key = randomUUID();
      let path;
      if (appendTo) {
        const current = await request(host, mappingPath(appendTo));
        check(current.status === 200, "productFailure");
        path =
          mappingPath(appendTo) +
          "/revisions?head=equal&expectedHeadRevisionId=" +
          guid(current.body.head.scope.revisionId) +
          "&originalFilename=browser-" +
          ordinal +
          ".native.json";
      } else
        path =
          projectPath(project) +
          "/mappings?name=" +
          encodeURIComponent("Browser mapping " + ordinal) +
          "&backendId=ferrule&description=Actual%20worker%20fixture&originalFilename=browser-" +
          ordinal +
          ".native.json";
      const send = () =>
        request(host, path, {
          method: "POST",
          body: fixture.bytes,
          media: "application/octet-stream",
          key,
          unsafe: true,
        });
      let reply = await send();
      const end = Date.now() + budget(30000);
      let receiptLocation = null;
      while (reply.status === 202) {
        const location = reply.headers.location;
        check(
          typeof location === "string" &&
            location.startsWith(
              projectPath(project) + "/revision-ingestions/",
            ) &&
            !location.includes("?") &&
            !location.includes("#"),
          "productFailure",
        );
        if (receiptLocation !== null) check(receiptLocation === location);
        receiptLocation = location;
        check(Date.now() < end, "deadline");
        await pause(150);
        reply = await request(host, location, {
          timeout: Math.min(5000, end - Date.now()),
        });
      }
      check(
        reply.status === 200 &&
          ["committedValid", "committedDecodedInvalid"].includes(
            reply.body.outcome,
          ) &&
          reply.body.receipt?.phase === "committed",
        "productFailure",
      );
      const scope = reply.body.receipt.futureRevision;
      check(
        scope.organizationId === project.organizationId &&
          scope.projectId === project.projectId &&
          (!appendTo || scope.mappingId === appendTo.mappingId),
      );
      for (const value of Object.values(scope)) guid(value);
      const revisionUrl = mappingPath(scope) + "/revisions/" + scope.revisionId;
      check(reply.body.committedRevisionUrl === revisionUrl);
      const native = jsonSql(
        "SELECT row_to_json(r) FROM braid.mapping_revisions r WHERE organization_id='" +
          guid(scope.organizationId) +
          "' AND project_id='" +
          guid(scope.projectId) +
          "' AND mapping_id='" +
          guid(scope.mappingId) +
          "' AND id='" +
          guid(scope.revisionId) +
          "'",
      );
      check(
        native.native_digest === fixture.sha256 &&
          String(native.native_byte_count) === String(fixture.bytes.length),
        "productFailure",
      );
      const file = join(
        storageRoot,
        "objects/native",
        ...[
          scope.organizationId,
          scope.projectId,
          scope.mappingId,
          scope.revisionId,
          guid(native.native_object_id),
        ].map((value) => value.replaceAll("-", "")),
      );
      check(
        sha(readFileSync(file + ".blob")) === fixture.sha256,
        "productFailure",
      );
      const beforeReplay = { rows: rows(), physical: physical() },
        replay = await send();
      check(
        replay.status === 200 &&
          equivalent(replay.body.receipt, reply.body.receipt) &&
          equivalent(beforeReplay, { rows: rows(), physical: physical() }),
        "productFailure",
      );
      const metadata = await request(host, revisionUrl);
      check(
        metadata.status === 200 &&
          metadata.headers["cache-control"] === "no-store",
        "productFailure",
      );
      validateRevision(metadata.body, fixture, scope);
      return { scope, metadata: metadata.body, revisionUrl };
    }
    async function page(host, path, field) {
      const result = [];
      let cursor = null;
      const seen = new Set();
      do {
        const reply = await request(
          host,
          path +
            "?limit=10" +
            (cursor ? "&cursor=" + encodeURIComponent(cursor) : ""),
        );
        check(
          reply.status === 200 &&
            Array.isArray(reply.body[field]) &&
            reply.body[field].length <= 10,
          "productFailure",
        );
        result.push(...reply.body[field]);
        cursor = reply.body.nextCursor ?? null;
        if (cursor !== null) {
          check(
            typeof cursor === "string" &&
              cursor.length > 0 &&
              cursor.length <= 8192 &&
              !seen.has(cursor),
          );
          seen.add(cursor);
        }
        check(result.length <= 32 && seen.size <= 4);
      } while (cursor !== null);
      return result;
    }
    async function initialize() {
      before = inventories();
      state.preexisting = before;
      save();
      const worker = creationScenario
          ? null
          : jsonDocker(["image", "inspect", workerImage])[0],
        postgres = jsonDocker(["image", "inspect", postgresImage])[0];
      check(
        (creationScenario ||
          (worker.Id === workerImage &&
            worker.Os === "linux" &&
            worker.Architecture === "amd64")) &&
          postgres.RepoDigests?.includes(postgresImage) &&
          postgres.Os === "linux" &&
          postgres.Architecture === "amd64",
      );
      await openEvents();
      const netAttempt = attempt("network", resourceName + "-network");
      createOnFreeSubnet((subnet) => {
        const created = run(
          cfg.docker,
          [
            "--host=unix://" + cfg.daemonSocket,
            "network",
            "create",
            "--subnet",
            subnet,
            ...labelArgs,
            netAttempt.name,
          ],
          { allowFailure: true, keepError: true },
        );
        check(created.code !== -1, "deadline");
        check(
          created.code === 0 || isPoolOverlap(created.stderr),
          "productFailure",
        );
        netAttempt.id = created.code === 0 ? created.stdout : null;
        return created.code === 0;
      });
      network = jsonDocker(["network", "inspect", netAttempt.id])[0];
      check(
        labelsMatch(network) &&
          network.Driver === "bridge" &&
          !network.Internal,
      );
      save();
      const volAttempt = attempt("volume", resourceName + "-data");
      docker(["volume", "create", ...labelArgs, volAttempt.name]);
      volume = jsonDocker(["volume", "inspect", volAttempt.name])[0];
      check(
        labelsMatch(volume) &&
          volume.Name === volAttempt.name &&
          volume.Driver === "local",
      );
      save();
      const pgAttempt = attempt("container", resourceName + "-postgres");
      // Choose one ephemeral loopback port once, then explicitly bind it so the same
      // container's normal stop/start cannot allocate a different port. No retry.
      const reservation = net.createServer();
      await new Promise((fulfil, reject) => {
        reservation.once("error", reject);
        reservation.listen(0, "127.0.0.1", fulfil);
      });
      const requestedPgPort = reservation.address().port;
      await new Promise((fulfil, reject) =>
        reservation.close((error) => (error ? reject(error) : fulfil())),
      );
      const envKeys = [
        "POSTGRES_DB",
        "POSTGRES_USER",
        "POSTGRES_PASSWORD",
        "POSTGRES_INITDB_ARGS",
        ...rolePairs.map((pair) => "BRAID_LOCAL_" + pair[1] + "_PASSWORD"),
      ];
      pgAttempt.id = docker(
        [
          "create",
          "--name",
          pgAttempt.name,
          "--network",
          network.Id,
          "--memory",
          "512m",
          "--memory-swap",
          "512m",
          "--cpus",
          "1",
          "--pids-limit",
          "128",
          "--publish",
          "127.0.0.1:" + requestedPgPort + ":5432",
          ...labelArgs,
          ...envKeys.flatMap((key) => ["--env", key]),
          "--mount",
          "type=volume,source=" +
            volume.Name +
            ",target=/var/lib/postgresql/data",
          postgresImage,
        ],
        { env: roleEnvironment },
      );
      save();
      docker([
        "cp",
        join(workspace, "deploy/local/postgres-init.sh"),
        pgAttempt.id + ":/docker-entrypoint-initdb.d/01-braid-roles.sh",
      ]);
      docker(["start", pgAttempt.id]);
      pg = jsonDocker(["inspect", pgAttempt.id])[0];
      check(pg.Image === postgres.Id);
      inspectOwned();
      pg.hostPort = pg.NetworkSettings.Ports["5432/tcp"][0].HostPort;
      check(pg.hostPort === String(requestedPgPort));
      state.postgres = {
        id: pg.Id,
        name: pg.Name,
        image: pg.Image,
        host: "127.0.0.1",
        hostPort: pg.hostPort,
        networkId: network.Id,
        volumeName: volume.Name,
        bindings: pg.HostConfig.PortBindings,
        assignedPorts: pg.NetworkSettings.Ports,
      };
      save();
      for (const [role, key] of rolePairs)
        connections[role] =
          "Host=127.0.0.1;Port=" +
          pg.hostPort +
          ";Database=braid;Username=" +
          role +
          ";Password=" +
          secrets.get(key) +
          ";Maximum Pool Size=1;Timeout=15;Command Timeout=15";
      connections.unavailable = connections.braid_catalog_reader.replace(
        ";Database=braid;",
        ";Database=braid_missing_" + nonce + ";",
      );
      await pgReady();
      check(
        sql("SELECT current_setting('server_version')", true).startsWith(
          "16.15 ",
        ),
      );
      check(
        sql(
          "SELECT count(*) FROM pg_database WHERE datname='braid_missing_" +
            nonce +
            "'",
          true,
        ) === "0",
      );
      const roles = jsonSql(
        "SELECT json_agg(json_build_object('name',rolname,'login',rolcanlogin,'super',rolsuper,'createDb',rolcreatedb,'createRole',rolcreaterole,'inherit',rolinherit,'replication',rolreplication,'bypass',rolbypassrls,'hasPassword',rolpassword IS NOT NULL) ORDER BY rolname) FROM pg_authid WHERE rolname LIKE 'braid_%'",
        true,
      );
      check(matchesBrowserFixtureRoles(roles));
      check(
        sql(
          "SELECT bool_and(auth_method='scram-sha-256') FROM pg_hba_file_rules WHERE type IN ('host','hostssl','hostnossl')",
          true,
        ) === "t",
      );
      tool(["migrate"]);
      const migrated = journal();
      check(matchesBrowserFixtureMigrationJournal(migrated, sourceHashes));
      tool(["migrate"]);
      check(equivalent(journal(), migrated));
      manifest = await scenario.setup(migrated);
      const finalRows = rows();
      check(
        Array.isArray(finalRows.project_payload_read_grants) &&
          finalRows.project_payload_read_grants.length === 0,
      );
      check(
        Array.isArray(finalRows.server_sessions) &&
          finalRows.server_sessions.length === 0,
      );
      const bytes = Buffer.from(JSON.stringify(manifest) + "\n");
      check(bytes.length <= 65536);
      state.initialSnapshot = {
        rowsSha256: sha(Buffer.from(JSON.stringify(finalRows))),
        physical: physical(),
        journal: migrated,
        credentialsSha256: sha(Buffer.from(JSON.stringify(credentialState()))),
      };
      atomic(cfg.manifestPath, bytes);
      state.manifestSha256 = sha(bytes);
      save();
      verifyInputBodies();
      emit({
        schemaVersion: `braid.${definition.kind}-browser-ready.v1`,
        fixtureId,
        manifestSha256: sha(bytes),
        profile: buildScenario
          ? manifest.initialReadProfile
          : creationScenario
            ? "creator"
            : metadataScenario ||
                uploadScenario ||
                appendScenario ||
                regressionScenario
              ? "publisher"
              : "viewer",
      });
    }

    function initializeStorage() {
      check(!creationScenario);
      run(
        cfg.dotnet,
        [
          join(cfg.publications.storageTool.directory, "Braid.StorageTool.dll"),
          "initialize-v2",
          storageRoot,
          cfg.publications.server.directory,
          workspace,
        ],
        { timeout: 30000 },
      );
      const marker = readFileSync(
        join(storageRoot, ".braid-private-storage"),
        "ascii",
      );
      check(
        /^braid-private-storage-v2\n[0-9a-f]{32}\n[0-9a-f]{32}\n$/.test(marker),
      );
    }
    function captureMembership(primary) {
      check(authorityProject === null);
      authorityProject = primary;
      membership = jsonSql(
        "SELECT row_to_json(m) FROM braid.project_memberships m WHERE organization_id='" +
          ids.organization +
          "' AND project_id='" +
          primary.projectId +
          "' AND principal_id='" +
          ids.viewer +
          "'",
      );
      organizationMembership = jsonSql(
        "SELECT row_to_json(m) FROM braid.organization_memberships m WHERE organization_id='" +
          ids.organization +
          "' AND principal_id='" +
          ids.viewer +
          "'",
      );
      check(
        membership.active === true &&
          membership.role === "viewer" &&
          organizationMembership.active === true,
      );
    }

    // A/B revocation uses only these two fixed, captured-identity owner writes.
    // The separate C membership seam below also accepts only captured creation identities.
    // General sql() above always opens READ ONLY and rejects any other top-level verb.
    function revoke(kind) {
      inspectOwned();
      check(!mutationHost);
      let query, expected;
      if (kind === "project") {
        check(
          !projectRevoked &&
            !orgRevoked &&
            membership.active &&
            /^[1-9][0-9]*$/.test(String(membership.version)),
        );
        expected = {
          ...membership,
          active: false,
          version: (BigInt(membership.version) + 1n).toString(),
        };
        query =
          "UPDATE braid.project_memberships SET active=false, version=version+1 WHERE organization_id='" +
          ids.organization +
          "' AND project_id='" +
          guid(authorityProject.projectId) +
          "' AND principal_id='" +
          ids.viewer +
          "' AND role='viewer' AND active=true AND version=" +
          BigInt(membership.version) +
          " RETURNING row_to_json(project_memberships)";
      } else {
        check(
          kind === "organization" &&
            !orgRevoked &&
            organizationMembership.active,
        );
        expected = { ...organizationMembership, active: false };
        query =
          "UPDATE braid.organization_memberships SET active=false WHERE organization_id='" +
          ids.organization +
          "' AND principal_id='" +
          ids.viewer +
          "' AND active=true RETURNING row_to_json(organization_memberships)";
      }
      const beforeRows = rows();
      const result = run(
        cfg.docker,
        [
          "--host=unix://" + cfg.daemonSocket,
          "exec",
          "--interactive",
          "--env",
          "PGPASSWORD",
          pg.Id,
          "psql",
          "-X",
          "-qAt",
          "--host=127.0.0.1",
          "--username",
          "braid_migrator",
          "--dbname",
          "braid",
          "--set=ON_ERROR_STOP=1",
        ],
        {
          env: { ...roleEnvironment, PGPASSWORD: secrets.get("MIGRATOR") },
          timeout: 10000,
          input:
            "BEGIN;\nSET LOCAL lock_timeout='1s';\nSET LOCAL statement_timeout='5s';\n" +
            query +
            ";\nCOMMIT;\n",
        },
      );
      const lines = result.stdout.split("\n").filter(Boolean);
      check(lines.length === 1, "productFailure");
      const actual = JSON.parse(lines[0]);
      if (kind === "project") {
        check(
          equivalent({ ...actual, version: String(actual.version) }, expected),
          "productFailure",
        );
        projectRevoked = true;
      } else {
        check(equivalent(actual, expected), "productFailure");
        orgRevoked = true;
      }
      const afterRows = rows(),
        table =
          kind === "project"
            ? "project_memberships"
            : "organization_memberships";
      const predicate = (row) =>
        row.organization_id === ids.organization &&
        row.principal_id === ids.viewer &&
        (kind !== "project" || row.project_id === authorityProject.projectId);
      check(
        afterRows[table].filter(predicate).length === 1 &&
          equivalent(afterRows[table].filter(predicate)[0], actual),
        "productFailure",
      );
      check(
        equivalent(
          beforeRows[table].filter((row) => !predicate(row)),
          afterRows[table].filter((row) => !predicate(row)),
        ) &&
          equivalent(
            { ...beforeRows, [table]: [] },
            { ...afterRows, [table]: [] },
          ),
        "productFailure",
      );
      state.observations.push({
        kind: kind + "-revoked",
        organizationId: ids.organization,
        principalId: ids.viewer,
        projectId: kind === "project" ? authorityProject.projectId : null,
      });
      save();
    }
    const capturedCreations = new Map();
    function captureCreatedFact(projectId) {
      check(
        creationScenario &&
          !capturedCreations.has(guid(projectId)) &&
          !mutationHost,
      );
      const current = rows();
      const fact = current.project_creations.filter(
        (row) =>
          row.organization_id === ids.organization &&
          row.project_id === projectId &&
          row.original_actor_id === ids.viewer,
      );
      const project = current.projects.filter(
        (row) =>
          row.organization_id === ids.organization && row.id === projectId,
      );
      const member = current.project_memberships.filter(
        (row) =>
          row.organization_id === ids.organization &&
          row.project_id === projectId,
      );
      check(fact.length === 1 && project.length === 1 && member.length === 1);
      const f = fact[0],
        p = project[0],
        m = member[0];
      check(
        p.creator_principal_id === ids.viewer &&
          p.name === f.name &&
          p.description === f.description &&
          String(p.version) === "1" &&
          p.created_at === p.updated_at &&
          p.created_at === f.created_at &&
          String(f.initial_project_version) === "1" &&
          f.initial_membership_role === "administrator" &&
          f.initial_membership_active === true &&
          String(f.initial_membership_version) === "1" &&
          m.principal_id === ids.viewer &&
          m.role === "administrator" &&
          m.active === true &&
          String(m.version) === "1" &&
          f.fingerprint_version === 1 &&
          /^[a-f0-9]{64}$/.test(f.fingerprint),
      );
      capturedCreations.set(projectId, f);
      return { organizationId: ids.organization, projectId };
    }
    function changeCreatedMembership(projectId, mode) {
      check(
        creationScenario &&
          !mutationHost &&
          ["viewer", "deleted"].includes(mode),
      );
      guid(projectId);
      inspectOwned();
      const captured = capturedCreations.get(projectId);
      check(captured);
      const old = rows();
      check(old.project_creations.some((row) => equivalent(row, captured)));
      const mine = (row) =>
        row.organization_id === ids.organization &&
        row.project_id === projectId &&
        row.principal_id === ids.viewer;
      const members = old.project_memberships.filter(mine);
      check(
        members.length === 1 &&
          members[0].active === true &&
          ["administrator", "viewer"].includes(members[0].role),
      );
      const member = members[0];
      check(/^[1-9][0-9]*$/.test(String(member.version)));
      if (mode === "viewer") check(member.role === "administrator");
      const where =
        " WHERE organization_id='" +
        ids.organization +
        "' AND project_id='" +
        projectId +
        "' AND principal_id='" +
        ids.viewer +
        "' AND role='" +
        member.role +
        "' AND active=true AND version=" +
        BigInt(member.version);
      const query =
        (mode === "viewer"
          ? "UPDATE braid.project_memberships SET role='viewer',version=version+1"
          : "DELETE FROM braid.project_memberships") +
        where +
        " RETURNING row_to_json(project_memberships)";
      const result = run(
        cfg.docker,
        [
          "--host=unix://" + cfg.daemonSocket,
          "exec",
          "--interactive",
          "--env",
          "PGPASSWORD",
          pg.Id,
          "psql",
          "-X",
          "-qAt",
          "--host=127.0.0.1",
          "--username",
          "braid_migrator",
          "--dbname",
          "braid",
          "--set=ON_ERROR_STOP=1",
        ],
        {
          env: { ...roleEnvironment, PGPASSWORD: secrets.get("MIGRATOR") },
          timeout: 10000,
          input:
            "BEGIN;\nSET LOCAL lock_timeout='1s';\nSET LOCAL statement_timeout='5s';\n" +
            query +
            ";\nCOMMIT;\n",
        },
      );
      const lines = result.stdout.split("\n").filter(Boolean);
      check(lines.length === 1, "productFailure");
      const actual = JSON.parse(lines[0]),
        after = rows();
      const expected =
        mode === "viewer"
          ? {
              ...member,
              role: "viewer",
              version: String(BigInt(member.version) + 1n),
            }
          : { ...member, version: String(member.version) };
      check(
        equivalent({ ...actual, version: String(actual.version) }, expected),
      );
      const remaining = after.project_memberships.filter(mine);
      check(
        mode === "viewer"
          ? remaining.length === 1 && equivalent(remaining[0], actual)
          : remaining.length === 0,
      );
      check(
        equivalent(
          old.project_memberships.filter((row) => !mine(row)),
          after.project_memberships.filter((row) => !mine(row)),
        ) &&
          equivalent(
            { ...old, project_memberships: [] },
            { ...after, project_memberships: [] },
          ),
      );
      state.observations.push({
        kind: "captured-creation-membership-change",
        projectId,
        mode,
        immutableCreationFactsAndAllOtherRowsPreserved: true,
      });
      save();
      return { organizationId: ids.organization, projectId };
    }
    let metadataPublisher = null;
    function captureMetadataPublisher(projectId) {
      check(
        (metadataScenario ||
          uploadScenario ||
          appendScenario ||
          regressionScenario) &&
          metadataPublisher === null &&
          !mutationHost,
      );
      guid(projectId);
      const current = rows(),
        project = current.projects.filter(
          (row) =>
            row.organization_id === ids.organization &&
            row.id === projectId &&
            row.creator_principal_id === ids.administrator,
        );
      const members = current.project_memberships.filter(
        (row) =>
          row.organization_id === ids.organization &&
          row.project_id === projectId &&
          row.principal_id === ids.publisher,
      );
      check(
        project.length === 1 &&
          members.length === 1 &&
          members[0].role === "publisher" &&
          members[0].active === true &&
          String(members[0].version) === "1",
      );
      metadataPublisher = { projectId, member: members[0] };
    }
    function changeMetadataPublisherRole(role) {
      check(
        (metadataScenario ||
          uploadScenario ||
          appendScenario ||
          regressionScenario) &&
          metadataPublisher !== null &&
          !mutationHost &&
          ["publisher", "viewer"].includes(role),
      );
      inspectOwned();
      const bound = metadataPublisher,
        old = rows();
      const mine = (row) =>
        row.organization_id === ids.organization &&
        row.project_id === bound.projectId &&
        row.principal_id === ids.publisher;
      const current = old.project_memberships.filter(mine);
      check(
        current.length === 1 &&
          equivalent(current[0], bound.member) &&
          bound.member.active === true &&
          bound.member.role !== role,
      );
      const version = BigInt(bound.member.version);
      check(version > 0n && version < 9223372036854775807n);
      const query =
        "UPDATE braid.project_memberships SET role='" +
        role +
        "',version=version+1 WHERE organization_id='" +
        ids.organization +
        "' AND project_id='" +
        bound.projectId +
        "' AND principal_id='" +
        ids.publisher +
        "' AND role='" +
        bound.member.role +
        "' AND active=true AND version=" +
        version +
        " RETURNING row_to_json(project_memberships)";
      const reply = run(
        cfg.docker,
        [
          "--host=unix://" + cfg.daemonSocket,
          "exec",
          "--interactive",
          "--env",
          "PGPASSWORD",
          pg.Id,
          "psql",
          "-X",
          "-qAt",
          "--host=127.0.0.1",
          "--username",
          "braid_migrator",
          "--dbname",
          "braid",
          "--set=ON_ERROR_STOP=1",
        ],
        {
          env: { ...roleEnvironment, PGPASSWORD: secrets.get("MIGRATOR") },
          timeout: 10000,
          input:
            "BEGIN;\nSET LOCAL lock_timeout='1s';\nSET LOCAL statement_timeout='5s';\n" +
            query +
            ";\nCOMMIT;\n",
        },
      );
      const lines = reply.stdout.split("\n").filter(Boolean);
      check(lines.length === 1, "productFailure");
      const actual = JSON.parse(lines[0]),
        after = rows();
      check(
        equivalent(
          { ...actual, version: String(actual.version) },
          { ...bound.member, role, version: String(version + 1n) },
        ) &&
          after.project_memberships.filter(mine).length === 1 &&
          equivalent(after.project_memberships.filter(mine)[0], actual) &&
          equivalent(
            old.project_memberships.filter((row) => !mine(row)),
            after.project_memberships.filter((row) => !mine(row)),
          ) &&
          equivalent(
            { ...old, project_memberships: [] },
            { ...after, project_memberships: [] },
          ),
      );
      metadataPublisher = { projectId: bound.projectId, member: actual };
      state.observations.push({
        kind: "captured-metadata-publisher-role",
        role,
        allUnrelatedRowsPreserved: true,
      });
      save();
    }
    function verifyInputBodies() {
      for (const item of sourceManifest)
        check(
          sha(readFileSync(join(workspace, item.path))) === item.sha256 &&
            (ordinary(join(workspace, item.path)).mode & 0o777) ===
              (String(item.mode) === "100755" ? 0o755 : 0o644),
        );
      for (const [key] of publicationKinds) {
        const item = cfg.publications[key],
          actual = walk(item.directory);
        check(actual.length === Object.keys(item.hashes).length);
        for (const file of actual)
          check(item.hashes[file.path] === file.sha256);
      }
      check(sha(readFileSync(cfg.manifestPath)) === state.manifestSha256);
    }
    async function normalPgRestart() {
      check(!readHost && !mutationHost);
      const original = inspectOwned();
      check(original.State.Running);
      const old = snapshot();
      docker(["stop", "--time", "30", pg.Id], { timeout: 35000 });
      const stopped = jsonDocker(["inspect", pg.Id])[0];
      check(
        !stopped.State.Running &&
          stopped.State.ExitCode === 0 &&
          !stopped.State.OOMKilled,
      );
      docker(["start", pg.Id]);
      const current = inspectOwned();
      check(
        current.State.Running &&
          current.State.StartedAt !== original.State.StartedAt &&
          current.Id === original.Id &&
          equivalent(
            current.HostConfig.PortBindings,
            original.HostConfig.PortBindings,
          ) &&
          equivalent(
            current.NetworkSettings.Ports,
            original.NetworkSettings.Ports,
          ) &&
          equivalent(current.Mounts, original.Mounts),
      );
      await pgReady();
      check(equivalent(old, snapshot()), "productFailure");
      verifyInputBodies();
      state.observations.push({
        kind: "same-owned-pg-normal-restart",
        id: pg.Id,
        originalStartedAt: original.State.StartedAt,
        currentStartedAt: current.State.StartedAt,
        normalStopExitCode: stopped.State.ExitCode,
        rowsCredentialsJournalAndPrivateFileIdentityPreserved: true,
      });
      save();
    }
    function emit(value) {
      const bytes = Buffer.from(JSON.stringify(value) + "\n");
      check(bytes.length <= 4096);
      process.stdout.write(bytes);
    }
    let closing = false,
      closed = false,
      faulted = false;
    const receipts = new Map();
    async function cleanup() {
      if (closed) return state.cleanupFailures.length === 0;
      closing = true;
      const oldDeadline = deadline;
      deadline = Date.now() + (buildScenario ? 360000 : 120000);
      const capture = async (phase, action) => {
        try {
          await action();
        } catch (error) {
          state.cleanupFailures.push({
            phase,
            category: error.category ?? "cleanupFailure",
          });
          save();
        }
      };
      await capture("scenario-settlement", async () => {
        await scenario?.beforeCleanup?.();
      });
      await capture("read-host", () => stopHost("read"));
      await capture("mutation-host", () => stopHost("mutation"));
      let workerSettlement = events === null;
      if (events)
        await capture("automatic-worker-settlement", async () => {
          await settleWorkers(false);
          workerSettlement = true;
        });
      // Never remove a database if an owned API remains live or an observed worker remains.
      const safe =
        !readHost && !mutationHost && workerSettlement && !eventError;
      if (!safe)
        state.cleanupFailures.push({
          phase: "resource-removal",
          category: "cleanupFailure",
        });
      if (safe)
        for (const attempt of [...state.attempts].reverse())
          await capture("owned-" + attempt.kind, async () => {
            const path =
              attempt.kind === "container"
                ? "/containers/"
                : attempt.kind === "network"
                  ? "/networks/"
                  : "/volumes/";
            const suffix = attempt.kind === "container" ? "/json" : "";
            const found = await daemon(
              path + encodeURIComponent(attempt.id ?? attempt.name) + suffix,
            );
            if (found.status === 404) {
              state.observations.push({
                kind: "owned-absence",
                resourceKind: attempt.kind,
                id: attempt.id,
                name: attempt.name,
                daemonStatus: 404,
              });
              return;
            }
            check(found.status === 200, "ownershipChanged");
            const value = JSON.parse(found.body);
            check(
              labelsMatch({ Labels: value.Config?.Labels ?? value.Labels }) &&
                (attempt.kind === "volume"
                  ? value.Name === attempt.name
                  : value.Id === (attempt.id ?? value.Id)),
              "ownershipChanged",
            );
            if (attempt.kind === "container") {
              check(
                value.Name === "/" + attempt.name &&
                  value.Image ===
                    jsonDocker(["image", "inspect", postgresImage])[0].Id,
                "ownershipChanged",
              );
              if (value.State.Running)
                docker(["stop", "--time", "30", value.Id], { timeout: 35000 });
              const stopped = jsonDocker(["inspect", value.Id])[0];
              check(
                !stopped.State.Running &&
                  stopped.State.ExitCode === 0 &&
                  !stopped.State.OOMKilled,
              );
              docker(["rm", value.Id]);
            } else if (attempt.kind === "network") {
              check(
                Object.keys(value.Containers ?? {}).length === 0,
                "ownershipChanged",
              );
              docker(["network", "rm", value.Id]);
            } else {
              if (volume)
                check(
                  value.Mountpoint === volume.Mountpoint,
                  "ownershipChanged",
                );
              docker(["volume", "rm", value.Name]);
            }
            check(
              (
                await daemon(
                  path +
                    encodeURIComponent(attempt.id ?? attempt.name) +
                    suffix,
                )
              ).status === 404,
              "cleanupFailure",
            );
            state.observations.push({
              kind: "owned-absence",
              resourceKind: attempt.kind,
              id: attempt.id,
              name: attempt.name,
              daemonStatus: 404,
            });
            save();
          });
      await capture("preexisting-preservation", async () => {
        if (!before) return;
        const after = inventories();
        state.resourcesAfter = after;
        for (const kind of ["containers", "networks", "volumes"])
          check(
            before[kind].every((id) => after[kind].includes(id)),
            "ownershipChanged",
          );
      });
      if (events)
        await capture("event-stream-close", async () => {
          eventClosed = true;
          await new Promise((fulfil, reject) => {
            let remaining = 2;
            const timer = setTimeout(
              () => reject(new Failure("cleanupFailure")),
              budget(5000),
            );
            const done = () => {
              remaining--;
              if (remaining === 0) {
                clearTimeout(timer);
                fulfil();
              }
            };
            if (events.response.closed) done();
            else events.response.once("close", done);
            if (events.request.closed) done();
            else events.request.once("close", done);
            events.response.destroy();
            events.request.destroy();
          });
        });
      await capture("input-stability", async () => {
        if (manifest) verifyInputBodies();
      });
      state.workerEvents = [...workerEvents.values()];
      if (eventError)
        state.cleanupFailures.push({
          phase: "event-stream",
          category: eventError,
        });
      state.terminal = {
        closedAtUtc: new Date().toISOString(),
        primaryFailure: state.primaryFailure,
        cleanupFailures: state.cleanupFailures,
        operatorPassed:
          state.primaryFailure === null && state.cleanupFailures.length === 0,
        noFullBrowserPassClaim: true,
        operatorWorkerRemoval: false,
      };
      save();
      closed = true;
      closing = false;
      deadline = oldDeadline;
      return state.cleanupFailures.length === 0;
    }
    async function control(item) {
      const base = {
        schemaVersion: `braid.${definition.kind}-browser-control.v1`,
        requestNonce: item.requestNonce,
        fixtureId,
        action: item.action,
      };
      const normalized = JSON.stringify(canonical(item)),
        prior = receipts.get(item.requestNonce);
      if (prior)
        return prior.envelope === normalized
          ? prior.response
          : { ...base, outcome: "refused", failure: "invalidControl" };
      if (
        receipts.size >= 64 ||
        (faulted && item.action !== "shutdown") ||
        closing ||
        closed
      )
        return { ...base, outcome: "refused", failure: "stateMismatch" };
      const maximum =
        item.action === "shutdown"
          ? buildScenario
            ? 360000
            : 120000
          : scenario.maximum(item.action);
      deadline = Date.now() + maximum;
      let response;
      try {
        check(!eventError);
        verifyInputBodies();
        if (item.action === "shutdown") {
          check(await cleanup(), "cleanupFailure");
          response = { ...base, outcome: "closed" };
        } else response = { ...base, ...(await scenario.dispatch(item)) };
      } catch (error) {
        const category = [
          "invalidControl",
          "ownershipChanged",
          "deadline",
          "productFailure",
          "stateMismatch",
          "cleanupFailure",
        ].includes(error.category)
          ? error.category
          : "productFailure";
        faulted = true;
        state.primaryFailure ??= {
          phase: item.action,
          category,
          site: failureSite(error),
        };
        response = { ...base, outcome: "failed", failure: category };
      } finally {
        deadline = Infinity;
      }
      receipts.set(item.requestNonce, { envelope: normalized, response });
      state.controls.push({ request: item, response });
      save();
      return response;
    }

    const fx = {
      Failure,
      check,
      sha,
      same,
      canonical,
      equivalent,
      guid,
      hash,
      cfg,
      fixtureId,
      ids,
      fixtures,
      provision,
      tool,
      initializeStorage,
      startHost,
      stopHost,
      settleWorkers,
      createProject,
      upload,
      projectPath,
      mappingPath,
      request,
      page,
      rows,
      tables,
      snapshot,
      physical,
      jsonSql,
      sql,
      captureMembership,
      ...(creationScenario
        ? { captureCreatedFact, changeCreatedMembership }
        : {}),
      ...(metadataScenario
        ? { captureMetadataPublisher, changeMetadataPublisherRole }
        : {}),
      ...(uploadScenario
        ? {
            captureUploadPublisher: captureMetadataPublisher,
            changeUploadPublisherRole: changeMetadataPublisherRole,
          }
        : {}),
      ...(appendScenario
        ? {
            captureAppendPublisher: captureMetadataPublisher,
            changeAppendPublisherRole: changeMetadataPublisherRole,
          }
        : {}),
      ...(regressionScenario
        ? {
            captureRegressionPublisher: captureMetadataPublisher,
            changeRegressionPublisherRole: changeMetadataPublisherRole,
          }
        : {}),
      verifyInputBodies,
      state,
      budget,
      revoke,
      normalPgRestart,
      save,
      fileIdentity,
      storageRoot,
      scratch,
      processIdentity,
      ownedChild,
      get readHost() {
        return readHost;
      },
      get mutationHost() {
        return mutationHost;
      },
      get projectRevoked() {
        return projectRevoked;
      },
      get orgRevoked() {
        return orgRevoked;
      },
      workerCreates() {
        return [...workerEvents.values()].filter((value) => value.create)
          .length;
      },
    };
    scenario = definition.create(fx);

    let interrupted = false;
    process.once("SIGTERM", () => {
      interrupted = true;
      process.stdin.destroy();
    });
    process.once("SIGINT", () => {
      interrupted = true;
      process.stdin.destroy();
    });
    process.stdout.on("error", () => {
      interrupted = true;
      process.stdin.destroy();
    });
    try {
      deadline = Date.now() + 300000;
      await initialize();
      deadline = Infinity;
      let pending = Buffer.alloc(0);
      for await (const chunk of process.stdin) {
        pending = Buffer.concat([pending, chunk]);
        while (pending.includes(10)) {
          const end = pending.indexOf(10),
            line = pending.subarray(0, end);
          pending = pending.subarray(end + 1);
          // No queued second owner action: the adapter must await the first acknowledgement.
          check(pending.length === 0, "invalidControl");
          const item = scenario.validateControl(line),
            response = await control(item);
          emit(response);
          if (item.action === "shutdown") {
            if (response.outcome !== "closed") process.exitCode = 1;
            break;
          }
        }
        check(pending.length <= 1024, "invalidControl");
        if (closed) break;
      }
      check(
        pending.length === 0 && !interrupted,
        interrupted ? "productFailure" : "invalidControl",
      );
    } catch (error) {
      state.primaryFailure ??= {
        phase: manifest ? "owner-pipe" : "setup",
        category: error.category ?? "productFailure",
        site: failureSite(error),
      };
      // Categorical only: never forward child/provider/SQL text or a JSON request body.
      process.stderr.write(
        JSON.stringify({
          schemaVersion: `braid.${definition.kind}-browser-operator-error.v1`,
          failure: state.primaryFailure.category,
        }) + "\n",
      );
      process.exitCode = 1;
    } finally {
      if (!(await cleanup()) || state.primaryFailure !== null)
        process.exitCode = 1;
    }
  } catch {
    // This includes malformed or held private admission. No raw parser/provider text.
    process.stderr.write(
      JSON.stringify({
        schemaVersion: `braid.${definition.kind}-browser-operator-error.v1`,
        failure: "stateMismatch",
      }) + "\n",
    );
    process.exitCode = 1;
  }
}
