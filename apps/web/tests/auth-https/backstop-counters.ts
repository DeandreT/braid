import { requireOwned, type Reply } from "./wire";
import type { NetworkCase } from "./conditional-policy";

export type BackstopCounts = Pick<
  Reply,
  | "processGeneration"
  | "admissionRefusals"
  | "admissionRefusalsTotal"
  | "proxyRefused"
  | "networkActive"
  | "proofPending"
  | "closedObservation"
  | "counterValidation"
>;
export function zeroBaseline(value: BackstopCounts, generation: number): void {
  requireOwned(
    value.processGeneration === generation &&
      value.admissionRefusals === 0 &&
      value.admissionRefusalsTotal === 0 &&
      value.proxyRefused === 0 &&
      value.networkActive === 0 &&
      value.proofPending === 0,
  );
}
const responseFaults = [
  "location-host",
  "location-ip",
  "location-port",
  "location-http",
  "location-file",
  "location-document",
  "location-duplicate",
  "refresh",
];
export function expectedBackstops(
  name: NetworkCase | null,
  categoryRefusals: number,
  resent = false,
): { host: number; proxy: number } {
  requireOwned(
    Number.isSafeInteger(categoryRefusals) &&
      categoryRefusals >= 0 &&
      categoryRefusals <= 1,
  );
  // An aborted response fault may be resent by Chromium with its consumed proof.
  requireOwned(!resent || (name !== null && responseFaults.includes(name)));
  if (name === "audio" || name === "paint")
    return { host: categoryRefusals === 0 ? 1 : 0, proxy: 0 };
  requireOwned(categoryRefusals === (name === "popup" ? 1 : 0));
  return {
    host:
      name &&
      [
        "preflight",
        "missing",
        "duplicate",
        "mismatch",
        "stale",
        "detach",
        "seal",
        "teardown",
      ].includes(name)
        ? 1
        : resent
          ? 1
          : 0,
    proxy: name === "proxy" ? 1 : 0,
  };
}
export function exactBackstops(
  value: BackstopCounts,
  generation: number,
  name: NetworkCase | null,
  categoryRefusals: number,
  closed: boolean,
  resent = false,
): void {
  const expected = expectedBackstops(name, categoryRefusals, resent);
  requireOwned(
    value.processGeneration === generation &&
      value.admissionRefusalsTotal === expected.host &&
      // Chromium also preconnects speculatively to the forbidden navigation target.
      (expected.proxy === 0
        ? value.proxyRefused === 0
        : value.proxyRefused >= 1 && value.proxyRefused <= 4) &&
      value.networkActive === 0 &&
      (!closed ||
        (value.closedObservation &&
          value.counterValidation &&
          value.proofPending === 0)),
  );
  // For a stale case the previous generation must contribute ZERO; a late refusal
  // there cannot substitute for the required actual new-generation refusal.
  if (!closed) requireOwned(value.admissionRefusals === expected.host);
}
