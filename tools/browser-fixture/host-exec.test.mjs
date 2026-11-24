import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { realpathSync } from "node:fs";
import { test } from "node:test";
import { hostExecRequest, observeHostExec } from "./host-exec.mjs";

const launcher = { image: "1:100", argv: Buffer.from("bun\0operator\0") };
const target = {
  image: "1:200",
  argv: Buffer.from("dotnet\0Braid.Server.dll\0"),
};
const request = {
  pid: 21,
  parent: 20,
  startTicks: "481",
  command: ["dotnet", "Braid.Server.dll"],
  launcher,
  target,
};
const sample = (before, after, argv, changes = {}) => ({
  parent: request.parent,
  startTicks: "481",
  before,
  after,
  argv,
  ...changes,
});
const preExec = sample(launcher.image, launcher.image, launcher.argv);
const stockTransition = sample(launcher.image, target.image, target.argv);
const admitted = sample(target.image, target.image, target.argv);

function observation(samples, { end = 100, alive = () => true } = {}) {
  let time = 0;
  let reads = 0;
  const result = observeHostExec(
    request,
    alive,
    end,
    () => {
      if (reads >= samples.length) throw Error("unexpected extra read");
      const next = samples[reads++];
      if (next instanceof Error) throw next;
      return next;
    },
    async (duration) => {
      time += duration;
    },
    () => time,
  );
  return { result, reads: () => reads };
}

test("exact retained launcher image waits for exec of the same owned child", async () => {
  const { result, reads } = observation([
    preExec,
    stockTransition,
    sample(target.image, target.image, Buffer.alloc(0)),
    admitted,
  ]);
  assert.deepEqual(await result, {
    identity: { pid: 21, startTicks: "481", command: request.command },
    sawOriginal: true,
    argvArityTwo: true,
  });
  assert.equal(reads(), 4);
});

test("another child, foreign image, mismatched args or lost process are not readiness", async () => {
  for (const invalid of [
    { ...admitted, startTicks: "482" },
    { ...admitted, parent: 22 },
    { ...admitted, after: "1:301" },
    { ...admitted, argv: launcher.argv },
    { ...preExec, argv: Buffer.alloc(0) },
    new Error("procfs unavailable"),
  ]) {
    const { result, reads } = observation([preExec, invalid, admitted]);
    const refused = await result;
    assert.equal(refused.identity, null);
    assert.equal(refused.sawOriginal, true);
    assert.equal(reads(), 2);
  }
});

test("a two-argument foreign command remains a command mismatch", async () => {
  const { result, reads } = observation([
    sample(target.image, target.image, Buffer.from("dotnet\0other.dll\0")),
    admitted,
  ]);
  assert.deepEqual(await result, {
    identity: null,
    sawOriginal: true,
    argvArityTwo: true,
  });
  assert.equal(reads(), 1);
  const extraEmpty = observation([
    sample(
      target.image,
      target.image,
      Buffer.from("dotnet\0Braid.Server.dll\0\0"),
    ),
  ]);
  assert.deepEqual(await extraEmpty.result, {
    identity: null,
    sawOriginal: true,
    argvArityTwo: false,
  });
});

test("no extra read or admission after the original deadline or child loss", async () => {
  const timedOut = observation([preExec, admitted], { end: 5 });
  assert.deepEqual(await timedOut.result, {
    identity: null,
    sawOriginal: true,
    argvArityTwo: true,
  });
  assert.equal(timedOut.reads(), 1);
  let checks = 0;
  const lost = observation([admitted], { alive: () => ++checks === 1 });
  assert.deepEqual(await lost.result, {
    identity: null,
    sawOriginal: true,
    argvArityTwo: true,
  });
  assert.equal(lost.reads(), 1);
});

test(
  "the live original executable and argv are checked in procfs",
  {
    skip: process.platform !== "linux",
  },
  async () => {
    const cat = realpathSync("/usr/bin/cat");
    const child = spawn(cat, [], { stdio: ["pipe", "ignore", "ignore"] });
    const spawned = once(child, "spawn");
    const closed = new Promise((resolve) => child.once("close", resolve));
    try {
      await spawned;
      const { identity, sawOriginal } = await observeHostExec(
        hostExecRequest(child.pid, [cat]),
        () => child.exitCode === null && child.signalCode === null,
        Date.now() + 1000,
      );
      assert.equal(sawOriginal, true);
      assert.equal(identity?.pid, child.pid);
      assert.deepEqual(identity?.command, [cat]);
      assert.match(identity?.startTicks, /^[0-9]+$/);
    } finally {
      child.stdin.end();
      await closed;
    }
  },
);
