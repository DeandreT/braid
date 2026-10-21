import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import {
  browserFixtureMigrations,
  browserFixtureProtectedTables,
  regressionReadProfile,
} from "./schema.mjs";

const migrations = new URL("../../migrations/postgres/", import.meta.url);

test("the browser fixture admits exactly the current ordered migration inventory", () => {
  assert.deepEqual(
    browserFixtureMigrations,
    readdirSync(migrations)
      .filter((name) => /^\d{4}_.*\.sql$/.test(name))
      .sort(),
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
