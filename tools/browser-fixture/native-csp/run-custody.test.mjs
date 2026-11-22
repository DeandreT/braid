import assert from "node:assert/strict";
import test from "node:test";
import { RunCustody } from "./run-custody.mjs";
const old = "A".repeat(32),
  next = "B".repeat(32),
  dispatch = "D".repeat(32);
const make = () => {
  const r = new RunCustody();
  r.enroll("main", old);
  return r;
};
test("seal does not erase a child; settled tickets and disposal cannot attest an unknown inventory", () => {
  const r = make(),
    p = r.root(old);
  r.sealIngress();
  const c = r.child(p);
  r.finish(p, old);
  assert.equal(r.refusal(), "pending-producer");
  r.finish(c, old);
  r.receipt(old, dispatch);
  r.disposed();
  assert.equal(r.refusal(), "native-producer-inventory-unproven");
});
test("replacement cannot settle queued publication or claim a prior dispatch", () => {
  const a = make();
  a.root(old);
  assert.throws(() => a.enroll("main", next), /retired-with-producer/);
  const b = make();
  b.enroll("main", next);
  assert.throws(() => b.receipt(old, dispatch), /original-generation-lost/);
});
test("other frame, foreign ticket, duplicate delivery, teardown and unknown worker/worklet refuse", () => {
  const a = make(),
    b = make();
  a.enroll("child", next);
  const root = a.root(old);
  assert.throws(() => a.finish(root, next), /wrong-producer/);
  assert.throws(() => b.finish(root, old), /wrong-producer/);
  const d = make();
  d.receipt(old, dispatch);
  assert.throws(() => d.receipt(old, dispatch), /invalid-dispatch/);
  const e = make();
  e.root(old);
  e.sealIngress();
  assert.throws(() => e.disposed(), /unsettled-disposal/);
  const f = make();
  assert.throws(() => f.unsupported(), /unsupported-target/);
  assert.equal(f.refusal(), "unsupported-target");
  const g = make();
  g.sealIngress();
  assert.throws(() => g.root(old), /late-publication/);
});
