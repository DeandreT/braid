import { mkdir, readFile, writeFile } from "node:fs/promises";
import openapiTS, { astToString } from "openapi-typescript";
import { format } from "prettier";

const args = process.argv.slice(2);
if (args.length > 1 || (args.length === 1 && args[0] !== "--check")) {
  console.error("Usage: bun tools/contracts-generator/generate.mjs [--check]");
  process.exit(1);
}

const schema = new URL("../../contracts/braid-v1.json", import.meta.url);
const directory = new URL("../../apps/web/src/api/", import.meta.url);
const output = new URL("generated.ts", directory);
const header = `/**
 * Generated from contracts/braid-v1.json. Do not edit manually.
 * Refresh with: bun run contracts:generate
 */
`;
const ast = await openapiTS(schema, { alphabetize: true });
const generated = await format(header + astToString(ast), {
  parser: "typescript",
});

if (args[0] === "--check") {
  let current;
  try {
    current = await readFile(output, "utf8");
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }
  if (current !== generated) {
    console.error(
      "Angular API types are missing or stale. Run bun run contracts:generate.",
    );
    process.exitCode = 1;
  } else {
    console.log("Angular API types match contracts/braid-v1.json.");
  }
} else {
  await mkdir(directory, { recursive: true });
  await writeFile(output, generated);
  console.log("Generated apps/web/src/api/generated.ts.");
}
