import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  chmodSync,
  existsSync,
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
  new URL("../../deploy/compose/endpoint-settlement.sh", import.meta.url),
);
const windows = process.platform === "win32";
// Bare `bash` on Windows can resolve to the WSL alias, which cannot use the
// native fixture's files or environment. These tests need only Git Bash.
const bash =
  process.env.BRAID_TEST_BASH ??
  (windows
    ? join(process.env.ProgramFiles ?? "C:\\Program Files", "Git/bin/bash.exe")
    : "bash");
assert.ok(
  !windows || existsSync(bash),
  "Install Git for Windows or set BRAID_TEST_BASH to its native bash.exe.",
);
const bashPath = (path) =>
  windows
    ? path
        .replaceAll("\\", "/")
        .replace(/^([A-Za-z]):/, (_, drive) => `/${drive.toLowerCase()}`)
    : path;

// A fake Docker CLI over a state file of container IDs. It answers `ps` only for the run's label
// filter and refuses every other selector, so an ancestor or name filter fails the test.
const fakeDocker = `#!/usr/bin/env bash
state=$FAKE_DOCKER_STATE
printf '%s\\n' "$*" >>"$state.calls"
case $1 in
  ps) [[ "$*" == "ps --all --quiet --filter label=io.braid.acceptance.run=$FAKE_RUN" ]] || exit 9; cat "$state" ;;
  inspect) printf '/braid-inspect-%s exited created=c started=s finished=f\\n' "\${@: -1}" ;;
  rm) id=\${@: -1}; [[ $id != stuck ]] || exit 1; grep -v -x -- "$id" "$state" >"$state.next" || true; mv "$state.next" "$state" ;;
  *) exit 8 ;;
esac
`;

function settle(containers, commands) {
  const directory = mkdtempSync(join(tmpdir(), "braid-settlement-"));
  try {
    const docker = join(directory, "docker");
    writeFileSync(docker, fakeDocker);
    chmodSync(docker, 0o700);
    const state = join(directory, "state");
    writeFileSync(state, containers.map((id) => `${id}\n`).join(""));
    const result = spawnSync(
      bash,
      [
        "--noprofile",
        "--norc",
        "-c",
        `set -u; source "$1"; ${commands}`,
        "settlement",
        bashPath(helper),
      ],
      {
        encoding: "utf8",
        env: {
          PATH: windows
            ? `${bashPath(directory)}:/usr/bin:/bin`
            : `${directory}:${process.env.PATH}`,
          ...(windows ? { SystemRoot: process.env.SystemRoot } : {}),
          FAKE_DOCKER_STATE: bashPath(state),
          FAKE_RUN: "braid-accept-run",
        },
      },
    );
    assert.equal(
      result.error,
      undefined,
      "The native settlement shell must start.",
    );
    assert.ok(
      existsSync(`${state}.calls`),
      `The settlement fixture did not reach its fake Docker CLI: ${result.stderr}`,
    );
    const calls = readFileSync(`${state}.calls`, "utf8");
    return { ...result, remaining: readFileSync(state, "utf8"), calls };
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

test("settled runtime containers are removed with evidence and fail the run", () => {
  const result = settle(
    ["a1", "b2"],
    'settle braid-accept-run "after the endpoint stopped"; settlement_status',
  );
  assert.equal(result.status, 1);
  assert.equal(result.remaining, "");
  assert.match(
    result.stderr,
    /Settled runtime container \(after the endpoint stopped\): \/braid-inspect-a1 exited created=c started=s finished=f\n/,
  );
  assert.match(result.stderr, /\/braid-inspect-b2 exited/);
  assert.match(
    result.stderr,
    /Manual settlement removed 2 runtime container\(s\); this run cannot pass, and a clean result needs a new run\./,
  );
  assert.doesNotMatch(result.calls, /ancestor|name=/);
});

test("settlement keeps failing the run after a later clean settle", () => {
  const result = settle(
    ["a1"],
    "settle braid-accept-run qualification; settle braid-accept-run cleanup; settlement_status",
  );
  assert.equal(result.status, 1);
  assert.match(result.stderr, /removed 1 runtime container/);
});

test("a run with nothing to settle passes and removes nothing", () => {
  const result = settle(
    [],
    "settle braid-accept-run cleanup && settlement_status",
  );
  assert.equal(result.status, 0);
  assert.equal(result.stderr, "");
  assert.doesNotMatch(result.calls, /^rm /m);
});

test("a container that cannot be removed fails the settlement step", () => {
  const result = settle(["stuck"], "settle braid-accept-run cleanup");
  assert.equal(result.status, 1);
  assert.equal(result.remaining, "stuck\n");
});
