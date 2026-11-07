import { defineConfig } from "@playwright/test";

// Only new synthetic return-path regressions. No PG, worker or real identity provider.
export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/search-return-path.spec.ts"],
  workers: 1,
  retries: 0,
  fullyParallel: false,
  use: {
    baseURL: "http://127.0.0.1:5184",
    headless: false,
    viewport: { width: 1280, height: 800 },
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
  },
  webServer: {
    command: "npm run dev -- --port 5184 --no-hmr",
    url: "http://127.0.0.1:5184",
    reuseExistingServer: false,
    gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
  },
});
