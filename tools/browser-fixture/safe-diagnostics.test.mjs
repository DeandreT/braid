import assert from "node:assert/strict";
import { test } from "node:test";
import {
  hostFailureSummary,
  reportSetupDiagnostic,
} from "./safe-diagnostics.mjs";

test("host diagnostics emit fixed categories without exception operands", () => {
  const summary = hostFailureSummary();
  summary.write(
    Buffer.from(
      "Unhandled exception. System.InvalidOperationException: private-secret\nOut of memory.\r\n",
    ),
  );
  assert.deepEqual(summary.snapshot(), [
    "outOfMemory",
    "unhandledInvalidOperation",
  ]);
  assert.equal(JSON.stringify(summary), "{}");
  assert.equal(JSON.stringify(summary.snapshot()).includes("private"), false);
});

test("split UTF8, split signatures and final unterminated lines are admitted safely", () => {
  const summary = hostFailureSummary();
  for (const byte of Buffer.from(
    "私密\nUnhandled exception. System.OperationCanceledException: hidden\nFatal error. hidden",
  ))
    summary.write(Buffer.from([byte]));
  assert.deepEqual(summary.snapshot(), [
    "fatalRuntimeError",
    "unhandledCancellation",
  ]);
});

test("long private output and embedded exception text do not become diagnostics", () => {
  const summary = hostFailureSummary();
  summary.write(Buffer.from("private".repeat(100000) + "\n"));
  summary.write(
    Buffer.from(
      "payload: Out of memory.\nUnhandled exception. Customer.Secret: hidden\n",
    ),
  );
  assert.deepEqual(summary.snapshot(), []);
});

test("multiple snapshots and repeated lines have bounded fixed output", () => {
  const summary = hostFailureSummary();
  summary.write(
    Buffer.from(
      "Unhandled exception. System.AggregateException: " +
        "private".repeat(1000),
    ),
  );
  assert.deepEqual(summary.snapshot(), ["unhandledAggregate"]);
  summary.write(
    Buffer.from("\nUnhandled exception. System.AggregateException: other\n"),
  );
  assert.deepEqual(summary.snapshot(), ["unhandledAggregate"]);
});

test("known stack namespaces become fixed categories without method names or paths", () => {
  const summary = hostFailureSummary();
  summary.write(
    Buffer.from(
      "   at Weft.Worker.SecretMethod(private-data) in /private/file:line 2\n   at Weft.Jobs.PrivateMethod()\n   at Wolverine.SecretMethod()\n   at Customer.SecretMethod()\n",
    ),
  );
  assert.deepEqual(summary.snapshot(), [
    "durableJobFrame",
    "wolverineFrame",
    "workerHostFrame",
  ]);
  assert.equal(JSON.stringify(summary.snapshot()).includes("Secret"), false);
  assert.equal(JSON.stringify(summary.snapshot()).includes("private"), false);
});

test("report setup retains only HTTP status and known contract categories", () => {
  assert.deepEqual(
    reportSetupDiagnostic("validation", {
      status: 200,
      body: {
        state: "failed",
        readOutcome: "observed",
        failure: "isolationFailure",
        report: "private",
        token: "secret",
      },
    }),
    {
      kind: "report-setup-result",
      operation: "validation",
      httpStatus: 200,
      state: "failed",
      readOutcome: "observed",
      failure: "isolationFailure",
    },
  );
});

test("unknown response strings and malformed status never leave the fixture", () => {
  const result = reportSetupDiagnostic("private", {
    status: "private",
    body: { state: "private", readOutcome: "private", failure: "private" },
  });
  assert.deepEqual(result, {
    kind: "report-setup-result",
    operation: "unknown",
    httpStatus: null,
    state: "unknown",
    readOutcome: "unknown",
    failure: "unknown",
  });
  assert.equal(JSON.stringify(result).includes("private"), false);
  assert.equal(reportSetupDiagnostic("projection", null).failure, null);
});
