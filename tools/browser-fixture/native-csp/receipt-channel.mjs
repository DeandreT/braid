// A driver on a private inherited IPC channel transports a single redacted
// dispatch. Node alone owns the signing key. Neither returns a cutoff verdict.
import assert from "node:assert/strict";
import { createHash, sign, verify } from "node:crypto";

const nativeID = /^[A-F0-9]{32}$/;
export function recordBytes(epoch, generation, msg) {
  assert.match(epoch, /^[a-f0-9]{32}$/);
  assert.match(generation, nativeID);
  assert.deepEqual(Object.keys(msg).sort(), [
    "dispatch",
    "epoch",
    "generation",
    "kind",
  ]);
  assert.equal(msg.kind, "dispatch-end");
  assert.equal(msg.epoch, epoch);
  assert.equal(msg.generation, generation);
  assert.match(msg.dispatch, nativeID);
  return Buffer.from(
    JSON.stringify([1, msg.kind, epoch, generation, msg.dispatch]),
  );
}
export function acknowledge(journal, generation, record, privateKey) {
  const bytes = recordBytes(journal.epoch, generation, record);
  const commitment = createHash("sha256").update(bytes).digest("hex");
  const ack = journal.accept(journal.epoch, 1, 1, "0".repeat(64), commitment);
  const signature = sign(
    null,
    Buffer.concat([bytes, Buffer.from(ack.digest, "hex")]),
    privateKey,
  ).toString("hex");
  return { kind: "receipt", digest: ack.digest, signature };
}
export function checkAck(pub, epoch, generation, record, ack) {
  const bytes = recordBytes(epoch, generation, record);
  assert.equal(ack.kind, "receipt");
  assert.match(ack.digest, /^[0-9a-f]{64}$/);
  assert.match(ack.signature, /^[0-9a-f]{128}$/);
  assert.ok(
    verify(
      null,
      Buffer.concat([bytes, Buffer.from(ack.digest, "hex")]),
      pub,
      Buffer.from(ack.signature, "hex"),
    ),
  );
}
