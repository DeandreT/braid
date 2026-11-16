// Runs actual wasm in Node and the native worker on the same immutable fixtures.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { pathToFileURL } from "node:url";
import { operationCases } from "./operation-cases.mjs";

const [gluePath, wasmPath, nativePath, ...directories] = process.argv.slice(2);
assert.ok(gluePath && wasmPath && nativePath && directories.length);
const core = await import(pathToFileURL(resolve(gluePath)).href);
core.initSync({ module: readFileSync(wasmPath) });
const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");
const empty = { format: "ferrule.edit.v1", operations: [] };
const binding = {
  scope: { target: { kind: "primary" }, children: [] },
  field: "code",
};
const edited = {
  format: "ferrule.edit.v1",
  operations: [
    {
      op: "bind",
      binding,
      expression: {
        kind: "constant",
        id: "4294967294",
        value: { type: "float", text: "-0.0" },
      },
    },
    {
      op: "edit_constant",
      node: "4294967294",
      value: { type: "int", text: "9007199254740993" },
    },
  ],
};
const frame = (bytes) => {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(bytes.length);
  return Buffer.concat([length, bytes]);
};
function nativeCheckpoint(native, operations) {
  const log = Buffer.from(JSON.stringify(operations));
  const request = {
    protocolVersion: "braid.ferrule-edit.v1",
    requestId: "11111111-1111-4111-8111-111111111111",
    operation: "edit",
    revision: {
      organizationId: "22222222-2222-4222-8222-222222222222",
      projectId: "33333333-3333-4333-8333-333333333333",
      mappingId: "44444444-4444-4444-8444-444444444444",
      revisionId: "55555555-5555-4555-8555-555555555555",
    },
    nativeDigest: digest(native),
    documentId: "entry.native.json",
    operationsDigest: digest(log),
  };
  const child = spawnSync(nativePath, [], {
    input: Buffer.concat([
      frame(Buffer.from(JSON.stringify(request))),
      frame(native),
      frame(log),
    ]),
    timeout: 10_000,
    maxBuffer: 21 * 1024 * 1024,
    env: {},
  });
  assert.equal(child.error, undefined);
  assert.equal(child.status, 0);
  assert.equal(child.stderr.length, 0);
  const length = child.stdout.readUInt32BE();
  const response = JSON.parse(child.stdout.subarray(4, 4 + length));
  assert.equal(response.failure, null, JSON.stringify(response.failure));
  assert.deepEqual(response.request, request);
  const result = response.result;
  const bytes = child.stdout.subarray(8 + length);
  assert.equal(child.stdout.readUInt32BE(4 + length), bytes.length);
  assert.equal(result.nativeByteCount, bytes.length);
  assert.equal(result.nativeDigest, digest(bytes));
  return {
    nativeDigest: result.nativeDigest,
    nativeByteCount: result.nativeByteCount,
    validation: result.validation,
    projection: result.projection,
  };
}

let checked = 0;
for (const directory of directories) {
  for (const file of readdirSync(directory).filter((file) =>
    file.endsWith(".json"),
  )) {
    const native = readFileSync(resolve(directory, file));
    const scripts =
      file === "valid-basic.json"
        ? [empty, edited]
        : file === "edit-v1.json"
          ? [empty, ...operationCases]
          : [empty];
    for (const operations of scripts) {
      const json = JSON.stringify(operations);
      const wasm = JSON.parse(core.qualify_checkpoint(native, json));
      assert.deepEqual(wasm, nativeCheckpoint(native, operations), file);
      // History is the same command-based implementation, not JS project snapshots.
      if (operations.operations.length > 0) {
        const session = new core.EditorSession(native, JSON.stringify(empty));
        const before = JSON.parse(session.view());
        for (const operation of operations.operations)
          session.apply(JSON.stringify(operation));
        const after = JSON.parse(session.view());
        let steps = 0;
        while (JSON.parse(session.view()).canUndo) {
          assert.ok(session.undo());
          assert.ok(++steps <= operations.operations.length);
        }
        const undone = JSON.parse(session.view());
        assert.deepEqual(undone.operations.operations, []);
        assert.deepEqual(undone.validation, before.validation);
        assert.deepEqual(undone.projection, before.projection);
        for (let index = 0; index < steps; index++) assert.ok(session.redo());
        assert.deepEqual(
          JSON.parse(session.view()).projection,
          after.projection,
        );
        session.free();
      }
      checked++;
    }
  }
}
console.log(
  `Native and wasm runtime checkpoints agree for ${checked} fixture scripts.`,
);
