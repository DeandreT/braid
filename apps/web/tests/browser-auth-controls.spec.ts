import { expect, test, type Page } from "@playwright/test";

// Controlled Angular protocol/presentation source only. These synthetic values are
// not cookies, provider facts, actual HTTPS admission, or end-to-end sign-in evidence.
const authToken = "YXV0aC1h";
const freshAuthToken = "YXV0aC1i";
const epochToken = "ZXBvY2gtYQ";
const freshEpochToken = "ZXBvY2gtYg";
const ownerA = `v1.${"A".repeat(43)}`;
const ownerB = `v1.${"B".repeat(42)}A`;
type SessionState = "anonymous" | "authenticationRequired" | "authenticated";

function status(state: SessionState, fresh = false, owner = ownerA) {
  return {
    version: 1,
    mode: "session",
    state,
    authActionRequestToken: fresh ? freshAuthToken : authToken,
    requestToken:
      state === "authenticated" ? (fresh ? freshEpochToken : epochToken) : null,
    identityContext: state === "authenticated" ? owner : null,
  };
}

const controls = (page: Page) =>
  page.getByRole("region", { name: "Sign-in and session", exact: true });
const summary = (page: Page) => controls(page).locator(".session-summary");

test.describe("controlled sign-in controls, not provider qualification", () => {
  for (const mode of ["disabled", "development"] as const) {
    test(`${mode} remains explicit and offers no auth mutation`, async ({
      page,
    }) => {
      await page.route("**/api/auth/session", (route) =>
        route.fulfill({ json: { version: 1, mode } }),
      );
      const observed = page.waitForResponse(
        (response) => new URL(response.url()).pathname === "/api/auth/session",
      );
      await page.goto("/examples");
      await observed;
      // Without browser sign-in there is no session panel or auth action.
      await expect(controls(page)).toHaveCount(0);
      await expect(page.locator(".environment-label")).toHaveText(
        mode === "development" ? "Development workspace" : "Workspace",
      );
      for (const name of ["Sign in", "Sign out", "Check session"])
        await expect(
          page.getByRole("button", { name, exact: true }),
        ).toHaveCount(0);
      await expect(
        page.getByRole("button", { name: "Review local recovery" }),
      ).toHaveCount(0);
      await expect(page.getByRole("heading", { level: 1 })).toContainText(
        "Understand your mapping.",
      );
    });
  }

  for (const code of [404, 503]) {
    test(`status ${code} is unavailable, never anonymous or Development`, async ({
      page,
    }) => {
      await page.route("**/api/auth/session", (route) =>
        route.fulfill({ status: code, body: "" }),
      );
      await page.goto("/examples");
      await expect(summary(page)).toHaveText("Sign-in status unavailable");
      await expect(
        controls(page).getByRole("button", { name: "Sign in", exact: true }),
      ).toHaveCount(0);
      await expect(
        controls(page).getByRole("button", { name: "Review local recovery" }),
      ).toHaveCount(0);
      await expect(controls(page)).toContainText(
        "does not mean you are signed out",
      );
    });
  }

  test("logout uses both distinct fresh tokens and only acknowledges exact 204", async ({
    page,
  }) => {
    let observations = 0;
    let posts = 0;
    await page.route("**/api/auth/session", (route) => {
      observations += 1;
      expect(route.request().headers()["x-weft-session"]).toBe("1");
      return route.fulfill({ json: status("authenticated", observations > 1) });
    });
    await page.route("**/api/auth/logout", async (route) => {
      posts += 1;
      const request = route.request();
      const headers = request.headers();
      expect(request.method()).toBe("POST");
      expect(request.postData()).toBeNull();
      expect(headers["content-type"]).toBeUndefined();
      expect(headers["x-weft-logout"]).toBe("1");
      expect(headers["x-weft-auth-token"]).toBe(freshAuthToken);
      expect(headers["x-xsrf-token"]).toBe(freshEpochToken);
      expect(headers["x-weft-login"]).toBeUndefined();
      expect(headers["x-weft-forget-local"]).toBeUndefined();
      await route.fulfill({ status: 204 });
    });
    await page.goto("/examples");
    await expect(summary(page)).toHaveText("Signed in to Weft");
    await controls(page)
      .getByRole("button", { name: "Sign out", exact: true })
      .click();
    await expect(controls(page)).toContainText(
      "Local sign-out was acknowledged.",
    );
    await expect(summary(page)).toHaveText("Session check needed");
    expect(posts).toBe(1);
    expect(observations).toBe(2);
  });

  for (const responseStatus of [200, 403, 503]) {
    test(`logout ${responseStatus} cannot acknowledge revocation or trigger fallback`, async ({
      page,
    }) => {
      let posts = 0;
      let forgets = 0;
      let observations = 0;
      await page.route("**/api/auth/session", (route) => {
        observations += 1;
        return route.fulfill({ json: status("authenticated") });
      });
      await page.route("**/api/auth/logout", (route) => {
        posts += 1;
        return route.fulfill({
          status: responseStatus,
          json: { version: 1, code: "unavailable" },
        });
      });
      await page.route("**/api/auth/forget-local", (route) => {
        forgets += 1;
        return route.abort();
      });
      await page.goto("/examples");
      await expect(summary(page)).toHaveText("Signed in to Weft");
      await controls(page)
        .getByRole("button", { name: "Sign out", exact: true })
        .click();
      await expect(controls(page)).toContainText("Sign-out was not confirmed.");
      await expect(controls(page)).not.toContainText(
        "Local sign-out was acknowledged.",
      );
      await expect(
        controls(page).getByRole("button", { name: "Review local recovery" }),
      ).toHaveCount(0);
      expect(posts).toBe(1);
      expect(forgets).toBe(0);
      expect(observations).toBe(2);
      await controls(page)
        .getByRole("button", { name: "Check session", exact: true })
        .click();
      await expect(summary(page)).toHaveText("Signed in to Weft");
      expect(posts).toBe(1);
      expect(observations).toBe(3);
    });
  }

  test("changed positive owner during action admission cannot inherit logout", async ({
    page,
  }) => {
    let observations = 0;
    let posts = 0;
    await page.route("**/api/auth/session", (route) => {
      observations += 1;
      return route.fulfill({
        json: status(
          "authenticated",
          false,
          observations === 1 ? ownerA : ownerB,
        ),
      });
    });
    await page.route("**/api/auth/logout", (route) => {
      posts += 1;
      return route.fulfill({ status: 204 });
    });
    await page.goto("/examples");
    await expect(summary(page)).toHaveText("Signed in to Weft");
    await controls(page)
      .getByRole("button", { name: "Sign out", exact: true })
      .click();
    await expect(controls(page)).toContainText("The action was not sent");
    expect(observations).toBe(2);
    expect(posts).toBe(0);
  });

  for (const accepted of [true, false]) {
    test(`explicit local recovery ${accepted ? "keeps revocation unconfirmed" : "refusal is not success"}`, async ({
      page,
      baseURL,
    }) => {
      let observations = 0;
      let posts = 0;
      expect(baseURL).toBeTruthy();
      // An unrelated readable framework-default cookie must not become an AF input.
      await page
        .context()
        .addCookies([
          { name: "XSRF-TOKEN", value: "c3RhbGUtYW1iaWVudA", url: baseURL! },
        ]);
      await page.route("**/api/auth/session", (route) => {
        observations += 1;
        return route.fulfill({
          json: status("authenticationRequired", observations > 1),
        });
      });
      await page.route("**/api/auth/forget-local", (route) => {
        posts += 1;
        const headers = route.request().headers();
        expect(headers["x-weft-forget-local"]).toBe("1");
        expect(headers["x-weft-auth-token"]).toBe(freshAuthToken);
        expect(headers["x-xsrf-token"]).toBeUndefined();
        expect(headers["x-weft-login"]).toBeUndefined();
        expect(headers["x-weft-logout"]).toBeUndefined();
        expect(route.request().postData()).toBeNull();
        return route.fulfill(
          accepted
            ? { json: { version: 1, outcome: "revocationUnconfirmed" } }
            : { status: 409, json: { version: 1, code: "forgetNotAvailable" } },
        );
      });
      await page.setViewportSize({ width: 390, height: 844 });
      await page.goto("/examples");
      await expect(summary(page)).toHaveText("Sign-in required");
      await controls(page)
        .getByRole("button", { name: "Review local recovery" })
        .click();
      const review = controls(page).getByRole("region", {
        name: "Forget this local session?",
      });
      await expect(review).toBeFocused();
      await expect(review).toContainText("a server session may still exist");
      expect(posts).toBe(0);
      await review.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(
        controls(page).getByRole("button", { name: "Review local recovery" }),
      ).toBeFocused();
      expect(posts).toBe(0);
      await controls(page)
        .getByRole("button", { name: "Review local recovery" })
        .click();
      await review
        .getByRole("button", {
          name: "Forget local session — revocation unconfirmed",
          exact: true,
        })
        .click();
      await expect(controls(page)).toContainText(
        accepted
          ? "Local forgetting was acknowledged. Revocation is unconfirmed"
          : "Local forgetting was not confirmed or was not available.",
      );
      if (!accepted)
        await expect(controls(page)).not.toContainText(
          "Local forgetting was acknowledged.",
        );
      expect(posts).toBe(1);
      expect(observations).toBe(2);
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth > innerWidth,
        ),
      ).toBe(false);
    });
  }

  for (const [index, authorizationUrl] of [
    "/relative",
    "http://example.invalid/",
    "https://user@example.invalid/",
    "https://example.invalid/#fragment",
    `https://example.invalid/${"x".repeat(16384)}`,
    "https://example.invalid/authorize?state=synthetic",
    "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/authorize?state=synthetic#fragment",
    `https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/authorize?state=${"x".repeat(16384)}`,
    "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=synthetic",
    "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/oauth2/v2.0/authorize?state=synthetic",
    "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/oauth2/v2.0/authorize?state=synthetic",
  ].entries()) {
    // Keep URL operands out of test titles and screenshot/report filenames.
    test(`invalid authorization destination ${index + 1} cannot navigate`, async ({
      page,
    }) => {
      let observations = 0;
      let posts = 0;
      await page.route("**/api/auth/session", (route) => {
        observations += 1;
        return route.fulfill({ json: status("anonymous", observations > 1) });
      });
      await page.route("**/api/auth/login", (route) => {
        posts += 1;
        const headers = route.request().headers();
        expect(headers["x-weft-login"]).toBe("1");
        expect(headers["x-weft-auth-token"]).toBe(freshAuthToken);
        expect(headers["x-xsrf-token"]).toBeUndefined();
        return route.fulfill({ json: { version: 1, authorizationUrl } });
      });
      await page.goto("/examples");
      await expect(summary(page)).toHaveText("Not signed in");
      await controls(page)
        .getByRole("button", { name: "Sign in", exact: true })
        .click();
      await expect(controls(page)).toContainText(
        "Sign-in could not be confirmed.",
      );
      expect(new URL(page.url()).pathname).toBe("/examples");
      expect(posts).toBe(1);
      expect(observations).toBe(2);
      await expect(controls(page)).not.toContainText(authorizationUrl);
    });
  }
});

// Additive positive controls cases. Nothing above is replaced or weakened.
// The provider request is the real native navigation, but its 204 is synthetic:
// no challenge, callback, cookies, Entra, HTTPS delivery or draft survival is proved.
type PositiveRoute = import("@playwright/test").Route;
type PositiveRequest = import("@playwright/test").Request;
type PositiveReply = Parameters<PositiveRoute["fulfill"]>[0];
const positiveEntry = "/auth-controls-positive";
// Required public interface of the pinned dvvb01V6 warning-only overlay.
const positiveUnloadWarning =
  "Signing in opens Microsoft and reloads this page. Unsaved drafts and in-tab recovery details may be lost. Previously sent changes may already have committed.";
const positiveOrganization = "10000000-0000-4000-8000-000000000001";
const positiveProjects = `/catalog/organizations/${positiveOrganization}/projects`;

function positiveRequire(condition: unknown): asserts condition {
  // Never put URLs, identities, tokens, headers or response bodies in failures.
  if (!condition) throw new Error("Controlled auth protocol invariant failed");
}

class PositiveHold {
  private release!: (reply: PositiveReply | null) => void;
  private entered!: () => void;
  private finished!: () => void;
  private readonly choice = new Promise<PositiveReply | null>((resolve) => {
    this.release = resolve;
  });
  readonly arrived = new Promise<void>((resolve) => {
    this.entered = resolve;
  });
  readonly settled = new Promise<void>((resolve) => {
    this.finished = resolve;
  });
  started = false;
  released = false;
  canceled = false;
  terminal: "fulfilled" | "aborted" | "canceled" | null = null;
  private original: PositiveRequest | null = null;

  owns(request: PositiveRequest): boolean {
    return request === this.original;
  }

  reply(reply: PositiveReply): void {
    positiveRequire(!this.released);
    this.released = true;
    this.release(reply);
  }

  cleanup(): void {
    if (!this.released) {
      this.released = true;
      this.release(null);
    }
  }

  async handle(route: PositiveRoute): Promise<void> {
    positiveRequire(!this.started);
    this.started = true;
    this.original = route.request();
    this.entered();
    try {
      const reply = await this.choice;
      try {
        if (reply === null) {
          await route.abort();
          this.terminal = "aborted";
        } else {
          await route.fulfill(reply);
          this.terminal = "fulfilled";
        }
      } catch {
        // Document replacement may already have canceled this original transport.
        // Only an observed failure for this exact request permits that outcome.
        positiveRequire(this.canceled);
        this.terminal = "canceled";
      }
    } finally {
      this.finished();
    }
  }
}

class PositiveAuthHarness {
  private readonly plans = new Map<
    string,
    Array<(route: PositiveRoute) => Promise<void>>
  >();
  private readonly holds: PositiveHold[] = [];
  private readonly totals = new Map<string, number>();
  private readonly routeWork: Array<{
    original: Promise<void>;
    terminal: Promise<PromiseSettledResult<void>[]>;
  }> = [];
  private unexpected = 0;
  private handlerErrors = 0;
  private responder: ReturnType<typeof status> | null = null;
  private readonly recordCanceled = (request: PositiveRequest): void => {
    for (const hold of this.holds) if (hold.owns(request)) hold.canceled = true;
  };
  readonly destination =
    "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/authorize?state=synthetic-positive-only";
  nativeAttempts = 0;
  nativeWireValid = false;
  loginWireValid = false;

  constructor(
    private readonly page: Page,
    private readonly origin: string,
  ) {}

  count(method: string, path: string): number {
    return this.totals.get(`${method} ${path}`) ?? 0;
  }

  plan(
    method: string,
    path: string,
    handler: (route: PositiveRoute) => Promise<void>,
  ): void {
    const key = `${method} ${path}`;
    const queue = this.plans.get(key) ?? [];
    queue.push(handler);
    this.plans.set(key, queue);
  }

  json(method: string, path: string, json: unknown): void {
    this.plan(method, path, (route) => route.fulfill({ json }));
  }

  status(state: SessionState, fresh = false, owner = ownerA): void {
    this.plan("GET", "/api/auth/session", async (route) => {
      positiveRequire(route.request().headers()["x-weft-session"] === "1");
      await route.fulfill({ json: status(state, fresh, owner) });
    });
  }

  /**
   * Answers every status read until stopped, after any queued status plans. Catalog
   * views supersede their first read at once, so how many reach the route varies.
   */
  respond(state: SessionState, fresh = false, owner = ownerA): void {
    positiveRequire(!this.responder);
    this.responder = status(state, fresh, owner);
  }

  stopResponding(): void {
    positiveRequire(this.responder);
    this.responder = null;
  }

  hold(
    method: string,
    path: string,
    check?: (request: PositiveRequest) => void,
  ): PositiveHold {
    const hold = new PositiveHold();
    this.holds.push(hold);
    this.plan(method, path, async (route) => {
      check?.(route.request());
      await hold.handle(route);
    });
    return hold;
  }

  login(request: PositiveRequest, authenticated: boolean): void {
    const headers = request.headers();
    this.loginWireValid =
      request.method() === "POST" &&
      request.postData() === null &&
      headers["content-type"] === undefined &&
      headers["x-weft-login"] === "1" &&
      headers["x-weft-auth-token"] === freshAuthToken &&
      headers["x-xsrf-token"] ===
        (authenticated ? freshEpochToken : undefined) &&
      headers["x-weft-logout"] === undefined &&
      headers["x-weft-forget-local"] === undefined &&
      headers["x-weft-session"] === undefined;
    positiveRequire(this.loginWireValid);
  }

  nativeNavigation(): Promise<void> {
    let finish!: () => void;
    const settled = new Promise<void>((resolve) => {
      finish = resolve;
    });
    this.plan("GET", this.destination, async (route) => {
      try {
        ++this.nativeAttempts;
        const request = route.request();
        const headers = await request.allHeaders();
        this.nativeWireValid =
          request.isNavigationRequest() &&
          request.frame() === this.page.mainFrame() &&
          headers["referer"] === undefined;
        positiveRequire(this.nativeWireValid);
        // Ending the provider navigation keeps this document, as a 204 would,
        // without leaving the navigation pending for the test runner.
        await route.abort("aborted");
      } finally {
        finish();
      }
    });
    return settled;
  }

  catalogContext() {
    return {
      mode: "session",
      identityContext: ownerA,
      requestToken: "Y2F0YWxvZy1vbmx5",
      actorPrincipalId: "10000000-0000-4000-8000-000000000002",
      allowedOrganizationId: positiveOrganization,
      organizationName: "Synthetic workspace",
      features: {
        readCatalog: true,
        createProjects: false,
        createMappings: false,
        editMetadata: false,
        edit: false,
        uploadRevisions: false,
        readReports: false,
        chat: false,
        preview: false,
        manageMembership: false,
        deriveReports: false,
      },
      organizationActions: { createProjects: false },
    };
  }

  async install(): Promise<void> {
    this.page.context().on("requestfailed", this.recordCanceled);
    await this.page.context().route("**/*", (route) => {
      const original = this.handleRoute(route);
      // Own the exact original task immediately, including non-plan route errors.
      // The callback returned to Playwright cannot create an unhandled rejection.
      const terminal = Promise.allSettled([original]);
      this.routeWork.push({ original, terminal });
      return terminal.then(() => {});
    });
  }

  private async handle(
    route: PositiveRoute,
    handler: () => Promise<void>,
  ): Promise<void> {
    try {
      await handler();
    } catch {
      ++this.handlerErrors;
      // Do not leave a failed assertion's original route unresolved. A route
      // that already settled may reject abort; handlerErrors still fails it.
      try {
        await route.abort();
      } catch {
        /* Already terminal; never logged. */
      }
    }
  }

  private async handleRoute(route: PositiveRoute): Promise<void> {
    const request = route.request();
    const url = new URL(request.url());
    const local = url.origin === this.origin;
    const path = local ? `${url.pathname}${url.search}` : request.url();
    const key = `${request.method()} ${path}`;
    const handler = this.plans.get(key)?.shift();
    if (handler) {
      this.totals.set(key, (this.totals.get(key) ?? 0) + 1);
      await this.handle(route, () => handler(route));
      return;
    }
    if (key === "GET /api/auth/session" && this.responder) {
      this.totals.set(key, (this.totals.get(key) ?? 0) + 1);
      const reply = this.responder;
      await this.handle(route, async () => {
        positiveRequire(request.headers()["x-weft-session"] === "1");
        await route.fulfill({ json: reply });
      });
      return;
    }
    // Only UI document/static delivery may reach the owned app host. API and
    // provider traffic must consume an exact, finite plan; never fall through.
    const document =
      request.isNavigationRequest() &&
      request.frame() === this.page.mainFrame() &&
      [positiveEntry, positiveProjects].includes(url.pathname) &&
      url.search === "";
    const staticAsset =
      ["script", "stylesheet", "font", "image"].includes(
        request.resourceType(),
      ) &&
      (/\.(?:m?js|css|woff2?|ico|svg|png)$/.test(url.pathname) ||
        url.pathname === "/@vite/client");
    if (
      local &&
      request.method() === "GET" &&
      !url.pathname.startsWith("/api/") &&
      (document || staticAsset)
    ) {
      await route.continue();
      return;
    }
    ++this.unexpected;
    await route.abort();
  }

  async cleanup(completed: boolean): Promise<void> {
    // Release every owned gate before closing, including a failed test's holds.
    for (const hold of this.holds) hold.cleanup();
    try {
      // All positive oracles run before fixture teardown. Leave fail-closed routing
      // installed throughout closure, even if a login completion starts navigation.
      // Context closure stops every page without running beforeunload handlers;
      // it disposes routing without an unrouteAll-before-close network window.
      await this.page.context().close();
    } finally {
      for (const hold of this.holds) hold.cleanup();
      // Closing settles blocked browser operations. Observe and drain ORIGINAL
      // work, not replacement/fake completion promises.
      let joined = 0;
      while (joined < this.routeWork.length) {
        const batch = this.routeWork.slice(joined);
        joined += batch.length;
        const results = await Promise.all(batch.map((work) => work.terminal));
        for (const result of results) {
          if (result[0].status === "rejected") ++this.handlerErrors;
        }
      }
      await Promise.all(
        this.holds.filter((hold) => hold.started).map((hold) => hold.settled),
      );
      this.page.context().off("requestfailed", this.recordCanceled);
    }
    expect(this.unexpected).toBe(0);
    expect(this.handlerErrors).toBe(0);
    if (completed) {
      expect(
        [...this.plans.values()].every((queue) => queue.length === 0),
      ).toBe(true);
      expect(
        this.holds.every((hold) => hold.started && hold.terminal !== null),
      ).toBe(true);
    }
  }
}

const positiveTest = test.extend<{ positiveAuth: PositiveAuthHarness }>({
  positiveAuth: async ({ page, baseURL }, use) => {
    positiveRequire(baseURL);
    const harness = new PositiveAuthHarness(page, new URL(baseURL).origin);
    await harness.install();
    let completed = false;
    try {
      await use(harness);
      completed = true;
    } finally {
      await harness.cleanup(completed);
    }
  },
});

positiveTest.describe(
  "positive native controls with synthetic provider isolation",
  () => {
    // This describe does not alter the pre-existing cases or their assertions.
    // Artifact options are worker-scoped, so failures keep the config defaults.
    positiveTest.use({ serviceWorkers: "block" });

    positiveTest(
      "anonymous login reaches native top-level navigation, then fresh bootstrap and separate catalog admission",
      async ({ page, positiveAuth: wire }) => {
        wire.status("anonymous");
        wire.status("anonymous", true);
        wire.plan("POST", "/api/auth/login", async (route) => {
          wire.login(route.request(), false);
          await route.fulfill({
            json: { version: 1, authorizationUrl: wire.destination },
          });
        });
        const native = wire.nativeNavigation();
        await page.goto(positiveEntry);
        await expect(summary(page)).toHaveText("Not signed in");
        await expect(
          controls(page).getByText(positiveUnloadWarning, { exact: true }),
        ).toBeVisible();
        await controls(page)
          .getByRole("button", { name: "Sign in", exact: true })
          .click();
        await expect(controls(page)).toContainText(
          "Sign-in is not yet confirmed.",
        );
        await expect.poll(() => wire.nativeAttempts).toBe(1);
        await native;
        expect(wire.nativeWireValid && wire.loginWireValid).toBe(true);
        await expect(summary(page)).toHaveText("Session check needed");
        expect(wire.count("GET", "/api/auth/session")).toBe(2);
        expect(wire.count("GET", "/api/v1/catalog/context")).toBe(0);

        // Explicit test-driven reload is NOT a callback and establishes no cookie.
        const bootstrap = wire.hold("GET", "/api/auth/session");
        await page.reload();
        await bootstrap.arrived;
        await expect(summary(page)).toHaveText("Checking sign-in status…");
        bootstrap.reply({ json: status("authenticated", true) });
        await bootstrap.settled;
        await expect(summary(page)).toHaveText("Signed in to Weft");
        expect(wire.count("GET", "/api/v1/catalog/context")).toBe(0);

        // Catalog landing and selected project list each own a separate admission.
        wire.respond("authenticated", true);
        const landing = wire.hold("GET", "/api/v1/catalog/context");
        const selection = wire.hold("GET", "/api/v1/catalog/context");
        wire.json(
          "GET",
          `/api/v1/organizations/${positiveOrganization}/projects?limit=10`,
          { projects: [], nextCursor: null },
        );
        await page
          .getByRole("navigation", { name: "Workspace", exact: true })
          .getByRole("link", { name: "Catalog", exact: true })
          .click();
        await landing.arrived;
        expect(
          wire.count(
            "GET",
            `/api/v1/organizations/${positiveOrganization}/projects?limit=10`,
          ),
        ).toBe(0);
        landing.reply({ json: wire.catalogContext() });
        await landing.settled;
        await selection.arrived;
        expect(
          wire.count(
            "GET",
            `/api/v1/organizations/${positiveOrganization}/projects?limit=10`,
          ),
        ).toBe(0);
        selection.reply({ json: wire.catalogContext() });
        await selection.settled;
        await expect(
          page.getByRole("heading", { name: "No projects", exact: true }),
        ).toBeVisible();
        wire.stopResponding();
        expect(wire.count("GET", "/api/v1/catalog/context")).toBe(2);
        expect(wire.count("POST", "/api/auth/login")).toBe(1);
        expect(wire.nativeAttempts).toBe(1);
      },
    );

    positiveTest(
      "authenticated Sign in again uses fresh dual-purpose headers, not catalog or readable-cookie tokens",
      async ({ page, baseURL, positiveAuth: wire }) => {
        positiveRequire(baseURL);
        await page
          .context()
          .addCookies([
            { name: "XSRF-TOKEN", value: "c3RhbGUtYW1iaWVudA", url: baseURL },
          ]);
        wire.status("authenticated");
        await page.goto(positiveEntry);
        await expect(summary(page)).toHaveText("Signed in to Weft");
        wire.respond("authenticated");
        wire.json("GET", "/api/v1/catalog/context", wire.catalogContext());
        wire.json("GET", "/api/v1/catalog/context", wire.catalogContext());
        wire.json(
          "GET",
          `/api/v1/organizations/${positiveOrganization}/projects?limit=10`,
          { projects: [], nextCursor: null },
        );
        await page
          .getByRole("navigation", { name: "Workspace", exact: true })
          .getByRole("link", { name: "Catalog", exact: true })
          .click();
        await expect(
          page.getByRole("heading", { name: "No projects", exact: true }),
        ).toBeVisible();
        wire.stopResponding();
        // Exactly one fresh admission read follows the deliberate action.
        const observed = wire.count("GET", "/api/auth/session");
        wire.status("authenticated", true);
        wire.plan("POST", "/api/auth/login", async (route) => {
          wire.login(route.request(), true);
          await route.fulfill({
            json: { version: 1, authorizationUrl: wire.destination },
          });
        });
        const native = wire.nativeNavigation();
        await expect(
          controls(page).getByText(positiveUnloadWarning, { exact: true }),
        ).toBeVisible();
        await controls(page)
          .getByRole("button", { name: "Sign in again", exact: true })
          .focus();
        await page.keyboard.press("Enter");
        await expect.poll(() => wire.nativeAttempts).toBe(1);
        await native;
        await expect(controls(page)).toContainText(
          "Sign-in is not yet confirmed.",
        );
        expect(wire.loginWireValid && wire.nativeWireValid).toBe(true);
        expect(wire.count("GET", "/api/auth/session")).toBe(observed + 1);
        expect(wire.count("POST", "/api/auth/login")).toBe(1);
        await expect(
          page.getByRole("heading", { name: "No projects", exact: true }),
        ).not.toBeVisible();
      },
    );

    positiveTest(
      "held original login blocks repeat keyboard, pointer and Check session attempts until one release",
      async ({ page, positiveAuth: wire }) => {
        wire.status("authenticated");
        wire.status("authenticated", true);
        const original = wire.hold("POST", "/api/auth/login", (request) =>
          wire.login(request, true),
        );
        const native = wire.nativeNavigation();
        await page.setViewportSize({ width: 390, height: 844 });
        await page.goto(positiveEntry);
        const signIn = controls(page).getByRole("button", {
          name: "Sign in again",
          exact: true,
        });
        await expect(signIn).toBeEnabled();
        const box = await signIn.boundingBox();
        positiveRequire(box);
        await signIn.focus();
        await page.keyboard.press("Enter");
        await original.arrived;
        await expect(controls(page)).toHaveAttribute("aria-busy", "true");
        await expect(controls(page)).toContainText("Requesting sign-in…");
        await expect(
          controls(page).getByRole("button", {
            name: "Check session",
            exact: true,
          }),
        ).toBeDisabled();
        await expect(signIn).toHaveCount(0);
        await expect(
          controls(page).getByRole("button", { name: "Sign out", exact: true }),
        ).toHaveCount(0);
        await expect(
          controls(page).getByRole("button", {
            name: "Review local recovery",
            exact: true,
          }),
        ).toHaveCount(0);
        await page.keyboard.press("Enter");
        await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
        const check = controls(page).getByRole("button", {
          name: "Check session",
          exact: true,
        });
        const checkBox = await check.boundingBox();
        positiveRequire(checkBox);
        await page.mouse.click(
          checkBox.x + checkBox.width / 2,
          checkBox.y + checkBox.height / 2,
        );
        await page.keyboard.press("Space");
        await page.waitForTimeout(200); // Deliberate bounded no-extra-request observation, not an assertion retry.
        expect(wire.count("GET", "/api/auth/session")).toBe(2);
        expect(wire.count("POST", "/api/auth/login")).toBe(1);
        expect(wire.nativeAttempts).toBe(0);
        original.reply({
          json: { version: 1, authorizationUrl: wire.destination },
        });
        await original.settled;
        await expect.poll(() => wire.nativeAttempts).toBe(1);
        await native;
        await expect(controls(page)).toHaveAttribute("aria-busy", "false");
        await expect(controls(page)).toContainText(
          "Sign-in is not yet confirmed.",
        );
        wire.status("authenticated", true);
        await check.click();
        await expect(summary(page)).toHaveText("Signed in to Weft");
        await page.waitForTimeout(200);
        expect(wire.nativeAttempts).toBe(1);
        expect(wire.count("POST", "/api/auth/login")).toBe(1);
        expect(
          await page.evaluate(
            () => document.documentElement.scrollWidth > innerWidth,
          ),
        ).toBe(false);
      },
    );

    for (const action of ["login", "logout"] as const) {
      positiveTest(
        `document replacement after dispatched ${action} cannot adopt the old response or replay acknowledgement`,
        async ({ page, positiveAuth: wire }) => {
          wire.status("authenticated");
          wire.status("authenticated", true);
          const original = wire.hold("POST", `/api/auth/${action}`);
          await page.goto(positiveEntry);
          await expect(summary(page)).toHaveText("Signed in to Weft");
          await controls(page)
            .getByRole("button", {
              name: action === "login" ? "Sign in again" : "Sign out",
              exact: true,
            })
            .click();
          await original.arrived;
          await expect(controls(page)).toHaveAttribute("aria-busy", "true");
          wire.status("authenticated", true, ownerB);
          await page.reload();
          await expect(summary(page)).toHaveText("Signed in to Weft");
          // The old HTTP outcome is unknown. Playwright reports no failure for a routed
          // request whose document was replaced, so this fulfillment is only a synthetic
          // late attempt against the replaced document, never evidence of no commit.
          original.reply(
            action === "login"
              ? { json: { version: 1, authorizationUrl: wire.destination } }
              : { status: 204 },
          );
          await original.settled;
          wire.status("authenticated", true, ownerB);
          await controls(page)
            .getByRole("button", { name: "Check session", exact: true })
            .click();
          await expect(summary(page)).toHaveText("Signed in to Weft");
          await page.waitForTimeout(200);
          await expect(
            controls(page).locator(
              ".session-notice:not(#sign-in-navigation-warning)",
            ),
          ).toHaveCount(0);
          await expect(
            controls(page).locator("#sign-in-navigation-warning"),
          ).toBeVisible();
          await expect(
            controls(page).locator("#sign-in-navigation-warning"),
          ).toHaveText(positiveUnloadWarning);
          expect(wire.count("POST", `/api/auth/${action}`)).toBe(1);
          expect(wire.count("GET", "/api/auth/session")).toBe(4);
          expect(wire.nativeAttempts).toBe(0);
          // No provider plan exists in this case: ANY attempted navigation fails closed.
        },
      );
    }
  },
);
