import {
  requireOwned,
  safeFailure,
  type Admission,
  type Control,
  type Input,
} from "./wire";
import {
  forbiddenTarget,
  networkCase,
  type NetworkCase,
} from "./conditional-policy";

type Header = { name: string; value: string };
// Private, one-shot, case-bound controller. No arbitrary URL/op/env bypass API.
export class TransportVector {
  readonly name: NetworkCase | null;
  phase: "normal" | "armed" | "ack" | "released" | "settled" = "normal";
  requests = 0;
  refusals = 0;
  errors = 0;
  scopes = 0;
  detached = false;
  resent = false;
  private release!: () => void;
  private acknowledged!: () => void;
  private readonly ack: Promise<void>;
  private readonly hold: Promise<void>;
  constructor(
    private readonly cfg: Input,
    private readonly control: Control,
  ) {
    this.name = networkCase(cfg.scenario);
    this.ack = new Promise((resolve) => {
      this.acknowledged = resolve;
    });
    this.hold = new Promise((resolve) => {
      this.release = resolve;
    });
    // Originals retained here; both are always released/joined during finish.
  }
  arm(): void {
    requireOwned(this.name && this.phase === "normal");
    this.phase = "armed";
  }
  target(): string {
    requireOwned(this.name);
    if (this.name === "proxy") return forbiddenTarget;
    if (["audio", "paint", "worker"].includes(this.name)) {
      const asset = this.cfg.assets.find((path) => /\.js$/.test(path));
      requireOwned(asset);
      return this.cfg.origin + "/" + asset;
    }
    return this.cfg.origin + "/sign-in";
  }
  admits(url: string): boolean {
    return (
      this.phase !== "normal" &&
      this.phase !== "settled" &&
      url === this.target()
    );
  }
  mark(): void {
    requireOwned(this.phase === "armed" && ++this.requests === 1);
  }
  isProofVector(): boolean {
    return [
      "preflight",
      "missing",
      "duplicate",
      "mismatch",
      "stale",
      "detach",
      "seal",
      "teardown",
    ].includes(this.name ?? "");
  }
  held(): boolean {
    return ["stale", "detach", "seal", "teardown"].includes(this.name ?? "");
  }
  async acknowledgedAndHold(): Promise<void> {
    requireOwned(this.held() && this.phase === "armed");
    this.phase = "ack";
    this.acknowledged();
    await this.hold;
  }
  private boundedAck(): Promise<void> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(safeFailure()), 10000);
      void this.ack.then(() => {
        clearTimeout(timer);
        resolve();
      });
    });
  }
  async waitAck(): Promise<void> {
    requireOwned(this.held());
    await this.boundedAck();
  }
  resume(): void {
    requireOwned(this.phase === "ack");
    this.phase = "released";
    this.release();
  }
  async registration(value: Admission): Promise<void> {
    if (
      this.name === "preflight" ||
      this.name === "missing" ||
      this.name === "proxy"
    )
      return;
    await this.control.call(
      "register",
      null,
      null,
      this.name === "mismatch"
        ? { ...value, rawTarget: "/auth-qualification" }
        : value,
    );
  }
  proofHeaders(base: Header[], proof: string): Header[] {
    if (this.name === "preflight" || this.name === "missing") return base;
    // Chromium HeaderEntry -> HttpRequestHeaders uses SetHeader (duplicates would
    // collapse). Send an explicit duplicated carrier value so it cannot normalize
    // back into one valid proof before the real host gate sees it.
    return [
      ...base,
      {
        name: "x-weft-qualification-admission",
        value: this.name === "duplicate" ? proof + "," + proof : proof,
      },
    ];
  }
  /** Chromium resent the aborted response fault; the host refused its used proof. */
  markResent(): void {
    requireOwned(
      this.name &&
        (this.name.startsWith("location-") || this.name === "refresh") &&
        this.phase !== "normal" &&
        !this.resent,
    );
    this.resent = true;
  }
  expectedRefusal(scope: boolean): void {
    requireOwned(this.name && this.phase !== "normal" && ++this.refusals <= 1);
    if (scope) {
      requireOwned(this.name === "popup");
      ++this.scopes;
      this.acknowledged();
    } else requireOwned(["worker", "audio", "paint"].includes(this.name));
  }
  expectedError(): void {
    requireOwned(
      this.name &&
        this.phase !== "normal" &&
        ++this.errors <= 1 &&
        [
          "location-host",
          "location-ip",
          "location-port",
          "location-http",
          "location-file",
          "location-document",
          "location-duplicate",
          "refresh",
          "proxy",
          "audio",
          "paint",
        ].includes(this.name),
    );
  }
  validate(): void {
    requireOwned(this.name && this.phase !== "normal");
    if (this.name === "popup")
      requireOwned(
        this.scopes === 1 && this.refusals === 1 && this.errors === 0,
      );
    else if (["frame", "disabled", "worker"].includes(this.name))
      requireOwned(
        this.requests === 0 && this.refusals === 0 && this.errors === 0,
      );
    else if (["audio", "paint"].includes(this.name))
      requireOwned(
        this.requests === 1 && this.refusals <= 1 && this.errors <= 1,
      );
    else if (this.name === "preflight")
      requireOwned(
        this.requests <= 1 && this.refusals === 0 && this.errors === 0,
      );
    else
      requireOwned(
        this.requests === 1 && this.refusals === 0 && this.errors <= 1,
      );
    this.phase = "settled";
  }
  async drain(): Promise<void> {
    this.acknowledged();
    this.release();
    await Promise.all([this.ack, this.hold]);
  }
  async waitScope(): Promise<void> {
    requireOwned(this.name === "popup");
    await this.boundedAck();
  }
}
