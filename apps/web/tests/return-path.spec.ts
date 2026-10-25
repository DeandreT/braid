import { readFileSync } from "node:fs";
import { test, expect } from "@playwright/test";
import {
  defaultReturnPath,
  isReturnPath,
  returnPathOrDefault,
} from "../src/auth/return-path";

// The server's rule (Weft.Identity.BrowserReturnPath) is tested against the same vectors.
const vectors = JSON.parse(
  readFileSync(
    new URL("../../../contracts/browser-return-paths.json", import.meta.url),
    "utf8",
  ),
) as {
  valid: string[];
  invalid: string[];
  tooLong: { prefix: string; fill: string; maximumLength: number };
};

test("return paths accept only application section paths", () => {
  for (const value of vectors.valid)
    expect(isReturnPath(value), value).toBe(true);
  for (const value of vectors.invalid)
    expect(isReturnPath(value), JSON.stringify(value)).toBe(false);
  const { prefix, fill, maximumLength } = vectors.tooLong;
  const longest = prefix + fill.repeat(maximumLength - prefix.length);
  expect(isReturnPath(longest)).toBe(true);
  expect(isReturnPath(longest + fill)).toBe(false);
  expect(isReturnPath(null)).toBe(false);
  expect(isReturnPath(["/catalog"])).toBe(false);
  expect(returnPathOrDefault("//evil.example")).toBe(defaultReturnPath);
  expect(returnPathOrDefault(vectors.valid[1])).toBe(vectors.valid[1]);
});
