import test from "node:test";
import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import { acknowledge, checkAck } from "./receipt-channel.mjs";
const epoch = "f".repeat(32),
  generation = "A".repeat(32),
  dispatch = "B".repeat(32);
const record = { kind: "dispatch-end", epoch, generation, dispatch };
const key = generateKeyPairSync("ed25519");
test("exact receipt only, after persistence", () => {
  let persisted = false;
  const ack = acknowledge(
    {
      epoch,
      accept() {
        persisted = true;
        return { digest: "0".repeat(64) };
      },
    },
    generation,
    record,
    key.privateKey,
  );
  assert.ok(persisted);
  checkAck(key.publicKey, epoch, generation, record, ack);
  for (const change of [
    { dispatch: generation },
    { generation: dispatch },
    { epoch: "0".repeat(32) },
    { raw: "private" },
  ])
    assert.throws(() =>
      checkAck(key.publicKey, epoch, generation, { ...record, ...change }, ack),
    );
  assert.throws(() =>
    checkAck(key.publicKey, epoch, generation, record, {
      ...ack,
      digest: "1".repeat(64),
    }),
  );
  assert.throws(() =>
    checkAck(
      generateKeyPairSync("ed25519").publicKey,
      epoch,
      generation,
      record,
      ack,
    ),
  );
  assert.throws(() =>
    acknowledge(
      {
        epoch,
        accept() {
          throw Error("storage");
        },
      },
      generation,
      record,
      key.privateKey,
    ),
  );
});
