import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import {
  browserFixtureMigrations,
  browserFixtureProtectedTables,
  regressionReadProfile,
} from "./schema.mjs";

const migrations = new URL("../../migrations/postgres/", import.meta.url);

test("production entry assets do not depend on an admitted base element", () => {
  const configuration = JSON.parse(
    readFileSync(
      new URL("../../apps/web/angular.json", import.meta.url),
      "utf8",
    ),
  );
  assert.equal(
    configuration.projects.weft.architect.build.configurations.production
      .deployUrl,
    "/",
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
  assert.match(sql, /weft\.csv-preview-request\.v1/);
  assert.match(sql, /weft\.csv-regression-case\.v1/);
  assert.match(sql, /weft\.csv-regression-run-result\.v1/);
  assert.match(
    sql,
    /comparisonPolicy' IS DISTINCT FROM 'weft\.regression\.exact-bytes\.v1'/,
  );
  assert.doesNotMatch(
    sql,
    /DROP TABLE|TRUNCATE|DELETE FROM|DISABLE ROW LEVEL SECURITY/,
  );
});

test("XML documents retain the base authorization and settlement fences", () => {
  assert.equal(browserFixtureMigrations.at(-1), "0042_xml_documents.sql");
  const sql = readFileSync(
    new URL("0042_xml_documents.sql", migrations),
    "utf8",
  );
  assert.match(sql, /CREATE TABLE weft\.xml_preview_documents/);
  assert.match(sql, /weft\.preview_authorized\(/);
  assert.match(sql, /weft\.preview_subject_authorized\(/);
  assert.match(sql, /FOR UPDATE/);
  assert.match(sql, /r\.lease_token IS DISTINCT FROM p_token/);
  assert.doesNotMatch(sql, /weft\.lifecycle_|flextext_profile/);
  assert.doesNotMatch(
    sql,
    /DROP TABLE|TRUNCATE (TABLE )?weft|DELETE FROM|DISABLE ROW LEVEL SECURITY|CREATE OR REPLACE FUNCTION/,
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
    assert.match(sql, new RegExp(`CREATE TABLE weft\\.${table}\\s*\\(`));
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
