import { test } from "@playwright/test";
import type {
  Browser,
  BrowserContext,
  CDPSession,
  Page,
} from "@playwright/test";
import { BrowserRootFetch } from "./root-fetch";
import { requestHeaders } from "./request-headers";
import { PauseOwner } from "./pause-owner";
import { CleanupPair } from "./cleanup-pair";
import {
  zeroBaseline,
  exactBackstops,
  type BackstopCounts,
} from "./backstop-counters";
import { requireOwned, type Input, type Reply, type Control } from "./wire";

// Component/protocol-double regressions, NOT actual browser/network results.
const origin = "https://127.0.0.1:43123";
const cfg = {
  origin,
  scenario: "positive",
  assets: ["inert.js"],
  organization: "11111111-1111-1111-1111-111111111111",
} as Input;
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((ok) => {
    resolve = ok;
  });
  return { promise, resolve };
}
const refused = (operation: () => unknown) => {
  let no = false;
  try {
    operation();
  } catch {
    no = true;
  }
  requireOwned(no);
};
function snapshot(host = 0, proxy = 0, closed = false): BackstopCounts {
  return {
    processGeneration: 1,
    admissionRefusals: host,
    admissionRefusalsTotal: host,
    proxyRefused: proxy,
    networkActive: 0,
    proofPending: 0,
    closedObservation: closed,
    counterValidation: closed,
  };
}
function reply(host = 0, proxy = 0): Reply {
  return {
    ...snapshot(host, proxy),
    version: 1,
    outcome: "ok",
    status: 0,
    callbackUrl: null,
    authorizationRequests: 0,
    tokenRequests: 0,
    assertionsAccepted: 0,
    pkceAccepted: 0,
    unexpected: 0,
    sessionRows: 0,
    activeRows: 0,
    revokedRows: 0,
    epochChanged: false,
    ringUnchanged: true,
    productDeliveries: 0,
    proofStripped: true,
    networkRequests: 0,
    networkPreflights: 0,
    networkAborts: 0,
    proofUsed: 0,
    networkArmed: false,
    admissionSealed: false,
    proxyApproved: 0,
  };
}

test("review header arguments: exact bootstrap and logout markers", () => {
  const bootstrap = requestHeaders(
    new URL(origin + "/api/auth/session"),
    "GET",
    [["X-Weft-Session", "1"]],
    false,
  );
  const logout = requestHeaders(
    new URL(origin + "/api/auth/logout"),
    "POST",
    [
      ["X-Weft-Logout", "1"],
      ["X-Weft-Auth-Token", "inert-action"],
      ["X-XSRF-TOKEN", "inert-epoch"],
    ],
    false,
  );
  requireOwned(
    bootstrap["x-weft-session"] === "1" &&
      logout["x-weft-logout"] === "1" &&
      logout["x-weft-auth-token"] === "inert-action",
  );
});
test("review header arguments: closed missing duplicate value route method and names", () => {
  for (const [path, method, entries] of [
    ["/api/auth/session", "GET", []],
    ["/api/auth/logout", "POST", [["X-Weft-Auth-Token", "inert-action"]]],
    [
      "/api/auth/session",
      "GET",
      [
        ["X-Weft-Session", "1"],
        ["x-weft-session", "1"],
      ],
    ],
    [
      "/api/auth/logout",
      "POST",
      [
        ["X-Weft-Logout", "1"],
        ["X-Weft-Logout", "1"],
      ],
    ],
    ["/api/auth/session", "GET", [["X-Weft-Session", "2"]]],
    ["/api/auth/logout", "POST", [["X-Weft-Logout", "1,1"]]],
    ["/sign-in", "GET", [["X-Weft-Session", "1"]]],
    ["/api/auth/login", "POST", [["X-Weft-Logout", "1"]]],
    ["/api/auth/session", "POST", [["X-Weft-Session", "1"]]],
    ["/api/auth/logout", "GET", [["X-Weft-Logout", "1"]]],
    ["/api/auth/session?unexpected=1", "GET", [["X-Weft-Session", "1"]]],
    [
      "/api/auth/session",
      "GET",
      [
        ["X-Weft-Session", "1"],
        ["X-Weft-Unknown", "1"],
      ],
    ],
    ["/api/auth/session", "GET", [["X-Weft-Session", "1\t"]]],
  ] as Array<[string, string, Array<[string, string]>]>)
    refused(() =>
      requestHeaders(new URL(origin + path), method, entries, false),
    );
});
test("review backstop totals: no hidden baseline or widened vector allowance", () => {
  zeroBaseline(snapshot(), 1);
  refused(() => zeroBaseline(snapshot(1), 1));
  refused(() => zeroBaseline(snapshot(0, 1), 1));
  refused(() => {
    zeroBaseline(snapshot(1), 1);
    exactBackstops(snapshot(2, 0, true), 1, "missing", 0, true);
  });
  refused(() => exactBackstops(snapshot(1, 0, true), 1, null, 0, true));
  refused(() => exactBackstops(snapshot(0, 1, true), 1, null, 0, true));
  exactBackstops(snapshot(1, 0, true), 1, "missing", 0, true);
  exactBackstops(snapshot(0, 1, true), 1, "proxy", 0, true);
  exactBackstops(snapshot(0, 4, true), 1, "proxy", 0, true);
  refused(() => exactBackstops(snapshot(0, 5, true), 1, "proxy", 0, true));
  refused(() => exactBackstops(snapshot(1, 0, true), 1, "frame", 0, true));
  refused(() => exactBackstops(snapshot(2, 0, true), 1, "missing", 0, true));
  refused(() => exactBackstops(snapshot(1, 0, false), 1, "missing", 0, true));
  refused(() =>
    exactBackstops(
      { ...snapshot(0, 0, true), counterValidation: false },
      1,
      null,
      0,
      true,
    ),
  );
  // Earlier peek may pass; a late closed-total increment must still fail.
  exactBackstops(snapshot(1), 1, "missing", 0, false);
  refused(() => exactBackstops(snapshot(2, 0, true), 1, "missing", 0, true));
  const hidden = { ...snapshot(1), admissionRefusalsTotal: 2 };
  refused(() => exactBackstops(hidden, 1, "stale", 0, false));
  // Only a response fault may report one resent refusal, and only when resent.
  exactBackstops(snapshot(1, 0, true), 1, "location-host", 0, true, true);
  exactBackstops(snapshot(0, 0, true), 1, "location-host", 0, true);
  refused(() =>
    exactBackstops(snapshot(1, 0, true), 1, "location-host", 0, true),
  );
  refused(() =>
    exactBackstops(snapshot(0, 0, true), 1, "location-host", 0, true, true),
  );
  refused(() =>
    exactBackstops(snapshot(2, 0, true), 1, "missing", 0, true, true),
  );
});
test("review pause owner: stage generations and original command failure", async () => {
  const book = new PauseOwner(),
    request = book.begin("inert", "request", "chain"),
    first = deferred<void>();
  let sent = 0;
  const a = book.resolve(request, () => {
    sent++;
    return first.promise;
  });
  const b = book.resolve(request, () => {
    sent++;
    return Promise.resolve();
  });
  requireOwned(request.state === "claimed" && sent === 1);
  first.resolve();
  await a;
  await b;
  const response = book.begin("inert", "response", "chain");
  await book.resolve(response, async () => {
    sent++;
  });
  const hop = book.begin("inert", "request", "next-chain");
  await book.resolve(hop, async () => {
    sent++;
  });
  await book.drain();
  // Callback increments are invisible to the earlier assertion narrowing.
  requireOwned((sent as number) === 3);
  const failing = new PauseOwner(),
    lease = failing.begin("inert", "request", "chain");
  let attempts = 0;
  const original = failing.resolve(lease, () => {
    attempts++;
    return Promise.reject(new Error("inert"));
  });
  const retry = failing.resolve(lease, async () => {
    attempts++;
  });
  const settled = await Promise.allSettled([original, retry]);
  requireOwned(
    attempts === 1 &&
      lease.state === "settled" &&
      lease.failed &&
      settled.every((value) => value.status === "rejected"),
  );
  const failedDrain = await Promise.allSettled([failing.drain()]);
  requireOwned(failedDrain[0]?.status === "rejected");
  const cancelled = new PauseOwner(),
    waiting = cancelled.begin("inert", "request", "chain");
  let failed = 0,
    continued = 0;
  await cancelled.resolve(waiting, async () => {
    failed++;
  });
  await cancelled.resolve(waiting, async () => {
    continued++;
  });
  await cancelled.drain();
  requireOwned(failed === 1 && continued === 0);
});

// Drives the ACTUAL sole-adapter handler through the supported public event/send
// shape using inert doubles; no browser, provider, file, server or protocol runs.
for (const phase of [
  "before-ack",
  "during-ack",
  "after-ack-before-continue",
  "before-native-fulfill",
  "continue-claimed",
  "bootstrap-continue-claimed",
] as const) {
  test("review actual adapter single-winner " + phase, async () => {
    const appEntered = deferred<void>(),
      appRelease = deferred<void>(),
      registering = deferred<void>(),
      ackRelease = deferred<void>();
    const continued = deferred<void>(),
      commandRelease = deferred<void>();
    let paused!: (event: unknown) => void,
      closed!: () => void,
      sealing: Promise<void> | null = null;
    let sealed = false,
      pending = 0,
      terminal = 0,
      fulfills = 0,
      registrations = 0;
    const session = {
      on(name: string, listener: (event?: unknown) => void) {
        if (name === "Fetch.requestPaused") paused = listener;
        if (name === "close") closed = listener;
        return this;
      },
      async send(
        method: string,
        params?: { headers?: Array<{ name: string; value: string }> },
      ) {
        if (method === "Fetch.enable") return {};
        terminal++;
        if (method === "Fetch.fulfillRequest") fulfills++;
        if (method === "Fetch.continueRequest") {
          const marker =
            phase === "continue-claimed" ? "x-weft-logout" : "x-weft-session";
          requireOwned(
            params?.headers?.some(
              (header) => header.name === marker && header.value === "1",
            ),
          );
          continued.resolve();
          await commandRelease.promise;
        }
        return {};
      },
    } as unknown as CDPSession;
    const page = {
      context: () => context,
      isClosed: () => false,
    } as unknown as Page;
    const context = {
      pages: () => [page],
      newCDPSession: async () => ({
        send: async (method: string) =>
          method === "Page.getFrameTree"
            ? { frameTree: { frame: { id: "frame" } } }
            : {
                targetInfo: {
                  type: "page",
                  targetId: "target",
                  browserContextId: "context",
                },
              },
        on: () => {},
        detach: async () => {},
      }),
    } as unknown as BrowserContext;
    const control = {
      async call(operation: string) {
        if (operation === "seal") {
          sealed = true;
          pending = 0;
          return { ...reply(), admissionSealed: true };
        }
        if (operation === "register") {
          registrations++;
          registering.resolve();
          if (phase === "during-ack") await ackRelease.promise;
          if (sealed) return { ...reply(), status: 409, admissionSealed: true };
          pending++;
          const ack = { ...reply(), status: 204 };
          if (phase === "after-ack-before-continue") {
            sealing = root.seal();
            void Promise.allSettled([sealing]);
          }
          return ack;
        }
        return reply();
      },
    } as unknown as Control;
    const root = new BrowserRootFetch(
      cfg,
      control,
      async () => {
        appEntered.resolve();
        await appRelease.promise;
        return {
          callback: origin + "/api/auth/callback/entra?code=inert&state=inert",
        };
      },
      async (request) => {
        appEntered.resolve();
        if (phase === "before-ack") await appRelease.promise;
        return { headers: request.headers, negative: null };
      },
      () => {},
    );
    await root.install({
      newBrowserCDPSession: async () => session,
      on: () => {},
    } as unknown as Browser);
    await root.bind(context, page);
    paused({
      requestId: "request",
      frameId: "frame",
      networkId: "network",
      resourceType: phase === "before-native-fulfill" ? "Document" : "Fetch",
      request: {
        url:
          phase === "before-native-fulfill"
            ? "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/authorize"
            : origin +
              (phase === "continue-claimed"
                ? "/api/auth/logout"
                : "/api/auth/session"),
        method: phase === "continue-claimed" ? "POST" : "GET",
        headers:
          phase === "before-native-fulfill"
            ? {}
            : phase === "continue-claimed"
              ? {
                  "X-Weft-Logout": "1",
                  "X-Weft-Auth-Token": "inert-action",
                  "X-XSRF-TOKEN": "inert-epoch",
                  Origin: origin,
                }
              : { "X-Weft-Session": "1" },
      },
    });
    if (phase === "before-ack" || phase === "before-native-fulfill")
      await appEntered.promise;
    else if (phase === "during-ack" || phase === "after-ack-before-continue")
      await registering.promise;
    else await continued.promise;
    sealing ??= root.seal();
    const observedSeal = Promise.allSettled([sealing]);
    appRelease.resolve();
    ackRelease.resolve();
    commandRelease.resolve();
    await sealing;
    await observedSeal;
    closed();
    await root.afterBrowserClosed();
    requireOwned(
      terminal === 1 &&
        fulfills === 0 &&
        sealed &&
        pending === 0 &&
        (phase !== "before-ack" || registrations === 0),
    );
  });
}
test("review cleanup pair: close failure never skips or retries original drain", async () => {
  const pair = new CleanupPair(),
    gate = deferred<void>();
  let closes = 0,
    drains = 0,
    completed = false;
  const first = pair.run(
    true,
    async () => {
      closes++;
      throw new Error("inert");
    },
    async () => {
      drains++;
      await gate.promise;
      completed = true;
    },
  );
  requireOwned(closes === 1 && drains === 1 && !completed);
  gate.resolve();
  const result = await first;
  await pair.run(
    false,
    async () => {
      closes++;
    },
    async () => {
      drains++;
    },
  );
  requireOwned(
    result.primaryFailed &&
      result.closeFailed &&
      !result.drainFailed &&
      completed &&
      closes === 1 &&
      drains === 1,
  );
});
test("review cleanup pair: primary and both failure categories remain", async () => {
  const result = await new CleanupPair().run(
    true,
    async () => {
      throw new Error("inert");
    },
    async () => {
      throw new Error("inert");
    },
  );
  requireOwned(
    result.primaryFailed && result.closeFailed && result.drainFailed,
  );
});
