import { test } from "@playwright/test";
import { continuationHeaders, expectedTuple } from "./conditional-policy";
import { requireOwned, type Input } from "./wire";

const cfg = {
  origin: "https://127.0.0.1:43123",
  organization: "11111111-1111-1111-1111-111111111111",
  assets: ["inert.js"],
} as Input;
test("conditional source arguments omit cookie and every sec-fetch spelling", () => {
  const args = continuationHeaders({
    COOKIE: "inert",
    "Sec-Fetch-Mode": "inert",
    "sEc-FeTcH-dEsT": "inert",
    "SEC-FETCH-unknown": "inert",
    Origin: cfg.origin,
    Accept: "text/html",
  });
  requireOwned(
    args.length === 2 &&
      args.every(
        (header) =>
          header.name.toLowerCase() !== "cookie" &&
          !header.name.toLowerCase().startsWith("sec-fetch-"),
      ) &&
      args.find((header) => header.name === "Origin")?.value === cfg.origin,
  );
});
test("conditional source registration expectation does not need early metadata", () => {
  const value = expectedTuple(
    cfg,
    new URL(cfg.origin + "/inert.js"),
    "GET",
    "Script",
    "normal",
  );
  requireOwned(
    value.destination === "script" &&
      value.mode === "cors" &&
      value.site === "same-origin" &&
      value.origin === cfg.origin,
  );
  const style = expectedTuple(
    { ...cfg, assets: ["inert.css"] },
    new URL(cfg.origin + "/inert.css"),
    "GET",
    "Stylesheet",
    "normal",
  );
  requireOwned(
    style.destination === "style" &&
      style.mode === "no-cors" &&
      style.origin === null,
  );
});
test("conditional source callback documents expect the restarted page-origin site", () => {
  const sites = (["normal", "callback", "return", "replay"] as const).map(
    (phase) =>
      expectedTuple(
        cfg,
        new URL(
          cfg.origin +
            (phase === "normal" || phase === "return"
              ? "/"
              : "/api/auth/callback/entra?code=inert&state=inert"),
        ),
        "GET",
        "Document",
        phase,
      ).site,
  );
  requireOwned(sites.join(",") === "none,same-origin,same-origin,same-origin");
});
test("conditional source expectation is closed not wildcard provenance", () => {
  let denied = false;
  try {
    expectedTuple(
      cfg,
      new URL(cfg.origin + "/inert.js"),
      "GET",
      "Other",
      "normal",
    );
  } catch {
    denied = true;
  }
  requireOwned(denied);
});
