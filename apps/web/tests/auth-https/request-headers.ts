import { requireOwned } from "./wire";

const allowed =
  /^(?:accept|accept-encoding|accept-language|cache-control|pragma|priority|user-agent|sec-ch-ua(?:-mobile|-platform)?|sec-fetch-[a-z-]+|upgrade-insecure-requests|origin|referer|cookie|host|connection|content-length|x-weft-session|x-weft-login|x-weft-logout|x-weft-auth-token|x-xsrf-token)$/;
const actions = ["x-weft-session", "x-weft-login", "x-weft-logout"] as const;
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
        (preflight && key === "x-weft-preflight-vector" && value === "1")) &&
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
  if (auth) {
    const marker =
      path === "/api/auth/session"
        ? "x-weft-session"
        : path === "/api/auth/login"
          ? "x-weft-login"
          : "x-weft-logout";
    requireOwned(
      !url.search &&
        method === (marker === "x-weft-session" ? "GET" : "POST") &&
        result[marker] === "1" &&
        actions.every(
          (name) => name === marker || !Object.hasOwn(result, name),
        ),
    );
    if (marker === "x-weft-session")
      requireOwned(
        !Object.hasOwn(result, "x-weft-auth-token") &&
          !Object.hasOwn(result, "x-xsrf-token"),
      );
    else
      requireOwned(
        typeof result["x-weft-auth-token"] === "string" &&
          result["x-weft-auth-token"]!.length > 0 &&
          (!Object.hasOwn(result, "x-xsrf-token") ||
            result["x-xsrf-token"]!.length > 0),
      );
  } else
    requireOwned(
      [...actions, "x-weft-auth-token", "x-xsrf-token"].every(
        (name) => !Object.hasOwn(result, name),
      ),
    );
  return result;
}
