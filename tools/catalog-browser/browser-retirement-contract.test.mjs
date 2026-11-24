import assert from "node:assert/strict";
import { test } from "node:test";
import { runOnce } from "./qualification-generations.mjs";
import {
  checkpoint,
  normalizeExtension,
  normalizeRemaining,
} from "./qualification-journal.mjs";

// Failure-only model. These are labels for test observations, not native
// fields, wire messages, or a way to observe Chrome. Fixtures cannot admit a run.
async function refuseRetirement(
  { owner, prepared, close, retained, exit },
  authenticateJournal,
  signal,
) {
  const { epoch, root, child } = owner;
  const ticket = prepared?.ticket; // Already normalized/authenticated at prepare.
  if (
    prepared?.session !== root ||
    prepared.when !== "before-ordinary-close-on-live-root" ||
    ticket?.epoch !== epoch ||
    ticket.scope !== "browser-end" ||
    ticket.browserWide !== true ||
    !Array.isArray(ticket.contextIds) ||
    ticket.contextIds.length !== 0
  )
    throw Error("missing exact root prepare or unretired contexts");
  if (close?.count !== 1 || close.root !== root || close.child !== child)
    throw Error("not one ordinary close of the original browser");
  if (close.outcome !== "resolved") throw Error("ordinary close failed");
  if (exit?.child !== child || exit.when !== "after-ordinary-close-start")
    throw Error("missing expected exit of the original child");
  if (exit.forced !== false || exit.signal !== null || exit.code !== 0)
    throw Error("forced or abnormal browser exit");
  if (retained?.state !== "complete")
    throw Error(`terminal evidence ${retained?.state ?? "missing"}`);
  if (retained.browserWide !== true)
    throw Error("terminal evidence lacks browser-wide coverage");
  // Reading a pre-pipe seal after EOF does not turn it into a terminal seal.
  if (retained.boundary !== "after-pipe-close")
    throw Error("no terminal coverage after pipe close");
  if (
    retained.root !== root ||
    retained.child !== child ||
    retained.epoch !== epoch ||
    retained.ticket !== ticket
  )
    throw Error("terminal evidence belongs to a different run");

  // Only the locally representable empty-population case is modeled here.
  // Real browser-level producer coverage and the shutdown witness remain
  // native requirements, not facts inferred from these zero counts.
  const extension = normalizeExtension(
    ticket,
    retained.extension,
    retained.settlements,
  );
  const finalizedHead = checkpoint(retained.finalizedHead);
  if (
    extension.checkpoint.sequence !== finalizedHead.sequence ||
    extension.checkpoint.digest !== finalizedHead.digest
  )
    throw Error("extension does not reach the claimed terminal head");
  const claim = Object.freeze({
    phase: "verify",
    ticket,
    extension,
    terminal: Object.freeze({
      scope: ticket.scope,
      browserWide: retained.browserWide,
      root,
      child,
      shutdownBoundary: retained.boundary,
      exit: Object.freeze({
        code: exit.code,
        signal: exit.signal,
        forced: exit.forced,
      }),
      remaining: normalizeRemaining(retained.remaining, true),
      finalizedHead,
    }),
  });
  // Read/authenticate retained evidence, never call verify on a CDP session
  // after its pipe closed. The verifier must independently establish the
  // whole claim, not trust the reported boundary, counts, or head.
  const evidence = await authenticateJournal(epoch, claim, signal);
  if (
    evidence?.session !== root ||
    evidence.claim !== claim ||
    evidence.authenticated !== true ||
    evidence.epochRecordIdsUnique !== true ||
    evidence.finalizedHeadVerified !== true
  )
    throw Error("terminal journal claim not authenticated");
  throw Error("model has no native admission");
}

function observation() {
  // Reference identities stand for bindings captured at original launch,
  // not JS mechanisms for proving the OS child or CDP session.
  const root = Object.freeze({});
  const child = Object.freeze({});
  const epoch = "browser-retirement-control";
  const head = checkpoint({ sequence: 1, digest: "a".repeat(64) });
  const ticket = Object.freeze({
    epoch,
    scope: "browser-end",
    browserWide: true,
    contextIds: Object.freeze([]),
    token: "root-prepare",
    receiptCheckpoint: head,
    origins: Object.freeze([]),
  });
  return {
    owner: { root, child, epoch },
    prepared: {
      session: root,
      ticket,
      when: "before-ordinary-close-on-live-root",
    },
    close: { count: 1, root, child, outcome: "resolved" },
    retained: {
      state: "complete",
      browserWide: true,
      boundary: "after-pipe-close",
      root,
      child,
      epoch,
      ticket,
      extension: { base: head, checkpoint: head, receipts: [] },
      settlements: [],
      finalizedHead: head,
      remaining: {
        producers: 0,
        dispatches: 0,
        receipts: 0,
        lateWork: 0,
        unrelatedWork: 0,
        droppedHandoffs: 0,
      },
    },
    exit: {
      child,
      when: "after-ordinary-close-start",
      forced: false,
      code: 0,
      signal: null,
    },
  };
}

test("only the original root prepare, one close, and disposition of the original child reach the terminal evidence check", async () => {
  for (const [part, change, refusal] of [
    ["prepared", { session: {} }, /exact root prepare/],
    ["prepared", { when: "after-close-start" }, /exact root prepare/],
    ["prepared", { ticket: { epoch: "other" } }, /exact root prepare/],
    ["close", { count: 0 }, /one ordinary close/],
    ["close", { count: 2 }, /one ordinary close/],
    ["close", { root: {} }, /one ordinary close/],
    ["close", { child: {} }, /one ordinary close/],
    ["close", { outcome: "rejected" }, /ordinary close failed/],
    ["exit", { child: {} }, /original child/],
    ["exit", { when: "before-ordinary-close-start" }, /expected exit/],
    ["exit", { forced: true }, /forced or abnormal/],
    ["exit", { code: null, signal: "SIGKILL" }, /forced or abnormal/],
    ["exit", { code: 1 }, /forced or abnormal/],
  ]) {
    const sample = observation();
    Object.assign(sample[part], change);
    await assert.rejects(refuseRetirement(sample, assert.fail), refusal);
  }
  const live = observation();
  live.prepared.ticket = {
    ...live.prepared.ticket,
    contextIds: ["still-live"],
  };
  await assert.rejects(
    refuseRetirement(live, assert.fail),
    /unretired contexts/,
  );
  const eofOnly = observation();
  delete eofOnly.exit;
  await assert.rejects(refuseRetirement(eofOnly, assert.fail), /expected exit/);
});

test("normal close and exit cannot repair missing, partial, lost, early, or foreign terminal evidence", async () => {
  const missing = observation();
  delete missing.retained;
  await assert.rejects(
    refuseRetirement(missing, assert.fail),
    /evidence missing/,
  );
  for (const [change, refusal] of [
    [{ state: "partial" }, /evidence partial/],
    [{ state: "lost" }, /evidence lost/],
    [{ browserWide: false }, /lacks browser-wide coverage/],
    [{ boundary: "before-pipe-close" }, /no terminal coverage/],
    [{ root: {} }, /different run/],
    [{ child: {} }, /different run/],
    [{ epoch: "foreign" }, /different run/],
    [{ ticket: {} }, /different run/],
    [
      { finalizedHead: { sequence: 2, digest: "b".repeat(64) } },
      /claimed terminal head/,
    ],
  ]) {
    const sample = observation();
    Object.assign(sample.retained, change);
    await assert.rejects(refuseRetirement(sample, assert.fail), refusal);
  }
  const pending = observation();
  pending.retained.remaining.producers = 1;
  await assert.rejects(
    refuseRetirement(pending, assert.fail),
    /did not settle/,
  );
});

test("retained authentication sees the exact frozen claim once; even a complete model echo cannot admit a run", async () => {
  for (const failure of [
    "not-final",
    "unverified-ids",
    "foreign-root",
    "other-claim",
    "lost",
    "none",
  ]) {
    const sample = observation();
    const { signal } = new AbortController();
    let calls = 0;
    const authenticateJournal = (epoch, claim, observedSignal) => {
      calls++;
      assert.equal(epoch, sample.owner.epoch);
      assert.equal(observedSignal, signal);
      assert.equal(claim.phase, "verify");
      assert.equal(claim.ticket, sample.prepared.ticket);
      assert.equal(claim.terminal.root, sample.owner.root);
      assert.equal(claim.terminal.child, sample.owner.child);
      assert.equal(claim.terminal.shutdownBoundary, "after-pipe-close");
      for (const record of [
        claim,
        claim.terminal,
        claim.terminal.exit,
        claim.extension,
        claim.terminal.finalizedHead,
      ])
        assert.equal(Object.isFrozen(record), true);
      if (failure === "lost") throw Error("authenticator lost");
      return {
        session: failure === "foreign-root" ? {} : sample.owner.root,
        claim: failure === "other-claim" ? { ...claim } : claim,
        authenticated: true,
        epochRecordIdsUnique: failure !== "unverified-ids",
        finalizedHeadVerified: failure !== "not-final",
      };
    };
    await assert.rejects(
      refuseRetirement(sample, authenticateJournal, signal),
      failure === "lost"
        ? /authenticator lost/
        : failure === "none"
          ? /no native admission/
          : /not authenticated/,
    );
    assert.equal(calls, 1);
  }
});

test("an owner revocation releases independent product cleanup while the one ordinary close is still pending", async () => {
  let revoke;
  let settleClose;
  const revoked = new Promise((_, reject) => {
    revoke = reject;
  });
  const pendingClose = new Promise((resolve) => {
    settleClose = resolve;
  });
  const deadline = Error("owner deadline revoked browser retirement");
  const cleanupFailure = Error("product reconciliation also failed");
  let closes = 0;
  let cleanups = 0;
  let closeSettled = false;
  const result = runOnce(
    async () => {
      const ordinaryClose = async () => {
        closes++;
        await pendingClose;
        closeSettled = true;
      };
      await Promise.race([ordinaryClose(), revoked]);
      assert.fail("revoked close cannot advance the fence");
    },
    async () => {
      cleanups++;
      assert.equal(closeSettled, false);
      throw cleanupFailure;
    },
  );
  revoke(deadline);
  await assert.rejects(result, (error) => {
    assert.ok(error instanceof AggregateError);
    assert.deepEqual(error.errors, [deadline, cleanupFailure]);
    return true;
  });
  settleClose(); // Late completion cannot undo the refusal.
  await pendingClose;
  assert.equal(closeSettled, true);
  assert.equal(closes, 1);
  assert.equal(cleanups, 1);
});
