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
    headers["x-braid-session"] === "1" &&
    ![
      "content-type",
      "content-encoding",
      "authorization",
      "x-xsrf-token",
      "x-braid-auth-token",
      "idempotency-key",
      "if-match",
      "if-none-match",
      "if-modified-since",
      "if-unmodified-since",
      "if-range",
      "identitycontext",
      "identity-context",
      "x-braid-identity-context",
      "x-braid-actor",
      "x-braid-role",
      "x-braid-login",
      "x-braid-logout",
      "x-braid-forget-local",
    ].some((name) => name in headers)
  );
}
