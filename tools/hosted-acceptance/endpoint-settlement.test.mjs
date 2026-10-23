import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  chmodSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

const helper = new URL(
  "../../deploy/compose/endpoint-settlement.sh",
  import.meta.url,
).pathname;

// A fake Docker CLI over a state file of container IDs. It answers `ps` only for the run's label
// filter and refuses every other selector, so an ancestor or name filter fails the test.
const fakeDocker = `#!/usr/bin/env bash
state=$FAKE_DOCKER_STATE
printf '%s\\n' "$*" >>"$state.calls"
case $1 in
  ps) [[ "$*" == "ps --all --quiet --filter label=io.weft.acceptance.run=$FAKE_RUN" ]] || exit 9; cat "$state" ;;
  inspect) printf '/weft-inspect-%s exited created=c started=s finished=f\\n' "\${@: -1}" ;;
  rm) id=\${@: -1}; [[ $id != stuck ]] || exit 1; grep -v -x -- "$id" "$state" >"$state.next" || true; mv "$state.next" "$state" ;;
  *) exit 8 ;;
esac
`;

function settle(containers, commands) {
  const directory = mkdtempSync(join(tmpdir(), "weft-settlement-"));
  try {
    const docker = join(directory, "docker");
    writeFileSync(docker, fakeDocker);
    chmodSync(docker, 0o700);
    const state = join(directory, "state");
    writeFileSync(state, containers.map((id) => `${id}\n`).join(""));
    const result = spawnSync(
      "bash",
      ["-c", `set -u; source "$1"; ${commands}`, "settlement", helper],
      {
        encoding: "utf8",
        env: {
          PATH: `${directory}:${process.env.PATH}`,
          FAKE_DOCKER_STATE: state,
          FAKE_RUN: "weft-accept-run",
        },
      },
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
    'settle weft-accept-run "after the endpoint stopped"; settlement_status',
  );
  assert.equal(result.status, 1);
  assert.equal(result.remaining, "");
  assert.match(
    result.stderr,
    /Settled runtime container \(after the endpoint stopped\): \/weft-inspect-a1 exited created=c started=s finished=f\n/,
  );
  assert.match(result.stderr, /\/weft-inspect-b2 exited/);
  assert.match(
    result.stderr,
    /Manual settlement removed 2 runtime container\(s\); this run cannot pass, and a clean result needs a new run\./,
  );
  assert.doesNotMatch(result.calls, /ancestor|name=/);
});

test("settlement keeps failing the run after a later clean settle", () => {
  const result = settle(
    ["a1"],
    "settle weft-accept-run qualification; settle weft-accept-run cleanup; settlement_status",
  );
  assert.equal(result.status, 1);
  assert.match(result.stderr, /removed 1 runtime container/);
});

test("a run with nothing to settle passes and removes nothing", () => {
  const result = settle(
    [],
    "settle weft-accept-run cleanup && settlement_status",
  );
  assert.equal(result.status, 0);
  assert.equal(result.stderr, "");
  assert.doesNotMatch(result.calls, /^rm /m);
});

test("a container that cannot be removed fails the settlement step", () => {
  const result = settle(["stuck"], "settle weft-accept-run cleanup");
  assert.equal(result.status, 1);
  assert.equal(result.remaining, "stuck\n");
});
