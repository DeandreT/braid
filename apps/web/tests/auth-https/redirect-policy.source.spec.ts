import { test } from "@playwright/test";
import {
  RedirectChainPolicy,
  type RedirectRequest,
  type RedirectScope,
} from "./redirect-policy";

// Public inert source vectors, NOT auth material, browser traffic, or OIDC evidence.
const app = "https://127.0.0.1:43123";
const native =
  "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/authorize?state=inert-source-vector";
const callback =
  app +
  "/api/auth/callback/entra?code=inert-source-code&state=inert-source-state";
const fixed = (condition: unknown): void => {
  if (!condition) throw new Error("Owned redirect source oracle failed.");
};
const refused = (operation: () => unknown): void => {
  let denied = false;
  try {
    operation();
  } catch {
    denied = true;
  }
  fixed(denied);
};
function plan() {
  const scope: RedirectScope = { context: {}, target: {}, frame: {} };
  const policy = new RedirectChainPolicy(app, native, scope);
  const request = (
    url: string,
    redirectedFrom: string | null = null,
  ): RedirectRequest => ({
    scope,
    url,
    redirectedFrom,
    method: "GET",
    bodyBytes: 0,
    authorization: false,
    contentType: false,
    appActionHeaders: false,
    cookie: url.startsWith(app),
    referer: false,
    resource: "Document",
  });
  return { policy, scope, request };
}
test("source model: exact native callback and native return consume separate single-use edges", () => {
  const { policy, request } = plan();
  policy.native(request(native));
  fixed(
    policy.issuer303(303, [{ name: "Location", value: callback }]) === callback,
  );
  fixed(!policy.facts().callbackConsumed);
  policy.callbackRequest(request(callback, native));
  fixed(policy.facts().callbackConsumed);
  refused(() => policy.callbackRequest(request(callback, native)));
  fixed(
    policy.callback303(303, [{ name: "Location", value: "/" }]) === app + "/",
  );
  policy.returnRequest(request(app + "/", callback));
  fixed(policy.facts().complete);
  refused(() => policy.returnRequest(request(app + "/", callback)));
});
test("source model: bad Location is rejected before either edge is armed", () => {
  for (const location of [
    "https://elsewhere.invalid/callback",
    "https://127.0.0.2:43123/api/auth/callback/entra?code=a&state=b",
    "https://127.0.0.1:43124/api/auth/callback/entra?code=a&state=b",
    "https://10.0.0.1/callback",
    "https://[::1]:43123/callback",
    "https://localhost:43123/callback",
    "https://user@127.0.0.1:43123/callback",
    "http://127.0.0.1:43123/callback",
    "file:///inert-source",
    "data:text/plain,inert-source",
    "javascript:void(0)",
    "wss://elsewhere.invalid/",
    "//elsewhere.invalid/",
    app + "/api/auth/logout",
    app + "/api/auth/callback/entra?code=a&state=b&state=c",
  ]) {
    const { policy, request } = plan();
    policy.native(request(native));
    refused(() =>
      policy.issuer303(303, [{ name: "Location", value: location }]),
    );
    fixed(!policy.facts().callbackConsumed);
  }
});
test("source model: callback redirects cannot select an external host API path or non-303 method rule", () => {
  for (const value of [
    "//elsewhere.invalid/",
    app + "/",
    "/api/auth/session",
    "/?unexpected=1",
    "https://10.0.0.1/",
  ]) {
    const { policy, request } = plan();
    policy.native(request(native));
    policy.issuer303(303, [{ name: "Location", value: callback }]);
    policy.callbackRequest(request(callback, native));
    refused(() => policy.callback303(303, [{ name: "Location", value }]));
    fixed(!policy.facts().returnConsumed);
  }
  for (const status of [301, 302, 307, 308]) {
    const { policy, request } = plan();
    policy.native(request(native));
    refused(() =>
      policy.issuer303(status, [{ name: "Location", value: callback }]),
    );
  }
});
test("source model: duplicate Location refresh changed method body credentials and foreign scope are refused", () => {
  for (const headers of [
    [
      { name: "Location", value: callback },
      { name: "location", value: callback },
    ],
    [
      { name: "Location", value: callback },
      { name: "Refresh", value: "0;url=/" },
    ],
  ]) {
    const { policy, request } = plan();
    policy.native(request(native));
    refused(() => policy.issuer303(303, headers));
  }
  for (const overrides of [
    { method: "POST" },
    { bodyBytes: 1 },
    { authorization: true },
    { appActionHeaders: true },
    { contentType: true },
    { referer: true },
    { resource: "Subdocument" },
    { scope: { context: {}, target: {}, frame: {} } },
  ]) {
    const { policy, request } = plan();
    policy.native(request(native));
    policy.issuer303(303, [{ name: "Location", value: callback }]);
    refused(() =>
      policy.callbackRequest({ ...request(callback, native), ...overrides }),
    );
    fixed(!policy.facts().callbackConsumed);
  }
});
test("source model: callback delivery without the exact immediate predecessor is refused", () => {
  for (const source of [null, app + "/", native + "&unexpected=1"]) {
    const { policy, request } = plan();
    policy.native(request(native));
    policy.issuer303(303, [{ name: "Location", value: callback }]);
    refused(() => policy.callbackRequest(request(callback, source)));
    fixed(!policy.facts().callbackConsumed);
  }
});
