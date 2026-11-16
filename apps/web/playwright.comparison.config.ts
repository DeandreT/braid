import { defineConfig } from "@playwright/test";

// Synthetic API routes qualify the comparison UI boundary; no API host is started.
export default defineConfig({
  testDir: "./tests",
  testMatch: "revision-comparison.spec.ts",
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:5174",
    headless: false,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command:
      "npm run dev -- --port 5174 --proxy-config proxy.synthetic.conf.json --no-hmr",
    url: "http://127.0.0.1:5174",
    reuseExistingServer: false,
    gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
    timeout: 120000,
  },
});
