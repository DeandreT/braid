import {
  chromium,
  type Browser,
  type BrowserContext,
  type Locator,
  type Page,
  type Response,
} from "@playwright/test";
import {
  Control,
  input,
  requireOwned,
  safeFailure,
  strictJson,
  type Input,
  type Reply,
} from "./wire";
import { BrowserRootFetch, type AppRequest } from "./root-fetch";
import { blinkRestriction } from "./conditional-policy";

const warning =
  "Signing in opens Microsoft and reloads this page. Unsaved drafts and in-tab recovery details may be lost. Previously sent changes may already have committed.";
const unresolved = [
  "login.microsoftonline.com",
  "accounts.google.com",
  "www.google.com",
];
type Status = {
  state: string;
  authActionRequestToken: string;
  requestToken: string | null;
};
type Mutation =
  "missingAction" | "missingEpoch" | "crossPurpose" | "staleEpoch" | null;
export async function until(
  predicate: () => boolean | Promise<boolean>,
): Promise<void> {
  const end = Date.now() + 10000;
  while (Date.now() < end) {
    if (await predicate()) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 20));
  }
  throw safeFailure();
}
export class HttpsFixture {
  readonly cfg: Input = input();
  readonly control = new Control(this.cfg);
  browser: Browser | null = null;
  context: BrowserContext | null = null;
  page: Page | null = null;
  private readonly boundary = new BrowserRootFetch(
    this.cfg,
    this.control,
    (request) => this.authorize(request),
    (request) => this.admitApplication(request),
    (body) => this.loginIssued(body),
  );
  get networkBoundary(): BrowserRootFetch {
    return this.boundary;
  }
  private caseDrain: (() => Promise<void>) | null = null;
  private readonly closureOriginals: Promise<void>[] = [];
  ownCaseDrain(drain: () => Promise<void>): void {
    requireOwned(
      this.boundary.vector.name && !this.caseDrain && !this.closeOriginal,
    );
    this.caseDrain = drain;
  }

  private tasks: Array<{
    original: Promise<void>;
    observer: Promise<PromiseSettledResult<void>[]>;
  }> = [];
  private responseTail: Promise<void> = Promise.resolve();
  private closeOriginal: Promise<void> | null = null;
  private contextClose: Promise<void> | null = null;
  private browserClose: Promise<void> | null = null;
  private errors = 0;
  private unexpected = 0;
  private status: Status | null = null;
  private challenge: {
    original: Promise<string | null>;
    resolve: (url: string | null) => void;
  } | null = null;
  private challenges: Array<{
    original: Promise<string | null>;
    resolve: (url: string | null) => void;
  }> = [];
  private nativeAdmitted = false;
  private rejectedCallbackResponse: Response | null = null;
  private mutation: Mutation = null;
  private staleEpoch = "";
  native = 0;
  callbackSuccess = 0;
  callbackFailure = 0;
  logoutAcknowledgements = 0;
  loginRejections = 0;
  dualHeaders = 0;
  private sessionCookies = 0;
  private closing = false;
  private catalogAdmissions = 0;
  private projectLists = 0;
  private own(original: Promise<void>): Promise<void> {
    const observer = Promise.allSettled([original]);
    this.tasks.push({ original, observer });
    return observer.then((result) => {
      if (result[0]?.status === "rejected") ++this.errors;
    });
  }
  async open(): Promise<void> {
    this.browser = await chromium.launch({
      executablePath: this.cfg.chromium,
      headless: false,
      // The provider is reached only through Fetch fulfillment. Its navigation
      // preconnect and Chromium's own GAIA/search connections bypass the proxy
      // only to fail in the browser resolver; nothing else bypasses it.
      proxy: {
        server: this.cfg.proxy,
        bypass: [...unresolved, "<-loopback>"].join(","),
      },
      args: [
        "--disable-breakpad",
        "--disable-crash-reporter",
        "--dns-prefetch-disable",
        "--host-resolver-rules=" +
          unresolved.map((host) => `MAP ${host} ~NOTFOUND`).join(", "),
        blinkRestriction,
      ],
    });
    requireOwned(this.browser.version() === "157.0.8090.0");
    await this.boundary.install(this.browser); // BEFORE any context/page creation
    this.context = await this.browser.newContext({
      ignoreHTTPSErrors: false,
      serviceWorkers: "block",
    });
    // Manual contexts inherit no runner action timeout; every wait stays bounded.
    this.context.setDefaultTimeout(30000);
    this.context.on("page", (page) => {
      if (this.page && page !== this.page) {
        if (
          this.boundary.vector.name !== "popup" ||
          this.boundary.vector.phase !== "armed" ||
          this.context!.pages().length !== 2
        )
          ++this.unexpected;
      }
    });
    this.page = await this.context.newPage();
    await this.boundary.bind(this.context, this.page);
    this.page.on("response", (response) => {
      const original = this.responseTail.then(() => this.response(response));
      this.responseTail = this.own(original);
    });
    this.page.on("pageerror", () => {
      ++this.errors;
    });
    await this.page.goto(this.cfg.origin + "/auth-qualification");
    await this.summary("Not signed in");
    requireOwned(
      await this.page.evaluate(
        () => isSecureContext && location.protocol === "https:",
      ),
    );
  }
  private async response(response: Response): Promise<void> {
    const url = new URL(response.url());
    if (url.origin !== this.cfg.origin) return;
    if (url.pathname === "/api/auth/session" && response.status() === 200) {
      const body = strictJson(
        new TextDecoder("utf-8", { fatal: true }).decode(await response.body()),
      ) as Record<string, unknown>;
      requireOwned(
        body["version"] === 1 &&
          body["mode"] === "session" &&
          typeof body["state"] === "string" &&
          typeof body["authActionRequestToken"] === "string",
      );
      requireOwned(
        body["requestToken"] === null ||
          typeof body["requestToken"] === "string",
      );
      this.status = body as unknown as Status;
    }
    if (url.pathname === "/api/auth/login" && response.status() !== 200) {
      // An admitted 200 body is observed by the Fetch owner before delivery.
      requireOwned(response.status() === 403);
      ++this.loginRejections;
    }
    if (url.pathname === "/api/auth/callback/entra") {
      if (response.status() === 303) {
        requireOwned((await response.headerValue("location")) === "/");
        ++this.callbackSuccess;
      } else {
        requireOwned(response.status() === 401 || response.status() === 503);
        this.rejectedCallbackResponse = response;
        ++this.callbackFailure;
      }
    }
    if (url.pathname === "/api/auth/logout") {
      requireOwned(response.status() === 204);
      ++this.logoutAcknowledgements;
    }
    if (url.pathname === "/api/v1/catalog/context") {
      requireOwned(response.status() === 200);
      ++this.catalogAdmissions;
    }
    if (
      url.pathname === `/api/v1/organizations/${this.cfg.organization}/projects`
    ) {
      requireOwned(response.status() === 200);
      ++this.projectLists;
    }
  }
  private loginIssued(text: string): void {
    const body = strictJson(text) as Record<string, unknown>;
    requireOwned(
      !this.closing &&
        body["version"] === 1 &&
        typeof body["authorizationUrl"] === "string" &&
        this.challenge,
    );
    this.challenge.resolve(body["authorizationUrl"]);
  }
  private async authorize(request: AppRequest): Promise<{ callback: string }> {
    requireOwned(
      !this.closing &&
        this.challenge &&
        !this.nativeAdmitted &&
        request.method === "GET",
    );
    const expected = await this.challenge.original,
      url = request.url;
    requireOwned(
      expected &&
        url.href === expected &&
        expected.length <= 16384 &&
        url.username === "" &&
        url.password === "" &&
        url.hash === "" &&
        /^\/[0-9a-f-]{36}\/oauth2\/v2\.0\/authorize$/.test(url.pathname) &&
        !request.headers["referer"],
    );
    this.nativeAdmitted = true;
    ++this.native;
    const transient = (await this.context!.cookies(this.cfg.origin)).filter(
      (cookie) => cookie.name.startsWith("__Host-Weft.Entra."),
    );
    requireOwned(
      transient.length === 2 &&
        transient.every(
          (cookie) =>
            cookie.secure &&
            cookie.httpOnly &&
            cookie.path === "/" &&
            cookie.sameSite === "None",
        ),
    );
    const reply = await this.control.call(
      "authorize",
      expected,
      this.cfg.scenario === "nonce" ? "badNonce" : "good",
      null,
      this.cfg.scenario === "state" ? "badState" : "good",
    );
    requireOwned(reply.status === 303 && reply.callbackUrl);
    if (this.cfg.scenario === "correlation")
      await this.context!.clearCookies({
        name: /^__Host-Weft\.Entra\.Correlation\./,
      });
    return { callback: reply.callbackUrl };
  }
  private async admitApplication(
    request: AppRequest,
  ): Promise<{ headers: Record<string, string>; negative: string | null }> {
    requireOwned(!this.closing);
    const url = request.url,
      headers = request.headers,
      key = request.method + " " + url.pathname;
    if (
      url.pathname.startsWith("/api/") &&
      url.pathname !== "/api/auth/callback/entra"
    )
      requireOwned(
        (!url.search &&
          [
            "GET /api/auth/session",
            "POST /api/auth/login",
            "POST /api/auth/logout",
            "GET /api/v1/catalog/context",
            "GET /api/v1/health",
            "GET /api/v1/examples/mappings",
          ].includes(key)) ||
          (request.method === "GET" &&
            url.pathname + url.search ===
              `/api/v1/organizations/${this.cfg.organization}/projects?limit=10`),
      );
    if (key !== "POST /api/auth/login" && key !== "POST /api/auth/logout")
      return { headers, negative: null };
    await this.responseTail;
    const status = this.status;
    requireOwned(
      status &&
        request.body === undefined &&
        headers["content-type"] === undefined &&
        headers["origin"] === this.cfg.origin &&
        headers[
          key === "POST /api/auth/login" ? "x-weft-login" : "x-weft-logout"
        ] === "1" &&
        headers["x-weft-auth-token"] === status.authActionRequestToken,
    );
    requireOwned(
      headers["x-xsrf-token"] ===
        (status.state === "authenticated" ? status.requestToken : undefined),
    );
    if (status.state === "authenticated") {
      requireOwned(
        status.requestToken &&
          status.requestToken !== status.authActionRequestToken,
      );
      if (key === "POST /api/auth/login") ++this.dualHeaders;
    }
    if (key === "POST /api/auth/logout") return { headers, negative: null };
    const negative = this.mutation,
      changed = { ...headers };
    if (negative) {
      requireOwned(status.state === "authenticated");
      if (negative === "missingAction") delete changed["x-weft-auth-token"];
      if (negative === "missingEpoch") delete changed["x-xsrf-token"];
      if (negative === "crossPurpose") {
        changed["x-weft-auth-token"] = status.requestToken!;
        changed["x-xsrf-token"] = status.authActionRequestToken;
      }
      if (negative === "staleEpoch") {
        requireOwned(this.staleEpoch);
        changed["x-xsrf-token"] = this.staleEpoch;
      }
      this.mutation = null;
    }
    return { headers: changed, negative };
  }
  private panel() {
    return this.page!.getByRole("region", {
      name: "Sign-in and session",
      exact: true,
    });
  }
  async summary(text: string): Promise<void> {
    await until(
      async () =>
        (await this.page!.locator(".session-summary").textContent()) === text,
    );
  }
  // Keyboard activation avoids pointer actionability, which waits on compositor
  // frames that a loaded virtual display may not produce promptly.
  private async activate(target: Locator): Promise<void> {
    await until(
      async () => (await target.count()) === 1 && (await target.isEnabled()),
    );
    await target.focus();
    await this.page!.keyboard.press("Enter");
  }
  async check(): Promise<void> {
    await this.activate(
      this.panel().getByRole("button", { name: "Check session", exact: true }),
    );
    await this.summary("Signed in to Weft");
    await this.responseTail;
  }
  async login(authenticated: boolean): Promise<void> {
    const notice = this.page!.locator("#sign-in-navigation-warning");
    requireOwned(
      (await notice.isVisible()) &&
        (await notice.textContent())?.replace(/\s+/g, " ").trim() === warning,
    );
    this.nativeAdmitted = false;
    let resolve!: (url: string | null) => void;
    const original = new Promise<string | null>((finish) => {
      resolve = finish;
    });
    this.challenge = { original, resolve };
    this.challenges.push(this.challenge);
    await this.activate(
      this.panel().getByRole("button", {
        name: authenticated ? "Sign in again" : "Sign in",
        exact: true,
      }),
    );
  }
  async authenticated(): Promise<void> {
    await this.summary("Signed in to Weft");
    await this.responseTail;
    requireOwned(
      this.callbackSuccess > 0 &&
        (await this.page!.evaluate(
          () => isSecureContext && location.protocol === "https:",
        )),
    );
    const cookies = await this.context!.cookies(this.cfg.origin);
    const session = cookies.filter(
      (cookie) => cookie.name === "__Host-Weft.Session",
    );
    requireOwned(
      session.length === 1 &&
        session.every(
          (cookie) =>
            cookie.secure &&
            cookie.httpOnly &&
            cookie.path === "/" &&
            cookie.domain === "127.0.0.1" &&
            cookie.sameSite === "Lax" &&
            cookie.expires === -1,
        ),
    );
    const af = cookies.filter((cookie) =>
      ["__Host-Weft.AuthAction", "__Host-Weft.Antiforgery"].includes(
        cookie.name,
      ),
    );
    requireOwned(
      af.length === 2 &&
        af.every(
          (cookie) =>
            cookie.secure &&
            cookie.httpOnly &&
            cookie.sameSite === "Strict" &&
            cookie.path === "/",
        ),
    );
    requireOwned(
      !cookies.some((cookie) => cookie.name.startsWith("__Host-Weft.Entra.")),
    );
    requireOwned(
      await this.page!.evaluate(
        () =>
          !document.cookie
            .split(";")
            .some((pair) => /^\s*__Host-Weft\./.test(pair)),
      ),
    );
    ++this.sessionCookies;
  }
  private async rejectedCallbackDocument(): Promise<void> {
    const original = (async () => {
      const deadline = Date.now() + 10000;
      const remaining = () => {
        const value = deadline - Date.now();
        requireOwned(value > 0);
        return value;
      };
      try {
        // The original positive rejection event remains mandatory; close is not an oracle.
        await until(() => this.callbackFailure === 1);
        const response = this.rejectedCallbackResponse;
        requireOwned(
          response &&
            this.page &&
            response.request().isNavigationRequest() &&
            response.request().frame() === this.page.mainFrame(),
        );
        const expected = response.url();
        // Predicate retains the private URL in memory, never in an assertion message.
        await this.page.waitForURL((url) => url.href === expected, {
          waitUntil: "domcontentloaded",
          timeout: remaining(),
        });
        await this.page
          .getByRole("heading", {
            name: "Sign-in could not be completed",
            exact: true,
          })
          .waitFor({ state: "visible", timeout: remaining() });
        requireOwned(
          this.callbackFailure === 1 &&
            this.rejectedCallbackResponse === response &&
            this.page.url() === expected,
        );
      } catch {
        throw safeFailure();
      }
    })();
    // Observe/retain the SAME original immediately. The caller still awaits its
    // actual success/failure; cleanup's existing tasks join also owns rejection.
    void this.own(original);
    await original;
  }
  async negativeCallback(): Promise<void> {
    await this.rejectedCallbackDocument();
    requireOwned(
      !(await this.context!.cookies(this.cfg.origin)).some(
        (cookie) => cookie.name === "__Host-Weft.Session",
      ),
    );
    const counts = await this.control.call("counts");
    requireOwned(
      counts.sessionRows === 0 &&
        counts.activeRows === 0 &&
        counts.unexpected === 0 &&
        counts.authorizationRequests === 1 &&
        this.native === 1,
    );
    requireOwned(
      counts.tokenRequests === (this.cfg.scenario === "nonce" ? 1 : 0),
    );
    if (this.cfg.scenario === "nonce")
      requireOwned(
        counts.assertionsAccepted === 1 && counts.pkceAccepted === 1,
      );
  }
  async counts(): Promise<Reply> {
    await this.responseTail;
    return this.control.call("counts");
  }
  async catalog(): Promise<void> {
    await this.activate(
      this.page!.getByRole("navigation", {
        name: "Workspace",
        exact: true,
      }).getByRole("link", { name: "Catalog", exact: true }),
    );
    await until(
      async () =>
        new URL(this.page!.url()).pathname ===
        `/catalog/organizations/${this.cfg.organization}/projects`,
    );
    await until(() => this.catalogAdmissions >= 1 && this.projectLists >= 1);
  }
  async tokenNegatives(): Promise<void> {
    requireOwned(this.status?.requestToken);
    this.staleEpoch = this.status.requestToken;
    await this.login(true);
    await until(() => this.callbackSuccess === 2);
    await this.authenticated();
    const baseline = await this.counts();
    for (const mutation of [
      "missingAction",
      "missingEpoch",
      "crossPurpose",
      "staleEpoch",
    ] as const) {
      this.mutation = mutation;
      const before = this.loginRejections;
      await this.login(true);
      await until(() => this.loginRejections === before + 1);
      await until(
        async () =>
          (await this.panel().textContent())?.includes(
            "Sign-in could not be confirmed.",
          ) === true,
      );
      await this.check();
      const after = await this.counts();
      requireOwned(
        after.sessionRows === baseline.sessionRows &&
          after.tokenRequests === baseline.tokenRequests &&
          after.authorizationRequests === baseline.authorizationRequests &&
          this.native === 2,
      );
    }
  }
  async replay(): Promise<void> {
    const callback = await this.boundary.replay();
    await this.page!.evaluate((url) => location.assign(url), callback);
    await this.rejectedCallbackDocument();
    const after = await this.counts();
    requireOwned(after.sessionRows === 1 && after.tokenRequests === 1);
  }
  async restartAndRefresh(): Promise<void> {
    const carrier = (await this.context!.cookies(this.cfg.origin)).find(
      (cookie) => cookie.name === "__Host-Weft.Session",
    )?.value;
    requireOwned(carrier);
    this.boundary.reloading();
    await this.page!.reload();
    await this.authenticated();
    requireOwned(
      (await this.context!.cookies(this.cfg.origin)).find(
        (cookie) => cookie.name === "__Host-Weft.Session",
      )?.value === carrier,
    );
    const before = await this.counts();
    // No synthetic reload substitutes for callback: it already completed above.
    const restarted = await this.control.call("restart");
    requireOwned(
      restarted.processGeneration === before.processGeneration + 1 &&
        restarted.ringUnchanged &&
        restarted.sessionRows === before.sessionRows,
    );
    await this.boundary.restarted();
    this.boundary.reloading();
    await this.page!.reload();
    await this.authenticated();
    requireOwned(
      (await this.context!.cookies(this.cfg.origin)).find(
        (cookie) => cookie.name === "__Host-Weft.Session",
      )?.value === carrier,
    );
    const after = await this.counts();
    requireOwned(
      after.ringUnchanged &&
        after.sessionRows === before.sessionRows &&
        after.authorizationRequests === 0 &&
        after.tokenRequests === 0,
    );
  }
  async logout(): Promise<void> {
    const before = await this.counts();
    await this.activate(
      this.panel().getByRole("button", { name: "Sign out", exact: true }),
    );
    await until(() => this.logoutAcknowledgements === 1);
    await until(
      async () =>
        (await this.panel().textContent())?.includes(
          "Local sign-out was acknowledged.",
        ) === true,
    );
    const counts = await this.counts();
    requireOwned(
      counts.sessionRows === before.sessionRows &&
        counts.revokedRows === before.revokedRows + 1 &&
        counts.activeRows === before.activeRows - 1,
    );
    requireOwned(
      !(await this.context!.cookies(this.cfg.origin)).some(
        (cookie) => cookie.name === "__Host-Weft.Session",
      ),
    );
    await this.activate(
      this.panel().getByRole("button", { name: "Check session", exact: true }),
    );
    await this.summary("Not signed in");
  }
  close(): Promise<void> {
    return (this.closeOriginal ??= this.closeCore());
  }
  private async closeCore(): Promise<void> {
    this.closing = true;
    for (const challenge of this.challenges) challenge.resolve(null);
    let failed = false;
    const begin = (start: () => Promise<void>) => {
      let original: Promise<void>;
      try {
        original = start();
      } catch {
        original = Promise.reject(safeFailure());
      }
      this.closureOriginals.push(original);
      return Promise.allSettled([original]);
    };
    const join = async (observed: Promise<PromiseSettledResult<void>[]>) => {
      if ((await observed)[0]?.status !== "fulfilled") failed = true;
    };
    const attempt = async (start: () => Promise<void>) => {
      await join(begin(start));
    };
    // Initiate cancellation before joining protocol/owner drains: a pending
    // callback must not have to wait for a close that is itself waiting for it.
    const sealed = begin(() => this.boundary.seal());
    const context = begin(
      () => (this.contextClose ??= this.context?.close() ?? Promise.resolve()),
    );
    const browser = begin(
      () => (this.browserClose ??= this.browser?.close() ?? Promise.resolve()),
    );
    await join(sealed);
    await join(context);
    await join(browser);
    await attempt(() => this.boundary.afterBrowserClosed());
    let index = 0;
    while (index < this.tasks.length) {
      const batch = this.tasks.slice(index);
      index += batch.length;
      await Promise.all(batch.map((item) => item.observer));
    }
    await Promise.all(this.challenges.map((challenge) => challenge.original));
    this.challenges.length = 0;
    await this.responseTail;
    if (this.boundary.vector.name) {
      requireOwned(this.caseDrain);
      await attempt(this.caseDrain);
    }
    // Parent's final ACK is forbidden until BOTH cleanup owners' originals settle.
    requireOwned(!failed && this.errors === 0 && this.unexpected === 0);
    const closed = await this.control.call("stop");
    this.boundary.verifyClosedObservation(closed);
    await this.control.drain();
    requireOwned(this.errors === 0 && this.unexpected === 0);
    this.status = null;
    this.challenge = null;
    this.rejectedCallbackResponse = null;
    this.staleEpoch = "";
  }
}
