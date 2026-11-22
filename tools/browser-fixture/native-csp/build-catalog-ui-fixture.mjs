import { createRequire } from "node:module";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
const root = fileURLToPath(new URL("../../../", import.meta.url));
const [dependencyRoot, output] = process.argv.slice(2);
assert.ok(dependencyRoot && output);
const require = createRequire(resolve(dependencyRoot, "package.json"));
const esbuild = require("esbuild");
assert.equal(esbuild.version, "0.28.2", "expected the root bun.lock esbuild");
await esbuild.build({
  absWorkingDir: root,
  entryPoints: ["apps/web/tests/catalog.fixture.ts"],
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node22",
  packages: "external",
  outfile: resolve(output),
  logLevel: "silent",
});
