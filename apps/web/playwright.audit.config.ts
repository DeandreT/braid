import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: ["audit.spec.ts"],
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  use: {
    baseURL: "http://127.0.0.1:5189",
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
  },
  webServer: {
    command:
      "npm run dev -- --port 5189 --proxy-config proxy.synthetic.conf.json --no-hmr",
    url: "http://127.0.0.1:5189",
    reuseExistingServer: false,
    timeout: 120000,
    gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
  },
});
