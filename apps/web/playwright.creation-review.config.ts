import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  testMatch: "creation-review.spec.ts",
  workers: 1,
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:5192",
    headless: true,
    launchOptions: { executablePath: process.env.BRAID_CHROMIUM_EXECUTABLE },
    screenshot: "only-on-failure",
  },
  webServer: {
    command: "bun run dev --port 5192 --no-hmr",
    url: "http://127.0.0.1:5192",
    reuseExistingServer: false,
    timeout: 120000,
  },
});
