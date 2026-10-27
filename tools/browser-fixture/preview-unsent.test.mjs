import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import ts from "typescript";

const source = await readFile(
  new URL(
    "../../apps/web/src/catalog/previews/preview-unsent.ts",
    import.meta.url,
  ),
  "utf8",
);
const { outputText } = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ESNext,
    target: ts.ScriptTarget.ES2022,
  },
});
const { hasUnsentPreview } = await import(
  `data:text/javascript;base64,${Buffer.from(outputText).toString("base64")}`
);
const empty = () => ({
  inputs: [{ name: "", text: "", file: null }],
  parameters: [],
  targetKind: "all",
  targetName: "",
  documentProfile: false,
  busy: null,
  pendingKey: false,
});

test("preview samples, file bytes, document choice and target/parameter drafts warn before full-page sign-in", () => {
  for (const change of [
    { inputs: [{ name: "", text: "ISA*sample~", file: null }] },
    { inputs: [{ name: "source", text: "", file: null }] },
    { inputs: [{ name: "", text: "", file: { bytes: new Uint8Array([1]) } }] },
    { inputs: [empty().inputs[0], empty().inputs[0]] },
    { parameters: [{}] },
    { documentProfile: true },
    { targetKind: "named" },
    { targetName: "output" },
  ])
    assert.equal(hasUnsentPreview({ ...empty(), ...change }), true);
});

test("unsettled mutations warn, but empty forms and server-recoverable reads do not", () => {
  assert.equal(hasUnsentPreview(empty()), false);
  assert.equal(hasUnsentPreview({ ...empty(), busy: "reading" }), false);
  for (const change of [
    { busy: "submitting" },
    { busy: "canceling" },
    { pendingKey: true },
  ])
    assert.equal(hasUnsentPreview({ ...empty(), ...change }), true);
});
