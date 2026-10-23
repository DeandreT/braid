import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const helper = fileURLToPath(
  new URL("../../deploy/compose/acceptance-lifecycle.sh", import.meta.url),
);
const windows = process.platform === "win32";
const bash =
  process.env.WEFT_TEST_BASH ??
  (windows
    ? join(process.env.ProgramFiles ?? "C:\\Program Files", "Git/bin/bash.exe")
    : "bash");
assert.ok(!windows || existsSync(bash), "Native Git Bash is required.");
const bashPath = (path) =>
  windows
    ? path
        .replaceAll("\\", "/")
        .replace(/^([A-Za-z]):/, (_, drive) => `/${drive.toLowerCase()}`)
    : path;

function run(script, commands, { held = false, cleanupFails = false } = {}) {
  const source = readFileSync(
    new URL(`../../deploy/compose/${script}`, import.meta.url),
    "utf8",
  );
  assert.match(
    source,
    /source "\$compose_directory\/acceptance-lifecycle\.sh"/,
  );
  assert.match(source, /acceptance_complete=true\s*$/);
  const start = source.indexOf("cleanup() {");
  const end = source.indexOf("trap cleanup EXIT", start);
  assert.ok(start >= 0 && end > start);
  const cleanup = source.slice(start, end + "trap cleanup EXIT".length);
  const root = mkdtempSync(join(tmpdir(), "weft-acceptance-lifecycle-"));
  const work = join(root, "work");
  const logs = join(root, "logs");
  mkdirSync(join(work, "playwright"), { recursive: true });
  writeFileSync(join(work, "playwright", "evidence.txt"), "synthetic evidence");
  try {
    const result = spawnSync(
      bash,
      [
        "--noprofile",
        "--norc",
        "-c",
        `set -euo pipefail
source "$1"
work=$2
project=weft-fixture
WEFT_ACCEPTANCE_LOG_DIRECTORY=$3
built_images=()
runtime_image=
owned_objects() { :; }
compose() {
  case "$1" in
    logs) printf 'synthetic service diagnostics\\n' ;;
    ps) : ;;
    *) return 91 ;;
  esac
}
docker() { return 92; }
settlement_status() { return 0; }
${cleanupFails ? "rm() { return 1; }" : ""}
${cleanup}
${commands}`,
        "acceptance-lifecycle",
        bashPath(helper),
        bashPath(work),
        bashPath(logs),
      ],
      {
        encoding: "utf8",
        timeout: 10_000,
        env: {
          PATH: "/usr/bin:/bin",
          ...(windows ? { SystemRoot: process.env.SystemRoot } : {}),
          ...(held ? { WEFT_ACCEPTANCE_HOLD: "1" } : {}),
        },
      },
    );
    assert.equal(result.error, undefined, "The isolated shell must settle.");
    return {
      ...result,
      diagnostics: existsSync(
        join(
          logs,
          "weft-fixture",
          script.startsWith("endpoint") ? "endpoint.log" : "services.log",
        ),
      ),
      browserEvidence: existsSync(
        join(logs, "weft-fixture", "playwright", "evidence.txt"),
      ),
    };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

for (const script of ["acceptance-test.sh", "endpoint-acceptance-test.sh"]) {
  test(`${script}: only completed stages allow a zero exit`, () => {
    const complete = run(script, "acceptance_complete=true");
    assert.equal(complete.status, 0);
    assert.equal(complete.diagnostics, false);
    const incomplete = run(script, "exit 0");
    assert.equal(incomplete.status, 1);
    assert.equal(incomplete.diagnostics, true);
    assert.doesNotMatch(incomplete.stdout, /Acceptance passed/);
  });

  test(`${script}: an existing failure survives completed stages`, () => {
    const result = run(script, "acceptance_complete=true; exit 7");
    assert.equal(result.status, 7);
    assert.equal(result.diagnostics, true);
    assert.doesNotMatch(result.stdout, /Acceptance passed/);
  });

  for (const [signal, status] of [
    ["INT", 130],
    ["TERM", 143],
  ]) {
    test(`${script}: ${signal} during a child command retains failure diagnostics`, () => {
      const result = run(
        script,
        `acceptance_complete=true; bash -c 'kill -${signal} "$PPID"'`,
      );
      assert.equal(result.status, status);
      assert.equal(result.diagnostics, true);
      assert.doesNotMatch(result.stdout, /Acceptance passed/);
      if (script === "acceptance-test.sh") {
        assert.equal(result.browserEvidence, true);
      }
    });
  }
}

test("held-stack cleanup requires explicit completion and never claims acceptance", () => {
  const complete = run("acceptance-test.sh", "acceptance_complete=true", {
    held: true,
  });
  assert.equal(complete.status, 0);
  assert.match(complete.stdout, /Held stack removed/);
  assert.doesNotMatch(complete.stdout, /Acceptance passed/);
  const incomplete = run("acceptance-test.sh", "exit 0", { held: true });
  assert.equal(incomplete.status, 1);
  assert.equal(incomplete.diagnostics, true);
  assert.doesNotMatch(
    incomplete.stdout,
    /Held stack removed|Acceptance passed/,
  );
});

test("cleanup failure still fails a completed acceptance", () => {
  const result = run("acceptance-test.sh", "acceptance_complete=true", {
    cleanupFails: true,
  });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /Cleanup of owned resources failed/);
  assert.doesNotMatch(result.stdout, /Acceptance passed/);
});
