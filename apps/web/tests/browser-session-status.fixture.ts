import type { Request } from "@playwright/test";

/** Admit only the actual host observation; callers retain their original GET delivery. */
export function isSessionStatusRequest(
  request: Request,
  origin: string,
): boolean {
  const headers = request.headers();
  return (
    request.url() === `${origin}/api/auth/session` &&
    request.method() === "GET" &&
    (request.postDataBuffer()?.length ?? 0) === 0 &&
    headers["x-weft-session"] === "1" &&
    ![
      "content-type",
      "content-encoding",
      "authorization",
      "x-xsrf-token",
      "x-weft-auth-token",
      "idempotency-key",
      "if-match",
      "if-none-match",
      "if-modified-since",
      "if-unmodified-since",
      "if-range",
      "identitycontext",
      "identity-context",
      "x-weft-identity-context",
      "x-weft-actor",
      "x-weft-role",
      "x-weft-login",
      "x-weft-logout",
      "x-weft-forget-local",
    ].some((name) => name in headers)
  );
}
