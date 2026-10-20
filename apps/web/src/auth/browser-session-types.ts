import type { components } from "../api/generated";

// Share the versioned authentication contract with the generated API schema.
export type AuthStatus = components["schemas"]["AuthStatusV1"];

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
