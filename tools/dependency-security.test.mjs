import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { test } from "node:test";

// Exercise the dependencies actually resolved by the production forms and CLI
// schema consumers. These tests use small synthetic values and never fetch URLs.
const webRequire = createRequire(
  new URL("../apps/web/package.json", import.meta.url),
);
const cliRequire = createRequire(
  webRequire.resolve("@angular/cli/package.json"),
);
const devkitRequire = createRequire(cliRequire.resolve("@angular-devkit/core"));
const formsRequire = createRequire(
  webRequire.resolve("@angular/forms/package.json"),
);
const buildRequire = createRequire(
  webRequire.resolve("@angular/build/package.json"),
);
const ajvRequire = createRequire(devkitRequire.resolve("ajv"));
const { z } = formsRequire("zod");
const uri = ajvRequire("fast-uri");
const Ajv = devkitRequire("ajv");

test("the Angular builder resolves the pinned Vite override", () => {
  const { overrides } = webRequire("../../package.json");
  assert.equal(typeof overrides.vite, "string");
  assert.equal(buildRequire("vite/package.json").version, overrides.vite);
});

test("Zod preserves valid and invalid bounded-array validation", () => {
  const schema = z.array(z.string()).max(2);
  assert.deepEqual(schema.parse([]), []);
  assert.deepEqual(schema.parse(["first", "second"]), ["first", "second"]);
  const result = schema.safeParse(["first", 2]);
  assert.equal(result.success, false);
  assert.deepEqual(
    result.error.issues.map(({ code, path }) => ({ code, path })),
    [{ code: "invalid_type", path: [1] }],
  );
});

test("Zod rejects an oversized bounded array before parsing its elements", () => {
  let parsed = 0;
  let refined = 0;
  const element = z.number().superRefine((value, context) => {
    parsed++;
    context.addIssue({ code: "custom", message: "synthetic invalid element" });
  });
  const schema = z
    .array(element)
    .max(2)
    .refine(() => ++refined);
  const result = schema.safeParse([0, 0, 0]);
  assert.equal(result.success, false);
  assert.equal(parsed, 0);
  assert.equal(refined, 0);
  assert.deepEqual(
    result.error.issues.map(({ code, path }) => ({ code, path })),
    [{ code: "too_big", path: [] }],
  );
  // The upstream fix gates leading upper bounds, not unbounded collections.
});

test("fast-uri does not activate a percent-encoded dangerous scheme", () => {
  for (const value of ["%6Aavascript:alert(1)", "%u006Aavascript:alert(1)"]) {
    assert.notEqual(uri.parse(value).scheme, "javascript");
    assert.doesNotMatch(uri.normalize(value), /^javascript:/i);
    assert.ok(uri.parse(value).error);
    assert.throws(
      () => uri.resolve("https://example.invalid/", value),
      /malformed/i,
    );
  }
});

test("fast-uri normalization preserves relative-reference resolution", () => {
  const base = "https://example.invalid/parent/current/";
  for (const reference of ["../x", "a/../b", "./value.json"]) {
    assert.equal(
      uri.resolve(base, uri.normalize(reference)),
      uri.resolve(base, reference),
    );
  }
});

test("fast-uri rejects ambiguous or whitespace-smuggled HTTP hosts", () => {
  for (const value of [
    "http://a@b@evil.invalid/",
    "http://exa\tmple.invalid/",
    "http://exa\nmple.invalid/",
    "http://exa\rmple.invalid/",
  ]) {
    assert.ok(uri.parse(value).error, "an ambiguous host must report an error");
  }
  const valid = uri.parse("https://example.invalid/path");
  assert.equal(valid.error, undefined);
  assert.equal(valid.host, "example.invalid");
});

test("fast-uri suffix references cannot introduce a bare dangerous scheme", () => {
  for (const scheme of ["javascript", "data", "vbscript"]) {
    // A suffix without options.scheme is parsed as an authority, not a path.
    // Exercise the advisory's no-authority precondition explicitly instead.
    const path = `${scheme}:synthetic`;
    for (const result of [
      uri.serialize({ path }, { reference: "suffix" }),
      uri.normalize(path, { reference: "suffix", scheme: "x" }),
    ]) {
      assert.equal(uri.parse(result).scheme, undefined);
      assert.doesNotMatch(result, /^(?:javascript|data|vbscript):/i);
    }
  }
});

test("AJV resolves registered ordinary schema references without a network loader", () => {
  const ajv = new Ajv();
  ajv.addSchema({
    $id: "https://example.invalid/schemas/value.json",
    type: "string",
    minLength: 1,
  });
  const validate = ajv.compile({
    $id: "https://example.invalid/schemas/root.json",
    type: "object",
    properties: { value: { $ref: "value.json" } },
    required: ["value"],
    additionalProperties: false,
  });
  assert.equal(validate({ value: "valid" }), true);
  assert.equal(validate({ value: "" }), false);
  assert.equal(validate({ value: 1 }), false);
  assert.equal(validate({}), false);
});
