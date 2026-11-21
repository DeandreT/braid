// Isolated Node custody test. Input must already have been admitted and
// redacted by an independent native transport; never exposed to a web realm.
import { createHmac, randomBytes } from 'node:crypto';
import { openSync, closeSync, writeSync, fsyncSync } from 'node:fs';

export class PrefixJournal {
  #fd; #key = randomBytes(32); #epoch = randomBytes(16).toString('hex');
  #prefix = 0; #last; #failed = false; #io;
  constructor(file, io = {writeSync, fsyncSync}) {
    this.#io = io;
    this.#fd = openSync(file, 'wx', 0o600);
  }
  get epoch() { return this.#epoch; }
  accept(epoch, seq, revision, prev, commitment) {
    try {
      if (this.#failed || this.#fd === null || epoch !== this.#epoch ||
          !Number.isSafeInteger(seq) || seq < 1 || seq > 65536 ||
          !Number.isSafeInteger(revision) || revision < 1 ||
          typeof prev !== 'string' || !/^[0-9a-f]{64}$/.test(prev) || prev.length !== 64 ||
          typeof commitment !== 'string' || commitment.length !== 64 ||
          !/^[0-9a-f]{64}$/.test(commitment)) throw Error();
      const bytes = Buffer.from(JSON.stringify([epoch, seq, revision, prev, commitment]) + '\n');
      if (seq === this.#prefix && this.#last?.bytes.equals(bytes)) return this.#last.ack;
      if (seq !== this.#prefix + 1 || prev !== (this.#last?.ack.digest ?? '0'.repeat(64)) ||
          revision < (this.#last?.ack.revision ?? 0)) throw Error();
      const digest = createHmac('sha256', this.#key).update(bytes).digest('hex');
      const ack = Object.freeze({epoch, seq, revision, digest});
      if (this.#io.writeSync(this.#fd, bytes) !== bytes.length) throw Error();
      this.#io.fsyncSync(this.#fd);
      this.#last = {bytes, ack}; this.#prefix = seq;
      return ack; // Receipt only; there is deliberately no clean verdict.
    } catch {
      this.#failed = true;
      throw Error('prefix refused');
    }
  }
  close() {
    if (this.#fd === null) return;
    const fd = this.#fd; this.#fd = null; this.#key.fill(0); closeSync(fd);
  }
}
