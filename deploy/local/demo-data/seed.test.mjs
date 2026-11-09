// Self-check for the demo catalog: node --test deploy/local/demo-data/seed.test.mjs
import assert from "node:assert/strict";
import test from "node:test";
import { projects, x12Document } from "./catalog.mjs";
import { base64Url, stableId } from "./native.mjs";

test("stable IDs are canonical, name-based UUIDs", () => {
  const value = stableId("weft-demo-v1", "mapping:invoicing/invoice-ledger");
  assert.match(
    value,
    /^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
  );
  assert.equal(
    value,
    stableId("weft-demo-v1", "mapping:invoicing/invoice-ledger"),
  );
  assert.notEqual(
    value,
    stableId("weft-demo-v2", "mapping:invoicing/invoice-ledger"),
  );
});

test("Base64url is unpadded", () => {
  assert.equal(base64Url("ab?"), "YWI_");
});

test("X12 samples have a fixed-width ISA and a correct SE count", () => {
  const document = x12Document("A", "B", "PO", 7, "850\nBEG*00*SA*1~\nCTT*1~");
  const segments = document.split("\n").filter(Boolean);
  assert.equal(segments[0].length, 106);
  assert.equal(segments.at(-3), "SE*4*0001~");
  assert.equal(segments.at(-1), "IEA*1*000000007~");
});

test("every mapping revision is a closed graph and every case names a sample", () => {
  const slugs = new Set();
  for (const project of projects)
    for (const spec of project.mappings) {
      assert.ok(!slugs.has(spec.slug), `duplicate slug ${spec.slug}`);
      slugs.add(spec.slug);
      const outputs = new Set();
      for (let revision = 1; revision <= spec.revisions; revision++) {
        const native = spec.native(revision);
        outputs.add(JSON.stringify(native));
        const nodes = native.graph.nodes;
        const references = [];
        const visit = (scope) => {
          for (const binding of scope.bindings) references.push(binding.node);
          for (const key of ["filter", "sort_by"])
            if (scope[key] !== undefined) references.push(scope[key]);
          for (const item of scope.sort_then_by ?? [])
            references.push(item.node);
          scope.children.forEach(visit);
        };
        visit(native.root);
        for (const node of Object.values(nodes))
          references.push(
            ...(node.args ?? []),
            ...["condition", "then", "else"]
              .map((key) => node[key])
              .filter((value) => value !== undefined),
          );
        for (const reference of references)
          assert.ok(
            nodes[reference],
            `${spec.slug}@${revision} references missing node ${reference}`,
          );
      }
      assert.equal(
        outputs.size,
        spec.revisions,
        `${spec.slug} has identical revisions`,
      );
      for (const item of spec.cases)
        assert.ok(
          spec.samples[item.sample],
          `${spec.slug} case ${item.name} has no sample`,
        );
    }
});
