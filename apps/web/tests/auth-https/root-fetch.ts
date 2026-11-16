import { randomBytes } from "node:crypto";
import type {
  Browser,
  BrowserContext,
  CDPSession,
  Page,
} from "@playwright/test";
import { requireOwned, type Control, type Input, type Reply } from "./wire";
import {
  continuationHeaders,
  expectedTuple,
  type DocumentPhase,
  type DocumentSite,
} from "./conditional-policy";
import { TransportVector } from "./transport-vector";
import { requestHeaders } from "./request-headers";
import { PauseOwner, type PauseLease } from "./pause-owner";
import { zeroBaseline, exactBackstops } from "./backstop-counters";
import type { StageDiagnostics } from "./stage-diagnostics";

type Pause = {
  requestId: string;
  frameId: string;
  resourceType: string;
  networkId?: string;
  redirectedRequestId?: string;
  request: {
    url: string;
    method: string;
    headers: Record<string, string>;
    postData?: string;
    hasPostData?: boolean;
  };
  responseStatusCode?: number;
  responseErrorReason?: string;
  responseHeaders?: Array<{ name: string; value: string }>;
};
type Plan = {
  url: string;
  method: string;
  frame: string;
  network: string;
  callback: boolean;
  vector: boolean;
  returnTarget?: string;
};
export type AppRequest = Readonly<{
  url: URL;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
}>;
export type NativeReply = Readonly<{ callback: string }>;
type MutationResult = {
  headers: Record<string, string>;
  negative: string | null;
};
const proofHeader = "x-braid-qualification-admission";

export class BrowserRootFetch {
  readonly vector: TransportVector;
  private session: CDPSession | null = null;
  private binding: {
    context: BrowserContext;
    page: Page;
    frame: string;
    target: string;
    contextId: string;
  } | null = null;
  private readonly originals: Promise<unknown>[] = [];
  private readonly observers: Promise<PromiseSettledResult<unknown>[]>[] = [];
  private readonly pauses = new PauseOwner();
  private heldLease: PauseLease | null = null;
  private readonly plans = new Map<string, Plan>();
  private callbackEdge: { url: string; predecessor: string } | null = null;
  private returnEdge: { url: string; predecessor: string } | null = null;
  private callbackReturn = "/";
  private lastCallback: string | null = null;
  private generation = 0;
  private firstBaseline: Reply | null = null;
  private closing = false;
  private epoch = 0;
  private disconnected = false;
  private failures = 0;
  private refused = 0;
  private seen = 0;
  private icons = 0;
  private cancellations = 0;
  private readonly canceled = new Set<string>();
  private entrySite: DocumentSite = "none";
  private reloadExpected = false;
  private sealOriginal: Promise<void> | null = null;
  private closeOriginal: Promise<void> | null = null;
  private readonly counts = new Map<string, number>();
  constructor(
    private readonly cfg: Input,
    private readonly control: Control,
    private readonly native: (request: AppRequest) => Promise<NativeReply>,
    private readonly app: (request: AppRequest) => Promise<MutationResult>,
    private readonly login: (body: string) => void,
    private readonly diagnostics?: StageDiagnostics,
  ) {
    this.vector = new TransportVector(cfg, control);
  }
  private own<T>(original: Promise<T>): Promise<T> {
    this.originals.push(original);
    const observer = Promise.allSettled([original]);
    this.observers.push(observer);
    void observer.then((results) => {
      if (results[0]?.status === "rejected") ++this.failures;
    });
    return original;
  }
  private async command(
    method: Parameters<CDPSession["send"]>[0],
    params: Parameters<CDPSession["send"]>[1],
  ): Promise<unknown> {
    requireOwned(this.session && !this.disconnected);
    return this.own(this.session.send(method, params));
  }
  private resolve(
    lease: PauseLease,
    method: Parameters<CDPSession["send"]>[0],
    params: Parameters<CDPSession["send"]>[1],
  ): Promise<void> {
    return this.pauses.resolve(lease, () => this.command(method, params));
  }
  private fail(lease: PauseLease): Promise<void> {
    return this.resolve(lease, "Fetch.failRequest", {
      requestId: lease.id,
      errorReason: "BlockedByClient",
    });
  }
  private admitting(lease: PauseLease, epoch: number): boolean {
    return !this.closing && this.epoch === epoch && lease.state === "open";
  }
  async install(browser: Browser): Promise<void> {
    this.session = await this.own(browser.newBrowserCDPSession());
    this.session.on("Fetch.requestPaused", (event) => {
      void this.own(this.handle(event as Pause));
    });
    this.session.on("close", () => {
      this.disconnected = true;
      if (!this.closing && !this.vector.detached) ++this.failures;
    });
    // Browser closure ends this session without a session close notification.
    browser.on("disconnected", () => {
      this.disconnected = true;
      if (!this.closing) ++this.failures;
    });
    await this.command("Fetch.enable", {
      patterns: [
        { urlPattern: "*", requestStage: "Request" },
        { urlPattern: "*", requestStage: "Response" },
      ],
    });
    this.diagnostics?.body("baseline"); // Fetch.enable returned; no protocol operands.
    const counts = await this.control.call("counts");
    this.generation = counts.processGeneration;
    requireOwned(this.generation === 1);
    zeroBaseline(counts, this.generation);
    this.firstBaseline = Object.freeze({ ...counts });
  }
  async bind(context: BrowserContext, page: Page): Promise<void> {
    requireOwned(this.session && !this.binding && context.pages().length === 1);
    const inspection = await this.own(context.newCDPSession(page));
    try {
      const tree = await this.own(inspection.send("Page.getFrameTree")),
        target = await this.own(inspection.send("Target.getTargetInfo"));
      requireOwned(
        tree.frameTree.frame.parentId === undefined &&
          target.targetInfo.type === "page" &&
          target.targetInfo.browserContextId,
      );
      this.binding = {
        context,
        page,
        frame: tree.frameTree.frame.id,
        target: target.targetInfo.targetId,
        contextId: target.targetInfo.browserContextId,
      };
    } finally {
      await this.own(inspection.detach());
    }
    // Product reads may be canceled by the page while their pause is held. Only an
    // observed cancellation of that exact request may explain a lost continuation.
    const observer = await this.own(context.newCDPSession(page));
    observer.on("Network.loadingFailed", (event) => {
      if (!event.canceled) return;
      requireOwned(this.canceled.size < 2048);
      this.canceled.add(event.requestId);
      for (const [id, plan] of this.plans)
        if (plan.network === event.requestId && !plan.vector)
          this.plans.delete(id);
    });
    await this.own(observer.send("Network.enable"));
  }
  /** The next main-frame document is a reload of the current entry. */
  reloading(): void {
    requireOwned(
      !this.reloadExpected &&
        !this.closing &&
        !this.callbackEdge &&
        !this.returnEdge,
    );
    this.reloadExpected = true;
  }
  private async pageCanceled(network: string): Promise<boolean> {
    const end = Date.now() + 2000;
    while (!this.canceled.has(network)) {
      if (Date.now() >= end) return false;
      await new Promise<void>((resolve) => setTimeout(resolve, 20));
    }
    return true;
  }
  /** Continues an owned pause; true only when the page had canceled that request. */
  private async continued(
    lease: PauseLease,
    method: "Fetch.continueRequest" | "Fetch.continueResponse",
    params: {
      requestId: string;
      headers?: Array<{ name: string; value: string }>;
    },
    network: string,
  ): Promise<boolean> {
    let canceled = false;
    await this.pauses.resolve(lease, () =>
      this.own(
        (async () => {
          requireOwned(this.session && !this.disconnected);
          try {
            await this.session.send(method, params);
          } catch (error) {
            if (!(await this.pageCanceled(network))) throw error;
            canceled = true;
            requireOwned(++this.cancellations <= 64);
          }
        })(),
      ),
    );
    return canceled;
  }
  private headers(event: Pause): Record<string, string> {
    return requestHeaders(
      new URL(event.request.url),
      event.request.method,
      Object.entries(event.request.headers),
      this.vector.name === "preflight" && this.vector.admits(event.request.url),
    );
  }
  private async handle(event: Pause): Promise<void> {
    const stage =
      event.responseStatusCode !== undefined ||
      event.responseErrorReason !== undefined
        ? "response"
        : "request";
    const lease = this.pauses.begin(
        event.requestId,
        stage,
        event.frameId +
          "|" +
          (event.networkId ?? "") +
          "|" +
          (event.redirectedRequestId ?? ""),
      ),
      epoch = this.epoch;
    try {
      // Worklet module fetches carry no page network id or page frame.
      const worklet =
        (this.vector.name === "audio" || this.vector.name === "paint") &&
        this.vector.admits(event.request.url);
      requireOwned(
        ++this.seen <= 2048 &&
          this.pauses.active <= 64 &&
          !this.disconnected &&
          this.binding &&
          (event.networkId || worklet),
      );
      if (
        !worklet &&
        (event.frameId !== this.binding.frame ||
          this.binding.context.pages().length !== 1)
      ) {
        requireOwned(
          !this.closing &&
            this.vector.name === "popup" &&
            this.vector.admits(event.request.url),
        );
        this.vector.expectedRefusal(true);
        await this.fail(lease);
        return;
      }
      requireOwned(
        (this.closing || !this.binding.page.isClosed()) &&
          this.binding.page.context() === this.binding.context,
      );
      if (stage === "response") {
        await this.response(event, lease, epoch);
        return;
      }
      if (
        event.resourceType === "Other" &&
        event.request.method === "GET" &&
        event.request.url === this.cfg.origin + "/favicon.ico" &&
        !event.redirectedRequestId
      ) {
        // Chromium's automatic page-icon probe is not product traffic. It fails
        // here, before any grant, and never reaches the host.
        requireOwned(++this.icons <= 40);
        await this.fail(lease);
        return;
      }
      if (
        this.vector.name === "preflight" &&
        event.request.method === "OPTIONS" &&
        this.vector.admits(event.request.url) &&
        !this.closing
      ) {
        // Chromium exposes the CORS preflight itself. It carries no proof and is
        // never altered, so the host's refusal is the witness.
        this.vector.mark();
        this.plans.set(event.requestId, {
          url: event.request.url,
          method: "OPTIONS",
          frame: event.frameId,
          network: event.networkId ?? "",
          callback: false,
          vector: true,
        });
        await this.resolve(lease, "Fetch.continueRequest", {
          requestId: event.requestId,
        });
        return;
      }
      requireOwned(
        !event.request.hasPostData && event.request.postData === undefined,
      );
      const url = new URL(event.request.url),
        headers = this.headers(event),
        document = event.resourceType === "Document";
      requireOwned(
        url.href === event.request.url &&
          !url.username &&
          !url.password &&
          !url.hash,
      );
      if (this.closing) {
        // Cancel only recognized owned work; late foreign/unknown requests still
        // fail qualification instead of being globally ignored during shutdown.
        expectedTuple(
          this.cfg,
          url,
          event.request.method,
          event.resourceType,
          url.pathname === "/api/auth/callback/entra" ? "callback" : "normal",
        );
        await this.fail(lease);
        return;
      }
      const vector = this.vector.phase !== "normal";
      if (vector) {
        requireOwned(this.vector.admits(url.href));
        this.vector.mark();
      }
      const request: AppRequest = {
        url,
        method: event.request.method,
        headers,
        body: event.request.postData,
      };
      if (!vector && url.origin === "https://login.microsoftonline.com") {
        requireOwned(
          document &&
            request.method === "GET" &&
            !headers["referer"] &&
            !headers["cookie"] &&
            !event.redirectedRequestId &&
            !this.callbackEdge &&
            !this.returnEdge,
        );
        const reply = await this.native(request);
        if (!this.admitting(lease, epoch)) {
          await this.fail(lease);
          return;
        }
        const callback = new URL(reply.callback);
        requireOwned(
          callback.origin === this.cfg.origin &&
            callback.pathname === "/api/auth/callback/entra" &&
            !callback.hash &&
            [...callback.searchParams.keys()].length === 2 &&
            callback.searchParams.getAll("code").length === 1 &&
            callback.searchParams.getAll("state").length === 1 &&
            callback.searchParams.get("code") &&
            callback.searchParams.get("state"),
        );
        this.callbackEdge = {
          url: reply.callback,
          predecessor: event.requestId,
        };
        this.lastCallback = reply.callback;
        await this.resolve(lease, "Fetch.fulfillRequest", {
          requestId: event.requestId,
          responseCode: 303,
          responseHeaders: [
            { name: "Location", value: reply.callback },
            { name: "Cache-Control", value: "no-store" },
            { name: "Referrer-Policy", value: "no-referrer" },
          ],
          body: "",
        });
        return;
      }
      if (vector && this.vector.name === "proxy") {
        requireOwned(
          document && request.method === "GET" && !event.redirectedRequestId,
        );
        this.plans.set(event.requestId, {
          url: url.href,
          method: "GET",
          frame: event.frameId,
          network: event.networkId ?? "",
          callback: false,
          vector: true,
        });
        await this.resolve(lease, "Fetch.continueRequest", {
          requestId: event.requestId,
        });
        return;
      }
      requireOwned(
        url.origin === this.cfg.origin &&
          !headers["authorization"] &&
          !headers["content-type"] &&
          request.method !== "OPTIONS",
      );
      const path = url.pathname + url.search,
        key = request.method + " " + url.pathname;
      const count = (this.counts.get(key) ?? 0) + 1;
      this.counts.set(key, count);
      requireOwned(count <= 40);
      const callback = url.pathname === "/api/auth/callback/entra";
      let phase: DocumentPhase = vector ? "vector" : "normal";
      if (callback) {
        requireOwned(
          !vector &&
            document &&
            request.method === "GET" &&
            this.callbackEdge &&
            this.callbackEdge.url === url.href &&
            (this.callbackEdge.predecessor === "replay"
              ? !event.redirectedRequestId
              : event.redirectedRequestId === this.callbackEdge.predecessor),
        );
        phase =
          this.callbackEdge.predecessor === "replay" ? "replay" : "callback";
        this.callbackEdge = null;
      } else if (event.redirectedRequestId) {
        requireOwned(
          !vector &&
            document &&
            this.returnEdge &&
            this.returnEdge.url === url.href &&
            event.redirectedRequestId === this.returnEdge.predecessor,
        );
        this.returnEdge = null;
        phase = "return";
      }
      if (
        document &&
        this.reloadExpected &&
        phase === "normal" &&
        !event.redirectedRequestId
      ) {
        requireOwned(url.href === this.binding.page.url());
        this.reloadExpected = false;
        phase = "reload";
      }
      if (document) requireOwned(!headers["origin"] && !headers["referer"]);
      if (
        vector &&
        ["audio", "paint", "worker"].includes(this.vector.name!) &&
        event.resourceType !== "Script"
      ) {
        this.vector.expectedRefusal(false);
        await this.fail(lease);
        return;
      }
      const tuple =
        vector && this.vector.name === "preflight"
          ? {
              destination: "empty",
              mode: "cors",
              site: "cross-site",
              origin: "null",
            }
          : expectedTuple(
              this.cfg,
              url,
              request.method,
              event.resourceType,
              phase,
              this.entrySite,
            );
      if (document && phase !== "reload" && phase !== "vector")
        this.entrySite = phase === "normal" ? "none" : "same-origin";
      if (vector && this.vector.name === "preflight")
        requireOwned(
          event.resourceType === "Fetch" &&
            request.method === "GET" &&
            headers["x-braid-preflight-vector"] === "1",
        );
      const mutation = vector
        ? { headers, negative: null }
        : await this.app(request);
      if (!this.admitting(lease, epoch)) {
        await this.fail(lease);
        return;
      }
      const forwarded = continuationHeaders(mutation.headers),
        proof = randomBytes(32).toString("hex");
      const returnTarget =
        key === "POST /api/auth/login"
          ? (mutation.headers["x-braid-return-to"] ?? "/")
          : undefined;
      requireOwned(
        returnTarget === undefined ||
          [
            "/",
            "/catalog",
            `/catalog/organizations/${this.cfg.organization}/projects`,
          ].includes(returnTarget),
      );
      const registration = {
        proof,
        generation: this.generation,
        rawTarget: path,
        method: request.method,
        ...tuple,
        negative: mutation.negative,
      };
      if (vector) await this.vector.registration(registration);
      else {
        const ack = await this.control.call(
          "register",
          null,
          null,
          registration,
        );
        if (!this.admitting(lease, epoch)) {
          await this.fail(lease);
          return;
        }
        requireOwned(ack.status === 204 && !ack.admissionSealed);
      }
      if (!this.admitting(lease, epoch)) {
        await this.fail(lease);
        return;
      }
      if (vector && this.vector.held()) {
        this.heldLease = lease;
        await this.vector.acknowledgedAndHold();
      }
      if (vector && this.vector.detached) {
        await this.pauses.join(lease);
        this.heldLease = null;
        return;
      }
      if (!this.admitting(lease, epoch)) {
        await this.fail(lease);
        return;
      }
      this.heldLease = null;
      this.plans.set(event.requestId, {
        url: url.href,
        method: request.method,
        frame: event.frameId,
        network: event.networkId ?? "",
        callback,
        vector,
        returnTarget,
      });
      if (vector)
        await this.resolve(lease, "Fetch.continueRequest", {
          requestId: event.requestId,
          headers: this.vector.proofHeaders(forwarded, proof),
        });
      else if (
        await this.continued(
          lease,
          "Fetch.continueRequest",
          {
            requestId: event.requestId,
            headers: [...forwarded, { name: proofHeader, value: proof }],
          },
          event.networkId ?? "",
        )
      ) {
        // The canceled request never reached the host; retire its unused grant.
        this.plans.delete(event.requestId);
        await this.control.call("withdraw", null, null, registration);
      }
    } catch {
      ++this.refused;
      try {
        await this.fail(lease);
      } catch {
        ++this.failures;
      }
    }
  }
  private async response(
    event: Pause,
    lease: PauseLease,
    epoch: number,
  ): Promise<void> {
    const plan = this.plans.get(event.requestId);
    requireOwned(
      plan &&
        plan.url === event.request.url &&
        plan.method === event.request.method &&
        plan.frame === event.frameId,
    );
    this.plans.delete(event.requestId);
    if (!this.admitting(lease, epoch)) {
      await this.fail(lease);
      return;
    }
    if (event.responseErrorReason && plan.vector) {
      this.vector.expectedError();
      await this.fail(lease);
      return;
    }
    requireOwned(!event.responseErrorReason);
    // An aborted response fault resent by Chromium returns the host's refusal.
    if (
      plan.vector &&
      event.responseStatusCode === 403 &&
      this.vector.name &&
      (this.vector.name.startsWith("location-") ||
        this.vector.name === "refresh")
    )
      this.vector.markResent();
    const headers = event.responseHeaders ?? [];
    requireOwned(
      headers.length <= 128 &&
        !headers.some((header) =>
          [proofHeader, "refresh", "upgrade"].includes(
            header.name.toLowerCase(),
          ),
        ) &&
        event.responseStatusCode !== 101,
    );
    const locations = headers.filter(
      (header) => header.name.toLowerCase() === "location",
    );
    if (event.responseStatusCode! >= 300 && event.responseStatusCode! < 400) {
      requireOwned(
        !plan.vector &&
          plan.callback &&
          event.responseStatusCode === 303 &&
          locations.length === 1 &&
          locations[0]?.value === this.callbackReturn &&
          !this.returnEdge,
      );
      this.returnEdge = {
        url: this.cfg.origin + this.callbackReturn,
        predecessor: event.requestId,
      };
    } else requireOwned(locations.length === 0);
    if (
      !plan.vector &&
      plan.method === "POST" &&
      new URL(plan.url).pathname === "/api/auth/login" &&
      event.responseStatusCode === 200
    ) {
      requireOwned(
        plan.returnTarget !== undefined &&
          [
            "/",
            "/catalog",
            `/catalog/organizations/${this.cfg.organization}/projects`,
          ].includes(plan.returnTarget),
      );
      this.callbackReturn = plan.returnTarget;
      // The page navigates as soon as it reads this body, which can leave a page
      // response-body read unsettled. Observe the same bytes here; the response
      // itself continues unmodified.
      const read = (await this.command("Fetch.getResponseBody", {
        requestId: event.requestId,
      })) as { body: string; base64Encoded: boolean };
      this.login(
        new TextDecoder("utf-8", { fatal: true }).decode(
          Buffer.from(read.body, read.base64Encoded ? "base64" : "utf8"),
        ),
      );
      if (!this.admitting(lease, epoch)) {
        await this.fail(lease);
        return;
      }
    }
    if (plan.vector)
      await this.resolve(lease, "Fetch.continueResponse", {
        requestId: event.requestId,
      });
    else
      await this.continued(
        lease,
        "Fetch.continueResponse",
        { requestId: event.requestId },
        plan.network,
      );
  }
  private async joinHandlers(): Promise<void> {
    let index = 0;
    while (index < this.observers.length) {
      const batch = this.observers.slice(index);
      index += batch.length;
      await Promise.all(batch);
    }
  }
  async beginVector(): Promise<void> {
    requireOwned(
      this.firstBaseline &&
        this.vector.name &&
        this.vector.phase === "normal" &&
        this.plans.size === 0 &&
        this.pauses.active === 0,
    );
    this.vector.arm();
    await this.joinHandlers();
    await this.control.call("barrier");
    zeroBaseline(await this.control.call("counts"), this.generation);
    await this.control.call("networkBegin");
  }
  async detachHeldVector(): Promise<void> {
    requireOwned(
      this.vector.name === "detach" &&
        this.vector.phase === "ack" &&
        this.session &&
        this.pauses.active === 1 &&
        this.heldLease,
    );
    await this.control.call("revoke");
    this.vector.detached = true;
    await this.pauses.resolve(this.heldLease, () =>
      this.own(this.session!.detach()),
    );
    requireOwned(this.disconnected);
    this.vector.resume();
  }
  async restartHeldVector(): Promise<void> {
    requireOwned(
      this.vector.name === "stale" &&
        this.vector.phase === "ack" &&
        this.pauses.active === 1,
    );
    const reply = await this.control.call("restart");
    requireOwned(
      reply.processGeneration === this.generation + 1 && reply.ringUnchanged,
    );
    zeroBaseline(reply, reply.processGeneration);
    this.generation = reply.processGeneration;
    this.vector.resume();
  }
  async sealHeldVector(): Promise<void> {
    requireOwned(
      ["seal", "teardown"].includes(this.vector.name ?? "") &&
        this.vector.phase === "ack" &&
        this.pauses.active === 1,
    );
    await this.control.call("seal");
    this.vector.resume(); // named old-proof vector, not adapter shutdown
  }
  async completeVector(settledFailure: boolean): Promise<void> {
    await this.joinHandlers();
    if (settledFailure)
      for (const [id, plan] of this.plans) {
        requireOwned(plan.vector && plan.url === this.vector.target());
        this.plans.delete(id);
      }
    requireOwned(
      this.plans.size === 0 &&
        this.pauses.active === 0 &&
        this.failures === 0 &&
        this.refused === 0,
    );
    this.vector.validate();
    await this.control.call("barrier");
    exactBackstops(
      await this.control.call("counts"),
      this.generation,
      this.vector.name,
      this.vector.refusals,
      false,
      this.vector.resent,
    );
  }
  async scopeVectorSettled(): Promise<void> {
    await this.vector.waitScope();
    await this.joinHandlers();
  }
  async replay(): Promise<string> {
    requireOwned(
      this.lastCallback &&
        !this.callbackEdge &&
        !this.returnEdge &&
        !this.closing,
    );
    await this.control.call("replay");
    this.callbackEdge = { url: this.lastCallback, predecessor: "replay" };
    return this.lastCallback;
  }
  async restarted(): Promise<void> {
    requireOwned(
      this.pauses.active === 0 && !this.callbackEdge && !this.returnEdge,
    );
    const counts = await this.control.call("counts");
    requireOwned(counts.processGeneration === this.generation + 1);
    zeroBaseline(counts, counts.processGeneration);
    this.generation = counts.processGeneration;
  }
  async settleRequests(): Promise<void> {
    const deadline = Date.now() + 10000;
    do {
      await this.joinHandlers();
      if (this.plans.size === 0 && this.pauses.active === 0) break;
      requireOwned(Date.now() < deadline);
      await new Promise<void>((resolve) => setTimeout(resolve, 20));
    } while (true);
    requireOwned(this.failures === 0 && this.refused === 0);
    await this.control.call("barrier");
  }
  seal(): Promise<void> {
    if (this.sealOriginal) return this.sealOriginal;
    this.closing = true;
    ++this.epoch;
    this.callbackEdge = null;
    this.returnEdge = null;
    return (this.sealOriginal = this.sealCore());
  }
  private async sealCore(): Promise<void> {
    const registry = this.control.call("seal"),
      observedRegistry = Promise.allSettled([registry]);
    // Every open lease is claimed synchronously. Claimed continuations are joined,
    // never raced with failRequest. First command failures remain fatal.
    const terminals = this.pauses
      .activeLeases()
      .map((lease) => this.fail(lease));
    const observedTerminals = Promise.allSettled(terminals);
    await this.vector.drain();
    await observedRegistry;
    await observedTerminals;
    await registry;
    await Promise.all(terminals);
  }
  afterBrowserClosed(): Promise<void> {
    return (this.closeOriginal ??= this.finish());
  }
  private async finish(): Promise<void> {
    requireOwned(this.closing);
    await this.joinHandlers();
    await this.pauses.drain();
    requireOwned(
      this.firstBaseline && this.disconnected && this.pauses.active === 0,
    );
    this.binding = null;
    this.plans.clear();
    this.lastCallback = null;
    requireOwned(
      this.failures === 0 &&
        this.refused === 0 &&
        (this.vector.name
          ? this.vector.phase === "settled"
          : this.vector.refusals === 0 && this.vector.errors === 0),
    );
  }
  verifyClosedObservation(reply: Reply): void {
    requireOwned(this.firstBaseline);
    exactBackstops(
      reply,
      this.generation,
      this.vector.name,
      this.vector.refusals,
      true,
      this.vector.resent,
    );
  }
}
