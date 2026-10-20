import { requireOwned, type Input } from "./wire";
import { networkCases, type NetworkCase } from "./network-names";
export { networkCases, type NetworkCase } from "./network-names";

export const blinkRestriction =
  "--disable-blink-features=AnimationWorklet,CSSLayoutAPI,SharedStorageAPI,AdInterestGroupAPI,Fledge,Parakeet";
export const networkCase = (scenario: string): NetworkCase | null => {
  if (!scenario.startsWith("network-")) return null;
  const name = scenario.slice(8);
  requireOwned(networkCases.includes(name as NetworkCase));
  return name as NetworkCase;
};
export const forbiddenTarget = "https://192.0.2.1:44443/"; // ONE inert RFC5737 proxy vector, never a provider.
export type Tuple = Readonly<{
  destination: string;
  mode: string;
  site: string;
  origin: string | null;
}>;
export type DocumentPhase =
  "normal" | "callback" | "return" | "replay" | "reload" | "vector";
export type DocumentSite = "none" | "same-origin";
// CONDITIONAL expectation from a closed operation/category, NOT reported metadata.
// Registration alone is not a valid consumable proof; final host equality is mandatory.
export function expectedTuple(
  cfg: Input,
  url: URL,
  method: string,
  resource: string,
  phase: DocumentPhase,
  entrySite: DocumentSite = "none",
): Tuple {
  requireOwned(url.origin === cfg.origin);
  const target = url.pathname + url.search;
  if (resource === "Document") {
    requireOwned(
      method === "GET" &&
        (url.pathname === "/api/auth/callback/entra"
          ? phase === "callback" || phase === "replay"
          : [
              "/",
              "/examples",
              "/auth-qualification",
              `/catalog/organizations/${cfg.organization}/projects`,
            ].includes(target)),
    );
    return {
      destination: "document",
      mode: "navigate",
      // The provider redirect is fulfilled by the Fetch owner, so Chromium restarts
      // the callback from the page origin rather than the provider's site. A reload
      // keeps the initiator of the entry it reloads.
      site:
        phase === "reload"
          ? entrySite
          : phase === "callback" || phase === "return" || phase === "replay"
            ? "same-origin"
            : "none",
      origin: null,
    };
  }
  if (resource === "Fetch" || resource === "XHR") {
    requireOwned(
      (method === "GET" &&
        [
          "/api/auth/session",
          "/api/v1/catalog/context",
          "/api/v1/health",
          "/api/v1/examples/mappings",
          `/api/v1/organizations/${cfg.organization}/projects?limit=10`,
        ].includes(target)) ||
        (method === "POST" &&
          ["/api/auth/login", "/api/auth/logout"].includes(target)),
    );
    return {
      destination: "empty",
      mode: "cors",
      site: "same-origin",
      origin: method === "POST" ? cfg.origin : null,
    };
  }
  requireOwned(
    method === "GET" &&
      !url.search &&
      cfg.assets.includes(url.pathname.slice(1)),
  );
  const entry: Record<string, [string, string]> = {
    Script: ["script", "cors"],
    Stylesheet: ["style", "no-cors"],
    Image: ["image", "no-cors"],
    Font: ["font", "cors"],
  };
  const tuple = entry[resource];
  requireOwned(tuple);
  return {
    destination: tuple[0],
    mode: tuple[1],
    site: "same-origin",
    // Chromium sends Origin on CORS-mode module scripts and fonts, same-origin too.
    origin: tuple[1] === "cors" ? cfg.origin : null,
  };
}
// Used by the ACTUAL continuation path and tested as arguments, not a source grep.
export function continuationHeaders(
  headers: Record<string, string>,
): Array<{ name: string; value: string }> {
  return Object.entries(headers)
    .filter(
      ([name]) =>
        name.toLowerCase() !== "cookie" &&
        !name.toLowerCase().startsWith("sec-fetch-"),
    )
    .map(([name, value]) => ({ name, value }));
}
