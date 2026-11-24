import { isDeepStrictEqual } from "node:util";
import {
  checkpoint,
  freezeOrigins,
  genesis,
  id,
  normalizeExtension,
  normalizeRemaining,
  ordinal,
} from "./qualification-journal.mjs";

// Runner-local contract, not a native protocol. The opt-in launcher still
// refuses without a reviewed browser-wide provider on the admitted driver.
const kinds = new Set(["page", "frame", "worker"]);

function identity(value) {
  if (
    !kinds.has(value?.kind) ||
    !id(value.contextId) ||
    !id(value.targetId) ||
    !id(value.generationId) ||
    !(value.kind === "page"
      ? value.parentGenerationId === null
      : id(value.parentGenerationId))
  )
    throw Error("invalid native generation identity");
  return Object.freeze({
    kind: value.kind,
    contextId: value.contextId,
    targetId: value.targetId,
    generationId: value.generationId,
    parentGenerationId: value.parentGenerationId,
  });
}

function exactSet(values, expected, key, matches) {
  if (!Array.isArray(values) || values.length !== expected.length) return false;
  const remaining = new Map(expected.map((value) => [key(value), value]));
  const matched = values.every((value) => {
    const name = key(value);
    if (!remaining.has(name) || !matches(value, remaining.get(name)))
      return false;
    remaining.delete(name);
    return true;
  });
  return matched && remaining.size === 0;
}

function report(errors, message) {
  if (errors.length > 1) throw new AggregateError(errors, message);
  if (errors.length) throw errors[0];
}

export class CatalogGenerations {
  #native;
  #session;
  #epoch;
  #timeoutMs;
  #contexts = new Map();
  #live = new Map();
  #issued = new Map();
  #fences = [];
  #tokens = new Set();
  #attempted = new Set();
  #latestInventoryRevision = -1;
  #latestCheckpoint = genesis;
  #disposing = null;
  #active = false;
  #failed = false;
  #ended = false;

  constructor(native, { epoch, timeoutMs = 10_000 } = {}) {
    if (
      !id(epoch) ||
      !native?.session ||
      typeof native.session !== "object" ||
      ![
        "enrollContext",
        "enroll",
        "prepare",
        "verify",
        "authenticateJournal",
      ].every((name) => typeof native[name] === "function") ||
      !Number.isSafeInteger(timeoutMs) ||
      timeoutMs < 1 ||
      timeoutMs > 10_000
    )
      throw Error("native browser-wide generation provider required");
    this.#native = native;
    this.#session = native.session;
    this.#epoch = epoch;
    this.#timeoutMs = timeoutMs;
  }

  // The provider must prove this is a new, owned, non-default native
  // context with no prior producers. An echoed ID is only a local check.
  async enrollContext(contextId) {
    return this.#transition(async () => {
      if (
        !id(contextId) ||
        this.#contexts.size >= 1024 ||
        this.#contexts.has(contextId)
      )
        throw Error("invalid or reused native context ID");
      const reply = await this.#call("enrollContext", this.#epoch, contextId);
      this.#stillOwned();
      if (reply?.epoch !== this.#epoch || reply.contextId !== contextId)
        throw Error("native context was not enrolled");
      this.#contexts.set(contextId, "live");
    });
  }

  // The native provider holds the producer inert until enrollment completes.
  async enroll(generation) {
    return this.#transition(() => this.#enroll(generation));
  }

  // A successful transition returns the replacement, still held by native
  // admission. Only then may the adapter release that new generation.
  async replace(generationId, replaceTarget) {
    return this.#transition(async () => {
      const previous = this.#live.get(generationId);
      const next = await this.#dispose("replace", generationId, replaceTarget);
      const newGeneration = identity(next);
      if (
        newGeneration.kind !== previous.kind ||
        newGeneration.contextId !== previous.contextId ||
        newGeneration.targetId !== previous.targetId ||
        newGeneration.parentGenerationId !== previous.parentGenerationId
      )
        throw Error("replacement changed target ownership");
      return this.#enroll(newGeneration);
    });
  }

  async close(generationId, closeTarget) {
    return this.#transition(
      () => this.#dispose("close", generationId, closeTarget),
      true,
    );
  }

  // Wrap the ordinary per-test context close, not the browser process close.
  async closeContext(contextId, ordinaryClose) {
    return this.#transition(async () => {
      if (!id(contextId) || this.#contexts.get(contextId) !== "live")
        throw Error("unknown, already closing or retired native context");
      this.#contexts.set(contextId, "closing");
      await this.#dispose("context-close", contextId, ordinaryClose);
      this.#contexts.set(contextId, "retired");
    }, true);
  }

  // Freeze the last fence on the live root. This seals the local lane
  // against further transitions; it does not verify browser retirement.
  // Actual retirement needs retained evidence after ordinary browser close.
  async prepareBrowserEnd() {
    return this.#transition(async () => {
      if (
        !this.#contexts.size ||
        this.#openContextIds().length ||
        this.#live.size
      )
        throw Error(
          "browser-end prepare requires every native context to be retired",
        );
      const ticket = await this.#prepare(
        "browser-end",
        null,
        Object.freeze([]),
        Object.freeze([]),
      );
      this.#stillOwned();
      this.#ended = true;
      return Object.freeze({ session: this.#session, ticket });
    });
  }

  // Optional early failure signal from native target events; never a receipt.
  observeDisposal(generationId) {
    if (this.#disposing?.generations.delete(generationId)) return true;
    this.#failed = true;
    return false;
  }

  // Optional signal of loss or disposal for an exact context. This does not
  // assume a CDP event exists, and an expected signal never proves retirement.
  observeContextDisposal(contextId) {
    if (this.#disposing?.contextId === contextId && contextId !== null) {
      this.#disposing.contextId = null;
      return true;
    }
    this.#failed = true;
    return false;
  }

  async #enroll(value) {
    const generation = identity(value);
    const parent = this.#live.get(generation.parentGenerationId);
    if (this.#contexts.get(generation.contextId) !== "live")
      throw Error("generation has no live enrolled native context");
    if (
      this.#issued.size >= 1024 ||
      this.#issued.has(generation.generationId) ||
      [...this.#live.values()].some(
        (item) =>
          item.kind === generation.kind &&
          item.targetId === generation.targetId,
      ) ||
      (generation.kind !== "page" &&
        (!parent ||
          parent.contextId !== generation.contextId ||
          (generation.kind === "frame" && parent.kind === "worker")))
    )
      throw Error("reused generation, live target or missing parent");
    const reply = await this.#call("enroll", this.#epoch, generation);
    this.#stillOwned();
    if (
      reply?.epoch !== this.#epoch ||
      !isDeepStrictEqual(reply.generation, generation)
    )
      throw Error("native generation was not enrolled");
    this.#issued.set(generation.generationId, generation);
    this.#live.set(generation.generationId, generation);
    return generation;
  }

  #openContextIds() {
    return [...this.#contexts]
      .filter(([, status]) => status !== "retired")
      .map(([contextId]) => contextId);
  }

  #scope(scope, root) {
    if (scope === "context-close")
      return {
        contextId: root,
        generations: [...this.#live.values()].filter(
          (item) => item.contextId === root,
        ),
      };
    if (!this.#live.has(root))
      throw Error("unknown or already disposed generation");
    const members = new Set([root]);
    const generations = [...this.#live.values()].filter((generation) => {
      if (members.has(generation.generationId)) return true;
      if (!members.has(generation.parentGenerationId)) return false;
      members.add(generation.generationId);
      return true;
    });
    return { contextId: this.#live.get(root).contextId, generations };
  }

  async #dispose(scope, root, action) {
    const selection = this.#scope(scope, root);
    const generations = Object.freeze(selection.generations);
    // Compare against independent native inventory, not just the selected
    // generations. A context can exist and be lost without any generations.
    const contextIds = Object.freeze(this.#openContextIds());
    if ((scope === "close" || scope === "replace") && this.#attempted.has(root))
      throw Error("duplicate generation close or replacement");
    for (const generation of generations)
      this.#attempted.add(generation.generationId);
    const errors = [];
    let ticket;
    try {
      this.#stillOwned();
      ticket = await this.#prepare(
        scope,
        selection.contextId,
        contextIds,
        generations,
      );
      this.#stillOwned();
      this.#disposing = {
        contextId: scope === "context-close" ? selection.contextId : null,
        generations: new Set(
          generations.map(({ generationId }) => generationId),
        ),
      };
    } catch (error) {
      errors.push(error);
    }

    let result;
    if (scope !== "replace" || !errors.length) {
      try {
        result = await action();
      } catch (error) {
        errors.push(error);
      }
    }
    let verified;
    if (ticket && this.#disposing) {
      try {
        verified = await this.#verify(ticket);
        this.#stillOwned();
      } catch (error) {
        errors.push(error);
      } finally {
        this.#disposing = null;
      }
    }
    report(errors, "native lifecycle and disposal failed");
    this.#latestInventoryRevision = verified.revision;
    this.#latestCheckpoint = verified.checkpoint;
    for (const { generationId } of generations) this.#live.delete(generationId);
    this.#fences.push(ticket);
    return result;
  }

  async #prepare(scope, contextId, contextIds, generations) {
    const browserWide = scope === "browser-end";
    const reply = await this.#call(
      "prepare",
      this.#epoch,
      Object.freeze({
        scope,
        contextId,
        expectedContexts: contextIds,
        expectedGenerations: generations,
        afterRevision: this.#latestInventoryRevision,
        previousTickets: Object.freeze([...this.#fences]),
        previousCheckpoint: this.#latestCheckpoint,
      }),
    );
    if (
      reply?.epoch !== this.#epoch ||
      reply.scope !== scope ||
      reply.contextId !== contextId ||
      reply.browserWide !== browserWide ||
      !exactSet(reply.contextIds, contextIds, (value) => value, Object.is) ||
      !id(reply.token) ||
      this.#tokens.has(reply.token) ||
      !ordinal(reply.revision) ||
      reply.revision < this.#latestInventoryRevision ||
      !exactSet(
        reply.inventory,
        generations,
        (item) => item?.generationId,
        isDeepStrictEqual,
      )
    )
      throw Error(
        "native prepare did not fence the exact inventory and receipt prefix",
      );
    const base = checkpoint(reply.receiptCheckpoint);
    if (
      base.sequence < this.#latestCheckpoint.sequence ||
      (base.sequence === this.#latestCheckpoint.sequence &&
        base.digest !== this.#latestCheckpoint.digest)
    )
      throw Error("native receipt prefix regressed or changed");
    const remaining = normalizeRemaining(reply.remaining, false);
    // This local shape cannot represent browser-only producers. Refuse an
    // outstanding population rather than dropping it from the final fence.
    if (browserWide && remaining.producers !== 0)
      throw Error("browser-end prepare has unrepresented producers");
    const ticket = Object.freeze({
      epoch: this.#epoch,
      scope,
      contextId,
      contextIds,
      browserWide,
      token: reply.token,
      revision: reply.revision,
      generations,
      receiptCheckpoint: base,
      origins: freezeOrigins(reply.origins, generations, base, remaining),
      remaining,
    });
    await this.#authenticate(
      Object.freeze({
        phase: "prepare",
        ticket,
        afterRevision: this.#latestInventoryRevision,
        previousTickets: Object.freeze([...this.#fences]),
        previousCheckpoint: this.#latestCheckpoint,
      }),
    );
    this.#tokens.add(ticket.token);
    return ticket;
  }

  async #verify(ticket) {
    const proof = await this.#call("verify", this.#epoch, ticket);
    const revision = proof?.inventoryRevision;
    const retiredContextId =
      ticket.scope === "context-close" ? ticket.contextId : null;
    const contextIds = Object.freeze(
      ticket.contextIds.filter((value) => value !== retiredContextId),
    );
    // Disposal can advance the current inventory. It cannot change the
    // original fence or authorize a non-disposal transition.
    if (
      !isDeepStrictEqual(proof?.ticket, ticket) ||
      !ordinal(revision) ||
      revision < ticket.revision ||
      proof.browserWide !== ticket.browserWide ||
      proof.retiredContextId !== retiredContextId ||
      !exactSet(proof.contextIds, contextIds, (value) => value, Object.is) ||
      !exactSet(
        proof.transitions,
        ticket.generations,
        (item) => item?.generationId,
        (item, generation) =>
          item.kind === "disposed" && item.contextId === generation.contextId,
      )
    )
      throw Error("native disposal did not settle the original fence");
    const extension = normalizeExtension(
      ticket,
      proof.extension,
      proof.settlements,
    );
    const finalizedHead = checkpoint(proof.finalizedHead);
    if (!isDeepStrictEqual(extension.checkpoint, finalizedHead))
      throw Error(
        "receipt extension does not reach the finalized journal head",
      );
    const terminal = Object.freeze({
      scope: ticket.scope,
      contextId: ticket.contextId,
      contextIds,
      retiredContextId,
      browserWide: proof.browserWide,
      inventoryRevision: revision,
      // The exact set was checked above; use the frozen original order and
      // canonical disposition rather than retaining mutable reply objects.
      transitions: Object.freeze(
        ticket.generations.map(({ contextId, generationId }) =>
          Object.freeze({ kind: "disposed", contextId, generationId }),
        ),
      ),
      remaining: normalizeRemaining(proof.remaining, true),
      finalizedHead,
    });
    await this.#authenticate(
      Object.freeze({ phase: "verify", ticket, extension, terminal }),
    );
    return {
      revision: terminal.inventoryRevision,
      checkpoint: terminal.finalizedHead,
    };
  }

  async #authenticate(claim) {
    // The guard cannot infer authenticity from that chain's shape or from
    // provider-generated counts. The same admitted session must verify the
    // complete immutable claim against its independently authenticated
    // evidence, including epoch-wide record uniqueness and checkpoint
    // ancestry. A valid MAC or an acknowledged prefix is not a terminal head.
    const evidence = await this.#call(
      "authenticateJournal",
      this.#epoch,
      claim,
    );
    if (
      evidence?.claim !== claim ||
      evidence.authenticated !== true ||
      evidence.epochRecordIdsUnique !== true ||
      (claim.phase === "prepare"
        ? evidence.checkpointAncestryVerified !== true
        : evidence.finalizedHeadVerified !== true)
    )
      throw Error("native receipt journal claim was not authenticated");
  }

  #stillOwned() {
    if (this.#failed)
      throw Error("native generation lifecycle previously refused");
  }

  async #call(name, ...args) {
    if (this.#native.session !== this.#session)
      throw Error("native browser session changed");
    const abort = new AbortController();
    let timer;
    try {
      const reply = await Promise.race([
        new Promise((_, reject) => {
          timer = setTimeout(() => {
            reject(Error(`native ${name} timed out`));
            abort.abort();
          }, this.#timeoutMs);
        }),
        Promise.resolve().then(() => this.#native[name](...args, abort.signal)),
      ]);
      if (
        this.#native.session !== this.#session ||
        reply?.session !== this.#session
      )
        throw Error("native browser session changed");
      return reply;
    } finally {
      clearTimeout(timer);
    }
  }

  async #transition(operation, cleanup = false) {
    if (this.#active || this.#ended || (this.#failed && !cleanup)) {
      this.#failed = true;
      throw Error("generation lifecycle is not available");
    }
    this.#active = true;
    try {
      const result = await operation();
      this.#stillOwned();
      return result;
    } catch (error) {
      this.#failed = true;
      throw error;
    } finally {
      this.#active = false;
    }
  }
}

/** Reconcile the product owner even if browser settlement refuses. Never retry. */
export async function runOnce(scenario, closeProduct) {
  let result;
  const errors = [];
  try {
    result = await scenario();
  } catch (error) {
    errors.push(error);
  }
  try {
    await closeProduct();
  } catch (error) {
    errors.push(error);
  }
  report(errors, "scenario and product cleanup failed");
  return result;
}
