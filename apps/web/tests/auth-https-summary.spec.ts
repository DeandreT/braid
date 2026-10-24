import { expect, test } from "@playwright/test";
import { sessionSummaryMatches } from "./auth-https/summary";

test("HTTPS session status ignores only surrounding layout whitespace", () => {
  for (const actual of [
    "Not signed in",
    " Not signed in ",
    "\n  Not signed in\t\n",
  ]) {
    expect(sessionSummaryMatches(actual, "Not signed in")).toBe(true);
  }
});

test("HTTPS session status rejects missing, different and internally altered text", () => {
  for (const actual of [
    null,
    "",
    " \n\t ",
    "Signed in to Weft",
    "Not  signed in",
    "Not\nsigned in",
    "Not signed in: unavailable",
  ]) {
    expect(sessionSummaryMatches(actual, "Not signed in")).toBe(false);
  }
});
