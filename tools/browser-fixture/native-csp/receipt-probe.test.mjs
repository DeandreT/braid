import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync, readFileSync, writeSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { PrefixJournal } from "./receipt-probe.mjs";

const zero = "0".repeat(64),
  h = "a".repeat(64);
function fixture(t, io) {
  const dir = mkdtempSync(join(tmpdir(), "braid-prefix-")),
    file = join(dir, "journal");
  const j = new PrefixJournal(file, io);
  t.after(() => {
    j.close();
    rmSync(dir, { recursive: true });
  });
  return { j, file, first: () => j.accept(j.epoch, 1, 2, zero, h) };
}
test("persist before receipt; exact retransmission does not append", (t) => {
  const { j, file, first } = fixture(t),
    ack = first();
  assert.deepEqual(JSON.parse(readFileSync(file, "utf8")), [
    j.epoch,
    1,
    2,
    zero,
    h,
  ]);
  assert.equal(first(), ack);
  assert.equal(j.accept(j.epoch, 2, 3, ack.digest, h).seq, 2);
  assert.equal(readFileSync(file, "utf8").trimEnd().split("\n").length, 2);
});
for (const [label, args] of [
  ["gap", (j) => [j.epoch, 3, 2, zero, h]],
  ["wrong epoch", () => ["not-admitted", 2, 2, zero, h]],
  ["modified duplicate", (j) => [j.epoch, 1, 3, zero, h]],
  ["wrong predecessor", (j) => [j.epoch, 2, 2, zero, h]],
  ["backward revision", (j, a) => [j.epoch, 2, 1, a.digest, h]],
  ["raw payload", (j, a) => [j.epoch, 2, 2, a.digest, "raw"]],
])
  test(label + " permanently refuses", (t) => {
    const { j, first } = fixture(t),
      a = first();
    assert.throws(() => j.accept(...args(j, a)), /refused/);
    assert.throws(first, /refused/);
  });
for (const [name, io] of [
  [
    "fsync",
    {
      writeSync,
      fsyncSync() {
        throw Error();
      },
    },
  ],
  [
    "short write",
    {
      writeSync() {
        return 0;
      },
      fsyncSync() {
        assert.fail();
      },
    },
  ],
])
  test(name + " failure never acknowledges", (t) => {
    const { first } = fixture(t, io);
    assert.throws(first, /refused/);
    assert.throws(first, /refused/);
  });
