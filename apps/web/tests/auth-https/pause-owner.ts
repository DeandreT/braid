import { requireOwned, safeFailure } from "./wire";

export type PauseStage = "request" | "response";
export type PauseLease = {
  readonly serial: number;
  readonly id: string;
  readonly stage: PauseStage;
  readonly chain: string;
  state: "open" | "claimed" | "settled";
  readonly done: Promise<void>;
  original: Promise<unknown> | null;
  failed: boolean;
};
type Entry = {
  lease: PauseLease;
  resolve: () => void;
  reject: (reason: Error) => void;
};
// One resolution owner per actual pause EVENT, not one per Fetch request forever.
// Request/response stages and redirect call-chains can legitimately reuse an id.
export class PauseOwner {
  private serial = 0;
  private readonly entries: Entry[] = [];
  private readonly observations: Promise<PromiseSettledResult<unknown>[]>[] =
    [];
  begin(id: string, stage: PauseStage, chain: string): PauseLease {
    requireOwned(
      this.entries.length < 2048 &&
        !this.entries.some(
          (entry) =>
            entry.lease.id === id &&
            entry.lease.stage === stage &&
            entry.lease.chain === chain &&
            entry.lease.state !== "settled",
        ),
    );
    let resolve!: () => void, reject!: (reason: Error) => void;
    const done = new Promise<void>((ok, fail) => {
      resolve = ok;
      reject = fail;
    });
    const lease: PauseLease = {
      serial: ++this.serial,
      id,
      stage,
      chain,
      state: "open",
      done,
      original: null,
      failed: false,
    };
    this.entries.push({ lease, resolve, reject });
    this.observations.push(Promise.allSettled([done]));
    return lease;
  }
  get active(): number {
    return this.entries.filter((entry) => entry.lease.state !== "settled")
      .length;
  }
  get failures(): number {
    return this.entries.filter((entry) => entry.lease.failed).length;
  }
  activeLeases(): PauseLease[] {
    return this.entries
      .filter((entry) => entry.lease.state !== "settled")
      .map((entry) => entry.lease);
  }
  resolve(lease: PauseLease, send: () => Promise<unknown>): Promise<void> {
    const entry = this.entries.find((value) => value.lease === lease);
    requireOwned(entry);
    if (lease.state !== "open") return lease.done; // join the SAME original, including its failure
    lease.state = "claimed"; // synchronous linearization BEFORE invoking send / any await
    let original: Promise<unknown>;
    try {
      original = send();
    } catch {
      original = Promise.reject(safeFailure());
    }
    lease.original = original;
    const observation = Promise.allSettled([original]);
    this.observations.push(observation);
    void observation.then((results) => {
      lease.state = "settled"; // NEVER before original protocol settlement
      if (results[0]?.status === "fulfilled") entry.resolve();
      else {
        lease.failed = true;
        entry.reject(safeFailure());
      }
    });
    return lease.done;
  }
  join(lease: PauseLease): Promise<void> {
    requireOwned(this.entries.some((entry) => entry.lease === lease));
    return lease.done;
  }
  async drain(): Promise<void> {
    let index = 0;
    while (index < this.observations.length) {
      const batch = this.observations.slice(index);
      index += batch.length;
      await Promise.all(batch);
    }
    requireOwned(this.active === 0 && this.failures === 0);
  }
}
