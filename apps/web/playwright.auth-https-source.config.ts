import { defineConfig } from "@playwright/test";

// Pure edge-predicate source oracles only. No fixture/input/browser/CDP/DB owner.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
export default defineConfig({
  testDir: "./tests/auth-https",
  testMatch: [
    "redirect-policy.source.spec.ts",
    "conditional-policy.source.spec.ts",
    "review-regressions.source.spec.ts",
  ],
  workers: 1,
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  reporter: "./tests/auth-https/reporter.ts",
  use: {
    trace: "off",
    screenshot: "off",
    video: "off",
    serviceWorkers: "block",
  },
});
