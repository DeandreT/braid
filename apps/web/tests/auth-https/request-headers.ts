import { requireOwned } from "./wire";

const allowed =
  /^(?:accept|accept-encoding|accept-language|cache-control|pragma|priority|user-agent|sec-ch-ua(?:-mobile|-platform)?|sec-fetch-[a-z-]+|upgrade-insecure-requests|origin|referer|cookie|host|connection|content-length|x-braid-session|x-braid-login|x-braid-logout|x-braid-auth-token|x-xsrf-token|x-braid-return-to)$/;
const actions = ["x-braid-session", "x-braid-login", "x-braid-logout"] as const;
// Called by the sole Fetch admission path, BEFORE any grant/continuation. Entries
// preserve duplicate spellings/order; canonicalization must not silently overwrite.
export function requestHeaders(
  url: URL,
  method: string,
  entries: readonly (readonly [string, string])[],
  preflight: boolean,
): Record<string, string> {
  requireOwned(entries.length <= 128);
  const result: Record<string, string> = Object.create(null) as Record<
    string,
    string
  >;
  for (const [name, value] of entries) {
    const key = name.toLowerCase();
    requireOwned(
      (allowed.test(key) ||
        (preflight && key === "x-braid-preflight-vector" && value === "1")) &&
        !Object.hasOwn(result, key) &&
        name.length > 0 &&
        name.length <= 128 &&
        value.length <= 16384 &&
        !/[\x00-\x1f\x7f]/.test(name + value),
    );
    result[key] = value;
  }
  const path = url.pathname,
    auth = [
      "/api/auth/session",
      "/api/auth/login",
      "/api/auth/logout",
    ].includes(path);
  if (Object.hasOwn(result, "x-braid-return-to"))
    requireOwned(
      method === "POST" &&
        path === "/api/auth/login" &&
        (result["x-braid-return-to"] === "/catalog" ||
          /^\/catalog\/organizations\/[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\/projects$/.test(
            result["x-braid-return-to"]!,
          )),
    );
  if (auth) {
    const marker =
      path === "/api/auth/session"
        ? "x-braid-session"
        : path === "/api/auth/login"
          ? "x-braid-login"
          : "x-braid-logout";
    requireOwned(
      !url.search &&
        method === (marker === "x-braid-session" ? "GET" : "POST") &&
        result[marker] === "1" &&
        actions.every(
          (name) => name === marker || !Object.hasOwn(result, name),
        ),
    );
    if (marker === "x-braid-session")
      requireOwned(
        !Object.hasOwn(result, "x-braid-auth-token") &&
          !Object.hasOwn(result, "x-xsrf-token"),
      );
    else
      requireOwned(
        typeof result["x-braid-auth-token"] === "string" &&
          result["x-braid-auth-token"]!.length > 0 &&
          (!Object.hasOwn(result, "x-xsrf-token") ||
            result["x-xsrf-token"]!.length > 0),
      );
  } else
    requireOwned(
      [...actions, "x-braid-auth-token", "x-xsrf-token"].every(
        (name) => !Object.hasOwn(result, name),
      ),
    );
  return result;
}
