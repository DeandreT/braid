import { defineConfig } from "@playwright/test";

const baseline = process.env.WEFT_AUTOMATION_BASELINE_ROOT;
const port = baseline ? 5225 : 5226;

export default defineConfig({
  testDir: "./tests",
  testMatch: "automation.spec.ts",
  outputDir: process.env.WEFT_AUTOMATION_RESULTS ?? "./test-results/automation",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  use: {
    viewport: { width: 1280, height: 900 },
    baseURL: `http://127.0.0.1:${port}`,
    headless: false,
    launchOptions: { executablePath: process.env.WEFT_CHROMIUM_EXECUTABLE },
    trace: "off",
    screenshot: "only-on-failure",
  },
  webServer: {
    command: `npm run dev -- --port ${port} --no-hmr`,
    cwd: baseline ? `${baseline}/apps/web` : undefined,
    url: `http://127.0.0.1:${port}`,
    reuseExistingServer: false,
    gracefulShutdown: { signal: "SIGTERM", timeout: 15000 },
  },
});
