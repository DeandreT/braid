// Receipt-side custody, never an authority for a browser-wide clean result.
// Producer tickets must refer to the generation that issued them. Only the
// holder can transfer/retire a ticket, including during teardown.
export class RunCustody {
  #byFrame = new Map();
  #byGeneration = new Map();
  #live = new Map();
  #phase = "open";
  #loss = null;
  #receipts = new Set();
  #reject(reason) {
    this.#loss ??= reason;
    throw Error(this.#loss);
  }
  #original(id) {
    const g = this.#byGeneration.get(id);
    if (!g || g.retired || this.#byFrame.get(g.frame) !== id)
      this.#reject("original-generation-lost");
    return g;
  }
  enroll(frame, id) {
    if (
      this.#phase !== "open" ||
      this.#loss ||
      this.#byGeneration.size >= 256 ||
      typeof frame !== "string" ||
      !frame.length ||
      frame.length > 128 ||
      typeof id !== "string" ||
      !/^[0-9A-F]{32}$/.test(id) ||
      id.length !== 32 ||
      this.#byGeneration.has(id)
    )
      this.#reject("invalid-enrollment");
    const previous = this.#byFrame.get(frame);
    if (previous !== undefined) this.retire(previous);
    this.#byFrame.set(frame, id);
    this.#byGeneration.set(id, { frame, retired: false });
  }
  root(id) {
    if (this.#phase !== "open" || this.#loss) this.#reject("late-publication");
    this.#original(id);
    return this.#issue(id);
  }
  #issue(id) {
    if (this.#live.size >= 16384) this.#reject("producer-limit");
    const token = Object.freeze({});
    this.#live.set(token, id);
    return token;
  }
  child(parent) {
    const id = this.#live.get(parent);
    this.#original(id);
    if (this.#loss || this.#phase === "disposed") this.#reject("late-transfer");
    return this.#issue(id);
  }
  finish(ticket, id) {
    this.#original(id);
    if (this.#live.get(ticket) !== id) this.#reject("wrong-producer");
    this.#live.delete(ticket);
  }
  receipt(id, dispatch) {
    this.#original(id);
    if (
      this.#phase === "disposed" ||
      this.#loss ||
      typeof dispatch !== "string" ||
      dispatch.length !== 32 ||
      !/^[0-9A-F]{32}$/.test(dispatch) ||
      this.#receipts.has(dispatch) ||
      this.#receipts.size >= 4096
    )
      this.#reject("invalid-dispatch");
    this.#receipts.add(dispatch);
  }
  retire(id) {
    const g = this.#original(id);
    if ([...this.#live.values()].includes(id))
      this.#reject("generation-retired-with-producer");
    g.retired = true;
    this.#byFrame.delete(g.frame);
  }
  unsupported() {
    this.#reject("unsupported-target");
  }
  sealIngress() {
    if (this.#phase !== "open") this.#reject("invalid-seal");
    this.#phase = "sealed";
  }
  disposed() {
    if (this.#phase !== "sealed" || this.#live.size)
      this.#reject("unsettled-disposal");
    this.#phase = "disposed";
  }
  refusal() {
    return (
      this.#loss ??
      (this.#live.size
        ? "pending-producer"
        : this.#phase !== "disposed"
          ? "teardown-not-settled"
          : "native-producer-inventory-unproven")
    );
  }
}
