import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { test } from "node:test";
import {
  browserFixtureRolePairs,
  browserFixtureUncredentialedRoles,
  matchesBrowserFixtureRoles,
} from "./roles.mjs";
import {
  browserFixtureContextFeatures,
  browserFixtureMigrations,
  browserFixtureProtectedTables,
  browserFixtureRowsQuery,
  matchesBrowserFixtureMigrationJournal,
  regressionReadProfile,
} from "./schema.mjs";

const migrations = new URL("../../migrations/postgres/", import.meta.url);

test("the browser fixture admits the complete initializer role roster without enabling opt-in passwords", () => {
  const init = readFileSync(
    new URL("../../deploy/local/postgres-init.sh", import.meta.url),
    "utf8",
  );
  const required = browserFixtureRolePairs.map(([name]) => name);
  const disabled = browserFixtureUncredentialedRoles;
  const names = [...required, ...disabled];
  assert.deepEqual(
    [...init.matchAll(/^CREATE ROLE (braid_\w+) /gm)]
      .map((match) => match[1])
      .sort(),
    names.toSorted(),
  );
  const rows = names.map((name) => ({
    name,
    hasPassword: required.includes(name),
    login: true,
    super: false,
    createDb: false,
    createRole: false,
    inherit: false,
    replication: false,
    bypass: false,
  }));
  assert.equal(matchesBrowserFixtureRoles(rows), true);
  const last = rows.at(-1);
  const rest = rows.slice(0, -1);
  for (const altered of [
    rest,
    [...rest, rows[0]],
    [...rest, { ...last, name: "braid_unknown" }],
    [...rest, { ...last, hasPassword: true }],
    [{ ...rows[0], hasPassword: false }, ...rows.slice(1)],
    ...[
      "super",
      "createDb",
      "createRole",
      "inherit",
      "replication",
      "bypass",
    ].map((key) => [...rest, { ...last, [key]: true }]),
  ])
    assert.equal(matchesBrowserFixtureRoles(altered), false);
});

test("the complete fixture snapshot keeps one ordered entry per table without a variadic call", () => {
  const tables = [
    ...browserFixtureProtectedTables,
    "organizations",
    "build_publications",
  ];
  assert.ok(tables.length > 50);
  const query = browserFixtureRowsQuery(tables);
  assert.match(query, /^SELECT json_object_agg\(/);
  assert.deepEqual(
    [...query.matchAll(/\((\d+),'([a-z_]+)',\(SELECT/g)].map(
      ([, ordinal, name]) => [Number(ordinal), name],
    ),
    tables.map((name, ordinal) => [ordinal, name]),
  );
  assert.match(query, /ORDER BY entry\.ordinal/);
  assert.match(query, /FROM braid\.build_publications r/);
  assert.match(query, /to_jsonb\(r\)-'artifact_body'/);
  assert.match(query, /'artifact_body_sha256'/);
  for (const invalid of [[], ["organizations", "organizations"], ["a'"]])
    assert.throws(() => browserFixtureRowsQuery(invalid));
});

test("catalog fixture feature expectations include default false fields and omit disabled optional capabilities", () => {
  const contract = JSON.parse(
    readFileSync(new URL("../../contracts/braid-v1.json", import.meta.url)),
  ).components.schemas.CatalogFeaturesV1;
  const host = {
    writer: false,
    creationOnly: false,
    uploadOnly: false,
    metadataEdits: false,
    reportMode: null,
  };
  const viewer = browserFixtureContextFeatures(host);
  const wireKeys = [
    ...contract.required,
    ...Object.keys(contract.properties).filter(
      (key) =>
        !contract.required.includes(key) &&
        contract.properties[key].default === false,
    ),
  ].sort();
  assert.deepEqual(Object.keys(viewer).sort(), wireKeys);
  assert.deepEqual(
    Object.keys(viewer).filter((key) => viewer[key]),
    ["readCatalog"],
  );
  for (const profile of [
    { writer: true },
    { creationOnly: true },
    { uploadOnly: true },
    { metadataEdits: true },
    { reportMode: "full" },
  ]) {
    const features = browserFixtureContextFeatures({ ...host, ...profile });
    assert.equal(features.edit, false);
    assert.equal(features.xmlPreview, false);
    assert.equal("csvPreview" in features, false);
    assert.equal("nativePreviewProfiles" in features, false);
  }
});

test("production entry assets do not depend on an admitted base element", () => {
  const configuration = JSON.parse(
    readFileSync(
      new URL("../../apps/web/angular.json", import.meta.url),
      "utf8",
    ),
  );
  assert.equal(
    configuration.projects.braid.architect.build.configurations.production
      .deployUrl,
    "/",
  );
});

test("the development server keeps entry assets root-absolute without deployUrl", () => {
  const read = (path) =>
    JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
  assert.equal(
    read("../../apps/web/angular.json").projects.braid.architect.serve.builder,
    "./tools/dev-server:dev-server",
  );
  // The copied option schema must track the pinned stock dev-server builder.
  assert.deepEqual(
    read("../../apps/web/tools/dev-server/schema.json"),
    read(
      "../../node_modules/@angular/build/src/builders/dev-server/schema.json",
    ),
  );
  const { rootAbsoluteEntryAssets } = createRequire(import.meta.url)(
    "../../apps/web/tools/dev-server/index.cjs",
  );
  assert.equal(
    rootAbsoluteEntryAssets(
      '<script type="module" src="/@vite/client"></script><link rel="stylesheet" href="styles.css"><link rel="modulepreload" href="chunk-A.js"><script src="main.js" type="module"></script><link rel="icon" href="data:,"><script src="https://example.test/x.js"></script>',
    ),
    '<script type="module" src="/@vite/client"></script><link rel="stylesheet" href="/styles.css"><link rel="modulepreload" href="/chunk-A.js"><script src="/main.js" type="module"></script><link rel="icon" href="data:,"><script src="https://example.test/x.js"></script>',
  );
});

test("the browser fixture admits exactly the current ordered migration inventory", () => {
  assert.deepEqual(
    browserFixtureMigrations,
    readdirSync(migrations)
      .filter((name) => /^\d{4}_.*\.sql$/.test(name))
      .sort(),
  );
});

test("the complete migration journal binds each version to the admitted SQL bytes", () => {
  const journal = browserFixtureMigrations.map((name, index) => ({
    version: index + 1,
    checksum: createHash("sha256")
      .update(readFileSync(new URL(name, migrations)))
      .digest("hex"),
  }));
  const sourceHashes = Object.fromEntries(
    browserFixtureMigrations.map((name, index) => [
      "migrations/postgres/" + name,
      journal[index].checksum,
    ]),
  );
  const workbook = "migrations/postgres/0069_portable_workbook.sql";
  assert.equal(
    sourceHashes[workbook],
    "4beed03b89b305fa65d443abf8036ad6fcb8811dc9941d3034098844dde75f48",
  );
  assert.equal(
    matchesBrowserFixtureMigrationJournal(journal, sourceHashes),
    true,
  );
  const previous = journal.slice(0, -1);
  const final = journal.at(-1);
  for (const rows of [
    previous,
    [...journal, final],
    [...previous, { ...final, version: 68 }],
    [...previous, { ...final, checksum: "0".repeat(64) }],
    [...journal].reverse(),
  ])
    assert.equal(
      matchesBrowserFixtureMigrationJournal(rows, sourceHashes),
      false,
    );
  const malformed = final.checksum + "\n";
  assert.equal(
    matchesBrowserFixtureMigrationJournal(
      [...previous, { ...final, checksum: malformed }],
      { ...sourceHashes, [workbook]: malformed },
    ),
    false,
  );
  delete sourceHashes[workbook];
  assert.equal(
    matchesBrowserFixtureMigrationJournal(journal, sourceHashes),
    false,
  );
});

test("CSV is an additive migration after the retained draft and chat write contracts", () => {
  assert.deepEqual(browserFixtureMigrations.slice(36, 41), [
    "0037_mapping_drafts.sql",
    "0038_mapping_draft_previews.sql",
    "0039_chat_mapping_writes.sql",
    "0040_chat_mapping_write_journal.sql",
    "0041_csv_documents.sql",
  ]);
  const sql = readFileSync(
    new URL("0041_csv_documents.sql", migrations),
    "utf8",
  );
  assert.match(sql, /braid\.csv-preview-request\.v1/);
  assert.match(sql, /braid\.csv-regression-case\.v1/);
  assert.match(sql, /braid\.csv-regression-run-result\.v1/);
  assert.match(
    sql,
    /comparisonPolicy' IS DISTINCT FROM 'braid\.regression\.exact-bytes\.v1'/,
  );
  assert.doesNotMatch(
    sql,
    /DROP TABLE|TRUNCATE|DELETE FROM|DISABLE ROW LEVEL SECURITY/,
  );
});

test("XML documents retain the base authorization and settlement fences", () => {
  assert.equal(browserFixtureMigrations[41], "0042_xml_documents.sql");
  const sql = readFileSync(
    new URL("0042_xml_documents.sql", migrations),
    "utf8",
  );
  assert.match(sql, /CREATE TABLE braid\.xml_preview_documents/);
  assert.match(sql, /braid\.preview_authorized\(/);
  assert.match(sql, /braid\.preview_subject_authorized\(/);
  assert.match(sql, /FOR UPDATE/);
  assert.match(sql, /r\.lease_token IS DISTINCT FROM p_token/);
  assert.doesNotMatch(sql, /braid\.lifecycle_|flextext_profile/);
  assert.doesNotMatch(
    sql,
    /DROP TABLE|TRUNCATE (TABLE )?braid|DELETE FROM|DISABLE ROW LEVEL SECURITY|CREATE OR REPLACE FUNCTION/,
  );
});

test("qualification and release state stays inside fixture conservation snapshots", () => {
  const sql = browserFixtureMigrations
    .map((name) => readFileSync(new URL(name, migrations), "utf8"))
    .join("\n");
  assert.equal(
    new Set(browserFixtureProtectedTables).size,
    browserFixtureProtectedTables.length,
  );
  for (const table of browserFixtureProtectedTables)
    assert.match(sql, new RegExp(`CREATE TABLE braid\\.${table}\\s*\\(`));
  const runtime = readFileSync(
    new URL("./runtime.mjs", import.meta.url),
    "utf8",
  );
  assert.match(runtime, /\.\.\.browserFixtureProtectedTables/);
  const build = readFileSync(
    new URL("../build-browser/operator.mjs", import.meta.url),
    "utf8",
  );
  assert.doesNotMatch(build, /!name\.startsWith\("build_"\)/);
});

test("regression report access is limited to the explicit read host", () => {
  const good = {
    regressionScenario: true,
    kind: "read",
    writer: false,
    uploadOnly: false,
    metadataEdits: false,
    creationOnly: false,
    reportMode: "full",
  };
  assert.equal(regressionReadProfile(good), true);
  assert.equal(regressionReadProfile({ ...good, reportMode: null }), true);
  for (const flag of ["writer", "uploadOnly", "metadataEdits", "creationOnly"])
    assert.equal(regressionReadProfile({ ...good, [flag]: true }), false);
  for (const change of [
    { kind: "mutation" },
    { regressionScenario: false },
    { reportMode: "profileUnavailable" },
    { reportMode: "readOnlyNoWorker" },
  ])
    assert.equal(regressionReadProfile({ ...good, ...change }), false);
});
