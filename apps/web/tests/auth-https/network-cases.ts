import { requireOwned, safeFailure, type Reply } from "./wire";
import type { HttpsFixture } from "./fixture";
import { zeroBaseline, exactBackstops } from "./backstop-counters";

// Real-browser case bodies. They run through the SAME owned fixture/parent host,
// not a policy model, secondary Fetch owner or Node HTTP substitute.
export class NetworkCaseOwner {
  private readonly originals: Promise<unknown>[] = [];
  private readonly observers: Promise<PromiseSettledResult<unknown>[]>[] = [];
  private failedDocument = false;
  private operationRejected = false;
  private popupSeen = false;
  private drainOriginal: Promise<void> | null = null;
  constructor(private readonly fixture: HttpsFixture) {
    fixture.ownCaseDrain(() => this.drain());
  }
  private own<T>(original: Promise<T>): Promise<T> {
    this.originals.push(original);
    this.observers.push(Promise.allSettled([original]));
    return original;
  }
  drain(): Promise<void> {
    return (this.drainOriginal ??= this.drainCore());
  }
  private async drainCore(): Promise<void> {
    let i = 0;
    while (i < this.observers.length) {
      const batch = this.observers.slice(i);
      i += batch.length;
      await Promise.all(batch);
    }
  }
  private async document(held: boolean): Promise<void> {
    const f = this.fixture,
      target = f.networkBoundary.vector.target(),
      page = f.page!;
    // First waiter original retained BEFORE triggering navigation; fixed predicate
    // closes over private URL, with no URL-bearing expect/assertion diagnostic.
    const request = this.own(
      page.waitForRequest(
        (value) => value.url() === target && value.isNavigationRequest(),
        { timeout: 10000 },
      ),
    );
    const failed = this.own(
      page.waitForEvent("requestfailed", {
        predicate: (value) =>
          value.url() === target && value.isNavigationRequest(),
        timeout: 10000,
      }),
    );
    const navigationOriginal = this.own(page.goto(target, { timeout: 15000 }));
    const navigation = this.own(
      navigationOriginal.then(
        (response) => ({ rejected: false, status: response?.status() ?? 0 }),
        () => ({ rejected: true, status: 0 }),
      ),
    );
    await request;
    if (held) {
      await this.own(f.networkBoundary.vector.waitAck());
      const ack = await f.control.call("counts");
      requireOwned(ack.proofPending === 1 && ack.networkActive === 0);
      if (f.networkBoundary.vector.name === "detach")
        await f.networkBoundary.detachHeldVector();
      else if (f.networkBoundary.vector.name === "stale")
        await f.networkBoundary.restartHeldVector();
      else await f.networkBoundary.sealHeldVector();
    }
    const result = await navigation;
    if (result.rejected) {
      await failed;
      this.failedDocument = true;
    } else {
      // No failure waiter is discarded on successful 403: dispose this page only
      // after the observed response, then join its original close rejection.
      requireOwned(result.status === 403);
      await this.own(page.close());
      await Promise.allSettled([failed]);
    }
    requireOwned(result.rejected || result.status === 403);
  }
  private async attemptedApi(
    kind: "frame" | "worker" | "audio" | "paint",
  ): Promise<void> {
    const target = this.fixture.networkBoundary.vector.target();
    const result = await this.own(
      this.fixture.page!.evaluate(
        async ({ kind, target }) => {
          let attempted = false,
            refusal = false;
          if (kind === "frame" || kind === "worker") {
            let remove = () => {};
            const violation = new Promise<boolean>((resolve, reject) => {
              const timer = setTimeout(() => {
                remove();
                reject(new Error("Qualification vector did not settle."));
              }, 10000);
              const listener = (event: SecurityPolicyViolationEvent) => {
                if (
                  event.isTrusted &&
                  event.effectiveDirective ===
                    (kind === "frame" ? "frame-src" : "worker-src") &&
                  event.violatedDirective === event.effectiveDirective &&
                  event.blockedURI === target &&
                  event.disposition === "enforce"
                ) {
                  clearTimeout(timer);
                  remove();
                  resolve(true);
                }
              };
              remove = () =>
                document.removeEventListener(
                  "securitypolicyviolation",
                  listener,
                );
              document.addEventListener("securitypolicyviolation", listener);
            });
            let frame: HTMLIFrameElement | null = null,
              worker: Worker | null = null;
            try {
              attempted = true;
              if (kind === "frame") {
                frame = document.createElement("iframe");
                frame.src = target;
                document.body.append(frame);
              } else {
                try {
                  worker = new Worker(target, { type: "module" });
                  worker.onerror = (event) => {
                    event.preventDefault();
                  };
                } catch {
                  /* actual CSP event still mandatory */
                }
              }
              refusal = await violation;
            } finally {
              remove();
              frame?.remove();
              worker?.terminate();
            }
          } else {
            let audio: AudioContext | null = null;
            try {
              attempted = true;
              if (kind === "audio") {
                audio = new AudioContext();
                await audio.audioWorklet.addModule(target);
              } else {
                const paint = (
                  CSS as unknown as {
                    paintWorklet?: { addModule(url: string): Promise<void> };
                  }
                ).paintWorklet;
                if (!paint) return { attempted: false, refusal: false };
                await paint.addModule(target);
              }
            } catch {
              refusal = true;
            } finally {
              if (audio) await audio.close();
            }
          }
          return { attempted, refusal };
        },
        { kind, target },
      ),
    );
    requireOwned(result.attempted && result.refusal);
    if (kind === "audio" || kind === "paint") this.operationRejected = true;
  }
  async run(): Promise<void> {
    const f = this.fixture,
      page = f.page!,
      adapter = f.networkBoundary,
      name = adapter.vector.name;
    requireOwned(name);
    await this.own(page.waitForLoadState("networkidle", { timeout: 10000 }));
    await adapter.beginVector();
    const before = await f.control.call("counts");
    zeroBaseline(before, before.processGeneration);
    requireOwned(before.networkArmed);
    if (name === "disabled") {
      const absent = await this.own(
        page.evaluate(() => {
          const css = CSS as unknown as Record<string, unknown>,
            nav = navigator as unknown as Record<string, unknown>;
          return (
            !("animationWorklet" in css) &&
            !("layoutWorklet" in css) &&
            !("sharedStorage" in window) &&
            !("joinAdInterestGroup" in nav) &&
            !("runAdAuction" in nav)
          );
        }),
      );
      requireOwned(absent);
    } else if (["frame", "worker", "audio", "paint"].includes(name))
      await this.attemptedApi(name as "frame" | "worker" | "audio" | "paint");
    else if (name === "popup") {
      const created = this.own(
        f.context!.waitForEvent("page", { timeout: 10000 }),
      );
      const attempted = await this.own(
        page.evaluate(
          (target) => window.open(target, "_blank") !== null,
          adapter.vector.target(),
        ),
      );
      const popup = await created;
      this.popupSeen = true;
      requireOwned(attempted && popup !== page);
      // The scope witness is the ORIGINAL root Fetch event and joined fail command,
      // registered before popup creation, even when page-event delivery is later.
      await this.own(adapter.scopeVectorSettled());
      await this.own(popup.close());
      requireOwned(adapter.vector.scopes === 1);
    } else if (name === "preflight") {
      // Fixed inert opaque document, no network or product content. Its actual CORS
      // fetch must cause a real OPTIONS refusal witnessed by the host counter.
      await this.own(
        page.goto(
          "data:text/html,<meta charset=utf-8><title>Qualification</title>",
        ),
      );
      const attempted = await this.own(
        page.evaluate(async (target) => {
          let rejected = false;
          try {
            await fetch(target, {
              mode: "cors",
              credentials: "omit",
              headers: { "X-Weft-Preflight-Vector": "1" },
            });
          } catch {
            rejected = true;
          }
          return { attempted: true, rejected };
        }, adapter.vector.target()),
      );
      requireOwned(attempted.attempted && attempted.rejected);
      this.operationRejected = true;
    } else await this.document(adapter.vector.held());
    await adapter.completeVector(this.failedDocument || this.operationRejected);
    const after = await f.control.call("counts");
    this.assertCounts(before, after);
    if (name === "teardown") {
      // Positive rejection/count oracle ALREADY established, then join ORIGINAL
      // context/browser closes. Teardown itself never replaces the negative oracle.
      await f.close();
    }
  }
  private assertCounts(before: Reply, after: Reply): void {
    const vector = this.fixture.networkBoundary.vector,
      name = vector.name!;
    const responseFault = name.startsWith("location-") || name === "refresh";
    const restarted = name === "stale";
    const deliveries =
      after.productDeliveries - (restarted ? 0 : before.productDeliveries);
    const refused = after.admissionRefusals;
    exactBackstops(
      after,
      before.processGeneration + (restarted ? 1 : 0),
      name,
      vector.refusals,
      false,
      vector.resent,
    );
    requireOwned(
      after.networkActive === 0 &&
        after.sessionRows === before.sessionRows &&
        after.activeRows === before.activeRows &&
        after.tokenRequests === before.tokenRequests &&
        after.authorizationRequests === before.authorizationRequests,
    );
    if (responseFault)
      // The aborted response either fails the navigation or, on a proven
      // connection, is resent by Chromium and refused for its used proof.
      requireOwned(
        deliveries === 1 &&
          refused === (vector.resent ? 1 : 0) &&
          after.networkAborts - before.networkAborts === 1 &&
          this.failedDocument,
      );
    else requireOwned(deliveries === 0); // zero unauthorized product deliveries, not all connections
    if (
      [
        "missing",
        "duplicate",
        "mismatch",
        "stale",
        "detach",
        "seal",
        "teardown",
      ].includes(name)
    )
      requireOwned(refused === 1);
    if (name === "mismatch")
      requireOwned(
        after.proofPending === 0 && after.proofUsed === before.proofUsed + 1,
      );
    if (name === "preflight")
      requireOwned(
        after.networkPreflights - before.networkPreflights === 1 &&
          refused === 1,
      );
    if (["audio", "paint"].includes(name))
      requireOwned(refused === (vector.refusals === 0 ? 1 : 0));
    if (["frame", "worker", "popup", "disabled", "proxy"].includes(name))
      requireOwned(refused === 0);
    if (name === "popup") requireOwned(this.popupSeen && vector.scopes === 1);
    if (name === "proxy")
      requireOwned(
        after.proxyRefused - before.proxyRefused >= 1 &&
          after.proxyRefused - before.proxyRefused <= 4 &&
          after.proxyApproved === before.proxyApproved &&
          this.failedDocument,
      );
    else requireOwned(after.proxyRefused === before.proxyRefused);
    if (name === "seal" || name === "teardown")
      requireOwned(after.admissionSealed && after.proofPending === 0);
    if (name === "detach")
      requireOwned(vector.detached && after.proofPending === 0);
    if (restarted)
      requireOwned(
        after.processGeneration === before.processGeneration + 1 &&
          after.ringUnchanged,
      );
  }
}
