import { isDeepStrictEqual } from "node:util";

const zeroDigest = "0".repeat(64);
const maxReceipts = 65_536;
const limitOrigins = 4096;
export const id = (value) =>
  typeof value === "string" && value.length > 0 && value.length <= 256;
export const ordinal = (value) => Number.isSafeInteger(value) && value >= 0;
const isDigest = (value) =>
  typeof value === "string" && value.length === 64 && /^[0-9a-f]+$/.test(value);
export const genesis = Object.freeze({ sequence: 0, digest: zeroDigest });

export function checkpoint(value) {
  if (
    !ordinal(value?.sequence) ||
    !isDigest(value.digest) ||
    (value.sequence === 0) !== (value.digest === zeroDigest)
  )
    throw Error("invalid native receipt checkpoint");
  return Object.freeze({ sequence: value.sequence, digest: value.digest });
}

export function normalizeRemaining(value, final) {
  const {
    producers,
    dispatches,
    receipts,
    lateWork,
    unrelatedWork,
    droppedHandoffs,
  } = value ?? {};
  if (
    ![producers, dispatches, receipts].every((count) =>
      final ? count === 0 : ordinal(count),
    ) ||
    ![lateWork, unrelatedWork, droppedHandoffs].every((count) => count === 0)
  )
    throw Error(
      final
        ? "native disposal did not settle the original fence"
        : "native prepare did not fence the exact inventory and receipt prefix",
    );
  return Object.freeze({
    producers,
    dispatches,
    receipts,
    lateWork,
    unrelatedWork,
    droppedHandoffs,
  });
}

function counters(value) {
  return (
    ordinal(value?.produced) &&
    ordinal(value.dispatched) &&
    ordinal(value.acknowledged) &&
    value.acknowledged <= value.dispatched &&
    value.dispatched <= value.produced
  );
}

export function freezeOrigins(entries, generations, base, remaining) {
  if (!Array.isArray(entries) || entries.length > limitOrigins)
    throw Error("invalid native producer population");
  const ids = new Set();
  const expected = new Set(generations.map((item) => item.generationId));
  const covered = new Set();
  const origins = [];
  let dispatches = 0;
  let receipts = 0;
  let acknowledged = 0;
  for (const entry of entries) {
    if (
      !id(entry?.originId) ||
      ids.has(entry.originId) ||
      !expected.has(entry.generationId) ||
      entry.producerCovered !== true ||
      !counters(entry)
    )
      throw Error("invalid native producer population or coverage");
    ids.add(entry.originId);
    covered.add(entry.generationId);
    dispatches += entry.produced - entry.dispatched;
    receipts += entry.dispatched - entry.acknowledged;
    acknowledged += entry.acknowledged;
    origins.push(
      Object.freeze({
        originId: entry.originId,
        generationId: entry.generationId,
        produced: entry.produced,
        dispatched: entry.dispatched,
        acknowledged: entry.acknowledged,
      }),
    );
  }
  if (covered.size !== expected.size)
    throw Error("missing native producer coverage");
  if (
    remaining.dispatches !== dispatches ||
    remaining.receipts !== receipts ||
    acknowledged > base.sequence
  )
    throw Error("native prepare has an unaccounted dispatch or receipt");
  return Object.freeze(origins);
}

/** Structural conservation only; the paired provider must authenticate this exact claim. */
export function normalizeExtension(ticket, extension, settlements) {
  const base = checkpoint(extension?.base);
  const end = checkpoint(extension?.checkpoint);
  if (
    !isDeepStrictEqual(base, ticket.receiptCheckpoint) ||
    !Array.isArray(extension.receipts) ||
    extension.receipts.length > maxReceipts ||
    end.sequence - base.sequence !== extension.receipts.length
  )
    throw Error(
      "native receipt extension does not start at the original fence",
    );

  const population = new Map(
    ticket.origins.map((origin) => [origin.originId, origin]),
  );
  const ids = new Set();
  const received = new Map();
  const receipts = [];
  let head = base;
  for (const receipt of extension.receipts) {
    const origin = population.get(receipt?.originId);
    if (
      !origin ||
      receipt.generationId !== origin.generationId ||
      !id(receipt.recordId) ||
      ids.has(receipt.recordId) ||
      !isDigest(receipt.commitment) ||
      !isDigest(receipt.digest) ||
      receipt.digest === zeroDigest ||
      receipt.sequence !== head.sequence + 1 ||
      receipt.previousDigest !== head.digest
    )
      throw Error(
        "native receipt extension is gapped, duplicated or has an unbound origin",
      );
    ids.add(receipt.recordId);
    received.set(origin.originId, (received.get(origin.originId) ?? 0) + 1);
    head = { sequence: receipt.sequence, digest: receipt.digest };
    receipts.push(
      Object.freeze({
        ...head,
        previousDigest: receipt.previousDigest,
        originId: origin.originId,
        generationId: origin.generationId,
        recordId: receipt.recordId,
        commitment: receipt.commitment,
      }),
    );
  }
  if (!isDeepStrictEqual(head, end))
    throw Error("native receipt extension has the wrong final checkpoint");

  if (!Array.isArray(settlements) || settlements.length !== population.size)
    throw Error("native origin retirement is incomplete");
  const retired = [];
  for (const final of settlements) {
    const origin = population.get(final?.originId);
    if (
      !origin ||
      final.generationId !== origin.generationId ||
      final.retired !== true ||
      !counters(final) ||
      final.produced < origin.produced ||
      final.produced !== final.dispatched ||
      final.dispatched !== final.acknowledged ||
      final.acknowledged - origin.acknowledged !==
        (received.get(origin.originId) ?? 0)
    )
      throw Error(
        "native origin is not retired and fully settled by its receipts",
      );
    population.delete(origin.originId);
    retired.push(
      Object.freeze({
        originId: origin.originId,
        generationId: origin.generationId,
        retired: true,
        produced: final.produced,
        dispatched: final.dispatched,
        acknowledged: final.acknowledged,
      }),
    );
  }
  return Object.freeze({
    base,
    checkpoint: end,
    receipts: Object.freeze(receipts),
    settlements: Object.freeze(retired),
  });
}
