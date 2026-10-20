import type { components } from "../api/generated";

// Share the versioned authentication contract with the generated API schema.
export type AuthStatus = components["schemas"]["AuthStatusV1"];
export type SessionAuthStatus = Extract<AuthStatus, { mode: "session" }>;
export type AuthLogin = components["schemas"]["AuthLoginV1"];
export type AuthForgetLocal = components["schemas"]["AuthForgetLocalV1"];

export type BrowserAuthAction = "login" | "logout" | "forgetLocal";
export type BrowserAuthActionView =
  | { readonly kind: "idle" }
  | {
      readonly kind: "admitting" | "pending" | "unconfirmed";
      readonly action: BrowserAuthAction;
    }
  | {
      readonly kind:
        | "notAdmitted"
        | "redirecting"
        | "logoutAcknowledged"
        | "revocationUnconfirmed";
    };

export type BrowserSessionView = {
  readonly kind:
    | "checking"
    | "disabled"
    | "development"
    | "anonymous"
    | "authenticationRequired"
    | "authenticated"
    | "unavailable";
};

export class BrowserSessionError extends Error {
  constructor() {
    super("Sign-in information could not be confirmed.");
    this.name = "BrowserSessionError";
  }
}

export function isIdentityContext(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^v1\.[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/.test(value)
  );
}

export function isAuthStatus(value: unknown): value is AuthStatus {
  if (!object(value) || value["version"] !== 1) return false;
  if (value["mode"] === "development" || value["mode"] === "disabled")
    return keys(value, ["version", "mode"]);
  if (
    value["mode"] !== "session" ||
    !keys(value, [
      "version",
      "mode",
      "state",
      "authActionRequestToken",
      "requestToken",
      "identityContext",
    ]) ||
    !isSessionRequestToken(value["authActionRequestToken"])
  )
    return false;
  if (value["state"] === "authenticated")
    return (
      isSessionRequestToken(value["requestToken"]) &&
      isIdentityContext(value["identityContext"])
    );
  return (
    (value["state"] === "anonymous" ||
      value["state"] === "authenticationRequired") &&
    value["requestToken"] === null &&
    value["identityContext"] === null
  );
}

export function isSessionRequestToken(value: unknown): value is string {
  if (
    typeof value !== "string" ||
    value.length < 2 ||
    value.length > 4096 ||
    !/^[A-Za-z0-9_-]+$/.test(value)
  )
    return false;
  const remainder = value.length % 4;
  return (
    remainder === 0 ||
    (remainder === 2 && /[AQgw]$/.test(value)) ||
    (remainder === 3 && /[AEIMQUYcgkosw048]$/.test(value))
  );
}

export function isAuthLogin(value: unknown): value is AuthLogin {
  return (
    object(value) &&
    keys(value, ["version", "authorizationUrl"]) &&
    value["version"] === 1 &&
    isAuthorizationUrl(value["authorizationUrl"])
  );
}

export function isAuthForgetLocal(value: unknown): value is AuthForgetLocal {
  return (
    object(value) &&
    keys(value, ["version", "outcome"]) &&
    value["version"] === 1 &&
    value["outcome"] === "revocationUnconfirmed"
  );
}

/** Navigation is limited to the fixed organizational Microsoft authorization endpoint. */
export function isAuthorizationUrl(value: unknown): value is string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > 16384 ||
    /[\s\\\u0000-\u001f\u007f]/u.test(value) ||
    value.includes("#") ||
    !/^https:\/\/login\.microsoftonline\.com\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/oauth2\/v2\.0\/authorize\?[^#]+$/.test(
      value,
    )
  )
    return false;
  try {
    const url = new URL(value);
    const tenant = url.pathname.split("/")[1];
    // The server remains the authority for the configured tenant and OIDC query.
    return (
      url.origin === "https://login.microsoftonline.com" &&
      url.username === "" &&
      url.password === "" &&
      url.hash === "" &&
      tenant !== "00000000-0000-0000-0000-000000000000" &&
      tenant !== "9188040d-6c67-4c5b-b112-36a304b66dad"
    );
  } catch {
    return false;
  }
}

function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function keys(
  value: Record<string, unknown>,
  expected: readonly string[],
): boolean {
  return (
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key))
  );
}
