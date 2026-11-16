import assert from "node:assert/strict";
import { test } from "node:test";
import {
  chmodSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  bodyStages,
  cleanupStages,
  stageReceipt,
  StageDiagnostics,
} from "../apps/web/tests/auth-https/stage-diagnostics.ts";

const source = (name) =>
  readFileSync(
    new URL(`../apps/web/tests/auth-https/${name}`, import.meta.url),
    "utf8",
  );
const filename = "stage-diagnostics.json";
const privateFs = process.platform === "linux";
function temporary(run) {
  const root = mkdtempSync(join(tmpdir(), "braid-stage-source-"));
  try {
    chmodSync(root, 0o700);
    run(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("stage receipt has exactly two closed failure categories and a fixed version", () => {
  assert.deepEqual(bodyStages, [
    "launch",
    "version",
    "rootFetch",
    "baseline",
    "context",
    "pageBinding",
    "navigation",
    "anonymous",
    "scenario",
  ]);
  assert.deepEqual(cleanupStages, [
    "seal",
    "context",
    "browser",
    "boundary",
    "finalStop",
  ]);
  for (const body of ["none", ...bodyStages]) {
    for (const cleanup of ["none", ...cleanupStages]) {
      const text = stageReceipt(body, cleanup);
      assert.deepEqual(JSON.parse(text), {
        version: 1,
        bodyFailure: body,
        cleanupFailure: cleanup,
      });
      assert.equal(
        text,
        `{"version":1,"bodyFailure":"${body}","cleanupFailure":"${cleanup}"}\n`,
      );
      assert.ok(Buffer.byteLength(text) < 128);
    }
  }
});

test("stage receipt rejects operands without rendering or coercing them", () => {
  const sentinel =
    "https://private.invalid/path?token=private-token\nprivate-stack";
  const poison = {
    toString() {
      throw new Error("Must not coerce an operand");
    },
  };
  for (const operand of [
    sentinel,
    new Error(sentinel),
    poison,
    null,
    true,
    1,
    {},
    [],
    "",
    "VERSION",
  ]) {
    for (const [body, cleanup] of [
      [operand, "none"],
      ["none", operand],
    ]) {
      assert.throws(() => stageReceipt(body, cleanup), {
        message: "Invalid qualification stage category.",
      });
    }
  }
});

test("diagnostic memory retains first categories, never supplied operands", () => {
  const diagnostic = new StageDiagnostics("unused");
  assert.equal(diagnostic.receipt(), stageReceipt("none", "none"));
  diagnostic.body("version");
  diagnostic.bodyFailed();
  diagnostic.body("scenario");
  diagnostic.bodyFailed();
  diagnostic.cleanup("browser");
  diagnostic.cleanupFailed();
  diagnostic.cleanupFailed("boundary");
  diagnostic.body("private-url-token-path");
  diagnostic.cleanupFailed("private-stack");
  assert.equal(diagnostic.receipt(), stageReceipt("version", "browser"));
  assert.ok(Object.isFrozen(bodyStages));
  assert.ok(Object.isFrozen(cleanupStages));
});

test(
  "private receipt records only first body and cleanup failures",
  { skip: !privateFs },
  () =>
    temporary((root) => {
      const diagnostic = new StageDiagnostics(root);
      diagnostic.body("navigation");
      diagnostic.bodyFailed();
      diagnostic.body("scenario");
      diagnostic.bodyFailed();
      diagnostic.cleanupFailed("context");
      diagnostic.cleanupFailed("browser");
      diagnostic.write();
      const path = join(root, filename);
      assert.deepEqual(JSON.parse(readFileSync(path, "utf8")), {
        version: 1,
        bodyFailure: "navigation",
        cleanupFailure: "context",
      });
      assert.equal(lstatSync(path).mode & 0o777, 0o600);
      assert.equal(lstatSync(root).mode & 0o777, 0o700);
      assert.equal(lstatSync(path).nlink, 1);
    }),
);

test(
  "receipt never overwrites an earlier original or follows a file link",
  { skip: !privateFs },
  () =>
    temporary((root) => {
      const path = join(root, filename);
      const original = "private-existing-evidence";
      writeFileSync(path, original, { mode: 0o600 });
      new StageDiagnostics(root).write();
      assert.equal(readFileSync(path, "utf8"), original);
      rmSync(path);
      const target = join(root, "original");
      writeFileSync(target, original, { mode: 0o600 });
      symlinkSync(target, path);
      new StageDiagnostics(root).write();
      assert.ok(lstatSync(path).isSymbolicLink());
      assert.equal(readFileSync(target, "utf8"), original);
    }),
);

test(
  "receipt refuses nonprivate and linked roots and survives unavailable storage",
  { skip: !privateFs },
  () =>
    temporary((root) => {
      const child = join(root, "private");
      mkdirSync(child, { mode: 0o700 });
      const linked = join(root, "linked");
      symlinkSync(child, linked);
      new StageDiagnostics(linked).write();
      assert.equal(existsSync(join(child, filename)), false);
      chmodSync(child, 0o755);
      new StageDiagnostics(child).write();
      assert.equal(existsSync(join(child, filename)), false);
      assert.doesNotThrow(() =>
        new StageDiagnostics(join(root, "absent")).write(),
      );
      assert.doesNotThrow(() => new StageDiagnostics("relative").write());
    }),
);

test("diagnostic sink has no exception operands, raw output, attachments or failure authority", () => {
  const diagnostic = source("stage-diagnostics.ts");
  assert.match(
    diagnostic,
    /constants\.O_WRONLY\s*\|\s*constants\.O_CREAT\s*\|\s*constants\.O_EXCL\s*\|\s*constants\.O_NOFOLLOW/,
  );
  assert.match(diagnostic, /0o600/);
  assert.match(diagnostic, /directory\.mode & 0o777\) !== 0o700/);
  assert.match(diagnostic, /realpathSync\(this\.root\) !== this\.root/);
  assert.match(diagnostic, /directory\.uid !== process\.geteuid\?\.\(\)/);
  assert.doesNotMatch(diagnostic, /catch\s*\([^)]*\)/);
  assert.doesNotMatch(
    diagnostic,
    /console\.|process\.(?:stdout|stderr)|attach\(|screenshot\(|trace\.|\.stack|\.message|JSON\.stringify\(this|unlink|rmSync|chmodSync/,
  );
  assert.equal((diagnostic.match(/writeFileSync\(/g) ?? []).length, 1);
  assert.match(diagnostic, /writeFileSync\(fd, receipt,/);
  for (const file of [
    "wire.ts",
    "request-headers.ts",
    "transport-vector.ts",
    "pause-owner.ts",
    "backstop-counters.ts",
  ])
    assert.doesNotMatch(source(file), /diagnostics|stageReceipt/);
});

test("actual fixture stages and failures remain observational with originals retained", () => {
  const fixture = source("fixture.ts");
  for (const stage of bodyStages.filter((stage) => stage !== "baseline"))
    assert.ok(fixture.includes(`this.diagnostics.body("${stage}")`));
  const boundary = source("root-fetch.ts");
  assert.ok(
    boundary.indexOf('this.diagnostics?.body("baseline")') >
      boundary.indexOf('this.command("Fetch.enable"'),
  );
  assert.ok(
    boundary.indexOf('this.diagnostics?.body("baseline")') <
      boundary.indexOf('this.control.call("counts")'),
  );
  assert.match(fixture, /this\.closureOriginals\.push\(original\)/);
  assert.match(fixture, /Promise\.allSettled\(\[original\]\)/);
  assert.match(fixture, /status !== "fulfilled"\) failed = true/);
  assert.match(
    fixture,
    /requireOwned\(!failed && this\.errors === 0 && this\.unexpected === 0\)/,
  );
  assert.match(fixture, /ignoreHTTPSErrors: false/);
  assert.match(fixture, /this\.browser\.version\(\) === "157\.0\.8090\.0"/);
  for (const file of ["flow.spec.ts", "network.spec.ts"]) {
    const spec = source(file);
    assert.match(spec, /catch \{\s*fixture\.diagnostics\.bodyFailed\(\)/);
    assert.ok(
      spec.indexOf("fixture.diagnostics.write()") >
        spec.indexOf("fixture.close()"),
    );
    assert.ok(
      spec.indexOf("fixture.diagnostics.write()") < spec.lastIndexOf("throw "),
    );
  }
  assert.match(
    source("flow.spec.ts"),
    /if \(primary \|\| cleanup\) throw safeFailure\(\)/,
  );
  assert.match(
    source("network.spec.ts"),
    /result\.primaryFailed \|\| result\.closeFailed \|\| result\.drainFailed/,
  );
  const parent = readFileSync(
    new URL(
      "../dotnet/tests/Braid.Tests/Identity/AuthBrowserHttpsQualificationTests.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const drain = parent.slice(
    parent.indexOf("private async Task DrainAsync("),
    parent.indexOf("internal async Task RequireSuccessAsync("),
  );
  assert.match(drain, /No raw child output is retained or forwarded/);
  assert.doesNotMatch(
    drain,
    /WriteLine|WriteAll|AppendAll|ITestOutputHelper|stage-diagnostics/,
  );
});
