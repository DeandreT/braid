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
  new URL("../../deploy/compose/test-subnets.sh", import.meta.url),
);
const windows = process.platform === "win32";
const bash =
  process.env.BRAID_TEST_BASH ??
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

// Runs write_test_subnets against a stub docker that reports the given existing subnets.
function write(networks, { existing = [], range } = {}) {
  const root = mkdtempSync(join(tmpdir(), "braid-test-subnets-"));
  try {
    const docker = join(root, "docker");
    writeFileSync(
      docker,
      `#!/usr/bin/env bash
case "$1 $2" in
  "network ls") ${existing.length ? `printf '%s\\n' ${existing.map((_, index) => `net${index}`).join(" ")}` : "true"} ;;
  "network inspect") printf '%s\\n' ${existing.join(" ") || "''"} ;;
  *) exit 1 ;;
esac
`,
    );
    chmodSync(docker, 0o755);
    const override = join(root, "subnets.yml");
    const result = spawnSync(
      bash,
      [
        "--noprofile",
        "--norc",
        "-c",
        `set -euo pipefail
PATH="$1:$PATH"
source "$2"
override=$3
shift 3
write_test_subnets "$override" "$@"`,
        "test-subnets",
        bashPath(root),
        bashPath(helper),
        bashPath(override),
        ...networks,
      ],
      {
        encoding: "utf8",
        env: {
          ...process.env,
          BRAID_TEST_SUBNET_RANGE: range ?? "",
        },
      },
    );
    return {
      status: result.status,
      stderr: result.stderr,
      override: existsSync(override) ? readFileSync(override, "utf8") : "",
    };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const subnets = (override) =>
  [...override.matchAll(/^ {2}(\w+):\n.*\n.*\n {8}- subnet: (\S+)$/gm)].map(
    ([, network, subnet]) => [network, subnet],
  );

test("each network gets a distinct /27 of the default range", () => {
  const result = write(["database", "egress", "ferrule"]);
  assert.equal(result.status, 0, result.stderr);
  const chosen = subnets(result.override);
  assert.deepEqual(
    chosen.map(([network]) => network),
    ["database", "egress", "ferrule"],
  );
  assert.equal(new Set(chosen.map(([, subnet]) => subnet)).size, 3);
  for (const [, subnet] of chosen)
    assert.match(subnet, /^198\.19\.\d+\.(\d+)\/27$/);
  for (const [, subnet] of chosen)
    assert.equal(Number(subnet.split(".")[3].split("/")[0]) % 32, 0);
});

test("existing networks and earlier choices are never overlapped", () => {
  // Of the four /27s in 10.250.0.0/25, the existing networks leave only 10.250.0.64/27 and 10.250.0.96/27.
  for (let run = 0; run < 8; run++) {
    const result = write(["database", "storage"], {
      range: "10.250.0.0/25",
      existing: ["172.17.0.0/16", "10.250.0.0/28", "10.250.0.32/27"],
    });
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(
      subnets(result.override)
        .map(([, subnet]) => subnet)
        .sort(),
      ["10.250.0.64/27", "10.250.0.96/27"],
    );
  }
});

test("a range without a free candidate fails with an exhaustion error", () => {
  const result = write(["database", "storage"], {
    range: "10.250.0.0/26",
    existing: ["10.250.0.0/27"],
  });
  assert.notEqual(result.status, 0);
  assert.match(
    result.stderr,
    /No free \/27 subnet for the storage test network in 10\.250\.0\.0\/26 after 2 candidates/,
  );
  assert.match(result.stderr, /BRAID_TEST_SUBNET_RANGE/);
});

test("invalid ranges are refused", () => {
  for (const range of [
    "2001:db8::/64",
    "198.19.0.0/28",
    "198.19.0.1/16",
    "198.019.0.0/16",
    "198.19.0.0",
    "not a range",
  ]) {
    const result = write(["database"], { range });
    assert.notEqual(result.status, 0, range);
    assert.match(
      result.stderr,
      /BRAID_TEST_SUBNET_RANGE must be an IPv4 network/,
      range,
    );
  }
});

test("only Docker pool overlap failures restart the stack on new subnets", () => {
  const root = mkdtempSync(join(tmpdir(), "braid-test-subnets-"));
  try {
    const log = join(root, "up.log");
    const overlapped = (text) => {
      writeFileSync(log, text);
      return (
        spawnSync(bash, [
          "--noprofile",
          "--norc",
          "-c",
          'source "$1"; test_subnets_overlapped "$2"',
          "test-subnets",
          bashPath(helper),
          bashPath(log),
        ]).status === 0
      );
    };
    assert.ok(
      overlapped(
        "failed to create network p_database: Error response from daemon: invalid pool request: Pool overlaps with other one on this address space\n",
      ),
    );
    assert.ok(
      !overlapped(
        "dependency failed to start: container p-migrate-1 exited (1)\n",
      ),
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
