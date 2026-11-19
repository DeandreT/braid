import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: ["organization-switching.spec.ts"],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  use: {
    baseURL: "http://127.0.0.1:5187",
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "only-on-failure",
  },
  webServer: {
    command:
      "bun run dev --port 5187 --proxy-config proxy.synthetic.conf.json --no-hmr",
    url: "http://127.0.0.1:5187",
    reuseExistingServer: false,
    timeout: 120_000,
    gracefulShutdown: { signal: "SIGTERM", timeout: 15_000 },
  },
});
