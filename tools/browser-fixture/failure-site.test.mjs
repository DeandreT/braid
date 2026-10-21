import assert from "node:assert/strict";
import { test } from "node:test";
import { failureSite } from "./failure-site.mjs";

test("operator failures retain only a safe check location", () => {
  const value = failureSite({
    stack:
      "Error: private-message\n    at check (/private/source/runtime.mjs:42:4)\n    at setup (/private/source/operator.mjs:235:7)",
  });
  assert.deepEqual(value, { module: "operator", line: 235 });
  assert.equal(JSON.stringify(value).includes("private"), false);
});

test("unexpected stacks and message-only errors expose no diagnostic text", () => {
  assert.equal(failureSite({ message: "private" }), null);
  assert.equal(
    failureSite({
      stack: "private\n    at secret (/private/customer.mjs:99:1)",
    }),
    null,
  );
  assert.equal(
    failureSite({
      stack:
        "private\n    at invalid (/private/runtime.mjs:99999999999999999:1)",
    }),
    null,
  );
});

test("command failures identify the caller rather than generic process wrappers", () => {
  assert.deepEqual(
    failureSite({
      stack:
        "Error: private\n    at check (/private/runtime.mjs:42:1)\n    at run (/private/runtime.mjs:420:2)\n    at docker (/private/runtime.mjs:428:3)\n    at setup (/private/operator.mjs:236:4)",
    }),
    { module: "operator", line: 236 },
  );
});
