import assert from "node:assert/strict";
import { generateKeyPairSync } from "node:crypto";
import { fork } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, rm } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { PrefixJournal } from "./receipt-probe.mjs";
import { acknowledge } from "./receipt-channel.mjs";
import { RunCustody } from "./run-custody.mjs";

for (const scenario of [
  "real",
  "replacement",
  "duplicate",
  "late",
  "synthetic",
  "missing",
  "wrong-epoch",
  "child-frame",
  "pending",
  "teardown",
]) {
  const dir = await mkdtemp(join(tmpdir(), "braid-receipt-"));
  const journal = new PrefixJournal(join(dir, "redacted.log"));
  const custody = new RunCustody();
  const { privateKey, publicKey } = generateKeyPairSync("ed25519");
  const child = fork(new URL("./receipt-driver.mjs", import.meta.url), [], {
    stdio: ["ignore", "ignore", "inherit", "ipc"],
  });
  let state = "arm",
    generation,
    error,
    receiptTicket;
  const timer = setTimeout(() => child.kill(), 15000);
  const exited = once(child, "exit");
  child.on("message", (msg) => {
    try {
      if (state === "arm") {
        assert.equal(msg.kind, "armed");
        assert.equal(msg.epoch, journal.epoch);
        assert.match(msg.generation, /^[A-F0-9]{32}$/);
        generation = msg.generation;
        custody.enroll("driver-original-root", generation);
        if (scenario === "real") receiptTicket = custody.root(generation);
        custody.sealIngress();
        state = scenario === "real" ? "dispatch" : "end";
      } else if (state === "dispatch") {
        custody.receipt(generation, msg.dispatch);
        const ack = acknowledge(journal, generation, msg, privateKey);
        state = "end";
        child.send(ack);
      } else {
        assert.equal(state, "end");
        assert.deepEqual(msg, { kind: "finished", scenario });
        if (receiptTicket) custody.finish(receiptTicket, generation);
        custody.retire(generation);
        state = "done";
      }
    } catch (e) {
      error = e;
      child.kill();
    }
  });
  try {
    child.send({
      scenario,
      epoch: journal.epoch,
      key: publicKey.export({ format: "pem", type: "spki" }),
    });
    const [code] = await exited;
    if (error) throw error;
    assert.equal(code, 0);
    assert.equal(state, "done");
    custody.disposed();
    assert.equal(custody.refusal(), "native-producer-inventory-unproven");
    console.log(
      scenario + ": local dispatch receipt control passed; cutoff unavailable",
    );
  } finally {
    clearTimeout(timer);
    journal.close();
    await rm(dir, { recursive: true });
  }
}
