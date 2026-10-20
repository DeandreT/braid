import { defineConfig } from "@playwright/test";
import { input } from "./tests/auth-https/wire";

const cfg = input(); // File/hash admission only; --list starts no fixture or child.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
export default defineConfig({
  testDir: "./tests/auth-https",
  testMatch: cfg.scenario.startsWith("network-")
    ? "network.spec.ts"
    : "flow.spec.ts",
  workers: 1,
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  timeout: 240000,
  reporter: "./tests/auth-https/reporter.ts",
  quiet: true,
  outputDir: cfg.root + "/playwright-output",
  use: {
    trace: "off",
    video: "off",
    screenshot: "off",
    serviceWorkers: "block",
  },
});
